using BveXEditor.Core.Editing;
using BveXEditor.Core.Format;
using BveXEditor.Core.Model;

namespace BveXEditor.Core.Tests;

public class BatchConvertTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bvex_batch_" + Guid.NewGuid().ToString("N"));

    public BatchConvertTests() => Directory.CreateDirectory(Path.Combine(_dir, "in", "sub"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string WriteBox(string relative, string? texture = null)
    {
        var scene = new XScene();
        var box = Primitives.Box(2, 2, 2);
        box.Materials[0].Texture = texture;
        scene.Meshes.Add(box);
        var path = Path.Combine(_dir, "in", relative);
        XFile.Save(path, scene);
        return path;
    }

    [Fact]
    public void ScalesAndMirrors_OverwriteKeepsBak()
    {
        var path = WriteBox("a.x");
        var o = new BatchOptions { Scale = 2, MirrorX = true };

        var files = BatchConvert.FindFiles(Path.Combine(_dir, "in"), o);
        Assert.Single(files);
        var result = BatchConvert.ProcessFile(files[0], Path.Combine(_dir, "in"), o);

        Assert.True(result.Ok, result.Message);
        Assert.True(File.Exists(path + ".bak"));
        var mesh = XFile.Load(path).Scene.Meshes[0];
        Assert.Equal(2f, mesh.Positions.Max(p => p.X), 3);
        Assert.Equal(4f, mesh.Positions.Max(p => p.Y), 3);
        MeshAssert.Outward(mesh, new System.Numerics.Vector3(0, 2, 0)); // 反転しても表向きのまま
    }

    [Fact]
    public void OutputFolder_KeepsStructure_AndRebasesTextures()
    {
        WriteBox(Path.Combine("sub", "b.x"), texture: "tex.png");
        File.WriteAllBytes(Path.Combine(_dir, "in", "sub", "tex.png"), [0]);
        var outDir = Path.Combine(_dir, "out");
        var o = new BatchOptions { OutputDirectory = outDir, ReplaceFrom = "tex", ReplaceTo = "tex" };

        var src = BatchConvert.FindFiles(Path.Combine(_dir, "in"), o).Single();
        var result = BatchConvert.ProcessFile(src, Path.Combine(_dir, "in"), o);

        Assert.True(result.Ok, result.Message);
        var outFile = Path.Combine(outDir, "sub", "b.x");
        Assert.True(File.Exists(outFile));
        var tex = XFile.Load(outFile).Scene.Meshes[0].Materials[0].Texture!;
        Assert.Equal(Path.GetFullPath(Path.Combine(_dir, "in", "sub", "tex.png")),
            Path.GetFullPath(Path.Combine(outDir, "sub", tex)), ignoreCase: true);
    }

    [Fact]
    public void ToPmx_WritesPmxNextToSource()
    {
        var path = WriteBox("c.x");
        var o = new BatchOptions { Format = BatchOutputFormat.Pmx };
        var result = BatchConvert.ProcessFile(path, Path.Combine(_dir, "in"), o);
        Assert.True(result.Ok, result.Message);
        Assert.True(File.Exists(Path.ChangeExtension(path, ".pmx")));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void BrokenFile_ReportsFailure()
    {
        var path = Path.Combine(_dir, "in", "broken.x");
        File.WriteAllText(path, "this is not an x file");
        var result = BatchConvert.ProcessFile(path, Path.Combine(_dir, "in"), new BatchOptions());
        Assert.False(result.Ok);
    }
}
