using System.Numerics;
using BveXEditor.Core.Editing;
using BveXEditor.Core.Imaging;
using BveXEditor.Core.Model;

namespace BveXEditor.Core.Tests;

public class BveCheckTests
{
    private static XScene SceneOf(XMesh m)
    {
        var s = new XScene();
        s.Meshes.Add(m);
        return s;
    }

    [Fact]
    public void CleanBox_HasNoWarnings()
    {
        var issues = BveCheck.Run(SceneOf(Primitives.Box(1, 1, 1)), Path.GetTempPath());
        Assert.DoesNotContain(issues, i => i.Severity != CheckSeverity.Info);
    }

    [Fact]
    public void FindsMissingAbsoluteAndNonAsciiTextures()
    {
        var mesh = Primitives.Plane(1, 1, vertical: true);
        mesh.Materials[0].Texture = "ない画像.png";
        mesh.Materials.Add(new XMaterial { Texture = @"C:\abs\tex.png" });
        mesh.Faces.Add(new XFace([0, 1, 2], 1));
        var issues = BveCheck.Run(SceneOf(mesh), Path.GetTempPath());

        Assert.Contains(issues, i => i.Severity == CheckSeverity.Error && i.Message.Contains("ない画像.png"));
        Assert.Contains(issues, i => i.Message.Contains("絶対パス"));
        Assert.Contains(issues, i => i.Message.Contains("日本語"));
    }

    [Fact]
    public void FindsDegenerateDuplicateConcaveAndUnused_AndFixes()
    {
        var mesh = new XMesh();
        mesh.Materials.Add(new XMaterial());
        mesh.Materials.Add(new XMaterial());
        int a = mesh.AddVertex(new(0, 0, 0)), b = mesh.AddVertex(new(0, 1, 0)), c = mesh.AddVertex(new(1, 1, 0)), d = mesh.AddVertex(new(1, 0, 0));
        mesh.Faces.Add(new XFace([a, b, c, d]));
        mesh.Faces.Add(new XFace([a, b, c, d])); // 重なり
        int e = mesh.AddVertex(new(2, 0, 0)), f = mesh.AddVertex(new(3, 0, 0));
        mesh.Faces.Add(new XFace([a, e, f])); // 一直線
        // L 字（へこみ）
        Vector3[] lShape = [new(0, 0, 5), new(0, 2, 5), new(1, 2, 5), new(1, 1, 5), new(2, 1, 5), new(2, 0, 5)];
        int[] l = lShape.Select(p => mesh.AddVertex(p)).ToArray();
        mesh.Faces.Add(new XFace(l));
        mesh.AddVertex(new(9, 9, 9)); // 未使用
        var scene = SceneOf(mesh);

        var issues = BveCheck.Run(scene, null);
        var dup = Assert.Single(issues, i => i.Message.Contains("重なった"));
        Assert.Equal([1], dup.Faces!);
        Assert.Contains(issues, i => i.Message.Contains("面積が 0") && i.Faces!.Contains(2));
        Assert.Contains(issues, i => i.Message.Contains("へこんだ") && i.Faces!.Contains(3));
        var unusedV = Assert.Single(issues, i => i.Fix == CheckFix.RemoveUnusedVertices);
        Assert.Single(issues, i => i.Fix == CheckFix.RemoveUnusedMaterials);

        BveCheck.Fix(scene, unusedV);
        Assert.DoesNotContain(BveCheck.Run(scene, null), i => i.Fix == CheckFix.RemoveUnusedVertices);
        BveCheck.Fix(scene, dup);
        Assert.DoesNotContain(BveCheck.Run(scene, null), i => i.Message.Contains("重なった"));
    }

    [Fact]
    public void ImageHeader_ReadsPngAndBmp()
    {
        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 1, 0, 0, 0, 0, 200];
        Assert.Equal((256, 200), ImageHeader.ReadSize(png));
        var bmp = new byte[26];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BitConverter.GetBytes(512).CopyTo(bmp, 18);
        BitConverter.GetBytes(-128).CopyTo(bmp, 22);
        Assert.Equal((512, 128), ImageHeader.ReadSize(bmp));
        Assert.True(ImageHeader.IsPowerOfTwo(1024));
        Assert.False(ImageHeader.IsPowerOfTwo(1000));
    }

    [Fact]
    public void TrackGuides_RailsAtGauge_OutlinesSymmetric()
    {
        var lines = TrackGuides.Build(GuideGauge.Narrow1067, platform: true);
        var rails = lines.Where(l => l.Kind == GuideKind.Rail).ToList();
        Assert.Equal(2, rails.Count);
        Assert.Equal(1.067f, MathF.Abs(rails[0].Points[0].X - rails[1].Points[0].X), 4);
        foreach (var l in lines.Where(l => l.Closed))
            Assert.Equal(0f, l.Points.Sum(p => p.X), 3);
        Assert.Equal(2, lines.Count(l => l.Kind == GuideKind.Platform));
        Assert.Empty(TrackGuides.Build(GuideGauge.None, true));
    }
}
