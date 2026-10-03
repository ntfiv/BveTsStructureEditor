using System.Numerics;
using BveXEditor.Core.Format;
using BveXEditor.Core.Model;

namespace BveXEditor.Core.Editing;

/// <summary>
/// 分割してできたストラクチャ 1 つ。<see cref="Index"/> は分割前から数えた区間の番号。
/// <see cref="ZEnd"/> は区間の終わり、<see cref="Extended"/> は重ね代ぶん伸ばした実際の終わり。
/// </summary>
public sealed record RepeaterPiece(int Index, float ZStart, float ZEnd, float Extended, XMesh Mesh)
{
    /// <summary>区間の長さ（＝繰り返しの間隔にする長さ）。</summary>
    public float Length => ZEnd - ZStart;
    /// <summary>実際に切り出した長さ（重ね代を含む）。</summary>
    public float CutLength => Extended - ZStart;
}

public sealed class RepeaterSplitOptions
{
    /// <summary>1 ピースの Z 方向の長さ (m)。</summary>
    public float Length { get; set; } = 5f;
    /// <summary>分割する範囲。null なら対象メッシュから自動で決める。</summary>
    public float? ZStart { get; set; }
    public float? ZEnd { get; set; }
    /// <summary>各ピースを Z=0 起点に動かす（Repeater は繰り返し位置にストラクチャの原点を置くため）。</summary>
    public bool AlignToZero { get; set; } = true;
    /// <summary>面が 1 枚も入らない区間を結果に含めない。</summary>
    public bool DropEmpty { get; set; } = true;
    /// <summary>
    /// 区間の終わりから先に伸ばす長さ (m)。曲線に並べたとき継ぎ目の外側に隙間が出ないよう、
    /// 5 m 間隔のピースを 5.05 m のように少し長く切り出すために使う。
    /// </summary>
    public float Overlap { get; set; }
    /// <summary>
    /// 伸ばした先がモデルの端を越えたら、先頭から回り込んで続きを取る（Repeater は全体を繰り返すため）。
    /// false なら端のピースは伸ばさない。
    /// </summary>
    public bool WrapAround { get; set; } = true;
}

/// <summary>
/// BVE の Repeater 用に、長いストラクチャを進行方向 (Z) の等間隔で切り分ける。
/// 切り目をまたぐ面は切って新しい頂点を作る（UV・頂点カラーは補間）。元のシーンは変えない。
/// </summary>
public static class RepeaterSplit
{
    public const float Epsilon = 0.001f;

    /// <summary>対象メッシュ（選択が空なら全体）の Z の範囲。頂点が 1 つもなければ null。</summary>
    public static (float Min, float Max)? ZRange(XScene scene, Selection sel)
    {
        float min = float.MaxValue, max = float.MinValue;
        foreach (var m in SelectionQuery.Meshes(scene, sel))
            foreach (var p in scene.Meshes[m].Positions)
            {
                min = MathF.Min(min, p.Z);
                max = MathF.Max(max, p.Z);
            }
        return min > max ? null : (min, max);
    }

    /// <summary>対象メッシュの X の範囲（重ね代の自動計算に使う、軌道中心から左右への張り出し）。</summary>
    public static (float Min, float Max)? XRange(XScene scene, Selection sel)
    {
        float min = float.MaxValue, max = float.MinValue;
        foreach (var m in SelectionQuery.Meshes(scene, sel))
            foreach (var p in scene.Meshes[m].Positions)
            {
                min = MathF.Min(min, p.X);
                max = MathF.Max(max, p.X);
            }
        return min > max ? null : (min, max);
    }

    public static List<RepeaterPiece> Split(XScene scene, Selection sel, RepeaterSplitOptions o)
    {
        var pieces = new List<RepeaterPiece>();
        if (o.Length <= 0) return pieces;
        var meshes = SelectionQuery.Meshes(scene, sel).Order().ToList();
        if (meshes.Count == 0) return pieces;

        float start = o.ZStart ?? ZRange(scene, sel)?.Min ?? 0;
        float end = o.ZEnd ?? ZRange(scene, sel)?.Max ?? 0;
        if (end - start <= Epsilon) return pieces;

        float overlap = MathF.Max(0, o.Overlap);
        int count = (int)MathF.Ceiling((end - start - Epsilon) / o.Length);
        for (int i = 0; i < count; i++)
        {
            float zs = start + i * o.Length;
            float ze = MathF.Min(zs + o.Length, end);
            // 伸ばした先。モデルの端を越えるぶんは、先頭から回り込んで取る
            float extended = ze + overlap;
            float wrap = o.WrapAround ? MathF.Max(0, MathF.Min(extended, zs + (end - start)) - end) : 0;
            float cutEnd = MathF.Min(extended, end);
            var mesh = new XMesh { Name = Name(scene, meshes, i) };
            foreach (var m in meshes)
                Append(mesh, scene.Meshes[m], zs, cutEnd, clipLow: i > 0, clipHigh: cutEnd < end - Epsilon);
            if (wrap > Epsilon)
                foreach (var m in meshes)
                    // 継ぎ目に重なる面（先頭の蓋など）は落とす: lowCoplanar = 0
                    Append(mesh, scene.Meshes[m], start, start + wrap, clipLow: true, clipHigh: start + wrap < end - Epsilon,
                        offsetZ: end - start, lowCoplanar: 0);

            MeshOps.CleanDegenerate(mesh);
            MeshOps.RemoveUnusedVertices(mesh);
            // 回り込みぶんで同じマテリアルが増えるので、同じものはまとめる
            MeshOps.MergeDuplicateMaterials(mesh);
            MeshOps.RemoveUnusedMaterials(mesh);
            if (mesh.Faces.Count == 0 && o.DropEmpty) continue;
            // 切り口で頂点が増えるので、読み込んだ法線は捨てて書き出し時に作り直す
            mesh.ClearNormals();
            // Z=0 へ寄せてから、切り口の計算と引き算で出る誤差（5.200001 など）を切り目ちょうどに揃える
            float shift = o.AlignToZero ? -zs : 0;
            float[] edges = [Round(zs + shift), Round(cutEnd + shift), Round(extended + shift)];
            for (int v = 0; v < mesh.Positions.Count; v++)
            {
                float z = mesh.Positions[v].Z + shift;
                foreach (float edge in edges)
                    if (MathF.Abs(z - edge) < Epsilon) { z = edge; break; }
                mesh.Positions[v] = mesh.Positions[v] with { Z = z };
            }
            pieces.Add(new RepeaterPiece(i, zs, ze, MathF.Min(extended, ze + overlap), mesh));
        }
        return pieces;
    }

    /// <summary>切り目の位置を 0.1 mm 単位に丸める（5.2000008 のような値を書き出さない）。</summary>
    private static float Round(float z) => MathF.Round(z, 4);

    private static string Name(XScene scene, List<int> meshes, int index)
    {
        var baseName = meshes.Count == 1 ? scene.Meshes[meshes[0]].Name : "Repeater";
        if (string.IsNullOrEmpty(baseName)) baseName = "Repeater";
        return $"{baseName}_{index:00}";
    }

    /// <summary>
    /// <paramref name="source"/> のうち zStart〜zEnd に入る部分を <paramref name="target"/> に足す。
    /// 切り目で面を切る（Sutherland–Hodgman。巻き順は変えない）。モデルの端では切らない。
    /// <paramref name="offsetZ"/> は足すときにずらす量（回り込みで先頭を後ろに持ってくる用）。
    /// </summary>
    private static void Append(XMesh target, XMesh source, float zStart, float zEnd, bool clipLow, bool clipHigh,
        float offsetZ = 0, int lowCoplanar = -1)
    {
        var work = source.Clone();
        // 切り目のすぐ近くの頂点は切り目に合わせる（細い切れ端を作らない）
        for (int i = 0; i < work.Positions.Count; i++)
        {
            var p = work.Positions[i];
            if (MathF.Abs(p.Z - zStart) < Epsilon) work.Positions[i] = p with { Z = zStart };
            else if (MathF.Abs(p.Z - zEnd) < Epsilon) work.Positions[i] = p with { Z = zEnd };
        }
        // 切り目にぴったり乗っている面は、向いている側のピースに入れる（両方に入れると重なってちらつく）
        if (clipLow) Clip(work, v => work.Positions[v].Z - zStart, keepCoplanarFacing: lowCoplanar);
        if (clipHigh) Clip(work, v => zEnd - work.Positions[v].Z, keepCoplanarFacing: +1);
        if (offsetZ != 0)
            for (int i = 0; i < work.Positions.Count; i++)
                work.Positions[i] = work.Positions[i] with { Z = work.Positions[i].Z + offsetZ };

        // 使うマテリアルだけを target に移し、面のマテリアル番号を付け替える
        var materialMap = new Dictionary<int, int>();
        var vertexMap = new Dictionary<int, int>();
        foreach (var f in work.Faces)
        {
            if (f.Indices.Length < 3) continue;
            if (!materialMap.TryGetValue(f.Material, out int mi))
            {
                var m = f.Material >= 0 && f.Material < work.Materials.Count ? work.Materials[f.Material] : new XMaterial();
                target.Materials.Add(m.Clone());
                materialMap[f.Material] = mi = target.Materials.Count - 1;
            }
            var idx = new int[f.Indices.Length];
            for (int j = 0; j < idx.Length; j++)
            {
                int v = f.Indices[j];
                if (!vertexMap.TryGetValue(v, out int nv))
                    vertexMap[v] = nv = target.AddVertex(work.Positions[v],
                        work.HasUV ? work.TexCoords[v] : null,
                        work.VertexColors.Count == work.Positions.Count && work.VertexColors.Count > 0 ? work.VertexColors[v] : null);
                idx[j] = nv;
            }
            target.Faces.Add(new XFace(idx, mi));
        }
    }

    /// <summary>
    /// <paramref name="side"/> が 0 以上の側だけを残す（<see cref="Symmetry"/> の対称化と同じ切り方）。
    /// 切り目に乗った面は、法線の Z が <paramref name="keepCoplanarFacing"/> と同じ向きのものだけ残す。
    /// </summary>
    private static void Clip(XMesh mesh, Func<int, float> side, int keepCoplanarFacing)
    {
        bool hasUv = mesh.HasUV;
        bool hasColor = mesh.VertexColors.Count == mesh.Positions.Count && mesh.VertexColors.Count > 0;
        var cuts = new Dictionary<(int, int), int>();

        int Cut(int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            if (cuts.TryGetValue(key, out int v)) return v;
            float da = side(a), db = side(b);
            float t = da / (da - db);
            var p = Vector3.Lerp(mesh.Positions[a], mesh.Positions[b], t);
            Vector2? uv = hasUv ? Vector2.Lerp(mesh.TexCoords[a], mesh.TexCoords[b], t) : null;
            Vector4? color = hasColor ? Vector4.Lerp(mesh.VertexColors[a], mesh.VertexColors[b], t) : null;
            return cuts[key] = mesh.AddVertex(p, uv, color);
        }

        var kept = new List<XFace>();
        foreach (var f in mesh.Faces)
        {
            var idx = f.Indices;
            if (idx.All(v => side(v) == 0))
            {
                if (MathF.Sign(mesh.FaceNormal(f).Z) == keepCoplanarFacing) kept.Add(f);
                continue;
            }
            if (idx.All(v => side(v) >= 0)) { kept.Add(f); continue; }
            if (idx.All(v => side(v) <= 0)) continue;

            var clipped = new List<int>();
            for (int j = 0; j < idx.Length; j++)
            {
                int a = idx[j], b = idx[(j + 1) % idx.Length];
                float da = side(a), db = side(b);
                if (da >= 0) clipped.Add(a);
                if ((da > 0 && db < 0) || (da < 0 && db > 0)) clipped.Add(Cut(a, b));
            }
            if (clipped.Count >= 3) kept.Add(new XFace(clipped.ToArray(), f.Material));
        }
        mesh.Faces.Clear();
        mesh.Faces.AddRange(kept);
    }

    /// <summary>ピースを 1 つずつ .x に書き出す。戻り値は 1 件ずつの結果。</summary>
    public static List<BatchFileResult> Save(IReadOnlyList<RepeaterPiece> pieces, string? sourceDirectory,
        string outputDirectory, string baseName, XWriteOptions options)
    {
        var results = new List<BatchFileResult>();
        foreach (var piece in pieces)
        {
            var path = Path.Combine(outputDirectory, $"{baseName}_{piece.Index:00}.x");
            try
            {
                var scene = new XScene();
                var mesh = piece.Mesh.Clone();
                scene.Meshes.Add(mesh);
                // 相対パスのテクスチャが出力先からも同じ画像を指すように直す
                if (sourceDirectory != null) TexturePaths.Rebase(scene.Meshes, sourceDirectory, outputDirectory);
                Directory.CreateDirectory(outputDirectory);
                XFile.Save(path, scene, options);
                results.Add(new BatchFileResult(mesh.Name, path, true,
                    $"{Path.GetFileName(path)}（{mesh.Faces.Count} 面・{piece.CutLength:0.###} m）"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XFormatException
                                           or NotSupportedException or ArgumentException)
            {
                results.Add(new BatchFileResult(piece.Mesh.Name, null, false, ex.Message));
            }
        }
        return results;
    }
}
