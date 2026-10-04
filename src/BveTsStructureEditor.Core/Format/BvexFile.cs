using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BveTsStructureEditor.Core.Model;

namespace BveTsStructureEditor.Core.Format;

public sealed record BvexLoadResult(XScene Scene, JsonObject? Editor, int Version, IReadOnlyList<string> Warnings);

/// <summary>
/// BveTs Structure Editor の作業ファイル (.bvex)。zip の中に project.json を 1 つ持つ。
/// モデルは .x に書き出すときの丸めや法線の作り直しをせず、読み込んだ値のまま保存する（頂点色・読み込んだ法線・面ごとの法線番号も）。
/// 画面の状態は <see cref="BvexLoadResult.Editor"/> に、アプリ側が決めた形の JSON で入れる（Core は中身を解釈しない）。
/// テクスチャは画像を含めず、パスだけを持つ。
/// </summary>
public static class BvexFile
{
    public const string Extension = ".bvex";
    public const int CurrentVersion = 1;
    private const string FormatName = "BveTs Structure Editor project";
    /// <summary>改名前（BVE X Editor）に保存した作業ファイルの形式名。読み込みだけ受け付ける。</summary>
    private const string LegacyFormatName = "BVE X Editor project";
    private const string EntryName = "project.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static bool IsBvex(string path) => string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>一時ファイルに書いてから置き換えるので、途中で失敗しても元のファイルは壊れない。</summary>
    public static void Save(string path, XScene scene, JsonObject? editor)
    {
        var full = Path.GetFullPath(path);
        var temp = full + ".tmp";
        using (var fs = File.Create(temp)) Write(fs, scene, editor);
        File.Move(temp, full, overwrite: true);
    }

    public static void Write(Stream stream, XScene scene, JsonObject? editor)
    {
        var doc = new ProjectDto
        {
            Format = FormatName,
            Version = CurrentVersion,
            Scene = SceneDto.From(scene),
            Editor = editor,
        };
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        var entry = zip.CreateEntry(EntryName, CompressionLevel.Optimal);
        using var es = entry.Open();
        JsonSerializer.Serialize(es, doc, Json);
    }

    public static BvexLoadResult Load(string path)
    {
        using var fs = File.OpenRead(path);
        return Read(fs);
    }

    public static BvexLoadResult Read(Stream stream)
    {
        ProjectDto? doc;
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var entry = zip.GetEntry(EntryName) ?? throw new XFormatException("作業ファイルではありません（project.json がありません）");
            using var es = entry.Open();
            doc = JsonSerializer.Deserialize<ProjectDto>(es, Json);
        }
        catch (InvalidDataException)
        {
            throw new XFormatException("作業ファイル (.bvex) として読めません（zip ではありません）");
        }
        catch (JsonException ex)
        {
            throw new XFormatException("作業ファイルの中身が壊れています: " + ex.Message);
        }
        if (doc is not { Format: FormatName or LegacyFormatName, Scene: not null })
            throw new XFormatException("作業ファイル (.bvex) ではありません");

        var warnings = new List<string>();
        if (doc.Version > CurrentVersion)
            warnings.Add($"新しい版 (v{doc.Version}) の BveTs Structure Editor で保存された作業ファイルです。読めない項目は無視しました");
        var scene = doc.Scene.ToScene(warnings);
        return new BvexLoadResult(scene, doc.Editor, doc.Version, warnings);
    }

    // ───────── 保存する形 ─────────

    private sealed class ProjectDto
    {
        public string? Format { get; set; }
        public int Version { get; set; }
        public SceneDto? Scene { get; set; }
        public JsonObject? Editor { get; set; }
    }

    private sealed class SceneDto
    {
        public List<MeshDto> Meshes { get; set; } = [];

        public static SceneDto From(XScene scene) => new() { Meshes = scene.Meshes.Select(MeshDto.From).ToList() };

        public XScene ToScene(List<string> warnings)
        {
            var s = new XScene();
            for (int i = 0; i < Meshes.Count; i++) s.Meshes.Add(Meshes[i].ToMesh(i, warnings));
            return s;
        }
    }

    private sealed class MeshDto
    {
        public string Name { get; set; } = "";
        /// <summary>x, y, z の順に並べた配列（数値の数を減らして読み書きを速くする）。</summary>
        public float[] Positions { get; set; } = [];
        public float[]? TexCoords { get; set; }
        public float[]? Colors { get; set; }
        public float[]? Normals { get; set; }
        public List<FaceDto> Faces { get; set; } = [];
        public List<MaterialDto> Materials { get; set; } = [];

        public static MeshDto From(XMesh m) => new()
        {
            Name = m.Name,
            Positions = m.Positions.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray(),
            TexCoords = m.TexCoords.Count > 0 ? m.TexCoords.SelectMany(t => new[] { t.X, t.Y }).ToArray() : null,
            Colors = m.VertexColors.Count > 0 ? m.VertexColors.SelectMany(c => new[] { c.X, c.Y, c.Z, c.W }).ToArray() : null,
            Normals = m.Normals.Count > 0 ? m.Normals.SelectMany(n => new[] { n.X, n.Y, n.Z }).ToArray() : null,
            Faces = m.Faces.Select(f => new FaceDto { I = f.Indices, M = f.Material, N = f.NormalIndices }).ToList(),
            Materials = m.Materials.Select(MaterialDto.From).ToList(),
        };

        public XMesh ToMesh(int index, List<string> warnings)
        {
            var m = new XMesh { Name = Name };
            for (int i = 0; i + 2 < Positions.Length; i += 3) m.Positions.Add(new Vector3(Positions[i], Positions[i + 1], Positions[i + 2]));
            if (TexCoords != null)
                for (int i = 0; i + 1 < TexCoords.Length; i += 2) m.TexCoords.Add(new Vector2(TexCoords[i], TexCoords[i + 1]));
            if (Colors != null)
                for (int i = 0; i + 3 < Colors.Length; i += 4) m.VertexColors.Add(new Vector4(Colors[i], Colors[i + 1], Colors[i + 2], Colors[i + 3]));
            if (Normals != null)
                for (int i = 0; i + 2 < Normals.Length; i += 3) m.Normals.Add(new Vector3(Normals[i], Normals[i + 1], Normals[i + 2]));
            m.Materials.AddRange(Materials.Select(x => x.ToMaterial()));

            int dropped = 0;
            foreach (var f in Faces)
            {
                if (f.I is not { Length: >= 3 } idx || idx.Any(v => v < 0 || v >= m.Positions.Count)) { dropped++; continue; }
                var normals = f.N is { } n && n.Length == idx.Length && n.All(v => v >= 0 && v < m.Normals.Count) ? n : null;
                m.Faces.Add(new XFace(idx, f.M) { NormalIndices = normals });
            }
            if (dropped > 0) warnings.Add($"メッシュ {index}（{Name}）: 頂点番号がおかしい面 {dropped} 枚を読み飛ばしました");
            if (m.TexCoords.Count != 0 && m.TexCoords.Count != m.Positions.Count)
            {
                warnings.Add($"メッシュ {index}（{Name}）: UV の数が頂点数と合わないので捨てました");
                m.TexCoords.Clear();
            }
            if (m.VertexColors.Count != 0 && m.VertexColors.Count != m.Positions.Count) m.VertexColors.Clear();
            return m;
        }
    }

    private sealed class FaceDto
    {
        public int[]? I { get; set; }
        public int M { get; set; }
        public int[]? N { get; set; }
    }

    private sealed class MaterialDto
    {
        public float[] Color { get; set; } = [1, 1, 1, 1];
        public float Power { get; set; }
        public float[] Specular { get; set; } = [0, 0, 0];
        public float[] Emissive { get; set; } = [0, 0, 0];
        public string? Texture { get; set; }

        public static MaterialDto From(XMaterial x) => new()
        {
            Color = [x.FaceColor.X, x.FaceColor.Y, x.FaceColor.Z, x.FaceColor.W],
            Power = x.Power,
            Specular = [x.Specular.X, x.Specular.Y, x.Specular.Z],
            Emissive = [x.Emissive.X, x.Emissive.Y, x.Emissive.Z],
            Texture = x.Texture,
        };

        public XMaterial ToMaterial() => new()
        {
            FaceColor = Color.Length >= 4 ? new Vector4(Color[0], Color[1], Color[2], Color[3]) : Vector4.One,
            Power = Power,
            Specular = Specular.Length >= 3 ? new Vector3(Specular[0], Specular[1], Specular[2]) : Vector3.Zero,
            Emissive = Emissive.Length >= 3 ? new Vector3(Emissive[0], Emissive[1], Emissive[2]) : Vector3.Zero,
            Texture = Texture,
        };
    }
}
