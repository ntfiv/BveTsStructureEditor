using System.Numerics;
using BveXEditor.Core.Editing;
using BveXEditor.Core.Format;
using BveXEditor.Core.Model;

namespace BveXEditor.Core.Tests;

public class EditingTests
{
    private static XScene SceneOf(params XMesh[] meshes)
    {
        var s = new XScene();
        s.Meshes.AddRange(meshes);
        return s;
    }

    private static void AssertOutward(XMesh mesh)
    {
        var center = mesh.Positions.Aggregate(Vector3.Zero, (a, b) => a + b) / mesh.Positions.Count;
        foreach (var f in mesh.Faces)
            Assert.True(Vector3.Dot(mesh.FaceNormal(f), mesh.FaceCenter(f) - center) > 0, "内向きの面があります");
    }

    [Fact]
    public void Primitives_Face_Outward()
    {
        AssertOutward(Primitives.Box(1, 2, 3));
        AssertOutward(Primitives.Cylinder(1, 2, 12));
        var plane = Primitives.Plane(1, 1, vertical: true);
        Assert.True(plane.FaceNormal(plane.Faces[0]).Z < -0.99f); // 手前 (-Z) 向き
    }

    [Theory]
    [InlineData(Primitives.ImageSizeMode.Height, 3f, 6f, 3f)]
    [InlineData(Primitives.ImageSizeMode.Width, 4f, 4f, 2f)]
    [InlineData(Primitives.ImageSizeMode.PerPixel, 0.01f, 8f, 4f)]
    public void ImagePlane_Keeps_Image_Aspect(Primitives.ImageSizeMode mode, float size, float expectW, float expectH)
    {
        var m = Primitives.ImagePlane("sign", 800, 400, mode, size, vertical: true, "tex/sign.png");
        var min = m.Positions.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
        var max = m.Positions.Aggregate(new Vector3(float.MinValue), Vector3.Max);
        Assert.Equal(expectW, max.X - min.X, 4);
        Assert.Equal(expectH, max.Y - min.Y, 4);
        Assert.Equal(0f, min.Y, 4);
        Assert.Equal("tex/sign.png", m.Materials[0].Texture);
        Assert.Equal("sign", m.Name);
        Assert.True(m.FaceNormal(m.Faces[0]).Z < -0.99f); // 手前向き
        // 画像の左上 (UV 0,0) が板の左上に来る
        var topLeft = Enumerable.Range(0, 4).OrderBy(i => m.Positions[i].X).ThenByDescending(i => m.Positions[i].Y).First();
        Assert.Equal(Vector2.Zero, m.TexCoords[topLeft]);
    }

    [Fact]
    public void Absolute_Textures_Under_Save_Folder_Become_Relative()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bvex_rel");
        var inside = Primitives.ImagePlane("a", 10, 10, Primitives.ImageSizeMode.Height, 1, true, Path.Combine(dir, "tex", "a.png"));
        var outside = Primitives.ImagePlane("b", 10, 10, Primitives.ImageSizeMode.Height, 1, true, @"Z:\other\b.png");
        var already = Primitives.ImagePlane("c", 10, 10, Primitives.ImageSizeMode.Height, 1, true, "c.png");
        var scene = SceneOf(inside, outside, already);

        Assert.Equal(1, TexturePaths.MakeRelative(scene, dir));
        Assert.Equal(Path.Combine("tex", "a.png"), inside.Materials[0].Texture);
        Assert.Equal(@"Z:\other\b.png", outside.Materials[0].Texture);
        Assert.Equal("c.png", already.Materials[0].Texture);
    }

    [Fact]
    public void Imported_Textures_Are_Rebased_To_Target_File()
    {
        var root = Path.Combine(Path.GetTempPath(), "bvex_rebase");
        var m = Primitives.Plane(1, 1, true);
        m.Materials[0].Texture = "tex/a.png";
        var fromDir = Path.Combine(root, "models", "station");
        var toDir = Path.Combine(root, "route");

        TexturePaths.Rebase([m], fromDir, toDir);
        Assert.Equal(Path.Combine("..", "models", "station", "tex", "a.png"), m.Materials[0].Texture);

        var n = Primitives.Plane(1, 1, true);
        n.Materials[0].Texture = "b.png";
        TexturePaths.Rebase([n], fromDir, null); // 置き先が未保存なら絶対パス
        Assert.Equal(Path.Combine(fromDir, "b.png"), n.Materials[0].Texture);
    }

    [Fact]
    public void Extrude_Makes_Closed_Outward_Prism()
    {
        var scene = SceneOf(Primitives.Plane(2, 2, vertical: false));
        var sel = new Selection();
        sel.SetMode(SelectMode.Face);
        sel.Set([new ElementRef(0, 0)]);
        MeshOps.Extrude(scene, sel, 1f);
        var mesh = scene.Meshes[0];
        Assert.Equal(5, mesh.Faces.Count);
        Assert.Equal(1f, mesh.Positions.Max(p => p.Y), 4);
        // 底面が無いので中心は上寄りだが、側面と上面は外向き
        var center = new Vector3(0, 0.5f, 0);
        foreach (var f in mesh.Faces)
            Assert.True(Vector3.Dot(mesh.FaceNormal(f), mesh.FaceCenter(f) - center) > 0);
    }

    [Fact]
    public void Mirror_Transform_Keeps_Faces_Outward()
    {
        var scene = SceneOf(Primitives.Box(1, 1, 1));
        MeshOps.Transform(scene, new Selection(), Matrix4x4.CreateScale(-1, 1, 1));
        AssertOutward(scene.Meshes[0]);
    }

    [Fact]
    public void Flip_Then_Save_Writes_Reversed_Normals()
    {
        var scene = SceneOf(Primitives.Plane(1, 1, vertical: true));
        MeshOps.RecomputeNormals(scene, new Selection(), 30);
        MeshOps.FlipFaces(scene, new Selection());
        var r = XFile.LoadText(XTextWriter.Write(scene));
        var mesh = r.Scene.Meshes[0];
        Assert.True(mesh.FaceNormal(mesh.Faces[0]).Z > 0.99f);
        Assert.True(mesh.Normals[mesh.Faces[0].NormalIndices![0]].Z > 0.99f);
    }

    [Fact]
    public void Weld_Merges_Box_Corners_When_Ignoring_UV()
    {
        var scene = SceneOf(Primitives.Box(1, 1, 1));
        Assert.Equal(24, scene.Meshes[0].Positions.Count);
        MeshOps.Weld(scene, new Selection(), 0.001f, respectUV: false);
        Assert.Equal(8, scene.Meshes[0].Positions.Count);
        Assert.Equal(6, scene.Meshes[0].Faces.Count);
    }

    [Fact]
    public void Detach_Duplicates_Shared_Vertices()
    {
        var scene = SceneOf(Primitives.Box(1, 1, 1));
        MeshOps.Weld(scene, new Selection(), 0.001f, respectUV: false);
        var sel = new Selection();
        sel.SetMode(SelectMode.Face);
        sel.Set([new ElementRef(0, 0)]);
        MeshOps.Detach(scene, sel);
        Assert.Equal(12, scene.Meshes[0].Positions.Count);
        Assert.DoesNotContain(scene.Meshes[0].Faces.Skip(1).SelectMany(f => f.Indices), i => scene.Meshes[0].Faces[0].Indices.Contains(i));
    }

    [Fact]
    public void Auto_UV_Projection_Fills_0_1_Per_Side()
    {
        var scene = SceneOf(Primitives.Box(2, 1, 4));
        MeshOps.Weld(scene, new Selection(), 0.001f, respectUV: false);
        UvOps.Project(scene, new Selection(), UvProjectAxis.Auto, UvFit.Stretch);
        var mesh = scene.Meshes[0];
        Assert.Equal(24, mesh.Positions.Count); // 6 方向に切り離される
        foreach (var f in mesh.Faces)
        {
            var uvs = f.Indices.Select(i => mesh.TexCoords[i]).ToList();
            Assert.Equal(0f, uvs.Min(u => u.X), 4);
            Assert.Equal(1f, uvs.Max(u => u.X), 4);
            Assert.Equal(0f, uvs.Min(u => u.Y), 4);
            Assert.Equal(1f, uvs.Max(u => u.Y), 4);
        }
        // 手前の面: 左上の頂点 (x 最小, y 最大) が UV (0,0)
        var front = mesh.Faces.First(f => mesh.FaceNormal(f).Z < -0.9f);
        var topLeft = front.Indices.OrderBy(i => mesh.Positions[i].X).ThenByDescending(i => mesh.Positions[i].Y).First();
        Assert.Equal(Vector2.Zero, mesh.TexCoords[topLeft]);
    }

    [Fact]
    public void Merge_And_Split_By_Material()
    {
        var a = Primitives.Box(1, 1, 1);
        var b = Primitives.Plane(1, 1, true);
        b.Materials[0].Texture = "b.png";
        var scene = SceneOf(a, b);
        MeshOps.MergeMeshes(scene, [0, 1]);
        Assert.Single(scene.Meshes);
        Assert.Equal(2, scene.Meshes[0].Materials.Count);
        Assert.Equal(7, scene.Meshes[0].Faces.Count);

        MeshOps.SplitByMaterial(scene, 0);
        Assert.Equal(2, scene.Meshes.Count);
        Assert.Equal(6, scene.Meshes[0].Faces.Count);
        Assert.Equal("b.png", Assert.Single(scene.Meshes[1].Materials).Texture);
        Assert.Equal(4, scene.Meshes[1].Positions.Count);
    }

    [Fact]
    public void Separate_Faces_Moves_Them_To_New_Mesh()
    {
        var scene = SceneOf(Primitives.Box(1, 1, 1));
        var sel = new Selection();
        sel.SetMode(SelectMode.Face);
        sel.Set([new ElementRef(0, 1), new ElementRef(0, 3)]);
        MeshOps.SeparateFaces(scene, sel);
        Assert.Equal(2, scene.Meshes.Count);
        Assert.Equal(4, scene.Meshes[0].Faces.Count);
        Assert.Equal(2, scene.Meshes[1].Faces.Count);
        Assert.Equal(8, scene.Meshes[1].Positions.Count);
    }

    [Fact]
    public void Create_Face_From_Vertex_Order()
    {
        var scene = SceneOf(Primitives.Plane(1, 1, true));
        scene.Meshes[0].Faces.Clear();
        var sel = new Selection();
        sel.SetMode(SelectMode.Vertex);
        sel.Set([new ElementRef(0, 0), new ElementRef(0, 1), new ElementRef(0, 2)]);
        MeshOps.CreateFace(scene, sel);
        Assert.Equal([0, 1, 2], scene.Meshes[0].Faces[0].Indices);
    }

    [Fact]
    public void Align_Bottom_Center()
    {
        var scene = SceneOf(Primitives.Box(2, 2, 2));
        MeshOps.Transform(scene, new Selection(), Matrix4x4.CreateTranslation(5, 3, -7));
        MeshOps.AlignToOrigin(scene, new Selection(), MeshOps.OriginAlign.BottomCenter);
        var b = scene.Bounds()!.Value;
        Assert.Equal(0f, b.Min.Y, 4);
        Assert.Equal(0f, (b.Min.X + b.Max.X) / 2, 4);
        Assert.Equal(0f, (b.Min.Z + b.Max.Z) / 2, 4);
    }
}
