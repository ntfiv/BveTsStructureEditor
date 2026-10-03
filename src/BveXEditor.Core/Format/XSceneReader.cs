using System.Numerics;
using BveXEditor.Core.Model;

namespace BveXEditor.Core.Format;

/// <summary>
/// 汎用データオブジェクトの木から <see cref="XScene"/> を組み立てる。
///
/// 方針: 壊れ気味のファイルでも「読めるところまで読んで、欠けは警告に書く」。
/// 例外で全部を諦めるより、画面に出して直せるほうが編集ツールとして役に立つ。
/// </summary>
internal sealed class XSceneReader
{
    private readonly List<string> _warnings;
    private readonly Dictionary<string, XDataObject> _named = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unknown = new(StringComparer.OrdinalIgnoreCase);

    public XSceneReader(List<string> warnings) => _warnings = warnings;

    public XScene Read(List<XDataObject> roots)
    {
        foreach (var r in roots) Index(r);

        // フレームから参照されるメッシュはトップレベルで二重に数えない
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in roots) CollectFrameRefs(r, referenced);

        var scene = new XScene();
        foreach (var r in roots)
        {
            if (Is(r, "Mesh"))
            {
                if (r.Name != null && referenced.Contains(r.Name)) continue;
                AddMesh(scene, r, Matrix4x4.Identity, r.Name);
            }
            else if (Is(r, "Frame")) ReadFrame(scene, r, Matrix4x4.Identity);
            else if (Is(r, "Material") || Is(r, "Header") || Is(r, "AnimTicksPerSecond")) { }
            else _unknown.Add(r.Template);
        }

        if (_unknown.Count > 0)
            _warnings.Add("読み飛ばしたテンプレート: " + string.Join(", ", _unknown.Order()));
        return scene;
    }

    private static bool Is(XDataObject o, string template) =>
        string.Equals(o.Template, template, StringComparison.OrdinalIgnoreCase);

    private void Index(XDataObject o)
    {
        if (o.Name != null) _named.TryAdd(o.Name, o);
        foreach (var c in o.Children) Index(c);
    }

    private static void CollectFrameRefs(XDataObject o, HashSet<string> refs)
    {
        if (Is(o, "Frame"))
            foreach (var item in o.Items)
                if (item is string s) refs.Add(s);
        foreach (var c in o.Children) CollectFrameRefs(c, refs);
    }

    private void ReadFrame(XScene scene, XDataObject frame, Matrix4x4 parent)
    {
        var local = Matrix4x4.Identity;
        foreach (var c in frame.Children)
        {
            if (Is(c, "FrameTransformMatrix"))
            {
                if (c.Numbers.Count >= 16)
                {
                    var n = c.Numbers;
                    local = new Matrix4x4(
                        (float)n[0], (float)n[1], (float)n[2], (float)n[3],
                        (float)n[4], (float)n[5], (float)n[6], (float)n[7],
                        (float)n[8], (float)n[9], (float)n[10], (float)n[11],
                        (float)n[12], (float)n[13], (float)n[14], (float)n[15]);
                }
                else _warnings.Add($"{c.Line} 行: FrameTransformMatrix の値が 16 個ありません");
            }
        }
        // DirectX は行ベクトル: world = local * parent
        var world = local * parent;

        foreach (var item in frame.Items)
        {
            switch (item)
            {
                case XDataObject c when Is(c, "Mesh"):
                    AddMesh(scene, c, world, c.Name ?? frame.Name);
                    break;
                case XDataObject c when Is(c, "Frame"):
                    ReadFrame(scene, c, world);
                    break;
                case XDataObject c when Is(c, "FrameTransformMatrix"):
                    break;
                case XDataObject c:
                    _unknown.Add(c.Template);
                    break;
                case string refName when _named.TryGetValue(refName, out var target):
                    if (Is(target, "Mesh")) AddMesh(scene, target, world, target.Name);
                    else if (Is(target, "Frame")) ReadFrame(scene, target, world);
                    break;
                case string refName:
                    _warnings.Add($"参照先が見つかりません: {{ {refName} }}");
                    break;
            }
        }
    }

    private void AddMesh(XScene scene, XDataObject o, Matrix4x4 world, string? name)
    {
        var mesh = ReadMesh(o);
        if (mesh == null) return;
        mesh.Name = name ?? $"Mesh{scene.Meshes.Count + 1}";

        if (!world.IsIdentity)
        {
            for (int i = 0; i < mesh.Positions.Count; i++) mesh.Positions[i] = Vector3.Transform(mesh.Positions[i], world);
            if (Matrix4x4.Invert(world, out var inv))
            {
                var nm = Matrix4x4.Transpose(inv);
                for (int i = 0; i < mesh.Normals.Count; i++)
                {
                    var n = Vector3.TransformNormal(mesh.Normals[i], nm);
                    mesh.Normals[i] = n.LengthSquared() > 0 ? Vector3.Normalize(n) : n;
                }
            }
            // 鏡像変換なら表裏が逆になるので巡回順を戻す
            if (world.GetDeterminant() < 0)
                foreach (var f in mesh.Faces)
                {
                    Array.Reverse(f.Indices);
                    if (f.NormalIndices != null) Array.Reverse(f.NormalIndices);
                }
        }
        scene.Meshes.Add(mesh);
    }

    /// <summary>
    /// 数の欄を個数として読む。壊れたファイルでは巨大な値や負の値・NaN が入ることがあるので、
    /// 0〜<paramref name="max"/> に収まらなければ -1 を返す（呼び出し側で「データが足りない」扱いにする）。
    /// </summary>
    private static int CountOf(double value, int max)
    {
        if (double.IsNaN(value) || value < 0 || value > max) return -1;
        return (int)value;
    }

    /// <summary>頂点番号などの整数。範囲外（NaN・巨大な値）は -1。</summary>
    private static int IndexOf(double value) =>
        double.IsNaN(value) || value < int.MinValue || value > int.MaxValue ? -1 : (int)value;

    private XMesh? ReadMesh(XDataObject o)
    {
        var where = $"{o.Line} 行の Mesh{(o.Name != null ? " " + o.Name : "")}";
        var n = o.Numbers;
        int k = 0;
        // 足し算で int があふれないよう long で比べる
        bool Has(long count) => count >= 0 && k + count <= n.Count;

        var mesh = new XMesh();
        if (!Has(1)) { _warnings.Add($"{where}: 頂点数がありません"); return null; }
        int nv = CountOf(n[k++], n.Count);
        if (nv < 0 || !Has(nv * 3L))
        {
            _warnings.Add($"{where}: 頂点データが足りません（{nv} 個のはず）");
            nv = Math.Max(0, (n.Count - k) / 3);
        }
        for (int i = 0; i < nv; i++, k += 3)
            mesh.Positions.Add(new Vector3((float)n[k], (float)n[k + 1], (float)n[k + 2]));

        int nf = Has(1) ? Math.Max(0, CountOf(n[k++], n.Count)) : 0;
        int dropped = 0;
        for (int i = 0; i < nf; i++)
        {
            if (!Has(1)) { _warnings.Add($"{where}: 面データが途中で終わっています（{i}/{nf}）"); break; }
            int c = CountOf(n[k++], n.Count);
            if (c < 0 || !Has(c)) { _warnings.Add($"{where}: 面データが途中で終わっています（{i}/{nf}）"); break; }
            var idx = new int[c];
            bool ok = c >= 3;
            for (int j = 0; j < c; j++)
            {
                idx[j] = IndexOf(n[k++]);
                if (idx[j] < 0 || idx[j] >= nv) ok = false;
            }
            if (ok) mesh.Faces.Add(new XFace(idx));
            else
            {
                dropped++;
                mesh.Faces.Add(new XFace([])); // 位置合わせのため仮に入れ、最後に取り除く
            }
        }

        foreach (var c in o.Children)
        {
            if (Is(c, "MeshMaterialList")) ReadMaterialList(mesh, c, where);
            else if (Is(c, "MeshNormals")) ReadNormals(mesh, c, where);
            else if (Is(c, "MeshTextureCoords")) ReadTexCoords(mesh, c, where);
            else if (Is(c, "MeshVertexColors")) ReadVertexColors(mesh, c, where);
            else _unknown.Add(c.Template);
        }

        if (dropped > 0)
        {
            mesh.Faces.RemoveAll(f => f.Indices.Length == 0);
            _warnings.Add($"{where}: 不正な面を {dropped} 個取り除きました（3 点未満か範囲外の頂点）");
        }
        mesh.EnsureMaterial();
        return mesh;
    }

    private void ReadMaterialList(XMesh mesh, XDataObject o, string where)
    {
        var n = o.Numbers;
        if (n.Count < 2) { _warnings.Add($"{where}: MeshMaterialList が壊れています"); return; }
        // マテリアル数は面の数を超えても意味がないので、壊れた巨大な値で補いすぎないよう面の数（最低 1）で抑える
        int nMat = Math.Min(Math.Max(0, CountOf(n[0], int.MaxValue)), Math.Max(1, mesh.Faces.Count));
        int nIdx = Math.Max(0, CountOf(n[1], int.MaxValue));
        int avail = Math.Min(nIdx, n.Count - 2);

        foreach (var item in o.Items)
        {
            XDataObject? m = item switch
            {
                XDataObject c when Is(c, "Material") => c,
                string r when _named.TryGetValue(r, out var t) && Is(t, "Material") => t,
                _ => null,
            };
            if (m != null) mesh.Materials.Add(ReadMaterial(m));
            else if (item is string r) _warnings.Add($"{where}: マテリアル参照 {{ {r} }} が見つかりません");
        }
        if (mesh.Materials.Count < nMat)
        {
            _warnings.Add($"{where}: マテリアルが {nMat} 個のはずが {mesh.Materials.Count} 個でした。白で補います");
            while (mesh.Materials.Count < nMat) mesh.Materials.Add(new XMaterial());
        }

        int last = 0;
        for (int i = 0; i < mesh.Faces.Count; i++)
        {
            // 面数より少ないときは最後の値を繰り返す（1 個だけ書く書き出し元がある）
            if (i < avail) last = IndexOf(n[2 + i]);
            mesh.Faces[i].Material = last;
        }
    }

    private XMaterial ReadMaterial(XDataObject o)
    {
        var n = o.Numbers;
        float F(int i, float def) => i < n.Count ? (float)n[i] : def;
        var mat = new XMaterial
        {
            FaceColor = new Vector4(F(0, 1), F(1, 1), F(2, 1), F(3, 1)),
            Power = F(4, 0),
            Specular = new Vector3(F(5, 0), F(6, 0), F(7, 0)),
            Emissive = new Vector3(F(8, 0), F(9, 0), F(10, 0)),
        };
        foreach (var c in o.Children)
        {
            if (Is(c, "TextureFilename") || Is(c, "TextureFileName"))
                mat.Texture = c.Strings.FirstOrDefault();
        }
        return mat;
    }

    private void ReadNormals(XMesh mesh, XDataObject o, string where)
    {
        var n = o.Numbers;
        int k = 0;
        if (n.Count < 1) return;
        int count = CountOf(n[k++], n.Count);
        if (count < 0 || k + count * 3L > n.Count) { _warnings.Add($"{where}: MeshNormals が足りないので法線は作り直します"); return; }
        var normals = new List<Vector3>(count);
        for (int i = 0; i < count; i++, k += 3) normals.Add(new Vector3((float)n[k], (float)n[k + 1], (float)n[k + 2]));
        int nf = k < n.Count ? Math.Max(0, CountOf(n[k++], n.Count)) : 0;
        var faceIdx = new List<int[]>(nf);
        for (int i = 0; i < nf; i++)
        {
            if (k >= n.Count) break;
            int c = CountOf(n[k++], n.Count);
            if (c < 0 || k + (long)c > n.Count) break;
            var idx = new int[c];
            for (int j = 0; j < c; j++) idx[j] = IndexOf(n[k++]);
            faceIdx.Add(idx);
        }

        // 面の数・角の数・範囲が合っているときだけ採用する
        bool ok = faceIdx.Count == mesh.Faces.Count;
        for (int i = 0; ok && i < faceIdx.Count; i++)
        {
            var fi = faceIdx[i];
            if (mesh.Faces[i].Indices.Length == 0) continue;
            ok = fi.Length == mesh.Faces[i].Indices.Length && fi.All(x => x >= 0 && x < count);
        }
        if (!ok) { _warnings.Add($"{where}: MeshNormals が面と対応していないので法線は作り直します"); return; }

        mesh.Normals.AddRange(normals);
        for (int i = 0; i < faceIdx.Count; i++) mesh.Faces[i].NormalIndices = faceIdx[i];
    }

    private void ReadTexCoords(XMesh mesh, XDataObject o, string where)
    {
        var n = o.Numbers;
        if (n.Count < 1) return;
        int count = Math.Max(0, CountOf(n[0], int.MaxValue));
        int avail = Math.Min(count, (n.Count - 1) / 2);
        if (count != mesh.Positions.Count || avail != count)
            _warnings.Add($"{where}: UV の数 ({avail}) が頂点数 ({mesh.Positions.Count}) と合いません。足りない分は 0 にします");
        for (int i = 0; i < avail && i < mesh.Positions.Count; i++)
            mesh.TexCoords.Add(new Vector2((float)n[1 + i * 2], (float)n[2 + i * 2]));
        mesh.EnsureUV();
    }

    private void ReadVertexColors(XMesh mesh, XDataObject o, string where)
    {
        var n = o.Numbers;
        if (n.Count < 1) return;
        int count = Math.Max(0, CountOf(n[0], int.MaxValue));
        var colors = Enumerable.Repeat(Vector4.One, mesh.Positions.Count).ToList();
        int k = 1;
        for (int i = 0; i < count && k + 5 <= n.Count; i++, k += 5)
        {
            int v = IndexOf(n[k]);
            if (v >= 0 && v < colors.Count)
                colors[v] = new Vector4((float)n[k + 1], (float)n[k + 2], (float)n[k + 3], (float)n[k + 4]);
        }
        if (count > 0) mesh.VertexColors.AddRange(colors);
        else _warnings.Add($"{where}: MeshVertexColors が空です");
    }
}
