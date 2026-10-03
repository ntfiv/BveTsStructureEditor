using System.IO;
using System.Text.Json;
using System.Windows.Controls;

namespace BveXEditor.App;

/// <summary>
/// 折りたたみの開閉など、画面の小さな状態を %AppData%\BveXEditor\ui.json に覚えておく。
/// 読み書きに失敗しても画面は既定の状態で動くので、黙って諦める。
/// </summary>
public static class UiState
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BveXEditor", "ui.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static Dictionary<string, bool>? _values;

    private static Dictionary<string, bool> Values
    {
        get
        {
            if (_values != null) return _values;
            try
            {
                _values = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(FilePath)) ?? []
                    : [];
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _values = [];
            }
            return _values;
        }
    }

    /// <summary>折りたたみの開閉を <paramref name="key"/> で覚える。覚えていなければ XAML の初期値のまま。</summary>
    public static void Track(Expander expander, string key)
    {
        if (Values.TryGetValue(key, out bool open)) expander.IsExpanded = open;
        expander.Expanded += (_, e) => { if (e.OriginalSource == expander) Set(key, true); };
        expander.Collapsed += (_, e) => { if (e.OriginalSource == expander) Set(key, false); };
    }

    private static void Set(string key, bool value)
    {
        if (Values.TryGetValue(key, out bool old) && old == value) return;
        Values[key] = value;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Values, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 覚えられなくても操作は続けられる
        }
    }
}
