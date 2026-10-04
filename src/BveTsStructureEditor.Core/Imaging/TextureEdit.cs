using System.Numerics;

namespace BveTsStructureEditor.Core.Imaging;

/// <summary>BGRA 8bit・ストレートアルファの画像。</summary>
public sealed class RgbaImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }

    public RgbaImage(int width, int height, byte[]? bgra = null)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("画像の大きさが不正です");
        Width = width;
        Height = height;
        Bgra = bgra ?? new byte[width * height * 4];
        if (Bgra.Length != width * height * 4) throw new ArgumentException("画素の数が大きさと合いません");
    }

    public Vector4 Get(int x, int y)
    {
        int i = (y * Width + x) * 4;
        return new Vector4(Bgra[i + 2], Bgra[i + 1], Bgra[i], Bgra[i + 3]) / 255f; // RGBA 0..1
    }

    public void Set(int x, int y, Vector4 rgba)
    {
        int i = (y * Width + x) * 4;
        var c = Vector4.Clamp(rgba, Vector4.Zero, Vector4.One) * 255f + new Vector4(0.5f);
        Bgra[i] = (byte)c.Z;
        Bgra[i + 1] = (byte)c.Y;
        Bgra[i + 2] = (byte)c.X;
        Bgra[i + 3] = (byte)c.W;
    }

    /// <summary>範囲外は端の画素を使う双一次補間。色はアルファで重みづけして混ぜる（透明部分の色がにじまないように）。</summary>
    public Vector4 Sample(float x, float y)
    {
        x = Math.Clamp(x - 0.5f, 0, Width - 1);
        y = Math.Clamp(y - 0.5f, 0, Height - 1);
        int x0 = (int)x, y0 = (int)y;
        int x1 = Math.Min(x0 + 1, Width - 1), y1 = Math.Min(y0 + 1, Height - 1);
        float fx = x - x0, fy = y - y0;
        return Blend([(Get(x0, y0), (1 - fx) * (1 - fy)), (Get(x1, y0), fx * (1 - fy)), (Get(x0, y1), (1 - fx) * fy), (Get(x1, y1), fx * fy)]);
    }

    internal static Vector4 Blend(ReadOnlySpan<(Vector4 C, float W)> parts)
    {
        var rgb = Vector3.Zero;
        float alpha = 0, weight = 0;
        foreach (var (c, w) in parts)
        {
            rgb += new Vector3(c.X, c.Y, c.Z) * (c.W * w);
            alpha += c.W * w;
            weight += w;
        }
        if (weight <= 0) return Vector4.Zero;
        var color = alpha > 1e-6f ? rgb / alpha : Vector3.Zero;
        return new Vector4(color, alpha / weight);
    }
}

/// <summary>取り込み時の編集の設定。<see cref="TextureEdit.Apply"/> はこの順で処理する: 歪み補正 → 回転・反転 → 透過色 → シームレス化 → 色調整 → 出力サイズ。</summary>
public sealed record TextureEditSettings
{
    /// <summary>元画像の中で使う四角形（左上・右上・右下・左下、画素座標）。null なら画像全体。</summary>
    public Vector2[]? Corners { get; init; }
    /// <summary>補正後の縦横比（幅 / 高さ）。0 以下なら四角形の辺の長さから決める。</summary>
    public float Aspect { get; init; }

    /// <summary>時計回りに 90° × この回数。</summary>
    public int RotateQuarterTurns { get; init; }
    public bool FlipHorizontal { get; init; }
    public bool FlipVertical { get; init; }

    public bool UseColorKey { get; init; }
    public Vector3 KeyColor { get; init; }
    /// <summary>この距離（RGB 0..1 の空間）までは完全に透明。</summary>
    public float KeyTolerance { get; init; } = 0.15f;
    /// <summary>許容差からさらにこの幅で徐々に不透明に戻す。</summary>
    public float KeySoftness { get; init; } = 0.05f;

    public bool SeamlessHorizontal { get; init; }
    public bool SeamlessVertical { get; init; }
    /// <summary>端を重ねてなじませる幅（画像の幅・高さに対する割合、0.02〜0.5）。</summary>
    public float SeamlessOverlap { get; init; } = 0.15f;

    /// <summary>-1..1（0 で変化なし）。</summary>
    public float Brightness { get; init; }
    public float Contrast { get; init; }
    public float Saturation { get; init; }

    /// <summary>出力の幅・高さ（画素）。0 なら縦横比を保って 2 の累乗に近い大きさを自動で決める。</summary>
    public int OutputWidth { get; init; }
    public int OutputHeight { get; init; }
    /// <summary>自動で決めるときの長辺の上限。</summary>
    public int MaxAutoSize { get; init; } = 1024;
}

/// <summary>編集した画像と、板にするときの見た目の縦横比（出力の画素数を 2 の累乗に伸ばしても元の形を保つため）。</summary>
public sealed record TextureEditResult(RgbaImage Image, float DisplayAspect);

public static class TextureEdit
{
    public static TextureEditResult Apply(RgbaImage source, TextureEditSettings s)
    {
        // 1. 歪み補正（四角形を長方形へ）
        var img = source;
        if (s.Corners is { Length: 4 } c && !IsFullImage(c, source))
        {
            float aspect = s.Aspect > 0 ? s.Aspect : EstimateAspect(c);
            // 補正後の画素数は、元の四角形の辺の長さくらいにする（情報を無駄にしない）
            float h = (Vector2.Distance(c[0], c[3]) + Vector2.Distance(c[1], c[2])) / 2;
            float w = (Vector2.Distance(c[0], c[1]) + Vector2.Distance(c[3], c[2])) / 2;
            float area = MathF.Max(w * h, 1);
            int outH = Math.Clamp((int)MathF.Round(MathF.Sqrt(area / aspect)), 1, 8192);
            int outW = Math.Clamp((int)MathF.Round(outH * aspect), 1, 8192);
            img = Warp(source, c, outW, outH);
        }
        // 四角形が全体のままでも、縦横比が指定されていれば見た目の比率として使う
        float displayAspect = s.Aspect > 0 ? s.Aspect : (float)img.Width / img.Height;

        // 2. 回転・反転
        int turns = ((s.RotateQuarterTurns % 4) + 4) % 4;
        if (turns != 0) { img = Rotate(img, turns); if (turns % 2 == 1) displayAspect = 1 / displayAspect; }
        if (s.FlipHorizontal || s.FlipVertical) img = Flip(img, s.FlipHorizontal, s.FlipVertical);

        // 3. 透過色
        if (s.UseColorKey) img = ColorKey(img, s.KeyColor, s.KeyTolerance, s.KeySoftness);

        // 4. シームレス化（端を重ねるので画像は少し小さくなるが、見た目の比率も同じ割合で変わる）
        if (s.SeamlessHorizontal || s.SeamlessVertical)
        {
            int w0 = img.Width, h0 = img.Height;
            img = Seamless(img, s.SeamlessHorizontal, s.SeamlessVertical, s.SeamlessOverlap);
            displayAspect *= ((float)img.Width / w0) / ((float)img.Height / h0);
        }

        // 5. 色調整
        if (s.Brightness != 0 || s.Contrast != 0 || s.Saturation != 0) img = Adjust(img, s.Brightness, s.Contrast, s.Saturation);

        // 6. 出力サイズ
        var (ow, oh) = OutputSize(displayAspect, s);
        if (ow != img.Width || oh != img.Height) img = Resize(img, ow, oh);
        return new TextureEditResult(img, displayAspect);
    }

    private static bool IsFullImage(Vector2[] c, RgbaImage img) =>
        Vector2.Distance(c[0], new(0, 0)) < 0.5f && Vector2.Distance(c[1], new(img.Width, 0)) < 0.5f &&
        Vector2.Distance(c[2], new(img.Width, img.Height)) < 0.5f && Vector2.Distance(c[3], new(0, img.Height)) < 0.5f;

    /// <summary>四角形の向かい合う辺の平均の長さから縦横比を見積もる（斜めから撮った分の縮みは補えないので、実寸がわかれば指定する）。</summary>
    public static float EstimateAspect(Vector2[] c)
    {
        float w = (Vector2.Distance(c[0], c[1]) + Vector2.Distance(c[3], c[2])) / 2;
        float h = (Vector2.Distance(c[0], c[3]) + Vector2.Distance(c[1], c[2])) / 2;
        return h > 1e-3f ? w / h : 1;
    }

    /// <summary>自動なら、長辺が上限を超えない 2 の累乗で、縦横比に近い組み合わせ。</summary>
    public static (int W, int H) OutputSize(float aspect, TextureEditSettings s)
    {
        int max = Math.Clamp(s.MaxAutoSize, 16, 4096);
        if (s.OutputWidth > 0 && s.OutputHeight > 0) return (s.OutputWidth, s.OutputHeight);
        if (s.OutputWidth > 0) return (s.OutputWidth, NearestPow2(s.OutputWidth / aspect, max));
        if (s.OutputHeight > 0) return (NearestPow2(s.OutputHeight * aspect, max), s.OutputHeight);
        return aspect >= 1 ? (max, NearestPow2(max / aspect, max)) : (NearestPow2(max * aspect, max), max);
    }

    private static int NearestPow2(float v, int max)
    {
        int p = 1;
        while (p < max && p * 2 <= v * 1.414f) p *= 2; // √2 倍までは下の累乗に丸める
        return Math.Clamp(p, 1, max);
    }

    // ───────── 射影変換 ─────────

    /// <summary>単位正方形 (0,0)(1,0)(1,1)(0,1) を四角形 <paramref name="q"/> へ写す 3×3 行列（行優先、m[8]=1）。</summary>
    public static double[] SquareToQuad(Vector2[] q)
    {
        double x0 = q[0].X, y0 = q[0].Y, x1 = q[1].X, y1 = q[1].Y, x2 = q[2].X, y2 = q[2].Y, x3 = q[3].X, y3 = q[3].Y;
        double sx = x0 - x1 + x2 - x3, sy = y0 - y1 + y2 - y3;
        double a, b, c = x0, d, e, f = y0, g, h;
        if (Math.Abs(sx) < 1e-9 && Math.Abs(sy) < 1e-9)
        {
            a = x1 - x0; b = x2 - x1; d = y1 - y0; e = y2 - y1; g = 0; h = 0;
        }
        else
        {
            double dx1 = x1 - x2, dx2 = x3 - x2, dy1 = y1 - y2, dy2 = y3 - y2;
            double den = dx1 * dy2 - dx2 * dy1;
            if (Math.Abs(den) < 1e-12) throw new ArgumentException("四角形がつぶれています");
            g = (sx * dy2 - dx2 * sy) / den;
            h = (dx1 * sy - sx * dy1) / den;
            a = x1 - x0 + g * x1; b = x3 - x0 + h * x3; d = y1 - y0 + g * y1; e = y3 - y0 + h * y3;
        }
        return [a, b, c, d, e, f, g, h, 1];
    }

    public static Vector2 MapSquare(double[] m, double u, double v)
    {
        double w = m[6] * u + m[7] * v + m[8];
        return new Vector2((float)((m[0] * u + m[1] * v + m[2]) / w), (float)((m[3] * u + m[4] * v + m[5]) / w));
    }

    /// <summary>元画像の四角形 <paramref name="corners"/>（左上・右上・右下・左下）を、<paramref name="width"/>×<paramref name="height"/> の長方形に引き伸ばす。</summary>
    public static RgbaImage Warp(RgbaImage src, Vector2[] corners, int width, int height)
    {
        var m = SquareToQuad(corners);
        var dst = new RgbaImage(width, height);
        Parallel.For(0, height, y =>
        {
            double v = (y + 0.5) / height;
            for (int x = 0; x < width; x++)
            {
                var p = MapSquare(m, (x + 0.5) / width, v);
                dst.Set(x, y, src.Sample(p.X, p.Y));
            }
        });
        return dst;
    }

    // ───────── 回転・反転 ─────────

    public static RgbaImage Rotate(RgbaImage src, int quarterTurnsClockwise)
    {
        int t = ((quarterTurnsClockwise % 4) + 4) % 4;
        if (t == 0) return src;
        int w = t % 2 == 0 ? src.Width : src.Height;
        int h = t % 2 == 0 ? src.Height : src.Width;
        var dst = new RgbaImage(w, h);
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
            {
                var (nx, ny) = t switch
                {
                    1 => (src.Height - 1 - y, x),
                    2 => (src.Width - 1 - x, src.Height - 1 - y),
                    _ => (y, src.Width - 1 - x),
                };
                Buffer.BlockCopy(src.Bgra, (y * src.Width + x) * 4, dst.Bgra, (ny * w + nx) * 4, 4);
            }
        return dst;
    }

    public static RgbaImage Flip(RgbaImage src, bool horizontal, bool vertical)
    {
        var dst = new RgbaImage(src.Width, src.Height);
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
            {
                int sx = horizontal ? src.Width - 1 - x : x;
                int sy = vertical ? src.Height - 1 - y : y;
                Buffer.BlockCopy(src.Bgra, (sy * src.Width + sx) * 4, dst.Bgra, (y * src.Width + x) * 4, 4);
            }
        return dst;
    }

    // ───────── 透過色 ─────────

    public static RgbaImage ColorKey(RgbaImage src, Vector3 key, float tolerance, float softness)
    {
        var dst = new RgbaImage(src.Width, src.Height, (byte[])src.Bgra.Clone());
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
            {
                var c = src.Get(x, y);
                float d = Vector3.Distance(new Vector3(c.X, c.Y, c.Z), key);
                float keep = softness <= 1e-6f ? (d <= tolerance ? 0 : 1) : Math.Clamp((d - tolerance) / softness, 0, 1);
                if (keep < 1) dst.Set(x, y, c with { W = c.W * keep });
            }
        return dst;
    }

    // ───────── シームレス化 ─────────

    /// <summary>
    /// 端を反対側の端と重ねて混ぜ、並べたときに継ぎ目が出ないようにする。
    /// 横なら幅が重ねた分（overlap × 幅）だけ小さくなる。出力の左端は元画像の「右端から重ね幅ぶん内側」の画素から始まり、
    /// 出力の右端は元画像のその 1 画素手前で終わるので、並べると元画像どおりに続く。
    /// </summary>
    public static RgbaImage Seamless(RgbaImage src, bool horizontal, bool vertical, float overlap)
    {
        overlap = Math.Clamp(overlap, 0.02f, 0.5f);
        var img = src;
        if (horizontal && img.Width >= 4)
        {
            int b = Math.Max(1, (int)(img.Width * overlap));
            int w = img.Width - b;
            var dst = new RgbaImage(w, img.Height);
            for (int y = 0; y < img.Height; y++)
                for (int x = 0; x < w; x++)
                {
                    if (x >= b) { dst.Set(x, y, img.Get(x, y)); continue; }
                    float t = (x + 0.5f) / b; // 0 = 右端側の画素、1 = 元の画素
                    dst.Set(x, y, RgbaImage.Blend([(img.Get(x + w, y), 1 - t), (img.Get(x, y), t)]));
                }
            img = dst;
        }
        if (vertical && img.Height >= 4)
        {
            int b = Math.Max(1, (int)(img.Height * overlap));
            int h = img.Height - b;
            var dst = new RgbaImage(img.Width, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < img.Width; x++)
                {
                    if (y >= b) { dst.Set(x, y, img.Get(x, y)); continue; }
                    float t = (y + 0.5f) / b;
                    dst.Set(x, y, RgbaImage.Blend([(img.Get(x, y + h), 1 - t), (img.Get(x, y), t)]));
                }
            img = dst;
        }
        return img;
    }

    // ───────── 色調整 ─────────

    public static RgbaImage Adjust(RgbaImage src, float brightness, float contrast, float saturation)
    {
        var dst = new RgbaImage(src.Width, src.Height);
        float cf = contrast >= 0 ? 1 + contrast * 2 : 1 + contrast; // -1..1 → 0..3
        float sf = 1 + saturation;
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
            {
                var c = src.Get(x, y);
                var rgb = new Vector3(c.X, c.Y, c.Z);
                rgb += new Vector3(brightness * 0.5f);
                rgb = (rgb - new Vector3(0.5f)) * cf + new Vector3(0.5f);
                float luma = Vector3.Dot(rgb, new Vector3(0.299f, 0.587f, 0.114f));
                rgb = new Vector3(luma) + (rgb - new Vector3(luma)) * sf;
                dst.Set(x, y, new Vector4(rgb, c.W));
            }
        return dst;
    }

    // ───────── 拡大縮小 ─────────

    /// <summary>縮小は元の画素を面積で平均し（ちらつきを防ぐ）、拡大は双一次補間。</summary>
    public static RgbaImage Resize(RgbaImage src, int width, int height)
    {
        var dst = new RgbaImage(width, height);
        float sx = (float)src.Width / width, sy = (float)src.Height / height;
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                if (sx <= 1 && sy <= 1)
                {
                    dst.Set(x, y, src.Sample((x + 0.5f) * sx, (y + 0.5f) * sy));
                    continue;
                }
                int x0 = (int)(x * sx), x1 = Math.Max(x0 + 1, Math.Min(src.Width, (int)MathF.Ceiling((x + 1) * sx)));
                int y0 = (int)(y * sy), y1 = Math.Max(y0 + 1, Math.Min(src.Height, (int)MathF.Ceiling((y + 1) * sy)));
                var rgb = Vector3.Zero;
                float alpha = 0;
                int count = 0;
                for (int yy = y0; yy < y1; yy++)
                    for (int xx = x0; xx < x1; xx++)
                    {
                        var c = src.Get(xx, yy);
                        rgb += new Vector3(c.X, c.Y, c.Z) * c.W;
                        alpha += c.W;
                        count++;
                    }
                dst.Set(x, y, new Vector4(alpha > 1e-6f ? rgb / alpha : Vector3.Zero, alpha / count));
            }
        });
        return dst;
    }
}
