using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BveTsStructureEditor.App.Document;

/// <summary>
/// 作業ファイル (.bvex) に入れる画面の状態。モデル本体は Core の BvexFile が別に持つ。
/// 項目を増やしても古いファイルが読めるよう、どれも省略可能にしておく。
/// ファイルのパス（下絵・書き出し先）は作業ファイルのフォルダーからの相対パスで持つ。
/// </summary>
public sealed class ProjectState
{
    public CameraState? Camera { get; set; }
    public ViewState? View { get; set; }
    public SelectionState? Selection { get; set; }
    public GuideState? Guides { get; set; }
    public List<ReferenceImage>? References { get; set; }
    public List<ReferenceObject>? ReferenceObjects { get; set; }
    public SaveState? Save { get; set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public JsonObject ToJson() => (JsonObject)JsonSerializer.SerializeToNode(this, Json)!;

    /// <summary>読めない（壊れている・形が違う）ときは null。モデルは開けるので、画面の状態だけ諦める。</summary>
    public static ProjectState? FromJson(JsonObject? json)
    {
        if (json == null) return null;
        try
        {
            return json.Deserialize<ProjectState>(Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>カメラ（WPF 座標のまま）。</summary>
public sealed record CameraState(double[] Position, double[] Look, double[] Up, bool Orthographic, double Width, double FieldOfView);

public sealed class ViewState
{
    public bool ShowTextures { get; set; } = true;
    public bool Wireframe { get; set; }
    public bool BackFaces { get; set; } = true;
    public bool Grid { get; set; } = true;
    /// <summary>寸法の表示。</summary>
    public bool Dimensions { get; set; }
    public List<int> HiddenMeshes { get; set; } = [];
    /// <summary>[メッシュ番号, マテリアル番号] の組。</summary>
    public List<int[]> HiddenMaterials { get; set; } = [];
    public string Gizmo { get; set; } = "Move";
    public bool MirrorX { get; set; }
    public int CenterTab { get; set; }
    public int RightTab { get; set; }
}

public sealed class SelectionState
{
    public string Mode { get; set; } = "Object";
    /// <summary>[メッシュ番号, 要素番号] の組（オブジェクト選択では要素番号 -1）。</summary>
    public List<int[]> Items { get; set; } = [];
    public int ActiveMesh { get; set; }
}

public sealed class GuideState
{
    public string Gauge { get; set; } = "None";
    public bool Platform { get; set; } = true;
}

public sealed class SaveState
{
    public string Encoding { get; set; } = "shift_jis";
    public int Precision { get; set; } = 6;
    public bool WriteTemplates { get; set; }
    public float SmoothAngle { get; set; } = 30;
    public float PmxScale { get; set; } = 1;
    /// <summary>最後に .x / .pmx を書き出した場所。</summary>
    public string? ExportPath { get; set; }
}
