using System.Numerics;
using BveXEditor.Core.Imaging;
using BveXEditor.Core.Model;

namespace BveXEditor.Core.Editing;

public enum CheckSeverity { Error, Warning, Info }

/// <summary>「直す」ボタンでできること。</summary>
public enum CheckFix { None, RemoveUnusedVertices, RemoveUnusedMaterials, DeleteFaces, TriangulateFaces }

/// <summary>見つかった問題 1 件。<see cref="Mesh"/> と <see cref="Faces"/>/<see cref="Vertices"/> で該当箇所を選べる。</summary>
public sealed record CheckIssue(CheckSeverity Severity, string Message, int Mesh = -1,
    int[]? Faces = null, int[]? Vertices = null, CheckFix Fix = CheckFix.None)
{
    public string SeverityLabel => Severity switch
    {
        CheckSeverity.Error => "エラー",
        CheckSeverity.Warning => "注意",
        _ => "情報",
    };
}

/// <summary>
/// BVE で読み込んだときに問題になりやすい点を調べる。
/// <paramref name="modelDirectory"/> は相対テクスチャパスの基準（未保存なら null）。
/// </summary>
public static class BveCheck
{
    public const int ManyFaces = 10000;
    public const int MaxTextureSize = 4096;

    public static List<CheckIssue> Run(XScene scene, string? modelDirectory, IEnumerable<string>? loadWarnings = null)
    {
        var issues = new List<CheckIssue>();
        CheckTextures(scene, modelDirectory, issues);
        for (int m = 0; m < scene.Meshes.Count; m++) CheckMesh(scene.Meshes[m], m, issues);
        foreach (var w in loadWarnings ?? []) issues.Add(new CheckIssue(CheckSeverity.Info, "読み込み時: " + w));
        return issues.OrderBy(i => i.Severity).ToList();
    }

    private static void CheckTextures(XScene scene, string? dir, List<CheckIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int m = 0; m < scene.Meshes.Count; m++)
            foreach (var mat in scene.Meshes[m].Materials)
            {
                var tex = mat.Texture;
                if (string.IsNullOrWhiteSpace(tex) || !seen.Add(tex)) continue;
                var rel = tex.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                bool rooted = Path.IsPathRooted(rel);
                if (rooted)
                    issues.Add(new CheckIssue(CheckSeverity.Warning, $"テクスチャが絶対パスです（ほかの PC では読めません）: {tex}", m));
                if (tex.Any(c => c > 0x7E))
                    issues.Add(new CheckIssue(CheckSeverity.Warning, $"テクスチャのパスに日本語などの文字があります（環境によって読めないことがあります）: {tex}", m));

                if (!rooted && dir == null)
                {
                    issues.Add(new CheckIssue(CheckSeverity.Info, $"未保存のため、テクスチャの場所を確認できません: {tex}", m));
                    continue;
                }
                var full = rooted ? rel : Path.GetFullPath(Path.Combine(dir!, rel));
                if (!File.Exists(full))
                {
                    issues.Add(new CheckIssue(CheckSeverity.Error, $"テクスチャが見つかりません: {tex}", m));
                    continue;
                }
                if (ImageHeader.ReadSize(full) is { } size)
                {
                    if (size.Width > MaxTextureSize || size.Height > MaxTextureSize)
                        issues.Add(new CheckIssue(CheckSeverity.Warning, $"テクスチャが大きすぎます（{size.Width}×{size.Height}）: {tex}", m));
                    else if (!ImageHeader.IsPowerOfTwo(size.Width) || !ImageHeader.IsPowerOfTwo(size.Height))
                        issues.Add(new CheckIssue(CheckSeverity.Info, $"テクスチャの大きさが 2 の累乗ではありません（{size.Width}×{size.Height}。古い環境ではぼやけることがあります）: {tex}", m));
                }
            }
    }

    private static void CheckMesh(XMesh mesh, int m, List<CheckIssue> issues)
    {
        string name = string.IsNullOrEmpty(mesh.Name) ? $"メッシュ {m}" : $"「{mesh.Name}」";
        if (mesh.Faces.Count == 0)
        {
            issues.Add(new CheckIssue(CheckSeverity.Info, $"{name} に面がありません", m));
            return;
        }
        if (mesh.Faces.Count >= ManyFaces)
            issues.Add(new CheckIssue(CheckSeverity.Info, $"{name} の面が多めです（{mesh.Faces.Count} 面。沿線にたくさん置くと重くなります）", m));

        var degenerate = new List<int>();
        var nonPlanar = new List<int>();
        var concave = new List<int>();
        var duplicates = new List<int>();
        var signatures = new HashSet<string>();
        for (int fi = 0; fi < mesh.Faces.Count; fi++)
        {
            var f = mesh.Faces[fi];
            if (f.Indices.Any(i => i < 0 || i >= mesh.Positions.Count)) { degenerate.Add(fi); continue; }
            var normal = mesh.FaceNormal(f);
            if (f.Indices.Length < 3 || normal.LengthSquared() < 1e-12f) { degenerate.Add(fi); continue; }

            var sig = string.Join("|", f.Indices.Select(i => Key(mesh.Positions[i])).Order());
            if (!signatures.Add(sig + "#" + Orientation(normal))) duplicates.Add(fi);

            if (f.Indices.Length < 4) continue;
            var n = Vector3.Normalize(normal);
            var p0 = mesh.Positions[f.Indices[0]];
            if (f.Indices.Any(i => MathF.Abs(Vector3.Dot(mesh.Positions[i] - p0, n)) > 0.001f)) nonPlanar.Add(fi);
            else if (!IsConvex(mesh.Positions, f.Indices, n)) concave.Add(fi);
        }
        if (degenerate.Count > 0)
            issues.Add(new CheckIssue(CheckSeverity.Warning, $"{name}: 面積が 0 の面が {degenerate.Count} 枚あります", m, Faces: [.. degenerate], Fix: CheckFix.DeleteFaces));
        if (duplicates.Count > 0)
            issues.Add(new CheckIssue(CheckSeverity.Warning, $"{name}: 同じ位置・同じ向きに重なった面が {duplicates.Count} 枚あります（ちらつきの原因）", m, Faces: [.. duplicates], Fix: CheckFix.DeleteFaces));
        if (nonPlanar.Count > 0)
            issues.Add(new CheckIssue(CheckSeverity.Info, $"{name}: 平らでない多角形が {nonPlanar.Count} 枚あります（BVE では三角形の分け方で形が変わります）", m, Faces: [.. nonPlanar], Fix: CheckFix.TriangulateFaces));
        if (concave.Count > 0)
            issues.Add(new CheckIssue(CheckSeverity.Warning, $"{name}: へこんだ多角形が {concave.Count} 枚あります（BVE では正しく描かれません。頂点を足して凸形に分けてください）", m, Faces: [.. concave]));

        var used = new bool[mesh.Positions.Count];
        foreach (var f in mesh.Faces)
            foreach (var i in f.Indices)
                if (i >= 0 && i < used.Length) used[i] = true;
        var unused = Enumerable.Range(0, used.Length).Where(i => !used[i]).ToArray();
        if (unused.Length > 0)
            issues.Add(new CheckIssue(CheckSeverity.Info, $"{name}: どの面にも使われていない頂点が {unused.Length} 個あります", m, Vertices: unused, Fix: CheckFix.RemoveUnusedVertices));

        var usedMats = mesh.Faces.Select(f => f.Material).ToHashSet();
        int unusedMats = Enumerable.Range(0, mesh.Materials.Count).Count(i => !usedMats.Contains(i));
        if (unusedMats > 0)
            issues.Add(new CheckIssue(CheckSeverity.Info, $"{name}: 使われていないマテリアルが {unusedMats} 個あります", m, Fix: CheckFix.RemoveUnusedMaterials));
    }

    private static string Key(Vector3 p) => $"{MathF.Round(p.X * 1e4f)},{MathF.Round(p.Y * 1e4f)},{MathF.Round(p.Z * 1e4f)}";

    private static string Orientation(Vector3 n)
    {
        n = Vector3.Normalize(n);
        return $"{MathF.Round(n.X * 100)},{MathF.Round(n.Y * 100)},{MathF.Round(n.Z * 100)}";
    }

    private static bool IsConvex(IReadOnlyList<Vector3> pos, int[] poly, Vector3 normal)
    {
        int n = poly.Length;
        for (int i = 0; i < n; i++)
        {
            var a = pos[poly[i]];
            var b = pos[poly[(i + 1) % n]];
            var c = pos[poly[(i + 2) % n]];
            if (Vector3.Dot(Vector3.Cross(b - a, c - b), normal) < -1e-9f) return false;
        }
        return true;
    }

    /// <summary>問題に付いた修正を行う。</summary>
    public static string Fix(XScene scene, CheckIssue issue)
    {
        if (issue.Mesh < 0 || issue.Mesh >= scene.Meshes.Count) return "直せる対象がありません";
        var mesh = scene.Meshes[issue.Mesh];
        switch (issue.Fix)
        {
            case CheckFix.RemoveUnusedVertices:
                return $"未使用の頂点を {MeshOps.RemoveUnusedVertices(mesh)} 個削除しました";
            case CheckFix.RemoveUnusedMaterials:
                return $"未使用のマテリアルを {MeshOps.RemoveUnusedMaterials(mesh)} 個削除しました";
            case CheckFix.DeleteFaces when issue.Faces != null:
            {
                var set = issue.Faces.ToHashSet();
                int removed = 0;
                for (int i = mesh.Faces.Count - 1; i >= 0; i--)
                    if (set.Contains(i)) { mesh.Faces.RemoveAt(i); removed++; }
                MeshOps.RemoveUnusedVertices(mesh);
                mesh.ClearNormals();
                return $"{removed} 面を削除しました";
            }
            case CheckFix.TriangulateFaces when issue.Faces != null:
            {
                var sel = new Selection();
                sel.SetMode(SelectMode.Face);
                sel.Set(issue.Faces.Select(f => new ElementRef(issue.Mesh, f)));
                return MeshOps.Triangulate(scene, sel);
            }
            default:
                return "この項目は自動では直せません";
        }
    }
}
