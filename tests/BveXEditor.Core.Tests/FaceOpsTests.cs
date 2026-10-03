using System.Numerics;
using BveXEditor.Core.Editing;
using BveXEditor.Core.Model;

namespace BveXEditor.Core.Tests;

internal static class MeshAssert
{
    public static (long, long, long) Key(Vector3 p) =>
        ((long)MathF.Round(p.X * 1e4f), (long)MathF.Round(p.Y * 1e4f), (long)MathF.Round(p.Z * 1e4f));

    /// <summary>位置で見て、すべての辺がちょうど 1 回ずつ逆向きに使われている（穴がなく向きもそろっている）。</summary>
    public static void ClosedAndConsistent(XMesh mesh)
    {
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
            Assert.True(directed.ContainsKey((b, a)), $"辺 {a}→{b} の反対側がありません（穴か向きの不一致）");
        }
    }

    public static void Outward(XMesh mesh, Vector3 center)
    {
        foreach (var f in mesh.Faces)
            Assert.True(Vector3.Dot(mesh.FaceNormal(f), mesh.FaceCenter(f) - center) > 0, "内向きの面があります");
    }

    public static float Area(XMesh mesh) => mesh.Faces.Sum(f =>
    {
        float a = 0;
        foreach (var (i, j, k) in MeshMath.Fan(f.Indices))
            a += Vector3.Cross(mesh.Positions[j] - mesh.Positions[i], mesh.Positions[k] - mesh.Positions[i]).Length() / 2;
        return a;
    });
}

public class FaceOpsTests
{
    [Fact]
    public void Pipe_ClosedOutward_WithHole()
    {
        var mesh = Primitives.Pipe(1f, 0.25f, 2f, 16);
        MeshAssert.ClosedAndConsistent(mesh);
        Assert.Equal(16 * 4, mesh.Faces.Count);
        Assert.Equal(0f, mesh.Positions.Min(p => p.Y), 4);
        Assert.Equal(2f, mesh.Positions.Max(p => p.Y), 4);
        // 穴: 軸からの距離は 0.75 か 1 だけ
        foreach (var p in mesh.Positions)
        {
            float r = new Vector2(p.X, p.Z).Length();
            Assert.True(MathF.Abs(r - 1f) < 1e-4f || MathF.Abs(r - 0.75f) < 1e-4f, $"半径 {r}");
        }
        // 外向き（符号付き体積が正 = 正 16 角形の差 × 高さ）
        float volume = 0;
        foreach (var f in mesh.Faces)
            foreach (var (a, b, c) in MeshMath.Fan(f.Indices))
                volume += Vector3.Dot(mesh.Positions[a], Vector3.Cross(mesh.Positions[b] - mesh.Positions[a], mesh.Positions[c] - mesh.Positions[a])) / 6;
        float polygon = 16 / 2f * MathF.Sin(MathF.Tau / 16);
        Assert.Equal(polygon * (1f - 0.75f * 0.75f) * 2f, volume, 3);

        Assert.Throws<ArgumentException>(() => Primitives.Pipe(1f, 1f, 2f, 16));
    }

    private static float SignedVolume(XMesh mesh)
    {
        float volume = 0;
        foreach (var f in mesh.Faces)
            foreach (var (a, b, c) in MeshMath.Fan(f.Indices))
                volume += Vector3.Dot(mesh.Positions[a], Vector3.Cross(mesh.Positions[b] - mesh.Positions[a], mesh.Positions[c] - mesh.Positions[a])) / 6;
        return volume;
    }

    [Theory]
    [InlineData(0f, 0f)]     // 角ばった棒
    [InlineData(0f, 0.5f)]   // 曲がった棒
    [InlineData(0.05f, 0f)]  // 角ばったパイプ
    [InlineData(0.05f, 0.5f)] // 曲がったパイプ
    public void Elbow_ClosedOutward_AndReachesEnds(float thickness, float bend)
    {
        var mesh = Primitives.Elbow(0.2f, thickness, 3f, 2f, bend, 16, Primitives.ElbowDirection.PlusZ);
        MeshAssert.ClosedAndConsistent(mesh);
        Assert.True(SignedVolume(mesh) > 0, "内向きの面があります");
        Assert.Equal(0f, mesh.Positions.Min(p => p.Y), 4);           // 縦の管は地面から
        Assert.Equal(2f, mesh.Positions.Max(p => p.Z), 3);           // 横の管は +Z へ 2 m
        Assert.Equal(3.2f, mesh.Positions.Max(p => p.Y), 3);         // 横の管の上面 = 中心線 3 m + 半径
        Assert.True(MathF.Abs(mesh.Positions.Max(p => p.X) - 0.2f) < 1e-3f, "横に曲がる向きが違います");
    }

    [Fact]
    public void Elbow_SharpCorner_KeepsTubeThickness()
    {
        // 角の継ぎ目でも、横の管の断面の半径は 0.2 のまま（二等分面で継いでいる）
        var mesh = Primitives.Elbow(0.2f, 0, 3f, 2f, 0, 16, Primitives.ElbowDirection.PlusX);
        var endCap = mesh.Positions.Where(p => MathF.Abs(p.X - 2f) < 1e-4f).ToList();
        Assert.NotEmpty(endCap);
        foreach (var p in endCap) Assert.True(new Vector2(p.Y - 3f, p.Z).Length() <= 0.2001f);

        Assert.Throws<ArgumentException>(() => Primitives.Elbow(0.2f, 0, 3f, 2f, 2.5f, 16));   // 曲げ半径が長すぎる
        Assert.Throws<ArgumentException>(() => Primitives.Elbow(0.2f, 0, 3f, 2f, 0.1f, 16));   // 管の半径より小さい
    }

    private static XScene SceneOf(XMesh m)
    {
        var s = new XScene();
        s.Meshes.Add(m);
        return s;
    }

    private static Selection Faces(params int[] faces)
    {
        var sel = new Selection();
        sel.SetMode(SelectMode.Face);
        sel.Set(faces.Select(f => new ElementRef(0, f)));
        return sel;
    }

    [Fact]
    public void DoubleSide_AddsReversedFaces_OnlyOnce()
    {
        var scene = SceneOf(Primitives.Plane(2, 1, vertical: true));
        var mesh = scene.Meshes[0];
        var n0 = mesh.FaceNormal(mesh.Faces[0]);

        FaceOps.DoubleSide(scene, new Selection());
        Assert.Equal(2, mesh.Faces.Count);
        Assert.True(Vector3.Dot(mesh.FaceNormal(mesh.Faces[1]), n0) < -0.99f);
        // 裏面は同じ位置に同じ UV を持つ別の頂点
        foreach (var v in mesh.Faces[1].Indices)
        {
            int front = mesh.Faces[0].Indices.Single(o => mesh.Positions[o] == mesh.Positions[v]);
            Assert.NotEqual(front, v);
            Assert.Equal(mesh.TexCoords[front], mesh.TexCoords[v]);
        }

        FaceOps.DoubleSide(scene, new Selection());
        Assert.Equal(2, mesh.Faces.Count);
    }

    [Fact]
    public void Inset_Quad_MakesFiveFaces_WithInnerSquare()
    {
        var scene = SceneOf(Primitives.Plane(2, 2, vertical: false));
        var mesh = scene.Meshes[0];
        var n0 = mesh.FaceNormal(mesh.Faces[0]);
        var sel = Faces(0);

        FaceOps.Inset(scene, sel, 0.25f);

        Assert.Equal(5, mesh.Faces.Count);
        var inner = mesh.Faces[0];
        for (int i = 0; i < 4; i++)
        {
            var a = mesh.Positions[inner.Indices[i]];
            var b = mesh.Positions[inner.Indices[(i + 1) % 4]];
            Assert.Equal(1.5f, Vector3.Distance(a, b), 3);
        }
        foreach (var f in mesh.Faces) Assert.True(Vector3.Dot(mesh.FaceNormal(f), n0) > 0.99f, "向きが変わった面があります");
        Assert.Equal(4f, MeshAssert.Area(mesh), 3);
        // UV は位置に比例（0..1 の板の 0.25 m 内側は 0.125）
        var uvs = inner.Indices.Select(v => mesh.TexCoords[v]).ToList();
        Assert.Equal(0.125f, uvs.Min(t => t.X), 3);
        Assert.Equal(0.875f, uvs.Max(t => t.X), 3);
    }

    [Fact]
    public void Inset_BoxFace_KeepsClosed()
    {
        var scene = SceneOf(Primitives.Box(2, 2, 2));
        FaceOps.Inset(scene, Faces(0, 3), 0.3f);
        MeshAssert.ClosedAndConsistent(scene.Meshes[0]);
        MeshAssert.Outward(scene.Meshes[0], new Vector3(0, 1, 0));
    }

    [Fact]
    public void ArrayLinear_CopiesAtEqualSpacing()
    {
        var scene = SceneOf(Primitives.Box(1, 1, 1));
        FaceOps.ArrayLinear(scene, new Selection(), 4, new Vector3(0, 0, 5));
        var mesh = scene.Meshes[0];
        Assert.Equal(24, mesh.Faces.Count);
        Assert.Equal(15f + 0.5f, mesh.Positions.Max(p => p.Z), 4);
        for (int k = 0; k < 4; k++)
        {
            var box = new XMesh();
            box.Positions.AddRange(mesh.Positions);
            box.Faces.AddRange(mesh.Faces.Skip(6 * k).Take(6));
            MeshAssert.Outward(box, new Vector3(0, 0.5f, 5 * k));
        }
    }

    [Fact]
    public void ArrayRadial_Full360_NoOverlap()
    {
        var mesh = new XMesh();
        mesh.Materials.Add(new XMaterial());
        Primitives.AddQuad(mesh, new Vector3(5, 0, 0), Vector3.UnitX, 1, 1);
        var scene = SceneOf(mesh);

        FaceOps.ArrayRadial(scene, new Selection(), 4, 360, Vector3.Zero);

        Assert.Equal(4, mesh.Faces.Count);
        var centers = mesh.Faces.Select(mesh.FaceCenter).ToList();
        foreach (var c in centers) Assert.Equal(5f, new Vector2(c.X, c.Z).Length(), 3);
        Assert.Equal(4, centers.Select(c => MeshAssert.Key(c)).Distinct().Count());
    }

    [Fact]
    public void Subdivide_Grid_InterpolatesUv()
    {
        var scene = SceneOf(Primitives.Plane(3, 2, vertical: true));
        var mesh = scene.Meshes[0];
        FaceOps.Subdivide(scene, new Selection(), 3, 2);
        Assert.Equal(6, mesh.Faces.Count);
        Assert.Equal(12, mesh.Positions.Count);
        Assert.Equal(6f, MeshAssert.Area(mesh), 3);
        // 立て看板は X が -1.5..1.5 で U が 0..1（向きはどちらでも、X に比例していればよい）
        var all = Enumerable.Range(0, mesh.Positions.Count).ToList();
        bool increasing = all.All(v => MathF.Abs((mesh.Positions[v].X + 1.5f) / 3f - mesh.TexCoords[v].X) < 1e-3f);
        bool decreasing = all.All(v => MathF.Abs((1.5f - mesh.Positions[v].X) / 3f - mesh.TexCoords[v].X) < 1e-3f);
        Assert.True(increasing || decreasing);
    }

    [Fact]
    public void LoopCut_AroundBox_AddsRingAndStaysClosed()
    {
        var scene = SceneOf(Primitives.Box(2, 2, 2));
        var mesh = scene.Meshes[0];
        // 手前の面の下の辺の両端（位置 (-1,0,-1) と (1,0,-1)）
        int a = Enumerable.Range(0, mesh.Positions.Count).First(i => mesh.Positions[i] == new Vector3(-1, 0, -1));
        int b = Enumerable.Range(0, mesh.Positions.Count).First(i => mesh.Positions[i] == new Vector3(1, 0, -1));
        var sel = new Selection();
        sel.SetMode(SelectMode.Vertex);
        sel.Set([new ElementRef(0, a), new ElementRef(0, b)]);

        var status = FaceOps.LoopCut(scene, sel, 1);

        Assert.Equal(10, mesh.Faces.Count);
        Assert.Contains("4 面", status);
        MeshAssert.ClosedAndConsistent(mesh);
        MeshAssert.Outward(mesh, new Vector3(0, 1, 0));
        // 切れ目は X=0 の輪
        Assert.Contains(mesh.Positions, p => p == new Vector3(0, 2, 1));
    }
}
