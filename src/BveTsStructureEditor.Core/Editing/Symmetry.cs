using System.Numerics;
using BveTsStructureEditor.Core.Model;

namespace BveTsStructureEditor.Core.Editing;

/// <summary>
/// 対称モードで「選択と一緒に鏡像で動かす頂点」。変形を始める前の位置から作る。
/// </summary>
public sealed class MirrorPlan
{
    public static readonly MirrorPlan Empty = new();

    /// <summary>反対側にある、選択に入っていない頂点（メッシュ番号 → 頂点番号）。</summary>
    public Dictionary<int, HashSet<int>> Targets { get; } = [];
    /// <summary>中心 (X=0) にある選択頂点。変形後も X=0 に留める。</summary>
    public Dictionary<int, HashSet<int>> Center { get; } = [];
    /// <summary>反対側に対応する頂点が見つからなかった選択頂点の数。</summary>
    public int Unmatched { get; internal set; }

    public int TargetCount => Targets.Values.Sum(s => s.Count);
    public IEnumerable<int> Meshes => Targets.Keys;
}

/// <summary>X=0 の面（BVE の左右）での対称編集。</summary>
public static class Symmetry
{
    public const float DefaultEpsilon = 0.001f;

    private static readonly Matrix4x4 MirrorX = Matrix4x4.CreateScale(-1, 1, 1);

    // ───────── 対称モード ─────────

    /// <summary>選択頂点ごとに、(-x, y, z) から <paramref name="eps"/> 以内の頂点を全メッシュから探す。</summary>
    public static MirrorPlan Plan(XScene scene, Selection sel, float eps = DefaultEpsilon)
    {
        var selected = SelectionQuery.Vertices(scene, sel, emptyMeansAll: false);
        if (selected.Count == 0) return MirrorPlan.Empty;

        float inv = 1f / MathF.Max(eps, 1e-6f);
        (long, long, long) Key(Vector3 p) => ((long)MathF.Floor(p.X * inv), (long)MathF.Floor(p.Y * inv), (long)MathF.Floor(p.Z * inv));
        var grid = new Dictionary<(long, long, long), List<(int M, int V)>>();
        for (int m = 0; m < scene.Meshes.Count; m++)
        {
            var positions = scene.Meshes[m].Positions;
            for (int v = 0; v < positions.Count; v++)
            {
                var k = Key(positions[v]);
                (grid.TryGetValue(k, out var b) ? b : grid[k] = []).Add((m, v));
            }
        }

        var plan = new MirrorPlan();
        foreach (var (m, verts) in selected)
        {
            var positions = scene.Meshes[m].Positions;
            foreach (var v in verts)
            {
                var p = positions[v];
                if (MathF.Abs(p.X) < eps)
                {
                    (plan.Center.TryGetValue(m, out var c) ? c : plan.Center[m] = []).Add(v);
                    continue;
                }
                var q = new Vector3(-p.X, p.Y, p.Z);
                var key = Key(q);
                bool found = false;
                for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                for (long dz = -1; dz <= 1; dz++)
                {
                    if (!grid.TryGetValue((key.Item1 + dx, key.Item2 + dy, key.Item3 + dz), out var bucket)) continue;
                    foreach (var (om, ov) in bucket)
                    {
                        if (Vector3.Distance(scene.Meshes[om].Positions[ov], q) > eps) continue;
                        found = true;
                        // 反対側も選ばれていれば、そちらはそのまま行列が掛かる
                        if (selected.TryGetValue(om, out var s) && s.Contains(ov)) continue;
                        (plan.Targets.TryGetValue(om, out var t) ? t : plan.Targets[om] = []).Add(ov);
                    }
                }
                if (!found) plan.Unmatched++;
            }
        }
        return plan;
    }

    /// <summary>選択に行列を掛け、反対側の頂点には鏡像の行列を掛ける。</summary>
    public static string Transform(XScene scene, Selection sel, Matrix4x4 matrix, MirrorPlan plan)
    {
        var status = MeshOps.Transform(scene, sel, matrix);
        if (plan.TargetCount == 0)
            return plan.Unmatched > 0 ? $"{status}（反対側の頂点が見つかりません）" : status;

        var mirrored = MirrorX * matrix * MirrorX;
        bool flip = matrix.GetDeterminant() < 0;
        foreach (var (m, verts) in plan.Targets)
        {
            var mesh = scene.Meshes[m];
            foreach (var v in verts) mesh.Positions[v] = Vector3.Transform(mesh.Positions[v], mirrored);
            mesh.ClearNormals();
            if (flip)
                foreach (var f in mesh.Faces)
                    if (f.Indices.All(verts.Contains)) MeshOps.Reverse(f);
        }
        // 片側だけを動かしたときは、中心の頂点を中心から離さない（全体を選んだときは対象がないのでここに来ない）
        foreach (var (m, verts) in plan.Center)
        {
            var positions = scene.Meshes[m].Positions;
            foreach (var v in verts) positions[v] = positions[v] with { X = 0 };
        }
        return $"{status}（反対側 {plan.TargetCount} 頂点も）" + (plan.Unmatched > 0 ? $"、対応なし {plan.Unmatched} 頂点" : "");
    }

    // ───────── 対称化 ─────────

    /// <summary>
    /// 片側を残して X=0 で切り、残した側の鏡像で反対側を作り直す。
    /// 対象は選択のあるメッシュ（空なら全体）。中心線上の頂点は左右で共有する。
    /// </summary>
    public static string Symmetrize(XScene scene, Selection sel, bool keepPositive, float eps = DefaultEpsilon)
    {
        int meshes = 0, before = 0, after = 0;
        foreach (var m in SelectionQuery.Meshes(scene, sel).Order())
        {
            var mesh = scene.Meshes[m];
            if (mesh.Faces.Count == 0) continue;
            before += mesh.Faces.Count;
            SymmetrizeMesh(mesh, keepPositive ? 1f : -1f, eps);
            after += mesh.Faces.Count;
            meshes++;
        }
        if (sel.Mode == SelectMode.Object) sel.Validate(scene);
        else sel.Clear();
        return meshes == 0
            ? "対称化する面がありません"
            : $"{meshes} メッシュを対称化しました（{(keepPositive ? "+X" : "-X")} 側を残す、面 {before} → {after}）";
    }

    private static void SymmetrizeMesh(XMesh mesh, float sign, float eps)
    {
        bool hasUv = mesh.HasUV;
        bool hasColor = mesh.VertexColors.Count == mesh.Positions.Count && mesh.VertexColors.Count > 0;

        // 中心付近を X=0 に揃えてから切ると、細い切れ端ができない
        for (int i = 0; i < mesh.Positions.Count; i++)
            if (MathF.Abs(mesh.Positions[i].X) < eps) mesh.Positions[i] = mesh.Positions[i] with { X = 0 };

        float Side(int v) => mesh.Positions[v].X * sign;
        var cuts = new Dictionary<(int, int), int>();
        int Cut(int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            if (cuts.TryGetValue(key, out int v)) return v;
            float da = Side(a), db = Side(b);
            float t = da / (da - db);
            var p = Vector3.Lerp(mesh.Positions[a], mesh.Positions[b], t) with { X = 0 };
            Vector2? uv = hasUv ? Vector2.Lerp(mesh.TexCoords[a], mesh.TexCoords[b], t) : null;
            Vector4? color = hasColor ? Vector4.Lerp(mesh.VertexColors[a], mesh.VertexColors[b], t) : null;
            return cuts[key] = mesh.AddVertex(p, uv, color);
        }

        var kept = new List<XFace>();
        foreach (var f in mesh.Faces)
        {
            var idx = f.Indices;
            if (idx.All(v => Side(v) >= 0)) { kept.Add(f); continue; }
            if (idx.All(v => Side(v) <= 0)) continue;

            // 残す側だけを切り出す（Sutherland–Hodgman）。巻き順はそのまま
            var clipped = new List<int>();
            for (int j = 0; j < idx.Length; j++)
            {
                int a = idx[j], b = idx[(j + 1) % idx.Length];
                float da = Side(a), db = Side(b);
                if (da >= 0) clipped.Add(a);
                if ((da > 0 && db < 0) || (da < 0 && db > 0)) clipped.Add(Cut(a, b));
            }
            if (clipped.Count >= 3) kept.Add(new XFace(clipped.ToArray(), f.Material));
        }

        // 鏡像。中心の頂点は共有し、面は裏返らないよう並びを逆にする
        var mirror = new Dictionary<int, int>();
        int Mirror(int v)
        {
            if (mesh.Positions[v].X == 0) return v;
            if (mirror.TryGetValue(v, out int mv)) return mv;
            var p = mesh.Positions[v];
            return mirror[v] = mesh.AddVertex(p with { X = -p.X },
                hasUv ? mesh.TexCoords[v] : null, hasColor ? mesh.VertexColors[v] : null);
        }
        var result = new List<XFace>(kept.Count * 2);
        foreach (var f in kept)
        {
            f.NormalIndices = null;
            result.Add(f);
        }
        foreach (var f in kept)
        {
            if (f.Indices.All(v => mesh.Positions[v].X == 0)) continue; // 中心の面そのものは 1 枚で足りる
            var idx = f.Indices.Select(Mirror).Reverse().ToArray();
            result.Add(new XFace(idx, f.Material));
        }

        mesh.Faces.Clear();
        mesh.Faces.AddRange(result);
        mesh.ClearNormals();
        MeshOps.CleanDegenerate(mesh);
        MeshOps.RemoveUnusedVertices(mesh);
    }
}
