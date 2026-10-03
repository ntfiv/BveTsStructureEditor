using System.Numerics;
using System.Text.Json.Nodes;
using BveXEditor.Core.Editing;
using BveXEditor.Core.Format;
using BveXEditor.Core.Model;

namespace BveXEditor.Core.Tests;

public class BvexFileTests
{
    private static XScene Sample()
    {
        var scene = new XScene();
        var box = Primitives.Box(1.1f, 2.3f, 0.7f);
        box.Name = "駅舎";
        box.Materials[0].FaceColor = new Vector4(0.25f, 0.5f, 0.125f, 0.75f);
        box.Materials[0].Power = 12.5f;
        box.Materials[0].Specular = new Vector3(0.1f, 0.2f, 0.3f);
        box.Materials[0].Emissive = new Vector3(0.01f, 0.02f, 0.03f);
        box.Materials[0].Texture = @"tex\壁.png";
        box.Materials.Add(new XMaterial());
        box.Faces[2].Material = 1;
        // 頂点カラーと、読み込んだ法線（面ごとの法線番号付き）
        foreach (var _ in box.Positions) box.VertexColors.Add(new Vector4(1, 0.5f, 0.25f, 1));
        box.Normals.Add(Vector3.UnitY);
        box.Normals.Add(-Vector3.UnitZ);
        box.Faces[0].NormalIndices = [1, 1, 1, 1];
        // .x の 6 桁では丸められてしまう値
        box.Positions[0] = new Vector3(0.123456789f, -1e-7f, 12345.678f);
        scene.Meshes.Add(box);
        scene.Meshes.Add(Primitives.Cylinder(0.3f, 5, 8));
        return scene;
    }

    [Fact]
    public void RoundTrip_KeepsModelExactly_AndEditorState()
    {
        var scene = Sample();
        var editor = new JsonObject { ["view"] = new JsonObject { ["ortho"] = true }, ["note"] = "テスト" };
        using var ms = new MemoryStream();
        BvexFile.Write(ms, scene, editor);
        ms.Position = 0;

        var loaded = BvexFile.Read(ms);

        Assert.Empty(loaded.Warnings);
        Assert.Equal(BvexFile.CurrentVersion, loaded.Version);
        Assert.Equal(scene.Meshes.Count, loaded.Scene.Meshes.Count);
        for (int m = 0; m < scene.Meshes.Count; m++)
        {
            var a = scene.Meshes[m];
            var b = loaded.Scene.Meshes[m];
            Assert.Equal(a.Name, b.Name);
            Assert.Equal(a.Positions, b.Positions);
            Assert.Equal(a.TexCoords, b.TexCoords);
            Assert.Equal(a.VertexColors, b.VertexColors);
            Assert.Equal(a.Normals, b.Normals);
            Assert.Equal(a.Faces.Count, b.Faces.Count);
            for (int f = 0; f < a.Faces.Count; f++)
            {
                Assert.Equal(a.Faces[f].Indices, b.Faces[f].Indices);
                Assert.Equal(a.Faces[f].Material, b.Faces[f].Material);
                Assert.Equal(a.Faces[f].NormalIndices, b.Faces[f].NormalIndices);
            }
            Assert.Equal(a.Materials.Count, b.Materials.Count);
            for (int i = 0; i < a.Materials.Count; i++) Assert.True(a.Materials[i].SameAs(b.Materials[i]));
        }
        Assert.Equal(new Vector3(0.123456789f, -1e-7f, 12345.678f), loaded.Scene.Meshes[0].Positions[0]);
        Assert.True(loaded.Editor!["view"]!["ortho"]!.GetValue<bool>());
        Assert.Equal("テスト", loaded.Editor["note"]!.GetValue<string>());
    }

    [Fact]
    public void Save_WritesAtomically_AndLoads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bvex_{Guid.NewGuid():N}.bvex");
        try
        {
            BvexFile.Save(path, Sample(), null);
            Assert.False(File.Exists(path + ".tmp"));
            var loaded = BvexFile.Load(path);
            Assert.Equal(2, loaded.Scene.Meshes.Count);
            Assert.Null(loaded.Editor);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NotAProject_IsRejected()
    {
        using var text = new MemoryStream("xof 0302txt 0064"u8.ToArray());
        Assert.Throws<XFormatException>(() => BvexFile.Read(text));

        using var zip = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            archive.CreateEntry("other.txt");
        zip.Position = 0;
        Assert.Throws<XFormatException>(() => BvexFile.Read(zip));
    }
}
