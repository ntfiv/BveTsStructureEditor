using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BveXEditor.Core.Imaging;

namespace BveXEditor.App.Rendering;

/// <summary>
/// テクスチャ画像の読み込みとキャッシュ。ファイルの更新日時が変わったら読み直す
/// （画像編集ソフトで直してから戻ってきたときに反映されるように）。
/// </summary>
public sealed class TextureCache
{
    private readonly Dictionary<string, (DateTime Stamp, BitmapSource? Image, string? Error)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public BitmapSource? Get(string? fullPath, out string? error)
    {
        error = null;
        if (fullPath is null) return null;
        if (!File.Exists(fullPath))
        {
            error = "見つかりません: " + fullPath;
            return null;
        }
        var stamp = File.GetLastWriteTimeUtc(fullPath);
        if (_cache.TryGetValue(fullPath, out var hit) && hit.Stamp == stamp)
        {
            error = hit.Error;
            return hit.Image;
        }

        BitmapSource? image = null;
        try
        {
            image = Load(fullPath);
        }
        catch (Exception ex)
        {
            error = $"読めません: {Path.GetFileName(fullPath)} ({ex.Message})";
        }
        _cache[fullPath] = (stamp, image, error);
        return image;
    }

    private static BitmapSource Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            var img = DdsDecoder.Decode(bytes);
            var bmp = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null, img.Bgra, img.Width * 4);
            bmp.Freeze();
            return bmp;
        }
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = new MemoryStream(bytes);
        bi.EndInit();
        bi.Freeze();
        return bi;
    }
}
