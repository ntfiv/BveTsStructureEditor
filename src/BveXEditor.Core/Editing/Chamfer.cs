using System.Numerics;
using BveXEditor.Core.Model;

namespace BveXEditor.Core.Editing;

/// <summary>
/// 面取り（1 段のベベル）。
///
/// 考え方: 面取りする辺に接する面の角を、その面のもう一方の辺に沿って <c>d</c> だけずらす。
/// 面の角の新しい位置は「元の頂点」「どの辺に沿ってずらしたか」だけで決まるので、
/// 隣り合う面が同じ点を共有でき、穴のない形になる。
/// - 角の片側の辺だけ面取り → もう片側の辺に沿って d
/// - 角の両側の辺を面取り → 両方の辺に沿って d ずつ（平行四辺形の頂点）
/// - 角のどちらの辺も面取りしないが、その頂点に面取りする辺がある → 角を 2 点に割る（角を切り落とす）
/// 面取りした辺には帯（四角形）を張り、3 本以上の面取りが集まる角に残る穴は多角形でふさぐ。
///
/// 頂点が面ごとに分かれているメッシュ（箱のプリミティブなど）でもつながりがわかるよう、
/// 同じ位置の頂点は同じ点として扱う。UV は面ごとに辺に沿って補間する。
/// </summary>
public static class Chamfer
{
    public const float DefaultMinAngle = 30f;

    public static string Apply(XScene scene, Selection sel, float distance, float minAngleDeg = DefaultMinAngle)
    {
        if (distance <= 0) return "面取りの大きさは正の数にしてください";
        int edges = 0, meshes = 0;
        bool clamped = false;
        var meshSet = SelectionQuery.Meshes(scene, sel);
        var verts = sel.Mode == SelectMode.Vertex ? SelectionQuery.Vertices(scene, sel, emptyMeansAll: false) : null;
        var faces = sel.Mode == SelectMode.Face ? SelectionQuery.Faces(scene, sel, emptyMeansAll: false) : null;
        foreach (var m in meshSet)
        {
            var (n, c) = ApplyToMesh(scene.Meshes[m],
                verts?.GetValueOrDefault(m) ?? (verts != null ? [] : null),
                faces?.GetValueOrDefault(m) ?? (faces != null ? [] : null),
                distance, minAngleDeg);
            if (n == 0) continue;
            edges += n;
            meshes++;
            clamped |= c;
        }
        if (edges == 0)
            return $"面取りできる辺がありません（{minAngleDeg:0}° 以上曲がっている、面が 2 枚つながった辺が対象）";
        sel.Clear();
        return $"{edges} 本の辺を面取りしました（メッシュ {meshes}）" + (clamped ? "。辺が短いので大きさを小さくしました" : "");
    }

    private readonly record struct PointKey(int V, int A, int B);

    /// <summary>
    /// 辺（頂点番号が同じ = UV もつながっている）を共有し、同じ平面・同じマテリアルで、
    /// 合わせると凸になる三角形 2 枚を四角形 1 枚にまとめる。面選択の番号はまとめた後の番号に付け替えて返す。
    /// </summary>
    private static HashSet<int>? MergeCoplanarTriangles(XMesh mesh, HashSet<int>? faceFilter)
    {
        var edgeOwner = new Dictionary<(int, int), List<(int Face, int Corner)>>();
        for (int f = 0; f < mesh.Faces.Count; f++)
        {
            var idx = mesh.Faces[f].Indices;
            if (idx.Length != 3) continue;
            for (int i = 0; i < 3; i++)
            {
                int a = idx[i], b = idx[(i + 1) % 3];
                var k = a < b ? (a, b) : (b, a);
                (edgeOwner.TryGetValue(k, out var l) ? l : edgeOwner[k] = []).Add((f, i));
            }
        }

        var mergedInto = new Dictionary<int, int>(); // 吸収された三角形 → 残る面
        var replacement = new Dictionary<int, int[]>();
        foreach (var (_, owners) in edgeOwner)
        {
            if (owners.Count != 2) continue;
            var (f1, c1) = owners[0];
            var (f2, c2) = owners[1];
            if (mergedInto.ContainsKey(f1) || mergedInto.ContainsKey(f2) || replacement.ContainsKey(f1) || replacement.ContainsKey(f2)) continue;
            var t1 = mesh.Faces[f1];
            var t2 = mesh.Faces[f2];
            if (t1.Material != t2.Material) continue;
            if ((faceFilter?.Contains(f1) ?? false) != (faceFilter?.Contains(f2) ?? false)) continue;
            if (Vector3.Dot(mesh.FaceNormal(t1), mesh.FaceNormal(t2)) < 0.9999f) continue;
            int x = t1.Indices[c1], y = t1.Indices[(c1 + 1) % 3], p = t1.Indices[(c1 + 2) % 3];
            if (t2.Indices[c2] != y || t2.Indices[(c2 + 1) % 3] != x) continue; // 向きが逆でない
            int q = t2.Indices[(c2 + 2) % 3];
            int[] quad = [x, q, y, p];
            if (!IsConvex(mesh.Positions, quad, mesh.FaceNormal(t1))) continue;
            replacement[f1] = quad;
            mergedInto[f2] = f1;
        }
        if (replacement.Count == 0) return faceFilter;

        var oldToNew = new Dictionary<int, int>();
        var faces = new List<XFace>();
        for (int f = 0; f < mesh.Faces.Count; f++)
        {
            if (mergedInto.ContainsKey(f)) continue;
            oldToNew[f] = faces.Count;
            var face = mesh.Faces[f];
            faces.Add(replacement.TryGetValue(f, out var quad) ? new XFace(quad, face.Material) : face);
        }
        foreach (var (absorbed, keeper) in mergedInto) oldToNew[absorbed] = oldToNew[keeper];
        mesh.Faces.Clear();
        mesh.Faces.AddRange(faces);
        foreach (var f in mesh.Faces) f.NormalIndices = null;
        return faceFilter?.Select(f => oldToNew[f]).ToHashSet();
    }

    private static bool IsConvex(IReadOnlyList<Vector3> pos, int[] poly, Vector3 normal)
    {
        for (int i = 0; i < poly.Length; i++)
        {
            var a = pos[poly[i]];
            var b = pos[poly[(i + 1) % poly.Length]];
            var c = pos[poly[(i + 2) % poly.Length]];
            // 表向きの並びでは、凸な角の Cross(b-a, c-b) は面の法線と同じ向きになる。逆向きならへこんだ角
            if (Vector3.Dot(Vector3.Cross(b - a, c - b), normal) < -1e-9f) return false;
        }
        return true;
    }

    /// <summary>1 つのメッシュを面取りする。戻り値は面取りした辺の数と、大きさを縮めたか。</summary>
    public static (int Edges, bool Clamped) ApplyToMesh(XMesh mesh, HashSet<int>? vertexFilter, HashSet<int>? faceFilter, float distance, float minAngleDeg)
    {
        if (mesh.Faces.Count == 0) return (0, false);
        bool hadUv = mesh.HasUV;

        // 0. 三角形に分割された平らな四角形（PMX や三角形化した .x に多い）を四角形に戻す。
        //    対角線が残っていると、面取りする頂点の角が対角線に沿ってもずれ、切り口が歪んで余計な穴埋めができる。
        faceFilter = MergeCoplanarTriangles(mesh, faceFilter);

        // 1. 同じ位置の頂点をまとめた「点」の番号
        var canon = new int[mesh.Positions.Count];
        var canonPos = new List<Vector3>();
        var byCell = new Dictionary<(long, long, long), int>();
        for (int i = 0; i < mesh.Positions.Count; i++)
        {
            var p = mesh.Positions[i];
            var key = ((long)MathF.Round(p.X * 1e4f), (long)MathF.Round(p.Y * 1e4f), (long)MathF.Round(p.Z * 1e4f));
            if (!byCell.TryGetValue(key, out int c))
            {
                c = canonPos.Count;
                canonPos.Add(p);
                byCell[key] = c;
            }
            canon[i] = c;
        }

        // 2. 辺 → (面, 面の中での辺の始点の位置)
        var edgeFaces = new Dictionary<(int, int), List<(int Face, int Corner)>>();
        var faceNormals = new Vector3[mesh.Faces.Count];
        var vertexNormal = new Dictionary<int, Vector3>();
        for (int f = 0; f < mesh.Faces.Count; f++)
        {
            var idx = mesh.Faces[f].Indices;
            faceNormals[f] = mesh.FaceNormal(mesh.Faces[f]);
            for (int i = 0; i < idx.Length; i++)
            {
                vertexNormal[canon[idx[i]]] = vertexNormal.GetValueOrDefault(canon[idx[i]]) + faceNormals[f];
                int a = canon[idx[i]], b = canon[idx[(i + 1) % idx.Length]];
                if (a == b) continue;
                var k = a < b ? (a, b) : (b, a);
                (edgeFaces.TryGetValue(k, out var list) ? list : edgeFaces[k] = []).Add((f, i));
            }
        }

        // 3. 面取りする辺: 面がちょうど 2 枚（向きが逆）で、選択に含まれ、指定の角度以上曲がっている
        var selectedCanon = vertexFilter?.Select(v => canon[v]).ToHashSet();
        float cosLimit = MathF.Cos(minAngleDeg * MathF.PI / 180f);
        var beveled = new HashSet<(int, int)>();
        foreach (var (k, list) in edgeFaces)
        {
            if (list.Count != 2) continue;
            var (f1, c1) = list[0];
            var (f2, c2) = list[1];
            if (canon[mesh.Faces[f1].Indices[c1]] == canon[mesh.Faces[f2].Indices[c2]]) continue; // 向きがそろっていない
            if (selectedCanon != null && !(selectedCanon.Contains(k.Item1) && selectedCanon.Contains(k.Item2))) continue;
            if (faceFilter != null && !faceFilter.Contains(f1) && !faceFilter.Contains(f2)) continue;
            if (Vector3.Dot(faceNormals[f1], faceNormals[f2]) > cosLimit + 1e-4f) continue;
            beveled.Add(k);
        }
        if (beveled.Count == 0) return (0, false);

        bool IsBeveled(int a, int b) => beveled.Contains(a < b ? (a, b) : (b, a));
        var hasBevel = new HashSet<int>(beveled.SelectMany(e => new[] { e.Item1, e.Item2 }));

        // 4. 大きさの上限: 頂点につながる辺の長さの 45% まで（点が辺の反対側を越えないように）。
        //    メッシュ全体で揃えると、どこか 1 本短い辺があるだけで全部の面取りが小さくなる。
        //    頂点ごとにすると 1 本の辺の両端で幅が変わり、先細りの面取りになる。
        //    そこで「面取りする辺でつながった頂点のまとまり」ごとに一番小さい値に揃える。
        var limit = new Dictionary<int, float>();
        foreach (var (k, _) in edgeFaces)
        {
            float half = Vector3.Distance(canonPos[k.Item1], canonPos[k.Item2]) * 0.45f;
            foreach (var v in new[] { k.Item1, k.Item2 })
                if (hasBevel.Contains(v)) limit[v] = MathF.Min(limit.GetValueOrDefault(v, distance), half);
        }
        var parent = hasBevel.ToDictionary(v => v, v => v);
        int Find(int v) { while (parent[v] != v) v = parent[v] = parent[parent[v]]; return v; }
        foreach (var (a, b) in beveled) parent[Find(a)] = Find(b);
        var groupLimit = new Dictionary<int, float>();
        foreach (var v in hasBevel)
            groupLimit[Find(v)] = MathF.Min(groupLimit.GetValueOrDefault(Find(v), distance), limit.GetValueOrDefault(v, distance));
        bool clamped = groupLimit.Values.Any(l => l < distance - 1e-6f);
        float D(int v) => hasBevel.Contains(v) ? groupLimit[Find(v)] : distance;

        Vector3 Dir(int from, int to) => Vector3.Normalize(canonPos[to] - canonPos[from]);

        // 5. 新しい点
        var pointIds = new Dictionary<PointKey, int>();
        var points = new List<Vector3>();
        var pointVertex = new List<int>();
        int Point(PointKey key)
        {
            if (pointIds.TryGetValue(key, out int id)) return id;
            var v = key.V;
            var pos = key.A < 0 ? canonPos[v]
                : key.B < 0 ? canonPos[v] + Dir(v, key.A) * D(v)
                : canonPos[v] + (Dir(v, key.A) + Dir(v, key.B)) * D(v);
            id = points.Count;
            points.Add(pos);
            pointVertex.Add(v);
            pointIds[key] = id;
            return id;
        }
        PointKey Slide(int v, int toward) => new(v, toward, -1);
        PointKey Corner(int v, int x, int y) => new(v, Math.Min(x, y), Math.Max(x, y));

        Vector2 Uv(int vertex) => hadUv ? mesh.TexCoords[vertex] : Vector2.Zero;
        Vector2 UvToward(int vi, int wi)
        {
            float len = Vector3.Distance(mesh.Positions[vi], mesh.Positions[wi]);
            return len < 1e-9f ? Uv(vi) : Vector2.Lerp(Uv(vi), Uv(wi), D(canon[vi]) / len);
        }

        // 6. 元の面を作り直す（角ごとに 1 点か 2 点）
        var newFaces = new List<(List<(int Point, Vector2 Uv)> Corners, int Material)>();
        var cornerPoints = new Dictionary<(int Face, int Corner), (int Point, Vector2 Uv)>(); // 面取りの帯で使う
        for (int f = 0; f < mesh.Faces.Count; f++)
        {
            var idx = mesh.Faces[f].Indices;
            int n = idx.Length;
            var poly = new List<(int, Vector2)>();
            for (int i = 0; i < n; i++)
            {
                int vi = idx[i], ui = idx[(i + n - 1) % n], wi = idx[(i + 1) % n];
                int v = canon[vi], u = canon[ui], w = canon[wi];
                bool e1 = u != v && IsBeveled(u, v), e2 = v != w && IsBeveled(v, w);
                if (e1 && e2)
                {
                    var uv = Uv(vi) + (UvToward(vi, ui) - Uv(vi)) + (UvToward(vi, wi) - Uv(vi));
                    var pt = (Point(Corner(v, u, w)), uv);
                    poly.Add(pt);
                    cornerPoints[(f, i)] = pt;
                }
                else if (e1)
                {
                    var pt = (Point(Slide(v, w)), UvToward(vi, wi));
                    poly.Add(pt);
                    cornerPoints[(f, i)] = pt;
                }
                else if (e2)
                {
                    var pt = (Point(Slide(v, u)), UvToward(vi, ui));
                    poly.Add(pt);
                    cornerPoints[(f, i)] = pt;
                }
                else if (hasBevel.Contains(v))
                {
                    poly.Add((Point(Slide(v, u)), UvToward(vi, ui)));
                    poly.Add((Point(Slide(v, w)), UvToward(vi, wi)));
                }
                else
                {
                    poly.Add((Point(new PointKey(v, -1, -1)), Uv(vi)));
                }
            }
            newFaces.Add((poly, mesh.Faces[f].Material));
        }

        // 7. 面取りした辺に帯を張る。面 f1 で a→b の辺なら [b1, a1, a2, b2] で元の面と向きがそろう
        foreach (var k in beveled)
        {
            var list = edgeFaces[k];
            var (f1, c1) = list[0];
            var (f2, c2) = list[1];
            int n1 = mesh.Faces[f1].Indices.Length, n2 = mesh.Faces[f2].Indices.Length;
            var a1 = cornerPoints[(f1, c1)];
            var b1 = cornerPoints[(f1, (c1 + 1) % n1)];
            var b2 = cornerPoints[(f2, c2)];            // f2 では b→a の順
            var a2 = cornerPoints[(f2, (c2 + 1) % n2)];
            var idx1 = mesh.Faces[f1].Indices;
            var strip = new List<(int, Vector2)>
            {
                (b1.Point, b1.Uv), (a1.Point, a1.Uv),
                (a2.Point, Uv(idx1[c1])), (b2.Point, Uv(idx1[(c1 + 1) % n1])),
            };
            newFaces.Add((strip, mesh.Faces[f1].Material));
        }

        // 8. 角に残った穴をふさぐ: 1 回しか使われていない辺のうち、両端が同じ元の頂点から生まれた点のもの
        var edgeUse = new Dictionary<(int, int), int>();
        var pointUv = new Dictionary<int, Vector2>();
        var pointMaterial = new Dictionary<int, int>();
        foreach (var (corners, material) in newFaces)
        {
            for (int i = 0; i < corners.Count; i++)
            {
                int p = corners[i].Point, q = corners[(i + 1) % corners.Count].Point;
                pointUv[p] = corners[i].Uv;
                pointMaterial.TryAdd(p, material);
                if (p == q) continue;
                var k = p < q ? (p, q) : (q, p);
                edgeUse[k] = edgeUse.GetValueOrDefault(k) + 1;
            }
        }
        var next = new Dictionary<int, int>();
        foreach (var (corners, _) in newFaces)
        {
            for (int i = 0; i < corners.Count; i++)
            {
                int p = corners[i].Point, q = corners[(i + 1) % corners.Count].Point;
                if (p == q || pointVertex[p] != pointVertex[q]) continue;
                if (edgeUse[p < q ? (p, q) : (q, p)] != 1) continue;
                next.TryAdd(q, p); // 穴は既存の辺と逆向きにたどる
            }
        }
        var visited = new HashSet<int>();
        foreach (var start in next.Keys.ToList())
        {
            if (visited.Contains(start)) continue;
            var loop = new List<int>();
            int cur = start;
            while (next.TryGetValue(cur, out int nx) && visited.Add(cur))
            {
                loop.Add(cur);
                cur = nx;
            }
            if (cur == start && loop.Count >= 3)
            {
                // 周りの面の向きから、たどった向きがはっきり逆なら裏返す（開いたメッシュなどでの保険）
                var normal = MeshMath.PolygonNormal(points, loop.ToArray());
                if (Vector3.Dot(normal, vertexNormal.GetValueOrDefault(pointVertex[loop[0]])) < -0.2f) loop.Reverse();
                newFaces.Add((loop.Select(p => (p, pointUv[p])).ToList(), pointMaterial[loop[0]]));
            }
        }

        // 9. メッシュを組み直す（位置と UV が同じ角は同じ頂点にする）
        var vertexOf = new Dictionary<(int, long, long), int>();
        mesh.Positions.Clear();
        mesh.TexCoords.Clear();
        bool hadColors = mesh.VertexColors.Count > 0;
        mesh.VertexColors.Clear();
        mesh.Faces.Clear();
        mesh.Normals.Clear();
        foreach (var (corners, material) in newFaces)
        {
            var indices = new List<int>();
            foreach (var (p, uv) in corners)
            {
                var key = (p, hadUv ? (long)MathF.Round(uv.X * 1e5f) : 0, hadUv ? (long)MathF.Round(uv.Y * 1e5f) : 0);
                if (!vertexOf.TryGetValue(key, out int vi))
                {
                    vi = mesh.Positions.Count;
                    mesh.Positions.Add(points[p]);
                    if (hadUv) mesh.TexCoords.Add(uv);
                    if (hadColors) mesh.VertexColors.Add(Vector4.One);
                    vertexOf[key] = vi;
                }
                if (indices.Count == 0 || indices[^1] != vi) indices.Add(vi);
            }
            if (indices.Count > 1 && indices[0] == indices[^1]) indices.RemoveAt(indices.Count - 1);
            if (indices.Count >= 3) mesh.Faces.Add(new XFace(indices.ToArray(), material));
        }
        return (beveled.Count, clamped);
    }
}
