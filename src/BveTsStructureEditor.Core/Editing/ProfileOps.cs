using System.Numerics;
using BveTsStructureEditor.Core.Model;

namespace BveTsStructureEditor.Core.Editing;

/// <summary>
/// クリックで描いた断面・輪郭から形を作る（押し出し・回転体）。
/// 蓋は耳切り法で三角形に分ける（レンダラも BVE も多角形を扇形に分けるので、へこんだ多角形のままだと崩れる）。
/// 面の表は、作ったあとに「外向き」と比べてそろえる。
/// </summary>
public static class ProfileOps
{
    // ───────── 三角形分割 ─────────

    /// <summary>
    /// 単純多角形（自己交差なし、時計回り・反時計回りどちらでも）を三角形に分ける。
    /// 返す三角形は入力の番号で、入力と同じ回り方。分けられなければ null。
    /// </summary>
    public static List<(int A, int B, int C)>? Triangulate(IReadOnlyList<Vector2> polygon)
    {
        int n = polygon.Count;
        if (n < 3) return null;
        float area = SignedArea(polygon);
        if (MathF.Abs(area) < 1e-9f) return null;
        float sign = MathF.Sign(area);

        var remaining = Enumerable.Range(0, n).ToList();
        var result = new List<(int, int, int)>();
        int guard = 0;
        while (remaining.Count > 3 && guard++ < n * n)
        {
            bool clipped = false;
            for (int i = 0; i < remaining.Count; i++)
            {
                int ia = remaining[(i + remaining.Count - 1) % remaining.Count];
                int ib = remaining[i];
                int ic = remaining[(i + 1) % remaining.Count];
                var a = polygon[ia];
                var b = polygon[ib];
                var c = polygon[ic];
                float cross = Cross(b - a, c - b) * sign;
                if (cross < -1e-9f) continue; // へこんだ角
                if (cross <= 1e-9f)
                {
                    // 一直線に並んだ点は取り除く
                    remaining.RemoveAt(i);
                    clipped = true;
                    break;
                }
                bool containsOther = false;
                foreach (var j in remaining)
                {
                    if (j == ia || j == ib || j == ic) continue;
                    if (InTriangle(polygon[j], a, b, c, sign)) { containsOther = true; break; }
                }
                if (containsOther) continue;
                result.Add((ia, ib, ic));
                remaining.RemoveAt(i);
                clipped = true;
                break;
            }
            if (!clipped) return null; // 自己交差など
        }
        if (remaining.Count == 3)
        {
            var (a, b, c) = (polygon[remaining[0]], polygon[remaining[1]], polygon[remaining[2]]);
            if (MathF.Abs(Cross(b - a, c - b)) > 1e-9f) result.Add((remaining[0], remaining[1], remaining[2]));
        }
        return result.Count > 0 ? result : null;
    }

    public static float SignedArea(IReadOnlyList<Vector2> p)
    {
        float s = 0;
        for (int i = 0; i < p.Count; i++) s += Cross(p[i], p[(i + 1) % p.Count]);
        return s / 2;
    }

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    private static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c, float sign) =>
        Cross(b - a, p - a) * sign >= -1e-9f && Cross(c - b, p - b) * sign >= -1e-9f && Cross(a - c, p - c) * sign >= -1e-9f;

    // ───────── 押し出し ─────────

    /// <summary>
    /// 平面上の多角形 <paramref name="points"/> を <paramref name="extrude"/> の向き・長さに押し出した閉じた形。
    /// UV は 1 m = 1（側面は周に沿った長さ×高さ、蓋は平面への投影）。
    /// </summary>
    public static XMesh ExtrudePolygon(IReadOnlyList<Vector3> points, Vector3 extrude, string name = "Extrude")
    {
        var pts = Dedupe(points);
        if (pts.Count < 3) throw new InvalidOperationException("3 点以上の多角形を描いてください");
        if (extrude.LengthSquared() < 1e-12f) throw new InvalidOperationException("押し出す長さが 0 です");

        var normal = Newell(pts);
        if (normal.LengthSquared() < 1e-12f) throw new InvalidOperationException("多角形の点が一直線に並んでいます");
        normal = Vector3.Normalize(normal);
        var (u, v) = GizmoMath.Perpendiculars(normal);
        var flat = pts.Select(p => new Vector2(Vector3.Dot(p, u), Vector3.Dot(p, v))).ToList();
        var tris = Triangulate(flat) ?? throw new InvalidOperationException("多角形を三角形に分けられません（線が交差していないか確認してください）");

        var mesh = new XMesh { Name = name };
        mesh.Materials.Add(new XMaterial());
        var up = Vector3.Normalize(extrude);

        // 蓋（下は押し出しと逆向き、上は押し出しの向きが表）
        foreach (var (cap, offset, outward) in new[] { (0, Vector3.Zero, -up), (1, extrude, up) })
            foreach (var (a, b, c) in tris)
            {
                int[] idx = [a, b, c];
                var face = idx.Select(i => mesh.AddVertex(pts[i] + offset, flat[i])).ToArray();
                AddOriented(mesh, face, outward);
            }

        // 側面。多角形の回り方（Newell の法線に対して反時計回り）から外向きを決める
        float along = 0;
        float height = extrude.Length();
        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            float len = Vector3.Distance(a, b);
            if (len < 1e-6f) continue;
            var outward = Vector3.Cross(b - a, normal);
            int i0 = mesh.AddVertex(a, new Vector2(along, height));
            int i1 = mesh.AddVertex(b, new Vector2(along + len, height));
            int i2 = mesh.AddVertex(b + extrude, new Vector2(along + len, 0));
            int i3 = mesh.AddVertex(a + extrude, new Vector2(along, 0));
            AddOriented(mesh, [i0, i1, i2, i3], outward);
            along += len;
        }
        return mesh;
    }

    // ───────── 回転体 ─────────

    /// <summary>
    /// X-Y 平面の輪郭（X が軸からの距離）を Y 軸まわりに回した形。X が 0 の点は軸上で 1 点にまとめる。
    /// <paramref name="angleDeg"/> が 360 未満なら扇形に開いた形。
    /// </summary>
    public static XMesh Lathe(IReadOnlyList<Vector2> profile, int segments, float angleDeg = 360, string name = "Lathe")
    {
        var pts = profile.Select(p => p with { X = MathF.Abs(p.X) < 1e-4f ? 0 : MathF.Abs(p.X) }).ToList();
        for (int i = pts.Count - 1; i > 0; i--)
            if (Vector2.Distance(pts[i], pts[i - 1]) < 1e-5f) pts.RemoveAt(i);
        if (pts.Count < 2) throw new InvalidOperationException("2 点以上の輪郭を描いてください");
        if (pts.All(p => p.X == 0)) throw new InvalidOperationException("輪郭が回転軸の上にあります。軸から離れた点を描いてください");
        segments = Math.Clamp(segments, 3, 256);
        float total = Math.Clamp(angleDeg, 1, 360) * MathF.PI / 180;

        // 輪郭の外向き（2D）。軸から離れる向きが多いほうにそろえる
        float vote = 0;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var d = pts[i + 1] - pts[i];
            vote += d.Y * (pts[i].X + pts[i + 1].X); // 法線 (dy, -dx) の X 成分 × 半径
        }
        float side = vote >= 0 ? 1 : -1;

        var mesh = new XMesh { Name = name };
        mesh.Materials.Add(new XMaterial());
        var lengths = new float[pts.Count];
        for (int i = 1; i < pts.Count; i++) lengths[i] = lengths[i - 1] + Vector2.Distance(pts[i], pts[i - 1]);
        float totalLen = MathF.Max(lengths[^1], 1e-6f);

        int columns = segments + 1; // UV の継ぎ目のために最後の列も別の頂点にする
        var grid = new int[pts.Count, columns];
        for (int i = 0; i < pts.Count; i++)
        {
            int axis = -1;
            for (int j = 0; j < columns; j++)
            {
                float t = total * j / segments;
                var uv = new Vector2((float)j / segments, 1 - lengths[i] / totalLen);
                if (pts[i].X == 0)
                {
                    if (axis < 0) axis = mesh.AddVertex(new Vector3(0, pts[i].Y, 0), uv);
                    grid[i, j] = axis;
                    continue;
                }
                grid[i, j] = mesh.AddVertex(new Vector3(pts[i].X * MathF.Cos(t), pts[i].Y, pts[i].X * MathF.Sin(t)), uv);
            }
        }
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var d = pts[i + 1] - pts[i];
            var n2 = new Vector2(d.Y, -d.X) * side;
            for (int j = 0; j < segments; j++)
            {
                float t = total * (j + 0.5f) / segments;
                var outward = new Vector3(n2.X * MathF.Cos(t), n2.Y, n2.X * MathF.Sin(t));
                int[] quad = [grid[i, j], grid[i + 1, j], grid[i + 1, j + 1], grid[i, j + 1]];
                var idx = quad.Where((v, k) => v != quad[(k + 3) % 4]).ToArray(); // 軸上で重なる角を詰める
                if (idx.Length < 3) continue;
                AddOriented(mesh, idx, outward);
            }
        }
        return mesh;
    }

    // ───────── 共通 ─────────

    private static void AddOriented(XMesh mesh, int[] idx, Vector3 outward)
    {
        var face = new XFace(idx);
        if (Vector3.Dot(mesh.FaceNormal(face), outward) < 0) Array.Reverse(face.Indices);
        mesh.Faces.Add(face);
    }

    private static Vector3 Newell(IReadOnlyList<Vector3> p)
    {
        var n = Vector3.Zero;
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % p.Count];
            n += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
        }
        return n;
    }

    private static List<Vector3> Dedupe(IReadOnlyList<Vector3> points)
    {
        var list = new List<Vector3>();
        foreach (var p in points)
            if (list.Count == 0 || Vector3.Distance(list[^1], p) > 1e-5f) list.Add(p);
        if (list.Count > 1 && Vector3.Distance(list[0], list[^1]) < 1e-5f) list.RemoveAt(list.Count - 1);
        return list;
    }
}
