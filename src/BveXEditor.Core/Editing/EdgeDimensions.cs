using System.Numerics;
using BveXEditor.Core.Model;

namespace BveXEditor.Core.Editing;

public enum DimensionKind
{
    /// <summary>面の辺。</summary>
    Edge,
    /// <summary>辺でつながっていない 2 頂点の間の距離。</summary>
    Distance,
    /// <summary>外形（外接箱）の幅・高さ・奥行。</summary>
    BoxX, BoxY, BoxZ,
}

/// <summary>寸法を出す 1 本（BVE 座標の 2 点）。</summary>
public readonly record struct Dimension(Vector3 A, Vector3 B, DimensionKind Kind)
{
    public float Length => Vector3.Distance(A, B);
}

/// <summary>
/// 3D ビューに出す寸法を選択から集める。
/// 面選択: 選んだ面の辺 / 頂点選択: 両端とも選んだ辺（辺でつながっていない 2 頂点ならその距離）/
/// オブジェクト選択・選択なし: 外形の幅 (X)・高さ (Y)・奥行 (Z)。
/// 同じ位置の辺（隣り合う面の共有辺や、UV の継ぎ目で分かれた頂点）は 1 本にまとめる。
/// </summary>
public static class EdgeDimensions
{
    /// <summary>これより辺が多いときは、辺の代わりに外形の寸法を出す（画面が数字で埋まるので）。</summary>
    public const int MaxEdges = 400;

    public static List<Dimension> Collect(XScene scene, Selection sel, ISet<int>? hiddenMeshes, out bool tooMany)
    {
        tooMany = false;
        if (sel.IsEmpty || sel.Mode == SelectMode.Object)
            return Box(scene, sel, hiddenMeshes);

        var edges = new Dictionary<(Vector3, Vector3), Dimension>();
        if (sel.Mode == SelectMode.Face)
        {
            foreach (var (m, faces) in SelectionQuery.Faces(scene, sel, emptyMeansAll: false))
            {
                var mesh = scene.Meshes[m];
                foreach (var f in faces) AddFaceEdges(mesh, mesh.Faces[f], edges, null);
            }
        }
        else
        {
            var selected = SelectionQuery.Vertices(scene, sel, emptyMeansAll: false);
            foreach (var (m, verts) in selected)
            {
                var mesh = scene.Meshes[m];
                foreach (var face in mesh.Faces) AddFaceEdges(mesh, face, edges, verts);
            }
            // 2 頂点だけ選んでいて辺でつながっていなければ、その間の距離
            var points = selected.SelectMany(kv => kv.Value.Select(v => scene.Meshes[kv.Key].Positions[v])).Distinct().ToList();
            if (edges.Count == 0 && points.Count == 2 && points[0] != points[1])
                return [new Dimension(points[0], points[1], DimensionKind.Distance)];
        }

        if (edges.Count > MaxEdges)
        {
            tooMany = true;
            return Box(scene, sel, hiddenMeshes);
        }
        return [.. edges.Values];
    }

    private static void AddFaceEdges(XMesh mesh, XFace face, Dictionary<(Vector3, Vector3), Dimension> edges, HashSet<int>? onlyBetween)
    {
        var idx = face.Indices;
        for (int i = 0; i < idx.Length; i++)
        {
            int a = idx[i], b = idx[(i + 1) % idx.Length];
            if (a < 0 || b < 0 || a >= mesh.Positions.Count || b >= mesh.Positions.Count) continue;
            if (onlyBetween != null && !(onlyBetween.Contains(a) && onlyBetween.Contains(b))) continue;
            var pa = mesh.Positions[a];
            var pb = mesh.Positions[b];
            if (Vector3.DistanceSquared(pa, pb) < 1e-12f) continue;
            var ka = Key(pa);
            var kb = Key(pb);
            var key = Less(ka, kb) ? (ka, kb) : (kb, ka);
            edges.TryAdd(key, new Dimension(pa, pb, DimensionKind.Edge));
        }
    }

    /// <summary>わずかな誤差で別の辺と数えないよう、0.1 mm 単位に丸めた位置。</summary>
    private static Vector3 Key(Vector3 p) => new(MathF.Round(p.X * 1e4f), MathF.Round(p.Y * 1e4f), MathF.Round(p.Z * 1e4f));

    private static bool Less(Vector3 a, Vector3 b) =>
        a.X != b.X ? a.X < b.X : a.Y != b.Y ? a.Y < b.Y : a.Z < b.Z;

    /// <summary>
    /// 外形の寸法。手前 (−Z) の下の辺に幅、手前左の縦の辺に高さ、右 (+X) の下の辺に奥行を置く。
    /// 選択なしなら表示中のメッシュ全体。
    /// </summary>
    public static List<Dimension> Box(XScene scene, Selection sel, ISet<int>? hiddenMeshes)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var (m, verts) in SelectionQuery.Vertices(scene, sel))
        {
            if (sel.IsEmpty && hiddenMeshes?.Contains(m) == true) continue;
            var pos = scene.Meshes[m].Positions;
            foreach (var v in verts)
            {
                min = Vector3.Min(min, pos[v]);
                max = Vector3.Max(max, pos[v]);
            }
        }
        var result = new List<Dimension>();
        if (min.X > max.X) return result;
        const float eps = 1e-5f;
        if (max.X - min.X > eps)
            result.Add(new Dimension(min, min with { X = max.X }, DimensionKind.BoxX));
        if (max.Y - min.Y > eps)
            result.Add(new Dimension(min, min with { Y = max.Y }, DimensionKind.BoxY));
        if (max.Z - min.Z > eps)
            result.Add(new Dimension(new Vector3(max.X, min.Y, min.Z), new Vector3(max.X, min.Y, max.Z), DimensionKind.BoxZ));
        return result;
    }
}
