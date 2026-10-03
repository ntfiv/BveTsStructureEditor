using System.Numerics;
using BveXEditor.Core.Format;
using BveXEditor.Core.Format.Pmx;
using BveXEditor.Core.Model;

namespace BveXEditor.Core.Editing;

public enum BatchOutputFormat { TextX, Pmx }

/// <summary>一括変換の設定。</summary>
public sealed class BatchOptions
{
    public bool Recursive { get; set; } = true;
    public bool IncludeX { get; set; } = true;
    public bool IncludePmx { get; set; }

    public float Scale { get; set; } = 1f;
    public bool MirrorX { get; set; }
    public bool MirrorZ { get; set; }
    /// <summary>テクスチャパスの中の文字列を置き換える（空なら何もしない）。</summary>
    public string ReplaceFrom { get; set; } = "";
    public string ReplaceTo { get; set; } = "";
    public bool MakeTexturesRelative { get; set; }
    public bool RemoveUnused { get; set; }

    public BatchOutputFormat Format { get; set; } = BatchOutputFormat.TextX;
    /// <summary>出力先のフォルダー（入力フォルダーからの構成を保つ）。null なら入力と同じ場所に書き、上書きするときは .bak を残す。</summary>
    public string? OutputDirectory { get; set; }

    public XWriteOptions WriteOptions { get; set; } = new();
    public PmxOptions PmxOptions { get; set; } = new();
}

public sealed record BatchFileResult(string Source, string? Output, bool Ok, string Message);

/// <summary>フォルダー内の .x / .pmx をまとめて読み、変形・パス修正をして書き出す。</summary>
public static class BatchConvert
{
    public static List<string> FindFiles(string directory, BatchOptions o)
    {
        var option = o.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.EnumerateFiles(directory, "*.*", option)
            .Where(f =>
            {
                var ext = Path.GetExtension(f);
                return (o.IncludeX && ext.Equals(".x", StringComparison.OrdinalIgnoreCase)) ||
                       (o.IncludePmx && ext.Equals(".pmx", StringComparison.OrdinalIgnoreCase));
            })
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>1 ファイルを処理する。例外は結果のメッセージにする。</summary>
    public static BatchFileResult ProcessFile(string source, string rootDirectory, BatchOptions o)
    {
        try
        {
            var srcDir = Path.GetDirectoryName(Path.GetFullPath(source))!;
            bool isPmx = Path.GetExtension(source).Equals(".pmx", StringComparison.OrdinalIgnoreCase);
            var scene = isPmx ? PmxReader.Load(source, o.PmxOptions).Scene : XFile.Load(source).Scene;
            var notes = new List<string>();

            // 変形（選択なし = 全体）
            var all = new Selection();
            if (MathF.Abs(o.Scale - 1f) > 1e-6f)
            {
                if (o.Scale <= 0) return new BatchFileResult(source, null, false, "倍率は 0 より大きくしてください");
                MeshOps.Transform(scene, all, Matrix4x4.CreateScale(o.Scale));
                notes.Add($"{o.Scale:0.###} 倍");
            }
            if (o.MirrorX) { MeshOps.Transform(scene, all, Matrix4x4.CreateScale(-1, 1, 1)); notes.Add("左右反転"); }
            if (o.MirrorZ) { MeshOps.Transform(scene, all, Matrix4x4.CreateScale(1, 1, -1)); notes.Add("前後反転"); }

            if (!string.IsNullOrEmpty(o.ReplaceFrom))
            {
                int replaced = 0;
                foreach (var mat in scene.Meshes.SelectMany(m => m.Materials))
                {
                    if (string.IsNullOrEmpty(mat.Texture) || !mat.Texture.Contains(o.ReplaceFrom, StringComparison.OrdinalIgnoreCase)) continue;
                    mat.Texture = mat.Texture.Replace(o.ReplaceFrom, o.ReplaceTo, StringComparison.OrdinalIgnoreCase);
                    replaced++;
                }
                if (replaced > 0) notes.Add($"テクスチャパス置換 {replaced}");
            }
            if (o.MakeTexturesRelative)
            {
                int n = TexturePaths.MakeRelative(scene, srcDir);
                if (n > 0) notes.Add($"相対パス化 {n}");
            }
            if (o.RemoveUnused)
            {
                int v = 0, m = 0;
                foreach (var mesh in scene.Meshes)
                {
                    v += MeshOps.RemoveUnusedVertices(mesh);
                    m += MeshOps.RemoveUnusedMaterials(mesh);
                }
                if (v + m > 0) notes.Add($"未使用 頂点 {v}・マテリアル {m} 削除");
            }

            // 出力先
            var ext = o.Format == BatchOutputFormat.Pmx ? ".pmx" : ".x";
            string output;
            if (o.OutputDirectory is { } outRoot)
            {
                var rel = Path.GetRelativePath(rootDirectory, Path.GetFullPath(source));
                output = Path.ChangeExtension(Path.Combine(outRoot, rel), ext);
                var outDir = Path.GetDirectoryName(Path.GetFullPath(output))!;
                Directory.CreateDirectory(outDir);
                // 相対パスのテクスチャが出力先からも同じ画像を指すように書き換える
                if (!string.Equals(outDir, srcDir, StringComparison.OrdinalIgnoreCase))
                    TexturePaths.Rebase(scene.Meshes, srcDir, outDir);
            }
            else
            {
                output = Path.ChangeExtension(source, ext);
            }
            if (File.Exists(output) && o.OutputDirectory == null)
            {
                File.Copy(output, output + ".bak", overwrite: true);
                notes.Add(".bak を作成");
            }

            if (o.Format == BatchOutputFormat.Pmx) PmxWriter.Save(output, scene, o.PmxOptions, o.WriteOptions.SmoothAngle);
            else XFile.Save(output, scene, o.WriteOptions);

            return new BatchFileResult(source, output, true,
                $"{Path.GetFileName(output)} に書き出しました" + (notes.Count > 0 ? "（" + string.Join("、", notes) + "）" : ""));
        }
        catch (Exception ex) when (ex is XFormatException or IOException or UnauthorizedAccessException or InvalidDataException or FormatException
                                       or InvalidOperationException or NotSupportedException or ArgumentException or IndexOutOfRangeException)
        {
            return new BatchFileResult(source, null, false, ex.Message);
        }
    }
}
