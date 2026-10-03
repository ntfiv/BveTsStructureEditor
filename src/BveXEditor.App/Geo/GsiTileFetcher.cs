using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BveXEditor.Core.Geo;

namespace BveXEditor.App.Geo;

/// <summary>地理院タイルの種類。</summary>
public sealed record GsiLayer(string Id, string Label, string Extension, int MaxZoom)
{
    public static readonly GsiLayer[] All =
    [
        new("std", "標準地図", "png", 18),
        new("pale", "淡色地図", "png", 18),
        new("seamlessphoto", "写真（全国最新写真）", "jpg", 18),
    ];

    public string UrlFor(int z, int x, int y) => $"https://cyberjapandata.gsi.go.jp/xyz/{Id}/{z}/{x}/{y}.{Extension}";
}

public sealed record GsiMapResult(string ImagePath, double WidthMeters, int Zoom, int TileCount);

/// <summary>
/// 地理院タイルを取ってきて 1 枚の画像につなぎ、中心を真ん中にした正方形に切り出して PNG で保存する。
/// 同じ場所・種類・範囲はキャッシュ（%LocalAppData%\BveXEditor\gsi）を使い、取り直さない。
/// </summary>
public static class GsiTileFetcher
{
    public const string Attribution = "出典: 国土地理院（地理院タイル）";

    /// <summary>一度に取るタイルの上限（サーバーに負担をかけすぎないため）。</summary>
    public const int MaxTiles = 256;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("BveXEditor/1.0 (+https://maps.gsi.go.jp/development/ichiran.html)");
        return c;
    }

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BveXEditor", "gsi");

    public static TilePlan PlanFor(double lat, double lon, double sizeMeters, GsiLayer layer) =>
        WebMercator.Plan(lat, lon, sizeMeters, WebMercator.ChooseZoom(lat, sizeMeters, layer.MaxZoom));

    public static async Task<GsiMapResult> FetchAsync(double lat, double lon, double sizeMeters, GsiLayer layer,
        IProgress<string>? progress, CancellationToken cancel)
    {
        var plan = PlanFor(lat, lon, sizeMeters, layer);
        if (plan.TileCount > MaxTiles)
            throw new InvalidOperationException($"タイルが多すぎます（{plan.TileCount} 枚）。範囲を小さくしてください");

        Directory.CreateDirectory(CacheDir);
        var name = string.Create(CultureInfo.InvariantCulture,
            $"{layer.Id}_z{plan.Zoom}_{lat:0.000000}_{lon:0.000000}_{sizeMeters:0}m.png");
        var path = Path.Combine(CacheDir, name);
        if (File.Exists(path))
        {
            progress?.Report("保存済みの地図を使います");
            return new GsiMapResult(path, plan.WidthMeters, plan.Zoom, 0);
        }

        int cols = plan.TileX1 - plan.TileX0 + 1, rows = plan.TileY1 - plan.TileY0 + 1;
        int stride = cols * WebMercator.TileSize * 4;
        var canvas = new byte[stride * rows * WebMercator.TileSize];
        int done = 0, missing = 0;
        using var gate = new SemaphoreSlim(4);

        var tasks = new List<Task>();
        for (int ty = plan.TileY0; ty <= plan.TileY1; ty++)
        for (int tx = plan.TileX0; tx <= plan.TileX1; tx++)
        {
            int x = tx, y = ty;
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(cancel);
                try
                {
                    using var res = await Http.GetAsync(layer.UrlFor(plan.Zoom, x, y), cancel);
                    if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        Interlocked.Increment(ref missing); // 海など、タイルが無い場所は空白のまま
                    }
                    else
                    {
                        res.EnsureSuccessStatusCode();
                        var bytes = await res.Content.ReadAsByteArrayAsync(cancel);
                        var pixels = DecodeBgra(bytes);
                        int ox = (x - plan.TileX0) * WebMercator.TileSize, oy = (y - plan.TileY0) * WebMercator.TileSize;
                        for (int row = 0; row < WebMercator.TileSize; row++)
                            Buffer.BlockCopy(pixels, row * WebMercator.TileSize * 4, canvas,
                                (oy + row) * stride + ox * 4, WebMercator.TileSize * 4);
                    }
                    int n = Interlocked.Increment(ref done);
                    progress?.Report($"タイルを取得中… {n} / {plan.TileCount}");
                }
                finally { gate.Release(); }
            }, cancel));
        }
        await Task.WhenAll(tasks);
        if (missing == plan.TileCount) throw new InvalidOperationException("この場所には地図のタイルがありません");

        // 正方形に切り出して保存
        var crop = new byte[plan.CropWidth * plan.CropHeight * 4];
        for (int row = 0; row < plan.CropHeight; row++)
            Buffer.BlockCopy(canvas, (plan.CropTop + row) * stride + plan.CropLeft * 4, crop, row * plan.CropWidth * 4, plan.CropWidth * 4);
        var bmp = BitmapSource.Create(plan.CropWidth, plan.CropHeight, 96, 96, PixelFormats.Bgra32, null, crop, plan.CropWidth * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        var temp = path + ".tmp";
        await using (var fs = File.Create(temp)) encoder.Save(fs);
        File.Move(temp, path, overwrite: true);
        progress?.Report("地図をつなぎ終えました");
        return new GsiMapResult(path, plan.WidthMeters, plan.Zoom, plan.TileCount);
    }

    private static byte[] DecodeBgra(byte[] data)
    {
        using var ms = new MemoryStream(data);
        var frame = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[WebMercator.TileSize * WebMercator.TileSize * 4];
        if (converted.PixelWidth != WebMercator.TileSize || converted.PixelHeight != WebMercator.TileSize)
            throw new InvalidDataException("タイルの大きさが 256px ではありません");
        converted.CopyPixels(pixels, WebMercator.TileSize * 4, 0);
        return pixels;
    }
}
