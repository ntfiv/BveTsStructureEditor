using System.Numerics;
using BveXEditor.Core.Editing;
using BveXEditor.Core.Model;

namespace BveXEditor.Core.Tests;

public class EdgeDimensionsTests
{
    private static XScene BoxScene(float w = 2, float h = 3, float d = 4)
    {
        var scene = new XScene();
        scene.Meshes.Add(Primitives.Box(w, h, d));
        return scene;
    }

    private static Selection Select(SelectMode mode, params ElementRef[] items)
    {
        var sel = new Selection();
        sel.SetMode(mode);
        sel.Set(items);
        return sel;
    }

    [Fact]
    public void EmptySelection_GivesOverallBox()
    {
        var dims = EdgeDimensions.Collect(BoxScene(), new Selection(), null, out bool tooMany);
        Assert.False(tooMany);
        Assert.Equal(3, dims.Count);
        Assert.Equal(2, dims.Single(d => d.Kind == DimensionKind.BoxX).Length, 4);
        Assert.Equal(3, dims.Single(d => d.Kind == DimensionKind.BoxY).Length, 4);
        Assert.Equal(4, dims.Single(d => d.Kind == DimensionKind.BoxZ).Length, 4);
    }

    [Fact]
    public void EmptySelection_SkipsHiddenMeshes()
    {
        var scene = BoxScene();
        var far = Primitives.Box(1, 1, 1);
        for (int i = 0; i < far.Positions.Count; i++) far.Positions[i] += new Vector3(100, 0, 0);
        scene.Meshes.Add(far);
        var dims = EdgeDimensions.Collect(scene, new Selection(), new HashSet<int> { 1 }, out _);
        Assert.Equal(2, dims.Single(d => d.Kind == DimensionKind.BoxX).Length, 4);
    }

    [Fact]
    public void OneFace_GivesItsFourEdges()
    {
        var scene = BoxScene();
        var dims = EdgeDimensions.Collect(scene, Select(SelectMode.Face, new ElementRef(0, 0)), null, out _);
        Assert.Equal(4, dims.Count);
        Assert.All(dims, d => Assert.Equal(DimensionKind.Edge, d.Kind));
        // 箱の面は幅・高さ・奥行のどれか 2 つの長さの長方形
        var lengths = dims.Select(d => MathF.Round(d.Length, 3)).OrderBy(x => x).ToList();
        Assert.Equal(lengths[0], lengths[1]);
        Assert.Equal(lengths[2], lengths[3]);
    }

    [Fact]
    public void WholeBoxFaces_CountSharedEdgesOnce()
    {
        var scene = BoxScene();
        var faces = Enumerable.Range(0, scene.Meshes[0].Faces.Count).Select(f => new ElementRef(0, f)).ToArray();
        var dims = EdgeDimensions.Collect(scene, Select(SelectMode.Face, faces), null, out _);
        // 箱の辺は 12 本（面ごとに頂点が分かれていても位置が同じなら 1 本）
        Assert.Equal(12, dims.Count);
    }

    [Fact]
    public void TwoVerticesOnAnEdge_GiveThatEdge_AndOtherwiseTheirDistance()
    {
        var scene = BoxScene();
        var mesh = scene.Meshes[0];
        var face = mesh.Faces[0];
        int a = face.Indices[0], b = face.Indices[1], c = face.Indices[2];

        var edge = EdgeDimensions.Collect(scene, Select(SelectMode.Vertex, new(0, a), new(0, b)), null, out _);
        Assert.Equal(DimensionKind.Edge, Assert.Single(edge).Kind);
        Assert.Equal(Vector3.Distance(mesh.Positions[a], mesh.Positions[b]), edge[0].Length, 4);

        var diagonal = EdgeDimensions.Collect(scene, Select(SelectMode.Vertex, new(0, a), new(0, c)), null, out _);
        Assert.Equal(DimensionKind.Distance, Assert.Single(diagonal).Kind);
        Assert.Equal(Vector3.Distance(mesh.Positions[a], mesh.Positions[c]), diagonal[0].Length, 4);
    }

    [Fact]
    public void ObjectSelection_GivesSelectedMeshBox()
    {
        var scene = BoxScene(2, 3, 4);
        scene.Meshes.Add(Primitives.Box(10, 10, 10));
        var dims = EdgeDimensions.Collect(scene, Select(SelectMode.Object, new ElementRef(0, -1)), null, out _);
        Assert.Equal(2, dims.Single(d => d.Kind == DimensionKind.BoxX).Length, 4);
    }

    [Fact]
    public void TooManyEdges_FallsBackToBox()
    {
        // 離れた四角形を 150 枚（辺 600 本）
        var mesh = new XMesh();
        for (int i = 0; i < 150; i++)
        {
            int s = mesh.Positions.Count;
            mesh.Positions.AddRange([new(i * 2, 0, 0), new(i * 2 + 1, 0, 0), new(i * 2 + 1, 1, 0), new(i * 2, 1, 0)]);
            mesh.Faces.Add(new XFace([s, s + 1, s + 2, s + 3], 0));
        }
        var scene = new XScene();
        scene.Meshes.Add(mesh);
        var faces = Enumerable.Range(0, mesh.Faces.Count).Select(f => new ElementRef(0, f)).ToArray();
        var dims = EdgeDimensions.Collect(scene, Select(SelectMode.Face, faces), null, out bool tooMany);
        Assert.True(tooMany);
        Assert.All(dims, d => Assert.NotEqual(DimensionKind.Edge, d.Kind));
    }
}
