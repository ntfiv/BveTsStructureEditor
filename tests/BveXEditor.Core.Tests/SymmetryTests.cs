using System.Numerics;
using BveXEditor.Core.Editing;
using BveXEditor.Core.Model;

namespace BveXEditor.Core.Tests;

public class SymmetryTests
{
    private static XScene SceneOf(XMesh m)
    {
        var s = new XScene();
        s.Meshes.Add(m);
        return s;
    }

    private static Selection VerticesWhere(XScene scene, Func<Vector3, bool> pred)
    {
        var sel = new Selection();
        sel.SetMode(SelectMode.Vertex);
        var mesh = scene.Meshes[0];
        sel.Set(Enumerable.Range(0, mesh.Positions.Count).Where(i => pred(mesh.Positions[i])).Select(i => new ElementRef(0, i)));
        return sel;
    }

    private static (long, long, long) Key(Vector3 p) =>
        ((long)MathF.Round(p.X * 1e4f), (long)MathF.Round(p.Y * 1e4f), (long)MathF.Round(p.Z * 1e4f));

    [Fact]
    public void Plan_FindsMirrorVertices_CenterAndUnmatched()
    {
        var mesh = Primitives.Box(2, 2, 2);
        int center = mesh.AddVertex(new Vector3(0, 5, 0));
        int lonely = mesh.AddVertex(new Vector3(3, 5, 0));
        var scene = SceneOf(mesh);
        var sel = VerticesWhere(scene, p => p.X > 0.5f && p.Y > 1.5f && p.Y < 3);
        sel.Add([new ElementRef(0, center), new ElementRef(0, lonely)]);

        var plan = Symmetry.Plan(scene, sel);

        Assert.True(plan.TargetCount > 0);
        foreach (var v in plan.Targets[0])
            Assert.True(mesh.Positions[v].X < -0.5f && mesh.Positions[v].Y > 1.5f);
        Assert.Contains(center, plan.Center[0]);
        Assert.Equal(1, plan.Unmatched);
    }

    [Fact]
    public void Transform_MovesMirrorSideOppositeInX()
    {
        var scene = SceneOf(Primitives.Box(2, 2, 2));
        var mesh = scene.Meshes[0];
        var sel = VerticesWhere(scene, p => p.X > 0.5f);
        var before = mesh.Positions.ToList();
        var plan = Symmetry.Plan(scene, sel);

        Symmetry.Transform(scene, sel, Matrix4x4.CreateTranslation(0.5f, 0.25f, 0.1f), plan);

        for (int i = 0; i < before.Count; i++)
        {
            var expected = before[i].X > 0.5f ? before[i] + new Vector3(0.5f, 0.25f, 0.1f)
                         : before[i].X < -0.5f ? before[i] + new Vector3(-0.5f, 0.25f, 0.1f)
                         : before[i];
            Assert.True(Vector3.Distance(expected, mesh.Positions[i]) < 1e-5f, $"{i}: {expected} != {mesh.Positions[i]}");
        }
    }

    [Fact]
    public void Transform_RotationIsMirrored()
    {
        var mesh = new XMesh();
        int a = mesh.AddVertex(new Vector3(1, 0, 0));
        int b = mesh.AddVertex(new Vector3(-1, 0, 0));
        var scene = SceneOf(mesh);
        var sel = VerticesWhere(scene, p => p.X > 0);
        var rot = GizmoMath.Rotation(Vector3.UnitY, MathF.PI / 2, new Vector3(2, 0, 0));

        Symmetry.Transform(scene, sel, rot, Symmetry.Plan(scene, sel));

        var pa = mesh.Positions[a];
        var pb = mesh.Positions[b];
        Assert.True(Vector3.Distance(pb, new Vector3(-pa.X, pa.Y, pa.Z)) < 1e-5f);
        Assert.True(MathF.Abs(pa.X - 1) > 0.1f || MathF.Abs(pa.Z) > 0.1f);
    }

    [Fact]
    public void Transform_BothSidesSelected_AppliesSameMatrix()
    {
        var mesh = new XMesh();
        mesh.AddVertex(new Vector3(1, 0, 0));
        mesh.AddVertex(new Vector3(-1, 0, 0));
        var scene = SceneOf(mesh);
        var sel = VerticesWhere(scene, _ => true);
        var plan = Symmetry.Plan(scene, sel);
        Assert.Equal(0, plan.TargetCount);

        Symmetry.Transform(scene, sel, Matrix4x4.CreateTranslation(1, 0, 0), plan);

        Assert.Equal(new Vector3(2, 0, 0), mesh.Positions[0]);
        Assert.Equal(new Vector3(0, 0, 0), mesh.Positions[1]);
    }

    [Fact]
    public void Transform_OneSide_KeepsCenterVerticesOnCenter()
    {
        var mesh = new XMesh();
        mesh.AddVertex(new Vector3(0, 0, 0));
        mesh.AddVertex(new Vector3(1, 0, 0));
        mesh.AddVertex(new Vector3(-1, 0, 0));
        var scene = SceneOf(mesh);
        var sel = VerticesWhere(scene, p => p.X >= 0);

        Symmetry.Transform(scene, sel, Matrix4x4.CreateTranslation(0.5f, 1, 0), Symmetry.Plan(scene, sel));

        Assert.Equal(new Vector3(0, 1, 0), mesh.Positions[0]);
        Assert.Equal(new Vector3(1.5f, 1, 0), mesh.Positions[1]);
        Assert.Equal(new Vector3(-1.5f, 1, 0), mesh.Positions[2]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Symmetrize_CutsAtCenterAndMirrors_ClosedAndOutward(bool keepPositive)
    {
        var mesh = Primitives.Box(2, 2, 2);
        float shift = keepPositive ? 0.5f : -0.5f; // 残す側を 1.5 m、捨てる側を 0.5 m にずらす
        for (int i = 0; i < mesh.Positions.Count; i++) mesh.Positions[i] += new Vector3(shift, 0, 0);
        var scene = SceneOf(mesh);

        Symmetry.Symmetrize(scene, new Selection(), keepPositive);

        Assert.Equal(1.5f, mesh.Positions.Max(p => p.X), 4);
        Assert.Equal(-1.5f, mesh.Positions.Min(p => p.X), 4);

        // 位置で見て、すべての辺がちょうど 2 回、逆向きに使われている
        var directed = new Dictionary<((long, long, long), (long, long, long)), int>();
        foreach (var f in mesh.Faces)
            for (int i = 0; i < f.Indices.Length; i++)
            {
                var a = Key(mesh.Positions[f.Indices[i]]);
                var b = Key(mesh.Positions[f.Indices[(i + 1) % f.Indices.Length]]);
                directed[(a, b)] = directed.GetValueOrDefault((a, b)) + 1;
            }
        foreach (var ((a, b), count) in directed)
        {
            Assert.Equal(1, count);
            Assert.True(directed.ContainsKey((b, a)), $"辺 {a}→{b} の反対側がありません");
        }

        var center = new Vector3(0, 1, 0);
        foreach (var f in mesh.Faces)
            Assert.True(Vector3.Dot(mesh.FaceNormal(f), mesh.FaceCenter(f) - center) > 0, "内向きの面があります");
    }

    [Fact]
    public void Symmetrize_InterpolatesUvAtCut()
    {
        var mesh = new XMesh();
        mesh.Materials.Add(new XMaterial());
        // x = -1..3 の地面の板。UV は x に比例
        int a = mesh.AddVertex(new Vector3(-1, 0, 0), new Vector2(0, 0));
        int b = mesh.AddVertex(new Vector3(-1, 0, 1), new Vector2(0, 1));
        int c = mesh.AddVertex(new Vector3(3, 0, 1), new Vector2(1, 1));
        int d = mesh.AddVertex(new Vector3(3, 0, 0), new Vector2(1, 0));
        mesh.Faces.Add(new XFace([a, b, c, d]));
        var scene = SceneOf(mesh);

        Symmetry.Symmetrize(scene, new Selection(), keepPositive: true);

        Assert.Equal(2, mesh.Faces.Count);
        var cut = Enumerable.Range(0, mesh.Positions.Count).Where(i => mesh.Positions[i].X == 0).ToList();
        Assert.Equal(2, cut.Count);
        foreach (var i in cut) Assert.Equal(0.25f, mesh.TexCoords[i].X, 4);
        // 表の向き（上向き）は両側で同じ
        foreach (var f in mesh.Faces) Assert.True(mesh.FaceNormal(f).Y * Math.Sign(mesh.FaceNormal(mesh.Faces[0]).Y) > 0);
    }
}
