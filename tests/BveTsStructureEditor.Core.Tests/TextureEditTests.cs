using System.Numerics;
using BveTsStructureEditor.Core.Imaging;

namespace BveTsStructureEditor.Core.Tests;

public class TextureEditTests
{
    /// <summary>横方向に赤が、縦方向に緑が増えるグラデーション。</summary>
    private static RgbaImage Gradient(int w, int h)
    {
        var img = new RgbaImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                img.Set(x, y, new Vector4((x + 0.5f) / w, (y + 0.5f) / h, 0.5f, 1));
        return img;
    }

    [Fact]
    public void SquareToQuad_MapsCorners()
    {
        Vector2[] q = [new(10, 20), new(200, 5), new(220, 180), new(0, 150)];
        var m = TextureEdit.SquareToQuad(q);
        Assert.True(Vector2.Distance(TextureEdit.MapSquare(m, 0, 0), q[0]) < 1e-3f);
        Assert.True(Vector2.Distance(TextureEdit.MapSquare(m, 1, 0), q[1]) < 1e-3f);
        Assert.True(Vector2.Distance(TextureEdit.MapSquare(m, 1, 1), q[2]) < 1e-3f);
        Assert.True(Vector2.Distance(TextureEdit.MapSquare(m, 0, 1), q[3]) < 1e-3f);
    }

    [Fact]
    public void Warp_OfRectangle_IsCropAndScale()
    {
        var src = Gradient(100, 100);
        // 左半分・上半分を 50×50 に
        var dst = TextureEdit.Warp(src, [new(0, 0), new(50, 0), new(50, 50), new(0, 50)], 50, 50);
        var c = dst.Get(49, 49);
        Assert.Equal(src.Get(49, 49).X, c.X, 2);
        Assert.Equal(src.Get(49, 49).Y, c.Y, 2);
    }

    [Fact]
    public void Apply_PerspectiveQuad_UsesGivenAspectAndPow2Output()
    {
        var src = Gradient(300, 200);
        var result = TextureEdit.Apply(src, new TextureEditSettings
        {
            Corners = [new(30, 10), new(270, 40), new(260, 190), new(20, 170)],
            Aspect = 2f,
            MaxAutoSize = 512,
        });
        Assert.Equal(2f, result.DisplayAspect, 3);
        Assert.Equal(512, result.Image.Width);
        Assert.Equal(256, result.Image.Height);
        // 左上の画素は元の四角形の左上付近の色
        var tl = result.Image.Get(0, 0);
        Assert.Equal(30f / 300, tl.X, 1);
    }

    [Fact]
    public void Seamless_Horizontal_WrapsContinuously()
    {
        var src = Gradient(100, 10);
        var dst = TextureEdit.Seamless(src, horizontal: true, vertical: false, overlap: 0.2f);
        Assert.Equal(80, dst.Width);
        // 右端から左端へ回り込んだときの差が、隣り合う画素どうしの差と同じくらい小さい
        float wrap = MathF.Abs(dst.Get(dst.Width - 1, 5).X - dst.Get(0, 5).X);
        float step = MathF.Abs(dst.Get(dst.Width - 1, 5).X - dst.Get(dst.Width - 2, 5).X);
        Assert.True(wrap <= step * 3 + 0.02f, $"継ぎ目の差 {wrap}（隣の差 {step}）");
        // 元の画像の端どうしは大きく違う
        Assert.True(MathF.Abs(src.Get(99, 5).X - src.Get(0, 5).X) > 0.9f);
    }

    [Fact]
    public void ColorKey_MakesMatchingPixelsTransparent()
    {
        var img = new RgbaImage(2, 1);
        img.Set(0, 0, new Vector4(0, 0, 1, 1));   // 青（空）
        img.Set(1, 0, new Vector4(0.6f, 0.4f, 0.2f, 1)); // 茶
        var dst = TextureEdit.ColorKey(img, new Vector3(0.05f, 0.05f, 0.95f), 0.15f, 0.05f);
        Assert.Equal(0f, dst.Get(0, 0).W, 2);
        Assert.Equal(1f, dst.Get(1, 0).W, 2);
    }

    [Fact]
    public void Resize_DownscaleAverages_AndIgnoresTransparentColor()
    {
        var img = new RgbaImage(2, 2);
        img.Set(0, 0, new Vector4(1, 0, 0, 1));
        img.Set(1, 0, new Vector4(1, 0, 0, 1));
        img.Set(0, 1, new Vector4(0, 1, 0, 0)); // 透明な緑は色に混ざらない
        img.Set(1, 1, new Vector4(1, 0, 0, 1));
        var dst = TextureEdit.Resize(img, 1, 1);
        var c = dst.Get(0, 0);
        Assert.Equal(1f, c.X, 2);
        Assert.Equal(0f, c.Y, 2);
        Assert.Equal(0.75f, c.W, 2);
    }

    [Fact]
    public void Rotate_And_Adjust()
    {
        var src = Gradient(4, 2);
        var r = TextureEdit.Rotate(src, 1);
        Assert.Equal((2, 4), (r.Width, r.Height));
        // 時計回り: 元の左下 (0,1) が右上ではなく左上 (0,0) に来る
        Assert.Equal(src.Get(0, 1), r.Get(0, 0));

        var bright = TextureEdit.Adjust(src, 0.5f, 0, 0);
        Assert.True(bright.Get(0, 0).X > src.Get(0, 0).X);
        var gray = TextureEdit.Adjust(src, 0, 0, -1);
        var g = gray.Get(3, 1);
        Assert.Equal(g.X, g.Y, 2);
    }

    [Fact]
    public void OutputSize_AutoPicksPowerOfTwo()
    {
        var s = new TextureEditSettings { MaxAutoSize = 1024 };
        Assert.Equal((1024, 512), TextureEdit.OutputSize(2f, s));
        Assert.Equal((256, 1024), TextureEdit.OutputSize(0.3f, s));
        Assert.Equal((512, 512), TextureEdit.OutputSize(1f, s with { OutputWidth = 512 }));
    }
}
