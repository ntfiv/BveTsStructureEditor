using System.Numerics;
using BveXEditor.Core.Editing;
using BveXEditor.Core.Format;
using BveXEditor.Core.Model;

namespace BveXEditor.Core.Tests;

public class ChamferTests
{
    private static XScene SceneOf(XMesh m)
    {
        var s = new XScene();
        s.Meshes.Add(m);
        return s;
    }

    private static (long, long, long) Key(Vector3 p) =>
        ((long)MathF.Round(p.X * 1e4f), (long)MathF.Round(p.Y * 1e4f), (long)MathF.Round(p.Z * 1e4f));

    /// <summary>位置で見て、すべての辺がちょうど 2 回、逆向きに使われている（穴がなく向きもそろっている）。</summary>
    private static void AssertClosedAndConsistent(XMesh mesh)
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

    private static void AssertOutward(XMesh mesh, Vector3 center)
    {
        foreach (var f in mesh.Faces)
            Assert.True(Vector3.Dot(mesh.FaceNormal(f), mesh.FaceCenter(f) - center) > 0, "内向きの面があります");
    }

    [Fact]
    public void Whole_Box_Gets_Strips_And_Corner_Triangles()
    {
        var scene = SceneOf(Primitives.Box(2, 2, 2));
        var msg = Chamfer.Apply(scene, new Selection(), 0.2f);
        var mesh = scene.Meshes[0];
        Assert.Contains("12 本", msg);
        // 元の 6 面 + 辺の帯 12 + 角の三角形 8
        Assert.Equal(26, mesh.Faces.Count);
        Assert.Equal(8, mesh.Faces.Count(f => f.Indices.Length == 3));
        AssertClosedAndConsistent(mesh);
        AssertOutward(mesh, new Vector3(0, 1, 0));
        // 外形の大きさは変わらない（角が削れるだけ）
        var min = mesh.Positions.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
        var max = mesh.Positions.Aggregate(new Vector3(float.MinValue), Vector3.Max);
        Assert.Equal(new Vector3(-1, 0, -1), min);
        Assert.Equal(new Vector3(1, 2, 1), max);
        // 元の角 (1,2,1) の点は無くなっている
        Assert.DoesNotContain(new Vector3(1, 2, 1), mesh.Positions);
        Assert.True(mesh.HasUV);
    }

    [Fact]
    public void Triangulated_Box_Is_Merged_Back_And_Gives_Same_Result()
    {
        // PMX などの三角形だけのメッシュ: 対角線で角がずれないよう、平らな三角形 2 枚を四角形に戻してから面取りする
        var scene = SceneOf(Primitives.Box(2, 2, 2));
        MeshOps.Triangulate(scene, new Selection());
        Assert.Equal(12, scene.Meshes[0].Faces.Count);
        var msg = Chamfer.Apply(scene, new Selection(), 0.2f);
        var mesh = scene.Meshes[0];
        Assert.Contains("12 本", msg);
        Assert.Equal(26, mesh.Faces.Count);
        AssertClosedAndConsistent(mesh);
        AssertOutward(mesh, new Vector3(0, 1, 0));
    }

    [Fact]
    public void Chamfer_Width_Is_Uniform_Along_A_Connected_Run()
    {
        // 高さ 4 の箱の上に薄い段（厚み 0.2）を載せた形を三角形で作る代わりに、
        // 箱の縦の辺 1 本だけを面取りし、上端の頂点に短い辺がつながっていても幅が揃うことを見る
        var scene = SceneOf(Primitives.Box(4, 4, 4));
        var mesh = scene.Meshes[0];
        var sel = new Selection();
        sel.SetMode(SelectMode.Vertex);
        sel.Set(Enumerable.Range(0, mesh.Positions.Count)
            .Where(i => MathF.Abs(mesh.Positions[i].X - 2) < 1e-4f && MathF.Abs(mesh.Positions[i].Z + 2) < 1e-4f)
            .Select(i => new ElementRef(0, i)));
        Chamfer.Apply(scene, sel, 1f);
        AssertClosedAndConsistent(mesh);
        // 帯の 4 隅: 上下どちらでも角から 1 m ずつ
        var strip = mesh.Faces.Single(f => { var n = mesh.FaceNormal(f); return n.X > 0.5f && n.Z < -0.5f; });
        foreach (var i in strip.Indices)
        {
            var p = mesh.Positions[i];
            Assert.True(MathF.Abs(p.X - 1) < 1e-4f || MathF.Abs(p.Z + 1) < 1e-4f, $"幅がずれています: {p}");
        }
    }

    [Fact]
    public void Top_Face_Selection_Chamfers_Its_Four_Edges_Only()
    {
        var scene = SceneOf(Primitives.Box(2, 2, 2));
        var mesh = scene.Meshes[0];
        int top = mesh.Faces.FindIndex(f => mesh.FaceNormal(f).Y > 0.9f);
        var sel = new Selection();
        sel.SetMode(SelectMode.Face);
        sel.Set([new ElementRef(0, top)]);
        var msg = Chamfer.Apply(scene, sel, 0.25f);
        Assert.Contains("4 本", msg);
        Assert.Equal(6 + 4, mesh.Faces.Count);
        AssertClosedAndConsistent(mesh);
        AssertOutward(mesh, new Vector3(0, 1, 0));
        // 上面は 0.25 ずつ内側に縮み、側面の上端は 0.25 下がる
        var topFace = mesh.Faces.First(f => mesh.FaceNormal(f).Y > 0.99f);
        Assert.All(topFace.Indices, i => Assert.Equal(0.75f, MathF.Abs(mesh.Positions[i].X), 4));
        Assert.Contains(mesh.Positions, p => MathF.Abs(p.Y - 1.75f) < 1e-4f);
    }

    [Fact]
    public void Single_Edge_From_Vertex_Selection()
    {
        var scene = SceneOf(Primitives.Box(2, 2, 2));
        var mesh = scene.Meshes[0];
        // 手前 (-Z) の上の辺: y=2, z=-1 の 2 点（面ごとに頂点が分かれているので、その位置の頂点を全部選ぶ）
        var sel = new Selection();
        sel.SetMode(SelectMode.Vertex);
        sel.Set(Enumerable.Range(0, mesh.Positions.Count)
            .Where(i => MathF.Abs(mesh.Positions[i].Y - 2) < 1e-4f && MathF.Abs(mesh.Positions[i].Z + 1) < 1e-4f)
            .Select(i => new ElementRef(0, i)));
        var msg = Chamfer.Apply(scene, sel, 0.3f);
        Assert.Contains("1 本", msg);
        Assert.Equal(7, mesh.Faces.Count);
        AssertClosedAndConsistent(mesh);
        AssertOutward(mesh, new Vector3(0, 1, 0));
        // 左右の面は角が切れて 5 角形になる
        Assert.Equal(2, mesh.Faces.Count(f => f.Indices.Length == 5));
    }

    [Fact]
    public void Too_Large_Distance_Is_Clamped_And_Stays_Closed()
    {
        var scene = SceneOf(Primitives.Box(1, 1, 1));
        var msg = Chamfer.Apply(scene, new Selection(), 5f);
        Assert.Contains("小さく", msg);
        AssertClosedAndConsistent(scene.Meshes[0]);
    }

    [Fact]
    public void Short_Edges_Limit_The_Chamfer_Near_Them_And_Stay_Closed()
    {
        // 横長の薄い板（10 × 0.2 × 4）: 上面の四隅はどれも厚みの短い辺につながるので、そこで制限がかかる
        var scene = new XScene();
        var slab = Primitives.Box(10, 0.2f, 4);
        scene.Meshes.Add(slab);
        // 上面の 4 辺だけ面取り（上面を選ぶ）
        int top = slab.Faces.FindIndex(f => slab.FaceNormal(f).Y > 0.9f);
        var sel = new Selection();
        sel.SetMode(SelectMode.Face);
        sel.Set([new ElementRef(0, top)]);
        Chamfer.Apply(scene, sel, 0.5f);
        AssertClosedAndConsistent(scene.Meshes[0]);
        // 上面の四隅は厚み (0.2) の 45% = 0.09 までしか下がれないので、面取りの帯の下端は y = 0.11
        Assert.Contains(scene.Meshes[0].Positions, p => MathF.Abs(p.Y - 0.11f) < 1e-3f);
    }

    [Fact]
    public void Flat_Or_Open_Edges_Are_Not_Chamfered()
    {
        var scene = SceneOf(Primitives.Plane(2, 2, vertical: false));
        var msg = Chamfer.Apply(scene, new Selection(), 0.1f);
        Assert.Contains("ありません", msg);
        Assert.Single(scene.Meshes[0].Faces);
    }

    [Fact]
    public void Cylinder_Side_Seams_Below_Angle_Are_Kept_But_Caps_Are_Chamfered()
    {
        var scene = SceneOf(Primitives.Cylinder(1, 2, 16));
        var msg = Chamfer.Apply(scene, new Selection(), 0.05f);
        // 側面どうし（22.5°）は対象外、上下のふちの 16 + 16 本だけ
        Assert.Contains("32 本", msg);
        AssertClosedAndConsistent(scene.Meshes[0]);
        // 保存して読み直せる
        var again = XFile.LoadText(XTextWriter.Write(scene));
        Assert.Equal(scene.Meshes[0].Faces.Count, again.Scene.Meshes[0].Faces.Count);
    }
}
