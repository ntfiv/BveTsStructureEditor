using System.Numerics;
using System.Text;
using BveXEditor.Core.Model;

namespace BveXEditor.Core.Format.Pmx;

public sealed class PmxOptions
{
    /// <summary>PMX の 1 単位を何 m とするか。読み込みで掛け、書き出しで割る。</summary>
    public float Scale { get; set; } = 1f;
}

public sealed record PmxLoadResult(XScene Scene, float Version, string ModelName, IReadOnlyList<string> Warnings, bool HasRigData)
{
    public string Describe() => $"PMX {Version:0.0}";
}

/// <summary>
/// PMX 2.0 / 2.1 の読み込み。エディタが扱うのは形とマテリアルだけ。
///
/// 座標系は MMD も左手系・Y 上・表は時計回りで、BVE 空間と同じなので軸や巻き順は変えない
/// （three.js の MMDLoader は右手系にするとき Z 反転と頂点順の反転を両方しており、同じ規約だとわかる）。
/// ボーン以降も最後まで読み進めるのは、読み込まないデータの数を警告に正確に出すため。
/// </summary>
public static class PmxReader
{
    public static PmxLoadResult Load(string path, PmxOptions? options = null) =>
        Load(File.ReadAllBytes(path), options, Path.GetFileNameWithoutExtension(path));

    /// <param name="fallbackName">モデル名が空のときのメッシュ名（ファイル名など）。</param>
    public static PmxLoadResult Load(byte[] data, PmxOptions? options = null, string? fallbackName = null)
    {
        options ??= new PmxOptions();
        var r = new Reader(data);
        if (data.Length < 9 || data[0] != 'P' || data[1] != 'M' || data[2] != 'X' || data[3] != ' ')
            throw new XFormatException("PMX ファイルではありません（先頭が \"PMX \" ではない）");
        r.Pos = 4;
        float version = r.F32();
        if (version < 2.0f || version > 2.1f + 1e-4f)
            throw new XFormatException($"未対応の PMX バージョンです ({version:0.0})");
        int globalsCount = r.U8();
        var globals = r.Bytes(globalsCount);
        if (globalsCount < 8) throw new XFormatException("PMX のヘッダーが壊れています");
        r.Encoding = globals[0] == 1 ? new UTF8Encoding(false) : Encoding.Unicode;
        int addUv = globals[1];
        r.VertexIndexSize = globals[2];
        r.TextureIndexSize = globals[3];
        r.MaterialIndexSize = globals[4];
        r.BoneIndexSize = globals[5];
        r.MorphIndexSize = globals[6];
        r.RigidIndexSize = globals[7];

        var warnings = new List<string>();
        string name = r.Text();
        r.Text(); // 英名
        r.Text(); // コメント
        r.Text(); // 英語コメント

        // 頂点
        int vertexCount = r.I32Count("頂点");
        var mesh = new XMesh { Name = !string.IsNullOrWhiteSpace(name) ? name : !string.IsNullOrWhiteSpace(fallbackName) ? fallbackName : "PMX" };
        int sdef = 0, weighted = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            var pos = r.Vec3() * options.Scale;
            var normal = r.Vec3();
            var uv = r.Vec2();
            r.Skip(16 * addUv);
            int weightType = r.U8();
            switch (weightType)
            {
                case 0: r.Skip(r.BoneIndexSize); break;
                case 1: r.Skip(r.BoneIndexSize * 2 + 4); weighted++; break;
                case 2: r.Skip(r.BoneIndexSize * 4 + 16); weighted++; break;
                case 3: r.Skip(r.BoneIndexSize * 2 + 4 + 36); weighted++; sdef++; break;
                case 4: r.Skip(r.BoneIndexSize * 4 + 16); weighted++; break;
                default: throw new XFormatException($"PMX の頂点 {i} のウェイト形式が不正です ({weightType})");
            }
            r.Skip(4); // エッジ倍率
            mesh.Positions.Add(pos);
            mesh.Normals.Add(normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY);
            mesh.TexCoords.Add(uv);
        }

        // 面（頂点インデックスの並び。3 つで 1 枚）
        int indexCount = r.I32Count("面");
        if (indexCount % 3 != 0) warnings.Add($"面のインデックス数 ({indexCount}) が 3 の倍数ではありません。余りは捨てます");
        var indices = new int[indexCount];
        for (int i = 0; i < indexCount; i++) indices[i] = r.VertexIndex();

        // テクスチャ
        int textureCount = r.I32Count("テクスチャ");
        var textures = new string[textureCount];
        for (int i = 0; i < textureCount; i++) textures[i] = r.Text();

        // マテリアル（面は先頭から順に、マテリアルごとの面数ぶんずつ使う）
        int materialCount = r.I32Count("マテリアル");
        int cursor = 0, dropped = 0, spheres = 0, toons = 0;
        for (int m = 0; m < materialCount; m++)
        {
            r.Text(); // 名前
            r.Text(); // 英名
            var diffuse = r.Vec4();
            var specular = r.Vec3();
            float power = r.F32();
            r.Vec3(); // 環境色
            r.U8();   // 描画フラグ
            r.Skip(16 + 4); // エッジ色・サイズ
            int tex = r.Index(r.TextureIndexSize);
            if (r.Index(r.TextureIndexSize) >= 0) spheres++;
            r.U8(); // スフィアモード
            // 共有 Toon フラグ: 1 なら続きは共有 Toon 番号 (1 バイト)、0 ならテクスチャのインデックス
            if (r.U8() == 1) { r.U8(); toons++; }
            else if (r.Index(r.TextureIndexSize) >= 0) toons++;
            r.Text(); // メモ
            int surface = r.I32();

            mesh.Materials.Add(new XMaterial
            {
                FaceColor = diffuse,
                Specular = specular,
                Power = power,
                Texture = tex >= 0 && tex < textures.Length ? textures[tex] : null,
            });

            int end = Math.Min(indexCount - indexCount % 3, cursor + Math.Max(0, surface));
            for (; cursor + 3 <= end; cursor += 3)
            {
                int a = indices[cursor], b = indices[cursor + 1], c = indices[cursor + 2];
                if (a < 0 || b < 0 || c < 0 || a >= vertexCount || b >= vertexCount || c >= vertexCount || a == b || b == c || a == c)
                {
                    dropped++;
                    continue;
                }
                mesh.Faces.Add(new XFace([a, b, c], m) { NormalIndices = [a, b, c] });
            }
            cursor = Math.Max(cursor, end);
        }
        if (cursor < indexCount - indexCount % 3)
            warnings.Add($"どのマテリアルにも属さない面が {(indexCount - cursor) / 3} 枚あったので捨てました");
        if (dropped > 0) warnings.Add($"不正な面（範囲外や潰れた三角形）を {dropped} 枚取り除きました");
        if (mesh.Materials.Count == 0) mesh.EnsureMaterial();

        // ここから先はエディタでは使わない。数だけ数えて警告にする
        var skipped = new List<string>();
        if (weighted > 0) skipped.Add($"複数ボーンのウェイト（頂点 {weighted}{(sdef > 0 ? $"、うち SDEF {sdef}" : "")}）");
        if (spheres > 0) skipped.Add($"スフィアマップ {spheres}");
        if (toons > 0) skipped.Add($"トゥーン {toons}");
        bool rig = weighted > 0;
        try
        {
            int bones = SkipBones(r);
            int morphs = SkipMorphs(r, version);
            int frames = SkipFrames(r);
            int bodies = SkipRigidBodies(r);
            int joints = SkipJoints(r);
            if (bones > 1) skipped.Add($"ボーン {bones}");
            if (morphs > 0) skipped.Add($"モーフ {morphs}");
            if (bodies > 0) skipped.Add($"剛体 {bodies}");
            if (joints > 0) skipped.Add($"ジョイント {joints}");
            if (frames > 2) skipped.Add($"表示枠 {frames}");
            if (version > 2.0f && r.Pos < data.Length) skipped.Add("ソフトボディ");
            rig |= bones > 1 || morphs > 0 || bodies > 0 || joints > 0;
        }
        catch (Exception ex) when (ex is XFormatException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            skipped.Add("ボーン以降のデータ（途中で読めなくなりました）");
            rig = true;
        }
        if (skipped.Count > 0)
            warnings.Add("読み込まなかったもの（保存し直すと失われます）: " + string.Join("、", skipped));

        var scene = new XScene();
        scene.Meshes.Add(mesh);
        return new PmxLoadResult(scene, version, mesh.Name, warnings, rig);
    }

    private static int SkipBones(Reader r)
    {
        int count = r.I32Count("ボーン");
        for (int i = 0; i < count; i++)
        {
            r.Text();
            r.Text();
            r.Skip(12);                 // 位置
            r.Skip(r.BoneIndexSize);    // 親
            r.Skip(4);                  // 変形階層
            int flags = r.U16();
            r.Skip((flags & 0x0001) != 0 ? r.BoneIndexSize : 12); // 接続先
            if ((flags & 0x0300) != 0) r.Skip(r.BoneIndexSize + 4); // 付与
            if ((flags & 0x0400) != 0) r.Skip(12);                  // 軸固定
            if ((flags & 0x0800) != 0) r.Skip(24);                  // ローカル軸
            if ((flags & 0x2000) != 0) r.Skip(4);                   // 外部親
            if ((flags & 0x0020) != 0)                              // IK
            {
                r.Skip(r.BoneIndexSize + 4 + 4);
                int links = r.I32Count("IK リンク");
                for (int k = 0; k < links; k++)
                {
                    r.Skip(r.BoneIndexSize);
                    if (r.U8() != 0) r.Skip(24);
                }
            }
        }
        return count;
    }

    private static int SkipMorphs(Reader r, float version)
    {
        int count = r.I32Count("モーフ");
        for (int i = 0; i < count; i++)
        {
            r.Text();
            r.Text();
            r.U8(); // パネル
            int type = r.U8();
            int offsets = r.I32Count("モーフのオフセット");
            int each = type switch
            {
                0 => r.MorphIndexSize + 4,                    // グループ
                1 => r.VertexIndexSize + 12,                  // 頂点
                2 => r.BoneIndexSize + 12 + 16,               // ボーン
                >= 3 and <= 7 => r.VertexIndexSize + 16,      // UV / 追加 UV
                8 => r.MaterialIndexSize + 1 + 16 + 12 + 4 + 12 + 16 + 4 + 16 + 16 + 16, // 材質
                9 when version > 2.0f => r.MorphIndexSize + 4,             // フリップ
                10 when version > 2.0f => r.RigidIndexSize + 1 + 12 + 12,  // インパルス
                _ => throw new XFormatException($"未知のモーフ種別です ({type})"),
            };
            r.Skip((long)each * offsets);
        }
        return count;
    }

    private static int SkipFrames(Reader r)
    {
        int count = r.I32Count("表示枠");
        for (int i = 0; i < count; i++)
        {
            r.Text();
            r.Text();
            r.U8();
            int elements = r.I32Count("表示枠の要素");
            for (int k = 0; k < elements; k++)
                r.Skip(r.U8() == 0 ? r.BoneIndexSize : r.MorphIndexSize);
        }
        return count;
    }

    private static int SkipRigidBodies(Reader r)
    {
        int count = r.I32Count("剛体");
        for (int i = 0; i < count; i++)
        {
            r.Text();
            r.Text();
            r.Skip(r.BoneIndexSize + 1 + 2 + 1 + 36 + 20 + 1);
        }
        return count;
    }

    private static int SkipJoints(Reader r)
    {
        int count = r.I32Count("ジョイント");
        for (int i = 0; i < count; i++)
        {
            r.Text();
            r.Text();
            r.U8();
            r.Skip(r.RigidIndexSize * 2 + 12 * 8);
        }
        return count;
    }

    private sealed class Reader(byte[] data)
    {
        public int Pos;
        public Encoding Encoding = Encoding.Unicode;
        public int VertexIndexSize = 4, TextureIndexSize = 4, MaterialIndexSize = 4, BoneIndexSize = 4, MorphIndexSize = 4, RigidIndexSize = 4;

        private void Need(long n)
        {
            if (n < 0 || Pos + n > data.Length) throw new XFormatException("PMX のデータが途中で終わっています");
        }

        public void Skip(long n)
        {
            Need(n);
            Pos += (int)n;
        }

        public byte[] Bytes(int n)
        {
            Need(n);
            var b = data.AsSpan(Pos, n).ToArray();
            Pos += n;
            return b;
        }

        public int U8() { Need(1); return data[Pos++]; }
        public int U16() { Need(2); int v = BitConverter.ToUInt16(data, Pos); Pos += 2; return v; }
        public int I32() { Need(4); int v = BitConverter.ToInt32(data, Pos); Pos += 4; return v; }
        public float F32() { Need(4); float v = BitConverter.ToSingle(data, Pos); Pos += 4; return v; }
        public Vector2 Vec2() => new(F32(), F32());
        public Vector3 Vec3() => new(F32(), F32(), F32());
        public Vector4 Vec4() => new(F32(), F32(), F32(), F32());

        /// <summary>要素数。負や、残りのバイト数より明らかに多い値は壊れているとみなす。</summary>
        public int I32Count(string what)
        {
            int n = I32();
            if (n < 0 || n > data.Length) throw new XFormatException($"PMX の{what}の数が不正です ({n})");
            return n;
        }

        public string Text()
        {
            int len = I32();
            if (len < 0) throw new XFormatException("PMX の文字列の長さが不正です");
            Need(len);
            var s = Encoding.GetString(data, Pos, len);
            Pos += len;
            return s;
        }

        /// <summary>頂点インデックスは 1・2 バイトなら符号なし。</summary>
        public int VertexIndex() => VertexIndexSize switch
        {
            1 => U8(),
            2 => U16(),
            _ => I32(),
        };

        /// <summary>頂点以外のインデックスは符号付き（-1 = なし）。</summary>
        public int Index(int size)
        {
            switch (size)
            {
                case 1: Need(1); return (sbyte)data[Pos++];
                case 2: Need(2); int v = BitConverter.ToInt16(data, Pos); Pos += 2; return v;
                default: return I32();
            }
        }
    }
}
