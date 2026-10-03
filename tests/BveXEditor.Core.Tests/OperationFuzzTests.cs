using System.Numerics;
using BveXEditor.Core.Editing;
using BveXEditor.Core.Format;
using BveXEditor.Core.Format.Pmx;
using BveXEditor.Core.Model;

namespace BveXEditor.Core.Tests;

/// <summary>
/// 実物のモデルと基本形に、ランダムな選択で編集操作を続けてかけ、
/// 例外で落ちないこと・形のデータが壊れない（頂点番号が範囲内、UV・色の数が頂点数と合う）ことを確かめる。
/// </summary>
public class OperationFuzzTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static IEnumerable<(string Name, XScene Scene)> Scenes()
    {
        // 配布物のテストデータはリポジトリに入れていないので、手元にあるときだけ混ぜる
        if (!Fixtures.Missing("real_sample_building.x"))
            yield return ("real_sample_building.x", XFile.Load(Fixture("real_sample_building.x")).Scene);
        if (!Fixtures.Missing("real_toyonokuni.x"))
            yield return ("real_toyonokuni.x", XFile.Load(Fixture("real_toyonokuni.x")).Scene);
        if (!Fixtures.Missing("kodan.pmx"))
            yield return ("kodan.pmx", PmxReader.Load(Fixture("kodan.pmx")).Scene);
        var prims = new XScene();
        prims.Meshes.Add(Primitives.Box(2, 3, 4));
        prims.Meshes.Add(Primitives.Cylinder(0.5f, 2, 12));
        prims.Meshes.Add(Primitives.Pipe(1, 0.1f, 2, 16));
        prims.Meshes.Add(Primitives.Elbow(0.2f, 0.05f, 3, 2, 0.5f, 12));
        prims.Meshes.Add(ProfileOps.Lathe([new(0, 0), new(1, 0), new(1, 2), new(0, 3)], 10));
        yield return ("primitives", prims);
    }

    private static Selection RandomSelection(XScene scene, Random rng)
    {
        var sel = new Selection();
        var mode = (SelectMode)rng.Next(3);
        sel.SetMode(mode);
        if (scene.Meshes.Count == 0 || rng.Next(6) == 0) return sel; // 空の選択 = 全体
        var items = new List<ElementRef>();
        int picks = rng.Next(1, 12);
        for (int i = 0; i < picks; i++)
        {
            int m = rng.Next(scene.Meshes.Count);
            var mesh = scene.Meshes[m];
            int index = mode switch
            {
                SelectMode.Object => -1,
                SelectMode.Face => mesh.Faces.Count == 0 ? -1 : rng.Next(mesh.Faces.Count),
                _ => mesh.Positions.Count == 0 ? -1 : rng.Next(mesh.Positions.Count),
            };
            if (mode != SelectMode.Object && index < 0) continue;
            items.Add(new ElementRef(m, index));
        }
        sel.Set(items);
        return sel;
    }

    private static readonly (string Name, Func<XScene, Selection, Random, string> Run)[] Operations =
    [
        ("Transform", (s, sel, r) => MeshOps.Transform(s, sel, Matrix4x4.CreateRotationY(r.NextSingle()) * Matrix4x4.CreateScale(r.NextSingle() * 2 - 1))),
        ("AlignToOrigin", (s, sel, _) => MeshOps.AlignToOrigin(s, sel, MeshOps.OriginAlign.BottomCenter)),
        ("FlipFaces", (s, sel, _) => MeshOps.FlipFaces(s, sel)),
        ("DeleteFaces", (s, sel, _) => MeshOps.DeleteFaces(s, sel)),
        ("DeleteVertices", (s, sel, _) => MeshOps.DeleteVertices(s, sel)),
        ("Triangulate", (s, sel, _) => MeshOps.Triangulate(s, sel)),
        ("CreateFace", (s, sel, _) => MeshOps.CreateFace(s, sel)),
        ("Detach", (s, sel, _) => MeshOps.Detach(s, sel)),
        ("Weld", (s, sel, r) => MeshOps.Weld(s, sel, r.NextSingle() * 0.5f, r.Next(2) == 0)),
        ("Extrude", (s, sel, r) => MeshOps.Extrude(s, sel, r.NextSingle() * 2 - 1)),
        ("SeparateFaces", (s, sel, _) => MeshOps.SeparateFaces(s, sel)),
        ("MergeMeshes", (s, _, r) => s.Meshes.Count == 0 ? "" : MeshOps.MergeMeshes(s, [0, r.Next(s.Meshes.Count)])),
        ("SplitByMaterial", (s, _, r) => s.Meshes.Count == 0 ? "" : MeshOps.SplitByMaterial(s, r.Next(s.Meshes.Count))),
        ("AssignMaterial", (s, sel, r) => s.Meshes.Count == 0 ? "" : MeshOps.AssignMaterial(s, sel, r.Next(s.Meshes.Count), r.Next(3))),
        ("RecomputeNormals", (s, sel, r) => MeshOps.RecomputeNormals(s, sel, r.NextSingle() * 90)),
        ("Chamfer", (s, sel, r) => Chamfer.Apply(s, sel, r.NextSingle() * 0.3f, r.NextSingle() * 90)),
        ("DoubleSide", (s, sel, _) => FaceOps.DoubleSide(s, sel)),
        ("Inset", (s, sel, r) => FaceOps.Inset(s, sel, r.NextSingle() * 0.5f)),
        ("ArrayLinear", (s, sel, r) => FaceOps.ArrayLinear(s, sel, r.Next(1, 4), new Vector3(0, 0, 3))),
        ("ArrayRadial", (s, sel, r) => FaceOps.ArrayRadial(s, sel, r.Next(1, 4), r.Next(2) == 0 ? 360 : 90, Vector3.Zero)),
        ("Subdivide", (s, sel, r) => FaceOps.Subdivide(s, sel, r.Next(1, 4), r.Next(1, 4))),
        ("LoopCut", (s, sel, r) => FaceOps.LoopCut(s, sel, r.Next(1, 3))),
        ("SymmetryTransform", (s, sel, r) => Symmetry.Transform(s, sel, Matrix4x4.CreateTranslation(r.NextSingle(), 0, 0), Symmetry.Plan(s, sel))),
        ("Symmetrize", (s, sel, r) => Symmetry.Symmetrize(s, sel, r.Next(2) == 0)),
        ("UvTransform", (s, sel, r) => UvOps.Transform(s, sel, Matrix3x2.CreateRotation(r.NextSingle()))),
        ("UvProject", (s, sel, r) => UvOps.Project(s, sel, (UvProjectAxis)r.Next(Enum.GetValues<UvProjectAxis>().Length), (UvFit)r.Next(Enum.GetValues<UvFit>().Length), 1 + r.NextSingle())),
        ("BveCheckFix", (s, _, _) =>
        {
            foreach (var issue in BveCheck.Run(s, null).Where(i => i.Fix != CheckFix.None).Take(2)) BveCheck.Fix(s, issue);
            return "";
        }),
    ];

    private static void AssertValid(XScene scene, string context)
    {
        for (int m = 0; m < scene.Meshes.Count; m++)
        {
            var mesh = scene.Meshes[m];
            int nv = mesh.Positions.Count;
            Assert.True(mesh.TexCoords.Count == 0 || mesh.TexCoords.Count == nv, $"{context}: メッシュ {m} の UV 数 {mesh.TexCoords.Count} ≠ 頂点数 {nv}");
            Assert.True(mesh.VertexColors.Count == 0 || mesh.VertexColors.Count == nv, $"{context}: メッシュ {m} の色の数 {mesh.VertexColors.Count} ≠ 頂点数 {nv}");
            foreach (var f in mesh.Faces)
            {
                Assert.True(f.Indices.All(i => i >= 0 && i < nv), $"{context}: メッシュ {m} に範囲外の頂点番号");
                if (f.NormalIndices != null)
                {
                    Assert.Equal(f.Indices.Length, f.NormalIndices.Length);
                    Assert.True(f.NormalIndices.All(i => i >= 0 && i < mesh.Normals.Count), $"{context}: メッシュ {m} に範囲外の法線番号");
                }
            }
        }
    }

    [Fact]
    public void RandomOperationSequences_DoNotThrow_AndKeepMeshesValid()
    {
        var failures = new List<string>();
        foreach (var (name, original) in Scenes())
        {
            for (int seed = 0; seed < 40; seed++)
            {
                var rng = new Random(seed * 7919 + name.Length);
                var scene = original.Clone();
                var trail = new List<string>();
                try
                {
                    for (int step = 0; step < 6; step++)
                    {
                        var (opName, run) = Operations[rng.Next(Operations.Length)];
                        var sel = RandomSelection(scene, rng);
                        trail.Add($"{opName}({sel.Mode}×{sel.Count})");
                        run(scene, sel, rng);
                        AssertValid(scene, $"{name} seed {seed}: {string.Join(" → ", trail)}");
                        // 書き出して読み直せる
                        XFile.LoadText(XTextWriter.Write(scene));
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{name} seed {seed}: {string.Join(" → ", trail)}\n  {ex.GetType().Name}: {ex.Message.Split('\n')[0]}\n  {ex.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("BveXEditor.Core."))?.Trim()}");
                }
            }
        }
        Assert.True(failures.Count == 0, $"{failures.Count} 件:\n" + string.Join("\n", failures.Take(12)));
    }
}
