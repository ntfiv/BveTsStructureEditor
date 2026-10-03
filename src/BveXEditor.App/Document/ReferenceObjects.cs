using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BveXEditor.App.Document;

/// <summary>
/// 3D ビューに半透明で置いて見比べるだけのモデル（.x / .pmx / .bvex）。
/// 編集・選択の対象にならず、.x / .pmx には書き出さない。作業ファイル (.bvex) には保存する。
/// </summary>
public sealed class ReferenceObject
{
    public string Path { get; set; } = "";
    public float OffsetX { get; set; }
    public float OffsetY { get; set; }
    public float OffsetZ { get; set; }
    /// <summary>回転（度）。モデルの原点まわりに X → Y → Z の順で回してから位置へ動かす（編集タブの変形欄と同じ決まり）。</summary>
    public float RotationX { get; set; }
    public float RotationY { get; set; }
    public float RotationZ { get; set; }
    public float Opacity { get; set; } = 0.5f;

    [JsonIgnore]
    public Vector3 Rotation
    {
        get => new(RotationX, RotationY, RotationZ);
        set { RotationX = value.X; RotationY = value.Y; RotationZ = value.Z; }
    }
    public bool Visible { get; set; } = true;

    [JsonIgnore]
    public Vector3 Offset
    {
        get => new(OffsetX, OffsetY, OffsetZ);
        set { OffsetX = value.X; OffsetY = value.Y; OffsetZ = value.Z; }
    }

    [JsonIgnore] public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// 参考オブジェクトの置き方を、開いているファイルのパスごとに %AppData%\BveXEditor\reference_objects.json へ保存する
/// （下絵の references.json と同じ考え方。未保存のファイルではメモリ上だけ）。
/// </summary>
public sealed class ReferenceObjectStore
{
    private static readonly string FilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BveXEditor", "reference_objects.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public List<ReferenceObject> Load(string? modelPath)
    {
        if (modelPath == null) return [];
        try
        {
            return ReadAll().TryGetValue(Key(modelPath), out var list) ? list : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void Save(string? modelPath, IReadOnlyList<ReferenceObject> items)
    {
        if (modelPath == null) return;
        try
        {
            var all = ReadAll();
            if (items.Count == 0) all.Remove(Key(modelPath));
            else all[Key(modelPath)] = items.ToList();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all, Json));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 覚えられなくても表示は続けられる
        }
    }

    private static string Key(string path) => System.IO.Path.GetFullPath(path).ToLowerInvariant();

    private static Dictionary<string, List<ReferenceObject>> ReadAll() =>
        File.Exists(FilePath)
            ? JsonSerializer.Deserialize<Dictionary<string, List<ReferenceObject>>>(File.ReadAllText(FilePath), Json) ?? []
            : [];
}
