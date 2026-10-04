using System.Numerics;
using BveTsStructureEditor.Core.Model;

namespace BveTsStructureEditor.Core.Editing;

/// <summary>
/// 面を増やす編集（両面化・インセット・配列複製・分割・ループカット）。
/// どれもシーンをその場で書き換え、ステータスバーに出す文字列を返す。
/// </summary>
public static class FaceOps
{
    // ───────── 共通 ─────────

    private static (long, long, long) PosKey(Vector3 p) =>
        ((long)MathF.Round(p.X * 1e4f), (long)MathF.Round(p.Y * 1e4f), (long)MathF.Round(p.Z * 1e4f));

    private static bool HasColor(XMesh mesh) => mesh.VertexColors.Count == mesh.Positions.Count && mesh.VertexColors.Count > 0;

    /// <summary>既存の頂点を重みづけで混ぜた新しい頂点を足す（位置・UV・頂点カラー）。</summary>
    private static int AddBlend(XMesh mesh, ReadOnlySpan<(int V, float W)> parts, Vector3? position = null)
    {
        var p = Vector3.Zero;
        var uv = Vector2.Zero;
        var color = Vector4.Zero;
        bool hasUv = mesh.HasUV, hasColor = HasColor(mesh);
        foreach (var (v, w) in parts)
        {
            p += mesh.Positions[v] * w;
            if (hasUv) uv += mesh.TexCoords[v] * w;
            if (hasColor) color += mesh.VertexColors[v] * w;
        }
        return mesh.AddVertex(position ?? p, hasUv ? uv : null, hasColor ? color : null);
    }

    private static int CopyVertex(XMesh mesh, int v, Vector3 position) =>
        mesh.AddVertex(position, mesh.HasUV ? mesh.TexCoords[v] : null, HasColor(mesh) ? mesh.VertexColors[v] : null);

    // ───────── 両面化 ─────────

    /// <summary>
    /// 対象の面に、頂点を複製して裏向きにした面を足す（BVE は裏面を描かないため、看板や柵を両側から見えるようにする）。
    /// すでに裏向きの面が同じ位置に重なっているものは飛ばす。
    /// </summary>
    public static string DoubleSide(XScene scene, Selection sel)
    {
        int added = 0, skipped = 0;
        foreach (var (m, faces) in SelectionQuery.Faces(scene, sel))
        {
            var mesh = scene.Meshes[m];
            var existing = new HashSet<string>();
            foreach (var f in mesh.Faces) existing.Add(Signature(mesh, f.Indices, reverse: false));
            foreach (var fi in faces.Order().ToList())
            {
                var f = mesh.Faces[fi];
                var back = Signature(mesh, f.Indices, reverse: true);
                if (existing.Contains(back)) { skipped++; continue; }
                var idx = f.Indices.Reverse().Select(v => CopyVertex(mesh, v, mesh.Positions[v])).ToArray();
                mesh.Faces.Add(new XFace(idx, f.Material));
                existing.Add(back);
                added++;
            }
            if (faces.Count > 0) mesh.ClearNormals();
        }
        return $"{added} 面に裏面を付けました" + (skipped > 0 ? $"（すでに両面の {skipped} 面は飛ばしました）" : "");
    }

    /// <summary>面の頂点位置の並び（回転をそろえた文字列）。向きも区別する。</summary>
    private static string Signature(XMesh mesh, int[] idx, bool reverse)
    {
        var keys = idx.Select(v => PosKey(mesh.Positions[v])).ToList();
        if (reverse) keys.Reverse();
        int start = 0;
        for (int i = 1; i < keys.Count; i++)
            if (keys[i].CompareTo(keys[start]) < 0) start = i;
        return string.Join("|", Enumerable.Range(0, keys.Count).Select(i => keys[(start + i) % keys.Count]));
    }

    // ───────── インセット ─────────

    /// <summary>
    /// 選んだ面の内側に一回り小さい面を作り、外周を四角形の枠でつなぐ（面ごと）。
    /// 選択は内側の面のまま残るので、続けて押し出すと窓や扉の凹みになる。
    /// </summary>
    public static string Inset(XScene scene, Selection sel, float distance)
    {
        if (distance <= 0) return "インセットの距離は 0 より大きくしてください";
        int count = 0;
        foreach (var (m, faces) in SelectionQuery.Faces(scene, sel, emptyMeansAll: false))
        {
            var mesh = scene.Meshes[m];
            foreach (var fi in faces.Order().ToList())
            {
                var f = mesh.Faces[fi];
                int n = f.Indices.Length;
                if (n < 3) continue;
                var pts = f.Indices.Select(v => mesh.Positions[v]).ToArray();
                var normal = mesh.FaceNormal(f);
                if (normal.LengthSquared() < 1e-12f) continue;
                var centroid = mesh.FaceCenter(f);

                // 各辺の内向きの単位ベクトル。向きは面全体で見て中心側にそろえる
                var inward = new Vector3[n];
                float vote = 0;
                for (int i = 0; i < n; i++)
                {
                    var e = pts[(i + 1) % n] - pts[i];
                    var d = Vector3.Cross(normal, e);
                    inward[i] = d.LengthSquared() > 1e-12f ? Vector3.Normalize(d) : Vector3.Zero;
                    vote += Vector3.Dot(inward[i], centroid - (pts[i] + pts[(i + 1) % n]) / 2);
                }
                if (vote < 0)
                    for (int i = 0; i < n; i++) inward[i] = -inward[i];

                var inner = new int[n];
                for (int i = 0; i < n; i++)
                {
                    var a = inward[(i + n - 1) % n];
                    var b = inward[i];
                    float denom = 1 + Vector3.Dot(a, b);
                    var offset = denom > 1e-3f ? (a + b) * (distance / denom) : b * distance;
                    // 中心を越えないように抑える
                    var toCenter = centroid - pts[i];
                    float limit = toCenter.Length() * 0.95f;
                    if (offset.Length() > limit && offset.LengthSquared() > 0) offset = Vector3.Normalize(offset) * limit;
                    var q = pts[i] + offset;
                    inner[i] = AddAffine(mesh, f.Indices, i, q);
                }

                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    mesh.Faces.Add(new XFace([f.Indices[i], f.Indices[j], inner[j], inner[i]], f.Material));
                }
                mesh.Faces[fi] = new XFace(inner, f.Material);
                count++;
            }
            if (faces.Count > 0) mesh.ClearNormals();
        }
        return count == 0 ? "インセットする面を選んでください" : $"{count} 面をインセットしました（選択は内側の面）";
    }

    /// <summary>
    /// 面の中の点 <paramref name="q"/> に頂点を作る。UV・色は、角 i とその両隣の 3 点から作った平面上の比率で決める
    /// （面に平らに貼った UV ならぴったり合う）。
    /// </summary>
    private static int AddAffine(XMesh mesh, int[] idx, int i, Vector3 q)
    {
        int n = idx.Length;
        int a = idx[(i + n - 1) % n], b = idx[i], c = idx[(i + 1) % n];
        var pa = mesh.Positions[a];
        var v0 = mesh.Positions[b] - pa;
        var v1 = mesh.Positions[c] - pa;
        var v2 = q - pa;
        float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1), d11 = Vector3.Dot(v1, v1);
        float d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1);
        float denom = d00 * d11 - d01 * d01;
        if (MathF.Abs(denom) < 1e-12f)
            return CopyVertex(mesh, b, q);
        float wb = (d11 * d20 - d01 * d21) / denom;
        float wc = (d00 * d21 - d01 * d20) / denom;
        return AddBlend(mesh, [(a, 1 - wb - wc), (b, wb), (c, wc)], q);
    }

    // ───────── 配列複製 ─────────

    /// <summary>対象の面を、<paramref name="count"/> 個（元を含む）になるよう <paramref name="offset"/> ずつずらして複製する。</summary>
    public static string ArrayLinear(XScene scene, Selection sel, int count, Vector3 offset) =>
        Array(scene, sel, count, k => Matrix4x4.CreateTranslation(offset * k), $"間隔 ({offset.X:0.###}, {offset.Y:0.###}, {offset.Z:0.###}) m");

    /// <summary>
    /// 対象の面を、<paramref name="center"/> を通る縦軸 (Y) まわりに <paramref name="count"/> 個（元を含む）並べる。
    /// <paramref name="totalAngleDeg"/> が 360 なら一周を等分し、それ以外は最初と最後がその角度だけ離れる。
    /// </summary>
    public static string ArrayRadial(XScene scene, Selection sel, int count, float totalAngleDeg, Vector3 center)
    {
        bool full = MathF.Abs(MathF.Abs(totalAngleDeg) - 360f) < 1e-3f;
        float step = count <= 1 ? 0 : (full ? totalAngleDeg / count : totalAngleDeg / (count - 1));
        return Array(scene, sel, count, k => GizmoMath.Rotation(Vector3.UnitY, step * k * MathF.PI / 180f, center), $"{step:0.###}° ずつ");
    }

    private static string Array(XScene scene, Selection sel, int count, Func<int, Matrix4x4> transformOf, string what)
    {
        if (count < 2) return "個数は 2 以上にしてください";
        if (count > 1000) return "個数が多すぎます（1000 個まで）";
        int faces = 0;
        foreach (var (m, faceSet) in SelectionQuery.Faces(scene, sel))
        {
            if (faceSet.Count == 0) continue;
            var mesh = scene.Meshes[m];
            var source = faceSet.Order().Select(i => mesh.Faces[i]).ToList();
            var verts = source.SelectMany(f => f.Indices).Distinct().ToList();
            for (int k = 1; k < count; k++)
            {
                var matrix = transformOf(k);
                bool flip = matrix.GetDeterminant() < 0;
                var map = new Dictionary<int, int>();
                foreach (var v in verts) map[v] = CopyVertex(mesh, v, Vector3.Transform(mesh.Positions[v], matrix));
                foreach (var f in source)
                {
                    var idx = f.Indices.Select(v => map[v]).ToArray();
                    if (flip) System.Array.Reverse(idx);
                    mesh.Faces.Add(new XFace(idx, f.Material));
                    faces++;
                }
            }
            mesh.ClearNormals();
        }
        return faces == 0 ? "複製する面がありません" : $"{count} 個に並べました（{what}、+{faces} 面）";
    }

    // ───────── 分割 ─────────

    /// <summary>選んだ四角形を <paramref name="u"/>×<paramref name="v"/> の格子に分ける。u は 1〜2 番目の角の辺の方向。</summary>
    public static string Subdivide(XScene scene, Selection sel, int u, int v)
    {
        if (u < 1 || v < 1 || (u == 1 && v == 1)) return "分割数は 1 以上で、どちらかを 2 以上にしてください";
        if (u * v > 10000) return "分割数が多すぎます";
        int done = 0, skipped = 0;
        foreach (var (m, faceSet) in SelectionQuery.Faces(scene, sel))
        {
            var mesh = scene.Meshes[m];
            // 隣り合う面で辺の上の点を共有する（頂点番号の組ごと）
            var edgePoints = new Dictionary<(int, int, int, int), int>();
            int EdgePoint(int a, int b, int n, int k)
            {
                if (k == 0) return a;
                if (k == n) return b;
                var key = a < b ? (a, b, n, k) : (b, a, n, n - k);
                if (edgePoints.TryGetValue(key, out int e)) return e;
                float t = (float)k / n;
                return edgePoints[key] = AddBlend(mesh, [(a, 1 - t), (b, t)]);
            }

            var result = new List<XFace>(mesh.Faces.Count);
            for (int fi = 0; fi < mesh.Faces.Count; fi++)
            {
                var f = mesh.Faces[fi];
                if (!faceSet.Contains(fi)) { result.Add(f); continue; }
                if (f.Indices.Length != 4) { result.Add(f); skipped++; continue; }
                int c0 = f.Indices[0], c1 = f.Indices[1], c2 = f.Indices[2], c3 = f.Indices[3];
                var grid = new int[u + 1, v + 1];
                for (int i = 0; i <= u; i++)
                {
                    grid[i, 0] = EdgePoint(c0, c1, u, i);
                    grid[i, v] = EdgePoint(c3, c2, u, i);
                }
                for (int j = 0; j <= v; j++)
                {
                    grid[0, j] = EdgePoint(c0, c3, v, j);
                    grid[u, j] = EdgePoint(c1, c2, v, j);
                }
                for (int i = 1; i < u; i++)
                    for (int j = 1; j < v; j++)
                    {
                        float s = (float)i / u, t = (float)j / v;
                        grid[i, j] = AddBlend(mesh, [(c0, (1 - s) * (1 - t)), (c1, s * (1 - t)), (c2, s * t), (c3, (1 - s) * t)]);
                    }
                for (int j = 0; j < v; j++)
                    for (int i = 0; i < u; i++)
                        result.Add(new XFace([grid[i, j], grid[i + 1, j], grid[i + 1, j + 1], grid[i, j + 1]], f.Material));
                done++;
            }
            if (result.Count != mesh.Faces.Count)
            {
                mesh.Faces.Clear();
                mesh.Faces.AddRange(result);
                mesh.ClearNormals();
            }
        }
        sel.Validate(scene);
        if (sel.Mode == SelectMode.Face) sel.Clear();
        return done == 0 ? "分割できる四角形がありません" : $"{done} 面を {u}×{v} に分割しました" + (skipped > 0 ? $"（四角形でない {skipped} 面は飛ばしました）" : "");
    }

    // ───────── ループカット ─────────

    /// <summary>
    /// 頂点選択で辺の両端の 2 頂点を選び、その辺と交わる四角形の帯をぐるっとたどって <paramref name="cuts"/> 本の切れ目を入れる。
    /// 帯は四角形でない面か、一周したところで止まる。UV の継ぎ目で頂点が分かれていても位置で見てつなぐ。
    /// </summary>
    public static string LoopCut(XScene scene, Selection sel, int cuts)
    {
        if (cuts < 1 || cuts > 100) return "切れ目の本数は 1〜100 にしてください";
        if (sel.Mode != SelectMode.Vertex || sel.Count != 2 || sel.Items[0].Mesh != sel.Items[1].Mesh)
            return "頂点選択で、切れ目と交わる辺の両端 2 頂点を選んでください";
        int m = sel.Items[0].Mesh;
        var mesh = scene.Meshes[m];
        var k0 = PosKey(mesh.Positions[sel.Items[0].Index]);
        var k1 = PosKey(mesh.Positions[sel.Items[1].Index]);

        ((long, long, long), (long, long, long)) EdgeKey(int a, int b)
        {
            var ka = PosKey(mesh.Positions[a]);
            var kb = PosKey(mesh.Positions[b]);
            return ka.CompareTo(kb) <= 0 ? (ka, kb) : (kb, ka);
        }

        var quadsByEdge = new Dictionary<((long, long, long), (long, long, long)), List<(int Face, int Edge)>>();
        for (int fi = 0; fi < mesh.Faces.Count; fi++)
        {
            var idx = mesh.Faces[fi].Indices;
            if (idx.Length != 4) continue;
            for (int j = 0; j < 4; j++)
            {
                var key = EdgeKey(idx[j], idx[(j + 1) % 4]);
                (quadsByEdge.TryGetValue(key, out var l) ? l : quadsByEdge[key] = []).Add((fi, j));
            }
        }

        var start = k0.CompareTo(k1) <= 0 ? (k0, k1) : (k1, k0);
        if (!quadsByEdge.TryGetValue(start, out var startFaces))
            return "選んだ 2 頂点を辺に持つ四角形がありません（対角の頂点ではなく、辺の両端を選んでください）";

        // 帯をたどる。面ごとに「切れ目と交わる辺」の番号を覚える
        var entry = new Dictionary<int, int>();
        var queue = new Queue<(int Face, int Edge)>(startFaces);
        while (queue.Count > 0)
        {
            var (fi, j) = queue.Dequeue();
            if (entry.ContainsKey(fi)) continue;
            entry[fi] = j;
            var idx = mesh.Faces[fi].Indices;
            foreach (var edge in new[] { j, (j + 2) % 4 })
                foreach (var next in quadsByEdge[EdgeKey(idx[edge], idx[(edge + 1) % 4])])
                    if (!entry.ContainsKey(next.Face)) queue.Enqueue(next);
        }

        var edgePoints = new Dictionary<(int, int, int), int>();
        int Point(int a, int b, int k) // a→b を cuts+1 等分した k 番目
        {
            if (k == 0) return a;
            if (k == cuts + 1) return b;
            var key = a < b ? (a, b, k) : (b, a, cuts + 1 - k);
            if (edgePoints.TryGetValue(key, out int e)) return e;
            float t = (float)k / (cuts + 1);
            return edgePoints[key] = AddBlend(mesh, [(a, 1 - t), (b, t)]);
        }

        var result = new List<XFace>();
        for (int fi = 0; fi < mesh.Faces.Count; fi++)
        {
            var f = mesh.Faces[fi];
            if (!entry.TryGetValue(fi, out int j)) { result.Add(f); continue; }
            int v0 = f.Indices[j], v1 = f.Indices[(j + 1) % 4], v2 = f.Indices[(j + 2) % 4], v3 = f.Indices[(j + 3) % 4];
            for (int k = 0; k <= cuts; k++)
            {
                result.Add(new XFace([Point(v0, v1, k), Point(v0, v1, k + 1), Point(v3, v2, k + 1), Point(v3, v2, k)], f.Material));
            }
        }
        mesh.Faces.Clear();
        mesh.Faces.AddRange(result);
        mesh.ClearNormals();
        return $"{entry.Count} 面に {cuts} 本の切れ目を入れました";
    }
}
