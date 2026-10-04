using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using BveTsStructureEditor.Core.Editing;

namespace BveTsStructureEditor.App.Document;

/// <summary>
/// 3D ビューに敷く下絵（図面・写真）1 枚。モデルではないので .x には書かず、選択の対象にもならない。
/// </summary>
public sealed class ReferenceImage
{
    public string Path { get; set; } = "";
    public ReferencePlane Plane { get; set; } = ReferencePlane.Ground;
    /// <summary>幅 (m)。高さは画像の縦横比から決める。</summary>
    public float Width { get; set; } = 20f;
    public float CenterX { get; set; }
    public float CenterY { get; set; }
    public float CenterZ { get; set; }
    public float Rotation { get; set; }
    public float Opacity { get; set; } = 0.6f;
    public bool Visible { get; set; } = true;
    /// <summary>一覧に出す名前（地理院地図など、ファイル名がわかりにくいとき）。空ならファイル名。</summary>
    public string? Label { get; set; }
    /// <summary>出典の表示（地理院タイルなど）。</summary>
    public string? Attribution { get; set; }

    [JsonIgnore]
    public Vector3 Center
    {
        get => new(CenterX, CenterY, CenterZ);
        set { CenterX = value.X; CenterY = value.Y; CenterZ = value.Z; }
    }

    [JsonIgnore] public string Name => string.IsNullOrEmpty(Label) ? System.IO.Path.GetFileName(Path) : Label;
}

/// <summary>
/// 下絵の設定を、.x のフルパスごとに %AppData%\BveTsStructureEditor\references.json へ保存する。
/// .x の中や横にファイルを増やさないため。未保存（無題）のファイルの下絵はメモリ上だけ。
/// </summary>
public sealed class ReferenceStore
{
    private static readonly string FilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BveTsStructureEditor", "references.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public List<ReferenceImage> Load(string? modelPath)
    {
        if (modelPath == null) return [];
        try
        {
            var all = ReadAll();
            return all.TryGetValue(Key(modelPath), out var list) ? list : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void Save(string? modelPath, IReadOnlyList<ReferenceImage> images)
    {
        if (modelPath == null) return;
        try
        {
            var all = ReadAll();
            if (images.Count == 0) all.Remove(Key(modelPath));
            else all[Key(modelPath)] = images.ToList();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all, Json));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 下絵の保存に失敗しても編集は続けられるので黙って諦める
        }
    }

    private static string Key(string path) => System.IO.Path.GetFullPath(path).ToLowerInvariant();

    private static Dictionary<string, List<ReferenceImage>> ReadAll()
    {
        if (!File.Exists(FilePath)) return [];
        return JsonSerializer.Deserialize<Dictionary<string, List<ReferenceImage>>>(File.ReadAllText(FilePath), Json) ?? [];
    }
}
