using System.Numerics;
using System.Text;
using BveTsStructureEditor.Core.Model;

namespace BveTsStructureEditor.Core.Format.Pmx;

/// <summary>
/// PMX 2.0 の書き出し。動かない静的モデルとして出す。
///
/// - 全メッシュを 1 つのモデルにまとめ、面は三角形に分割する
/// - PMX は頂点ごとに法線・UV を 1 つしか持てないので、角ごとに法線が違う頂点は分けて出す
/// - PMX エディタや MMD で開けるよう、ルートボーン 1 本と必須の表示枠 2 つ（Root / 表情）を付ける
/// </summary>
public static class PmxWriter
{
    public static void Save(string path, XScene scene, PmxOptions? options = null, float smoothAngle = 30f)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        File.WriteAllBytes(path, Write(scene, name, options, smoothAngle));
    }

    public static byte[] Write(XScene scene, string modelName, PmxOptions? options = null, float smoothAngle = 30f)
    {
        options ??= new PmxOptions();
        float inv = options.Scale != 0 ? 1f / options.Scale : 1f;

        // 頂点の展開: (メッシュ, 頂点, 法線) ごとに 1 つ
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var textures = new List<string>();
        var materials = new List<(XMaterial Mat, string Name, List<int> Indices)>();

        for (int mi = 0; mi < scene.Meshes.Count; mi++)
        {
            var mesh = scene.Meshes[mi];
            if (mesh.Faces.Count == 0) continue;
            mesh.EnsureMaterial();

            List<Vector3> meshNormals;
            IReadOnlyList<int[]> faceNormals;
            if (mesh.HasExplicitNormals)
            {
                meshNormals = mesh.Normals;
                faceNormals = mesh.Faces.Select(f => f.NormalIndices!).ToList();
            }
            else
            {
                var (n, fn) = MeshMath.ComputeNormals(mesh, smoothAngle);
                meshNormals = n;
                faceNormals = fn;
            }

            var map = new Dictionary<(int V, int N), int>();
            int Vertex(int v, int n)
            {
                if (map.TryGetValue((v, n), out int index)) return index;
                index = positions.Count;
                positions.Add(mesh.Positions[v] * inv);
                normals.Add(meshNormals[n]);
                uvs.Add(mesh.HasUV ? mesh.TexCoords[v] : Vector2.Zero);
                map[(v, n)] = index;
                return index;
            }

            var perMaterial = mesh.Materials.Select(_ => new List<int>()).ToArray();
            for (int fi = 0; fi < mesh.Faces.Count; fi++)
            {
                var f = mesh.Faces[fi];
                var fn = faceNormals[fi];
                var list = perMaterial[f.Material];
                for (int k = 1; k + 1 < f.Indices.Length; k++)
                {
                    // 同じ頂点を 2 回使う潰れた三角形は PMX では意味がないので出さない
                    if (f.Indices[0] == f.Indices[k] || f.Indices[k] == f.Indices[k + 1] || f.Indices[0] == f.Indices[k + 1]) continue;
                    list.Add(Vertex(f.Indices[0], fn[0]));
                    list.Add(Vertex(f.Indices[k], fn[k]));
                    list.Add(Vertex(f.Indices[k + 1], fn[k + 1]));
                }
            }
            for (int m = 0; m < mesh.Materials.Count; m++)
            {
                if (perMaterial[m].Count == 0) continue;
                var baseName = string.IsNullOrWhiteSpace(mesh.Name) ? $"mesh{mi}" : mesh.Name;
                materials.Add((mesh.Materials[m], mesh.Materials.Count > 1 ? $"{baseName}_{m}" : baseName, perMaterial[m]));
            }
        }

        foreach (var (mat, _, _) in materials)
            if (!string.IsNullOrEmpty(mat.Texture) && !textures.Contains(mat.Texture, StringComparer.OrdinalIgnoreCase))
                textures.Add(mat.Texture);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var enc = Encoding.Unicode;
        void Text(string s)
        {
            var b = enc.GetBytes(s);
            w.Write(b.Length);
            w.Write(b);
        }
        void Vec2(Vector2 v) { w.Write(v.X); w.Write(v.Y); }
        void Vec3(Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
        void Vec4(Vector4 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); w.Write(v.W); }

        // ヘッダー: 文字は UTF-16LE、追加 UV なし、インデックスはすべて 4 バイト
        w.Write(Encoding.ASCII.GetBytes("PMX "));
        w.Write(2.0f);
        w.Write((byte)8);
        w.Write(new byte[] { 0, 0, 4, 4, 4, 4, 4, 4 });
        Text(modelName);
        Text(modelName);
        Text("BveTs Structure Editor で書き出した静的モデル");
        Text("Exported by BveTs Structure Editor (static model)");

        w.Write(positions.Count);
        for (int i = 0; i < positions.Count; i++)
        {
            Vec3(positions[i]);
            Vec3(normals[i]);
            Vec2(uvs[i]);
            w.Write((byte)0);   // BDEF1
            w.Write(0);         // ボーン 0
            w.Write(1f);        // エッジ倍率
        }

        w.Write(materials.Sum(m => m.Indices.Count));
        foreach (var (_, _, indices) in materials)
            foreach (var i in indices) w.Write(i);

        w.Write(textures.Count);
        foreach (var t in textures) Text(t);

        w.Write(materials.Count);
        foreach (var (mat, name, indices) in materials)
        {
            Text(name);
            Text(name);
            Vec4(mat.FaceColor);
            Vec3(mat.Specular);
            w.Write(mat.Power);
            // 環境色: X には無いので、拡散色の半分に発光色を足したもの（MMD で暗くなりすぎない目安）
            Vec3(Vector3.Min(new Vector3(mat.FaceColor.X, mat.FaceColor.Y, mat.FaceColor.Z) * 0.5f + mat.Emissive, Vector3.One));
            w.Write((byte)0x0E); // 地面影・セルフ影マップ・セルフ影（両面描画・エッジはなし）
            Vec4(new Vector4(0, 0, 0, 1));
            w.Write(1f);
            w.Write(string.IsNullOrEmpty(mat.Texture) ? -1 : textures.FindIndex(t => string.Equals(t, mat.Texture, StringComparison.OrdinalIgnoreCase)));
            w.Write(-1);        // スフィア
            w.Write((byte)0);   // スフィアモード
            w.Write((byte)0);   // 共有 Toon フラグ（0 = 続きはテクスチャのインデックス）
            w.Write(-1);        // トゥーン
            Text("");
            w.Write(indices.Count);
        }

        // ルートボーン 1 本
        w.Write(1);
        Text("センター");
        Text("center");
        Vec3(Vector3.Zero);
        w.Write(-1);            // 親なし
        w.Write(0);             // 変形階層
        w.Write((ushort)0x001E); // 回転・移動・表示・操作可（接続先は位置で指定）
        Vec3(Vector3.Zero);

        w.Write(0);             // モーフ

        // 表示枠: Root（ボーン 0）と 表情（空）。どちらも特殊枠
        w.Write(2);
        Text("Root");
        Text("Root");
        w.Write((byte)1);
        w.Write(1);
        w.Write((byte)0);
        w.Write(0);
        Text("表情");
        Text("Exp");
        w.Write((byte)1);
        w.Write(0);

        w.Write(0);             // 剛体
        w.Write(0);             // ジョイント
        w.Flush();
        return ms.ToArray();
    }
}
