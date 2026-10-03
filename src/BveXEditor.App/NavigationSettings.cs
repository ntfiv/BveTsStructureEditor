using System.IO;
using System.Text.Json;

namespace BveXEditor.App;

/// <summary>3D ビューの視点の動かし方。左ボタンは選択とギズモに使うので、どれも中・右ボタンで動かす。</summary>
public enum NavigationStyle
{
    /// <summary>右: 回転 / 中・Shift+右: 移動 / Ctrl+右: ズーム（既定）。</summary>
    Metasequoia,
    /// <summary>中: 回転 / Shift+中: 移動 / Ctrl+中: ズーム。</summary>
    Blender,
    /// <summary>中: 回転 / Shift+中: 移動。</summary>
    SketchUp,
    /// <summary>Shift+中: 回転 / 中: 移動。</summary>
    Fusion,
}

/// <summary>
/// 視点の操作の好み。アプリ全体の設定として %AppData%\BveXEditor\navigation.json に覚える（作業ファイルには入れない）。
/// 読み書きに失敗したら既定値で動く。
/// </summary>
public sealed class NavigationSettings
{
    public NavigationStyle Style { get; set; } = NavigationStyle.Metasequoia;
    /// <summary>ホイールのズーム方向を逆にする。</summary>
    public bool InvertZoom { get; set; }
    /// <summary>マウスの位置を中心に回転・ズームする（false なら画面の中心）。</summary>
    public bool AroundMouse { get; set; } = true;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BveXEditor", "navigation.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static NavigationSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<NavigationSettings>(File.ReadAllText(FilePath)) ?? new();
                if (!Enum.IsDefined(s.Style)) s.Style = NavigationStyle.Metasequoia;
                return s;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 既定値で動く
        }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 覚えられなくても操作は続けられる
        }
    }

    public static string DisplayName(NavigationStyle style) => style switch
    {
        NavigationStyle.Blender => "Blender 風",
        NavigationStyle.SketchUp => "SketchUp 風",
        NavigationStyle.Fusion => "Fusion 風",
        _ => "標準（メタセコイア風）",
    };

    /// <summary>方式の割り当て（回転 / 移動 / ドラッグでのズーム）の説明。</summary>
    public static string Describe(NavigationStyle style) => style switch
    {
        NavigationStyle.Blender => "中ドラッグ: 回転 / Shift+中ドラッグ: 移動 / Ctrl+中ドラッグ: ズーム",
        NavigationStyle.SketchUp => "中ドラッグ: 回転 / Shift+中ドラッグ: 移動",
        NavigationStyle.Fusion => "Shift+中ドラッグ: 回転 / 中ドラッグ: 移動",
        _ => "右ドラッグ: 回転 / 中ドラッグ（Shift+右）: 移動 / Ctrl+右ドラッグ: ズーム",
    };
}
