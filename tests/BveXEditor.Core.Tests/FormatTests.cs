using System.Numerics;
using System.Text;
using BveXEditor.Core.Editing;
using BveXEditor.Core.Format;
using BveXEditor.Core.Model;

namespace BveXEditor.Core.Tests;

public class FormatTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    internal static void AssertSameScene(XScene expected, XScene actual, float eps = 1e-4f)
    {
        Assert.Equal(expected.Meshes.Count, actual.Meshes.Count);
        for (int m = 0; m < expected.Meshes.Count; m++)
        {
            var e = expected.Meshes[m];
            var a = actual.Meshes[m];
            Assert.Equal(e.Positions.Count, a.Positions.Count);
            for (int i = 0; i < e.Positions.Count; i++)
                Assert.True(Vector3.Distance(e.Positions[i], a.Positions[i]) < eps, $"mesh {m} vertex {i}: {e.Positions[i]} != {a.Positions[i]}");
            Assert.Equal(e.Faces.Count, a.Faces.Count);
            for (int i = 0; i < e.Faces.Count; i++)
            {
                Assert.Equal(e.Faces[i].Indices, a.Faces[i].Indices);
                Assert.Equal(e.Faces[i].Material, a.Faces[i].Material);
            }
            Assert.Equal(e.HasUV, a.HasUV);
            for (int i = 0; i < e.TexCoords.Count; i++)
                Assert.True(Vector2.Distance(e.TexCoords[i], a.TexCoords[i]) < eps);
            Assert.Equal(e.Materials.Count, a.Materials.Count);
            for (int i = 0; i < e.Materials.Count; i++)
            {
                Assert.Equal(e.Materials[i].Texture, a.Materials[i].Texture);
                Assert.True(Vector4.Distance(e.Materials[i].FaceColor, a.Materials[i].FaceColor) < eps);
            }
        }
    }

    [Fact]
    public void Box_RoundTrips_ThroughText()
    {
        var scene = new XScene();
        var box = Primitives.Box(2, 3, 4);
        box.Materials[0].Texture = "壁.png";
        box.Materials[0].FaceColor = new Vector4(0.5f, 0.25f, 1, 0.75f);
        scene.Meshes.Add(box);

        var text = XTextWriter.Write(scene);
        var loaded = XFile.LoadText(text);
        Assert.Empty(loaded.Warnings);
        AssertSameScene(scene, loaded.Scene);
    }

    [Fact]
    public void Saved_ShiftJis_File_Reads_Japanese_Texture_Name()
    {
        var scene = new XScene();
        var plane = Primitives.Plane(1, 1, vertical: true);
        plane.Materials[0].Texture = "テクスチャ\\看板.png";
        scene.Meshes.Add(plane);
        var path = Path.GetTempFileName();
        try
        {
            XFile.Save(path, scene);
            var r = XFile.Load(path);
            Assert.Equal("テクスチャ\\看板.png", r.Scene.Meshes[0].Materials[0].Texture);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("real_toyonokuni.x")]
    [InlineData("real_sample_building.x")]
    public void Real_Files_Load_And_RoundTrip(string name)
    {
        if (Fixtures.Missing(name)) return; // 配布物のテストデータはリポジトリに入れていない
        var r = XFile.Load(Fixture(name));
        Assert.NotEmpty(r.Scene.Meshes);
        Assert.True(r.Scene.TotalFaces > 0);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("不正") || w.Contains("足りません"));

        var again = XFile.LoadText(XTextWriter.Write(r.Scene));
        AssertSameScene(r.Scene, again.Scene);
    }

    [Theory]
    [InlineData("grid_tzip.x")]
    [InlineData("grid_tzip_nock.x")]
    public void Tzip_With_CrossChunk_References_Matches_Text(string name)
    {
        var text = XFile.Load(Fixture("grid_txt.x"));
        var zip = XFile.Load(Fixture(name));
        Assert.True(zip.Header.Compressed);
        Assert.Equal(XEncodingKind.Text, zip.Header.Kind);
        Assert.Equal(3721, zip.Scene.Meshes[0].Positions.Count);
        Assert.Equal("テクスチャ.png", zip.Scene.Meshes[0].Materials[0].Texture);
        AssertSameScene(text.Scene, zip.Scene);
    }

    [Theory]
    [InlineData("real_toyonokuni.x", false)]
    [InlineData("real_sample_building.x", false)]
    [InlineData("grid_txt.x", true)]
    [InlineData("real_toyonokuni.x", true)]
    public void Binary_And_Bzip_Match_Text(string name, bool compressed)
    {
        if (Fixtures.Missing(name)) return;
        var bytes = File.ReadAllBytes(Fixture(name));
        var textScene = XFile.Load(bytes).Scene;
        var text = XFile.DetectEncoding(bytes).GetString(bytes);
        var bin = XFile.Load(BinaryEncoder.FromText(text, compressed));
        Assert.Equal(XEncodingKind.Binary, bin.Header.Kind);
        Assert.Equal(compressed, bin.Header.Compressed);
        AssertSameScene(textScene, bin.Scene);
    }

    [Fact]
    public void Frames_Are_Baked_And_Mirror_Keeps_Front_Side()
    {
        const string src = """
            xof 0303txt 0032
            Material Red { 1.0;0.0;0.0;1.0;; 5.0; 0.0;0.0;0.0;; 0.0;0.0;0.0;; }
            Mesh Quad {
             4; 0;0;0;, 0;1;0;, 1;1;0;, 1;0;0;;
             1; 4;0,1,2,3;;
             MeshMaterialList { 1; 1; 0;; { Red } }
            }
            Frame Root {
             FrameTransformMatrix { 1,0,0,0, 0,1,0,0, 0,0,1,0, 10,0,0,1;; }
             Frame Mirror {
              FrameTransformMatrix { -1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1;; }
              { Quad }
             }
            }
            """;
        var r = XFile.LoadText(src);
        var mesh = Assert.Single(r.Scene.Meshes);
        Assert.Equal(new Vector3(1, 0, 0), mesh.Materials[0].FaceColor.AsVector3());
        // 鏡像 (x→-x) → 平行移動 (+10)
        Assert.Contains(new Vector3(9, 1, 0), mesh.Positions);
        Assert.Contains(new Vector3(10, 0, 0), mesh.Positions);
        // 元の四角形は -Z 向き。鏡像後も -Z 向きのままであること
        Assert.True(mesh.FaceNormal(mesh.Faces[0]).Z < -0.9f);
    }

    [Fact]
    public void Broken_Faces_Are_Dropped_With_Warning()
    {
        const string src = """
            xof 0303txt 0032
            Mesh {
             3; 0;0;0;, 0;1;0;, 1;1;0;;
             3; 3;0,1,2;, 3;0,1,9;, 2;0,1;;
            }
            """;
        var r = XFile.LoadText(src);
        Assert.Single(r.Scene.Meshes[0].Faces);
        Assert.Contains(r.Warnings, w => w.Contains("不正な面を 2"));
    }

    [Fact]
    public void Compact_Separators_And_Comments_Are_Tolerated()
    {
        const string src = "xof 0303txt 0032\n# comment\nMesh m{3;0;0;0;,0;1;0;,1;1;0;;1;3;0,1,2;;// c\nMeshTextureCoords{3;0;0;,0;1;,1;1;;}}";
        var r = XFile.LoadText(src);
        Assert.Equal(3, r.Scene.Meshes[0].TexCoords.Count);
        Assert.Equal("m", r.Scene.Meshes[0].Name);
    }

    [Fact]
    public void Header_Rejects_Non_X()
    {
        Assert.Throws<XFormatException>(() => XFile.Load(Encoding.ASCII.GetBytes("hello world, not x file")));
    }
}

internal static class VecExt
{
    public static Vector3 AsVector3(this Vector4 v) => new(v.X, v.Y, v.Z);
}
