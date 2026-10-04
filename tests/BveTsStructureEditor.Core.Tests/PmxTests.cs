using System.Numerics;
using System.Text;
using BveTsStructureEditor.Core.Editing;
using BveTsStructureEditor.Core.Format;
using BveTsStructureEditor.Core.Format.Pmx;
using BveTsStructureEditor.Core.Model;

namespace BveTsStructureEditor.Core.Tests;

public class PmxTests
{
    [Fact]
    public void Written_Pmx_Reads_Back_With_Same_Shape_And_Materials()
    {
        var box = Primitives.Box(2, 3, 4);
        box.Name = "駅舎";
        box.Materials[0].Texture = "tex/壁.png";
        box.Materials[0].FaceColor = new Vector4(0.8f, 0.6f, 0.4f, 1);
        var sign = Primitives.Plane(1, 1, vertical: true);
        sign.Materials[0].FaceColor = new Vector4(1, 0, 0, 0.5f);
        var scene = new XScene();
        scene.Meshes.Add(box);
        scene.Meshes.Add(sign);

        var bytes = PmxWriter.Write(scene, "テスト");
        var r = PmxReader.Load(bytes);

        Assert.Empty(r.Warnings);
        Assert.False(r.HasRigData);
        Assert.Equal("テスト", r.ModelName);
        var mesh = Assert.Single(r.Scene.Meshes);
        Assert.Equal(2, mesh.Materials.Count);
        Assert.Equal("tex/壁.png", mesh.Materials[0].Texture);
        Assert.Null(mesh.Materials[1].Texture);
        Assert.Equal(new Vector4(1, 0, 0, 0.5f), mesh.Materials[1].FaceColor);
        // 四角形 7 枚 → 三角形 14 枚。箱は面ごとに法線が違うので頂点は 24 + 4
        Assert.Equal(14, mesh.Faces.Count);
        Assert.Equal(28, mesh.Positions.Count);
        Assert.True(mesh.HasExplicitNormals);

        // 面の向き（外向き）と大きさが保たれている
        var boxFaces = mesh.Faces.Where(f => f.Material == 0).ToList();
        var center = new Vector3(0, 1.5f, 0);
        foreach (var f in boxFaces)
            Assert.True(Vector3.Dot(mesh.FaceNormal(f), mesh.FaceCenter(f) - center) > 0);
        var max = mesh.Positions.Aggregate(new Vector3(float.MinValue), Vector3.Max);
        Assert.Equal(new Vector3(1, 3, 2), max);
        // 法線は面の向きと一致
        foreach (var f in mesh.Faces)
            Assert.True(Vector3.Dot(mesh.FaceNormal(f), mesh.Normals[f.NormalIndices![0]]) > 0.99f);
    }

    [Fact]
    public void Scale_Is_Applied_On_Read_And_Undone_On_Write()
    {
        var scene = new XScene();
        scene.Meshes.Add(Primitives.Box(8, 8, 8));
        var opt = new PmxOptions { Scale = 0.08f };
        var bytes = PmxWriter.Write(scene, "s", opt);
        var raw = PmxReader.Load(bytes); // 換算なしで読むと 100 単位
        Assert.Equal(100f, raw.Scene.Meshes[0].Positions.Max(p => p.Y), 2);
        var scaled = PmxReader.Load(bytes, opt);
        Assert.Equal(8f, scaled.Scene.Meshes[0].Positions.Max(p => p.Y), 3);
    }

    [Fact]
    public void Reads_Rigged_Model_With_Small_Indices_And_Reports_Skipped_Data()
    {
        var bytes = RiggedPmx();
        var r = PmxReader.Load(bytes);
        var mesh = Assert.Single(r.Scene.Meshes);
        Assert.Equal("リグ付き", mesh.Name);
        Assert.Equal(4, mesh.Positions.Count);
        Assert.Equal(2, mesh.Faces.Count);
        Assert.Equal([0, 1, 2], mesh.Faces[0].Indices);
        Assert.Equal(1, mesh.Faces[1].Material);
        Assert.Equal("a.png", mesh.Materials[0].Texture);
        Assert.Equal(new Vector2(0.25f, 0.75f), mesh.TexCoords[1]);
        Assert.True(r.HasRigData);
        var w = Assert.Single(r.Warnings);
        Assert.Contains("ボーン 2", w);
        Assert.Contains("モーフ 2", w);
        Assert.Contains("剛体 1", w);
        Assert.Contains("ジョイント 1", w);
        Assert.Contains("SDEF 1", w);
        Assert.Contains("スフィアマップ 1", w);
        Assert.Contains("トゥーン 1", w);
    }

    /// <summary>実際に配布されている PMX（user 提供）。共有 Toon フラグの読み落としで開けなかった。</summary>
    [Theory]
    [InlineData("kodan.pmx", 71, 50, true)]
    [InlineData("WallUnder_QN_25m_hukusen.pmx", 48, 18, false)]
    [InlineData("WallUnder_QN_25m_tansen.pmx", 84, 36, false)]
    public void Real_Pmx_Files_Load_To_The_End(string name, int vertices, int faces, bool rig)
    {
        if (Fixtures.Missing(name)) return; // 配布物のテストデータはリポジトリに入れていない
        var r = PmxReader.Load(Fixtures.Path(name));
        Assert.Equal(vertices, r.Scene.TotalVertices);
        Assert.Equal(faces, r.Scene.TotalFaces);
        Assert.Equal(rig, r.HasRigData);
        // 途中で読めなくなった・面が落ちた、という警告が出ないこと（最後まで正しく読めている）
        Assert.DoesNotContain(r.Warnings, w => w.Contains("途中") || w.Contains("不正") || w.Contains("捨て"));

        // 書き出し → 読み直しで形とマテリアルが保たれる
        var again = PmxReader.Load(PmxWriter.Write(r.Scene, name));
        Assert.Equal(faces, again.Scene.TotalFaces);
        Assert.Equal(r.Scene.Meshes[0].Materials.Select(m => m.Texture), again.Scene.Meshes[0].Materials.Select(m => m.Texture));
    }

    [Fact]
    public void Rejects_Non_Pmx()
    {
        Assert.Throws<XFormatException>(() => PmxReader.Load(Encoding.ASCII.GetBytes("xof 0303txt 0032 hello")));
    }

    /// <summary>
    /// 実際の配布モデルに近い PMX を手で組み立てる: UTF-8、追加 UV 1、頂点インデックス 1 バイト、
    /// ボーン 2 バイト、BDEF2 と SDEF、IK 付きボーン、頂点・材質モーフ、剛体とジョイント。
    /// </summary>
    private static byte[] RiggedPmx()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var enc = new UTF8Encoding(false);
        void Text(string s) { var b = enc.GetBytes(s); w.Write(b.Length); w.Write(b); }
        void F(params float[] v) { foreach (var x in v) w.Write(x); }

        w.Write(Encoding.ASCII.GetBytes("PMX "));
        w.Write(2.0f);
        w.Write((byte)8);
        // UTF-8, 追加UV 1, 頂点idx 1, テクスチャ 1, 材質 1, ボーン 2, モーフ 1, 剛体 1
        w.Write(new byte[] { 1, 1, 1, 1, 1, 2, 1, 1 });
        Text("リグ付き"); Text("rigged"); Text(""); Text("");

        w.Write(4);
        var uv = new[] { (0f, 0f), (0.25f, 0.75f), (1f, 1f), (1f, 0f) };
        for (int i = 0; i < 4; i++)
        {
            F(i, i % 2, 0);  // 位置
            F(0, 0, -1);     // 法線
            F(uv[i].Item1, uv[i].Item2);
            F(0, 0, 0, 0);   // 追加 UV
            if (i == 3)
            {
                w.Write((byte)3);                 // SDEF
                w.Write((short)0); w.Write((short)1); F(0.5f);
                F(0, 0, 0, 0, 0, 0, 0, 0, 0);
            }
            else
            {
                w.Write((byte)1);                 // BDEF2
                w.Write((short)0); w.Write((short)1); F(0.3f);
            }
            F(1);                                 // エッジ
        }

        w.Write(6);
        foreach (var i in new byte[] { 0, 1, 2, 0, 2, 3 }) w.Write(i);

        w.Write(2);
        Text("a.png"); Text("sphere.spa");

        w.Write(2);
        for (int m = 0; m < 2; m++)
        {
            Text($"材質{m}"); Text("");
            F(1, 1, 1, 1); F(0, 0, 0); F(5); F(0.5f, 0.5f, 0.5f);
            w.Write((byte)0x10);
            F(0, 0, 0, 1); F(1);
            w.Write((sbyte)(m == 0 ? 0 : -1));    // テクスチャ
            w.Write((sbyte)(m == 0 ? 1 : -1));    // スフィア
            w.Write((byte)1);                     // スフィアモード
            if (m == 0) { w.Write((byte)1); w.Write((byte)3); }        // 共有 Toon (toon04)
            else { w.Write((byte)0); w.Write((sbyte)-1); }            // 個別 Toon なし
            Text("");
            w.Write(3);
        }

        // ボーン 2 本（2 本目は IK と付与付き）
        w.Write(2);
        Text("センター"); Text("center"); F(0, 0, 0); w.Write((short)-1); w.Write(0); w.Write((ushort)0x001F); w.Write((short)1);
        Text("足ＩＫ"); Text("leg IK"); F(0, 1, 0); w.Write((short)0); w.Write(0);
        w.Write((ushort)(0x001E | 0x0020 | 0x0100 | 0x0400));
        F(0, 1, 0);                               // 接続先（位置）
        w.Write((short)0); F(1);                  // 付与
        F(1, 0, 0);                               // 軸固定
        w.Write((short)0); w.Write(40); F(2);     // IK ターゲット・ループ・制限角
        w.Write(1); w.Write((short)0); w.Write((byte)1); F(0, 0, 0, 1, 1, 1);

        // モーフ 2（頂点、材質）
        w.Write(2);
        Text("あ"); Text("a"); w.Write((byte)1); w.Write((byte)1); w.Write(2);
        for (int i = 0; i < 2; i++) { w.Write((byte)i); F(0, 0.1f, 0); }
        Text("照"); Text("light"); w.Write((byte)4); w.Write((byte)8); w.Write(1);
        w.Write((sbyte)-1); w.Write((byte)0); F(new float[4 + 3 + 1 + 3 + 4 + 1 + 4 + 4 + 4]);

        // 表示枠 2
        w.Write(2);
        Text("Root"); Text("Root"); w.Write((byte)1); w.Write(1); w.Write((byte)0); w.Write((short)0);
        Text("表情"); Text("Exp"); w.Write((byte)1); w.Write(1); w.Write((byte)1); w.Write((sbyte)0);

        // 剛体 1
        w.Write(1);
        Text("体"); Text("body"); w.Write((short)0); w.Write((byte)0); w.Write((ushort)0xFFFF); w.Write((byte)1);
        F(new float[9]); F(1, 0.5f, 0.5f, 0, 0.5f); w.Write((byte)0);

        // ジョイント 1
        w.Write(1);
        Text("J"); Text("J"); w.Write((byte)0); w.Write((sbyte)0); w.Write((sbyte)0); F(new float[24]);

        w.Flush();
        return ms.ToArray();
    }
}
