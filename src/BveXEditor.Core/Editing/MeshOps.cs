using System.Numerics;
using BveXEditor.Core.Model;

namespace BveXEditor.Core.Editing;

/// <summary>
/// 形の編集。どれもシーンをその場で書き換える（元に戻すは呼ぶ側がスナップショットで持つ）。
/// 戻り値の文字列はステータスバーにそのまま出す。
/// </summary>
public static class MeshOps
{
    // ───────── 変形 ─────────

    public static Vector3 Center(XScene scene, Selection sel)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        bool any = false;
        foreach (var (m, verts) in SelectionQuery.Vertices(scene, sel))
            foreach (var v in verts)
            {
                var p = scene.Meshes[m].Positions[v];
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
                any = true;
            }
        return any ? (min + max) / 2 : Vector3.Zero;
    }

    /// <summary>選択（空なら全体）の頂点に行列を掛ける。</summary>
    public static string Transform(XScene scene, Selection sel, Matrix4x4 matrix)
    {
        int count = 0;
        bool mirror = matrix.GetDeterminant() < 0;
        foreach (var (m, verts) in SelectionQuery.Vertices(scene, sel))
        {
            var mesh = scene.Meshes[m];
            foreach (var v in verts) mesh.Positions[v] = Vector3.Transform(mesh.Positions[v], matrix);
            count += verts.Count;

            bool whole = verts.Count == mesh.Positions.Count;
            if (whole && mesh.Normals.Count > 0 && Matrix4x4.Invert(matrix, out var inv))
            {
                var nm = Matrix4x4.Transpose(inv);
                for (int i = 0; i < mesh.Normals.Count; i++)
                {
                    var n = Vector3.TransformNormal(mesh.Normals[i], nm);
                    mesh.Normals[i] = n.LengthSquared() > 0 ? Vector3.Normalize(n) : n;
                }
            }
            else if (!whole) mesh.ClearNormals();

            if (mirror)
                foreach (var f in mesh.Faces)
                    if (f.Indices.All(verts.Contains)) Reverse(f);
        }
        return $"{count} 頂点を変形しました";
    }

    public static Matrix4x4 BuildTransform(Vector3 translate, Vector3 rotateDeg, Vector3 scale, Vector3 pivot)
    {
        const float d2r = MathF.PI / 180f;
        return Matrix4x4.CreateTranslation(-pivot)
             * Matrix4x4.CreateScale(scale)
             * Matrix4x4.CreateRotationX(rotateDeg.X * d2r)
             * Matrix4x4.CreateRotationY(rotateDeg.Y * d2r)
             * Matrix4x4.CreateRotationZ(rotateDeg.Z * d2r)
             * Matrix4x4.CreateTranslation(pivot + translate);
    }

    public enum OriginAlign { BottomCenter, Center, KeepY }

    /// <summary>選択範囲の外接箱を原点に合わせて平行移動する。</summary>
    public static string AlignToOrigin(XScene scene, Selection sel, OriginAlign mode)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var verts = SelectionQuery.Vertices(scene, sel);
        foreach (var (m, vs) in verts)
            foreach (var v in vs)
            {
                min = Vector3.Min(min, scene.Meshes[m].Positions[v]);
                max = Vector3.Max(max, scene.Meshes[m].Positions[v]);
            }
        if (min.X > max.X) return "対象がありません";
        var c = (min + max) / 2;
        var offset = mode switch
        {
            OriginAlign.BottomCenter => new Vector3(-c.X, -min.Y, -c.Z),
            OriginAlign.Center => -c,
            _ => new Vector3(-c.X, 0, -c.Z),
        };
        Transform(scene, sel, Matrix4x4.CreateTranslation(offset));
        return $"({offset.X:0.###}, {offset.Y:0.###}, {offset.Z:0.###}) 移動しました";
    }

    // ───────── 面 ─────────

    internal static void Reverse(XFace f)
    {
        Array.Reverse(f.Indices);
        if (f.NormalIndices != null) Array.Reverse(f.NormalIndices);
    }

    public static string FlipFaces(XScene scene, Selection sel)
    {
        int count = 0;
        foreach (var (m, faces) in SelectionQuery.Faces(scene, sel))
        {
            var mesh = scene.Meshes[m];
            foreach (var f in faces) Reverse(mesh.Faces[f]);
            // 読み込んだ法線は向きが逆のままになるので作り直させる
            if (faces.Count > 0) mesh.ClearNormals();
            count += faces.Count;
        }
        return $"{count} 面を裏返しました";
    }

    public static string DeleteFaces(XScene scene, Selection sel)
    {
        int count = 0;
        foreach (var (m, faces) in SelectionQuery.Faces(scene, sel, emptyMeansAll: false))
        {
            var mesh = scene.Meshes[m];
            var keep = mesh.Faces.Where((_, i) => !faces.Contains(i)).ToList();
            count += mesh.Faces.Count - keep.Count;
            mesh.Faces.Clear();
            mesh.Faces.AddRange(keep);
            RemoveUnusedVertices(mesh);
        }
        sel.Clear();
        return $"{count} 面を削除しました";
    }

    public static string DeleteVertices(XScene scene, Selection sel)
    {
        int faces = 0;
        foreach (var (m, verts) in SelectionQuery.Vertices(scene, sel, emptyMeansAll: false))
        {
            var mesh = scene.Meshes[m];
            faces += mesh.Faces.RemoveAll(f => f.Indices.Any(verts.Contains));
            RemoveUnusedVertices(mesh);
        }
        sel.Clear();
        return $"頂点と、それを使う {faces} 面を削除しました";
    }

    /// <summary>どの面からも使われていない頂点を詰める。</summary>
    public static int RemoveUnusedVertices(XMesh mesh)
    {
        var used = new bool[mesh.Positions.Count];
        foreach (var f in mesh.Faces)
            foreach (var i in f.Indices) used[i] = true;
        var remap = new int[used.Length];
        int n = 0;
        for (int i = 0; i < used.Length; i++) remap[i] = used[i] ? n++ : -1;
        if (n == used.Length) return 0;

        Compact(mesh.Positions, used);
        if (mesh.TexCoords.Count == used.Length) Compact(mesh.TexCoords, used);
        if (mesh.VertexColors.Count == used.Length) Compact(mesh.VertexColors, used);
        foreach (var f in mesh.Faces)
            for (int j = 0; j < f.Indices.Length; j++) f.Indices[j] = remap[f.Indices[j]];
        return used.Length - n;
    }

    private static void Compact<T>(List<T> list, bool[] keep)
    {
        int w = 0;
        for (int r = 0; r < list.Count; r++)
            if (keep[r]) list[w++] = list[r];
        list.RemoveRange(w, list.Count - w);
    }

    public static string Triangulate(XScene scene, Selection sel)
    {
        int added = 0;
        foreach (var (m, faces) in SelectionQuery.Faces(scene, sel))
        {
            var mesh = scene.Meshes[m];
            var result = new List<XFace>();
            for (int i = 0; i < mesh.Faces.Count; i++)
            {
                var f = mesh.Faces[i];
                if (!faces.Contains(i) || f.Indices.Length <= 3) { result.Add(f); continue; }
                for (int k = 1; k + 1 < f.Indices.Length; k++)
                {
                    var nf = new XFace([f.Indices[0], f.Indices[k], f.Indices[k + 1]], f.Material);
                    if (f.NormalIndices != null) nf.NormalIndices = [f.NormalIndices[0], f.NormalIndices[k], f.NormalIndices[k + 1]];
                    result.Add(nf);
                    added++;
                }
                added--;
            }
            mesh.Faces.Clear();
            mesh.Faces.AddRange(result);
        }
        return $"三角形に分割しました（+{added} 面）";
    }

    /// <summary>選んだ頂点の順に面を張る。</summary>
    public static string CreateFace(XScene scene, Selection sel)
    {
        if (sel.Mode != SelectMode.Vertex) return "頂点選択モードで 3 点以上を順に選んでください";
        var refs = sel.Items;
        if (refs.Count < 3) return "3 点以上を選んでください";
        int m = refs[0].Mesh;
        if (refs.Any(r => r.Mesh != m)) return "同じメッシュの頂点だけを選んでください";
        var mesh = scene.Meshes[m];
        mesh.EnsureMaterial();
        mesh.Faces.Add(new XFace(refs.Select(r => r.Index).ToArray()));
        mesh.ClearNormals();
        return $"{refs.Count} 角形の面を作りました（裏向きなら「面を裏返す」）";
    }

    /// <summary>
    /// 選んだ面の頂点を、他の面と共有しないよう複製して切り離す。
    /// .x は頂点ごとに UV を 1 つしか持てないので、UV の継ぎ目を作るときに使う。
    /// </summary>
    public static string Detach(XScene scene, Selection sel)
    {
        int dup = 0;
        foreach (var (m, faces) in SelectionQuery.Faces(scene, sel, emptyMeansAll: false))
        {
            var mesh = scene.Meshes[m];
            var group = new int[mesh.Faces.Count];
            for (int i = 0; i < group.Length; i++) group[i] = faces.Contains(i) ? 1 : 0;
            dup += SplitVerticesByGroup(mesh, group);
        }
        return $"{dup} 頂点を複製して切り離しました";
    }

    /// <summary>
    /// 面ごとのグループ番号で、グループをまたいで共有されている頂点を複製する。
    /// 最初に使ったグループが元の頂点を持ち続ける。
    /// </summary>
    public static int SplitVerticesByGroup(XMesh mesh, int[] faceGroup)
    {
        var owner = new Dictionary<int, int>();
        var copies = new Dictionary<(int V, int G), int>();
        int dup = 0;
        for (int f = 0; f < mesh.Faces.Count; f++)
        {
            var face = mesh.Faces[f];
            int g = faceGroup[f];
            for (int j = 0; j < face.Indices.Length; j++)
            {
                int v = face.Indices[j];
                if (!owner.TryGetValue(v, out int og)) { owner[v] = g; continue; }
                if (og == g) continue;
                if (!copies.TryGetValue((v, g), out int nv))
                {
                    nv = mesh.AddVertex(mesh.Positions[v],
                        mesh.TexCoords.Count > v ? mesh.TexCoords[v] : null,
                        mesh.VertexColors.Count > v ? mesh.VertexColors[v] : null);
                    copies[(v, g)] = nv;
                    dup++;
                }
                face.Indices[j] = nv;
            }
        }
        return dup;
    }

    /// <summary>
    /// 近い位置の頂点を 1 つにまとめる。<paramref name="respectUV"/> なら UV も同じものだけ。
    /// 選択が空ならメッシュ全体。
    /// </summary>
    public static string Weld(XScene scene, Selection sel, float epsilon = 0.0005f, bool respectUV = true)
    {
        int merged = 0, degenerate = 0;
        foreach (var (m, verts) in SelectionQuery.Vertices(scene, sel))
        {
            var mesh = scene.Meshes[m];
            int mergedHere = 0;
            var list = verts.Order().ToList();
            var target = Enumerable.Range(0, mesh.Positions.Count).ToArray();
            var cell = new Dictionary<(long, long, long), List<int>>();
            float inv = 1f / Math.Max(epsilon, 1e-6f);
            foreach (var v in list)
            {
                var p = mesh.Positions[v];
                var key = ((long)MathF.Floor(p.X * inv), (long)MathF.Floor(p.Y * inv), (long)MathF.Floor(p.Z * inv));
                int found = -1;
                for (long dx = -1; dx <= 1 && found < 0; dx++)
                for (long dy = -1; dy <= 1 && found < 0; dy++)
                for (long dz = -1; dz <= 1 && found < 0; dz++)
                {
                    if (!cell.TryGetValue((key.Item1 + dx, key.Item2 + dy, key.Item3 + dz), out var bucket)) continue;
                    foreach (var o in bucket)
                    {
                        if (Vector3.Distance(mesh.Positions[o], p) > epsilon) continue;
                        if (respectUV && mesh.HasUV && Vector2.Distance(mesh.TexCoords[o], mesh.TexCoords[v]) > 1e-5f) continue;
                        found = o;
                        break;
                    }
                }
                if (found >= 0) { target[v] = found; mergedHere++; }
                else (cell.TryGetValue(key, out var b) ? b : cell[key] = []).Add(v);
            }
            merged += mergedHere;
            if (mergedHere == 0) continue;

            foreach (var f in mesh.Faces)
                for (int j = 0; j < f.Indices.Length; j++) f.Indices[j] = target[f.Indices[j]];
            degenerate += CleanDegenerate(mesh);
            RemoveUnusedVertices(mesh);
            mesh.ClearNormals();
        }
        sel.Validate(scene);
        return $"{merged} 頂点をまとめました" + (degenerate > 0 ? $"（潰れた面 {degenerate} 個を削除）" : "");
    }

    /// <summary>連続する同じ頂点を詰め、3 点未満になった面を消す。</summary>
    public static int CleanDegenerate(XMesh mesh)
    {
        int removed = 0;
        for (int i = mesh.Faces.Count - 1; i >= 0; i--)
        {
            var f = mesh.Faces[i];
            var idx = new List<int>();
            foreach (var v in f.Indices)
                if (idx.Count == 0 || idx[^1] != v) idx.Add(v);
            if (idx.Count > 1 && idx[0] == idx[^1]) idx.RemoveAt(idx.Count - 1);
            if (idx.Count < 3) { mesh.Faces.RemoveAt(i); removed++; continue; }
            if (idx.Count != f.Indices.Length) { f.Indices = idx.ToArray(); f.NormalIndices = null; }
        }
        return removed;
    }

    /// <summary>選んだ面を法線方向に押し出し、側面を張る。</summary>
    public static string Extrude(XScene scene, Selection sel, float distance)
    {
        int total = 0;
        foreach (var (m, faces) in SelectionQuery.Faces(scene, sel, emptyMeansAll: false))
        {
            var mesh = scene.Meshes[m];
            if (faces.Count == 0) continue;

            // 頂点ごとの押し出し方向（使っている選択面の法線の平均）
            var dir = new Dictionary<int, Vector3>();
            foreach (var fi in faces)
            {
                var n = mesh.FaceNormal(mesh.Faces[fi]);
                foreach (var v in mesh.Faces[fi].Indices)
                    dir[v] = (dir.TryGetValue(v, out var d) ? d : Vector3.Zero) + n;
            }

            // 境界辺: 選択面の中で 1 回しか出てこない辺
            var edgeCount = new Dictionary<(int, int), int>();
            foreach (var fi in faces)
            {
                var idx = mesh.Faces[fi].Indices;
                for (int j = 0; j < idx.Length; j++)
                {
                    int a = idx[j], b = idx[(j + 1) % idx.Length];
                    var key = a < b ? (a, b) : (b, a);
                    edgeCount[key] = edgeCount.GetValueOrDefault(key) + 1;
                }
            }

            var newIndex = new Dictionary<int, int>();
            foreach (var (v, d) in dir)
            {
                var n = d.LengthSquared() > 1e-12f ? Vector3.Normalize(d) : Vector3.Zero;
                newIndex[v] = mesh.AddVertex(mesh.Positions[v] + n * distance,
                    mesh.HasUV ? mesh.TexCoords[v] : null,
                    mesh.VertexColors.Count > v ? mesh.VertexColors[v] : null);
            }

            var sides = new List<XFace>();
            foreach (var fi in faces)
            {
                var f = mesh.Faces[fi];
                var idx = f.Indices;
                for (int j = 0; j < idx.Length; j++)
                {
                    int a = idx[j], b = idx[(j + 1) % idx.Length];
                    var key = a < b ? (a, b) : (b, a);
                    if (edgeCount[key] != 1) continue;
                    // 元の面が a→b の向きのとき、[a, b, b', a'] で外向きになる
                    sides.Add(new XFace([a, b, newIndex[b], newIndex[a]], f.Material));
                }
                f.Indices = idx.Select(v => newIndex[v]).ToArray();
                f.NormalIndices = null;
            }
            mesh.Faces.AddRange(sides);
            mesh.ClearNormals();
            total += faces.Count;
        }
        return total == 0 ? "押し出す面を選んでください" : $"{total} 面を {distance:0.###} m 押し出しました";
    }

    // ───────── メッシュ単位 ─────────

    /// <summary>選んだ面を新しいメッシュに移す。</summary>
    public static string SeparateFaces(XScene scene, Selection sel)
    {
        var faces = SelectionQuery.Faces(scene, sel, emptyMeansAll: false);
        int created = 0;
        foreach (var (m, set) in faces.OrderByDescending(kv => kv.Key))
        {
            var src = scene.Meshes[m];
            if (set.Count == 0 || set.Count == src.Faces.Count) continue;
            var dst = src.Clone();
            dst.Name = src.Name + "_分離";
            dst.Faces.Clear();
            dst.Faces.AddRange(src.Faces.Where((_, i) => set.Contains(i)).Select(f => f.Clone()));
            var rest = src.Faces.Where((_, i) => !set.Contains(i)).ToList();
            src.Faces.Clear();
            src.Faces.AddRange(rest);
            RemoveUnusedVertices(src);
            RemoveUnusedVertices(dst);
            RemoveUnusedMaterials(dst);
            if (dst.Normals.Count == 0 || dst.Faces.Any(f => f.NormalIndices == null)) dst.ClearNormals();
            scene.Meshes.Insert(m + 1, dst);
            created++;
        }
        sel.Clear();
        return created == 0 ? "メッシュの一部の面を選んでください" : $"{created} 個のメッシュに分離しました";
    }

    public static string MergeMeshes(XScene scene, IReadOnlyCollection<int> meshIndices)
    {
        var list = meshIndices.Where(i => i >= 0 && i < scene.Meshes.Count).Distinct().Order().ToList();
        if (list.Count < 2) return "2 つ以上のメッシュを選んでください";
        var dst = scene.Meshes[list[0]];
        dst.EnsureMaterial();
        foreach (var mi in list.Skip(1))
        {
            var src = scene.Meshes[mi];
            src.EnsureMaterial();
            int baseV = dst.Positions.Count;
            bool uv = dst.HasUV || src.HasUV;
            if (uv) { dst.EnsureUV(); src.EnsureUV(); }
            bool colors = dst.VertexColors.Count > 0 || src.VertexColors.Count > 0;
            if (colors)
            {
                while (dst.VertexColors.Count < dst.Positions.Count) dst.VertexColors.Add(Vector4.One);
            }

            dst.Positions.AddRange(src.Positions);
            if (uv) dst.TexCoords.AddRange(src.TexCoords);
            if (colors)
            {
                dst.VertexColors.AddRange(src.VertexColors);
                while (dst.VertexColors.Count < dst.Positions.Count) dst.VertexColors.Add(Vector4.One);
            }

            var matMap = src.Materials.Select(sm =>
            {
                int found = dst.Materials.FindIndex(dm => dm.SameAs(sm));
                if (found >= 0) return found;
                dst.Materials.Add(sm.Clone());
                return dst.Materials.Count - 1;
            }).ToArray();

            bool normals = dst.HasExplicitNormals && src.HasExplicitNormals;
            int baseN = dst.Normals.Count;
            if (normals) dst.Normals.AddRange(src.Normals);
            foreach (var f in src.Faces)
            {
                var nf = new XFace(f.Indices.Select(i => i + baseV).ToArray(), matMap[f.Material]);
                if (normals) nf.NormalIndices = f.NormalIndices!.Select(i => i + baseN).ToArray();
                dst.Faces.Add(nf);
            }
            if (!normals) dst.ClearNormals();
        }
        for (int i = list.Count - 1; i >= 1; i--) scene.Meshes.RemoveAt(list[i]);
        return $"{list.Count} 個のメッシュを結合しました";
    }

    public static string SplitByMaterial(XScene scene, int meshIndex)
    {
        var src = scene.Meshes[meshIndex];
        var groups = src.Faces.GroupBy(f => f.Material).OrderBy(g => g.Key).ToList();
        if (groups.Count < 2) return "マテリアルが 1 種類なので分けられません";
        var created = new List<XMesh>();
        foreach (var g in groups)
        {
            var dst = src.Clone();
            dst.Name = $"{src.Name}_{g.Key}";
            var keep = g.ToHashSet();
            dst.Faces.Clear();
            dst.Faces.AddRange(src.Faces.Where(keep.Contains).Select(f => f.Clone()));
            RemoveUnusedVertices(dst);
            RemoveUnusedMaterials(dst);
            created.Add(dst);
        }
        scene.Meshes.RemoveAt(meshIndex);
        scene.Meshes.InsertRange(meshIndex, created);
        return $"マテリアルごとに {created.Count} 個へ分けました";
    }

    // ───────── マテリアル ─────────

    public static string AssignMaterial(XScene scene, Selection sel, int meshIndex, int material)
    {
        int count = 0;
        foreach (var (m, faces) in SelectionQuery.Faces(scene, sel, emptyMeansAll: false))
        {
            if (m != meshIndex) continue;
            foreach (var f in faces) scene.Meshes[m].Faces[f].Material = material;
            count += faces.Count;
        }
        return count == 0 ? "このメッシュの面を選んでください" : $"{count} 面にマテリアル {material} を割り当てました";
    }

    public static int RemoveUnusedMaterials(XMesh mesh)
    {
        var used = mesh.Faces.Select(f => f.Material).ToHashSet();
        var remap = new int[mesh.Materials.Count];
        var keep = new List<XMaterial>();
        for (int i = 0; i < mesh.Materials.Count; i++)
        {
            if (used.Contains(i)) { remap[i] = keep.Count; keep.Add(mesh.Materials[i]); }
            else remap[i] = -1;
        }
        int removed = mesh.Materials.Count - keep.Count;
        if (removed == 0) return 0;
        mesh.Materials.Clear();
        mesh.Materials.AddRange(keep);
        foreach (var f in mesh.Faces) f.Material = Math.Max(0, remap[f.Material]);
        mesh.EnsureMaterial();
        return removed;
    }

    public static void DeleteMaterial(XMesh mesh, int index)
    {
        if (mesh.Materials.Count <= 1) return;
        mesh.Materials.RemoveAt(index);
        foreach (var f in mesh.Faces)
        {
            if (f.Material == index) f.Material = 0;
            else if (f.Material > index) f.Material--;
        }
    }

    public static int MergeDuplicateMaterials(XMesh mesh)
    {
        var remap = new int[mesh.Materials.Count];
        var keep = new List<XMaterial>();
        for (int i = 0; i < mesh.Materials.Count; i++)
        {
            int found = keep.FindIndex(k => k.SameAs(mesh.Materials[i]));
            if (found < 0) { remap[i] = keep.Count; keep.Add(mesh.Materials[i]); }
            else remap[i] = found;
        }
        int merged = mesh.Materials.Count - keep.Count;
        mesh.Materials.Clear();
        mesh.Materials.AddRange(keep);
        foreach (var f in mesh.Faces) f.Material = remap[f.Material];
        return merged;
    }

    public static string RecomputeNormals(XScene scene, Selection sel, float smoothAngle)
    {
        foreach (var m in SelectionQuery.Meshes(scene, sel))
        {
            var mesh = scene.Meshes[m];
            var (normals, idx) = MeshMath.ComputeNormals(mesh, smoothAngle);
            mesh.Normals.Clear();
            mesh.Normals.AddRange(normals);
            for (int i = 0; i < mesh.Faces.Count; i++) mesh.Faces[i].NormalIndices = idx[i];
        }
        return $"法線を作り直しました（スムーズ角 {smoothAngle:0}°）";
    }
}
