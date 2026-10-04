using System.IO.Compression;
using System.Text;
using BveTsStructureEditor.Core.Format;

namespace BveTsStructureEditor.Core.Tests;

/// <summary>
/// テキスト .x をトークン単位でバイナリ .x に置き換える、テスト専用のエンコーダー。
/// 実際の書き出し元と同じく、整数は INTEGER_LIST、小数は FLOAT_LIST にまとめる。
/// </summary>
internal static class BinaryEncoder
{
    public static byte[] FromText(string text, bool compressed = false)
    {
        var body = text[(text.IndexOf('\n') + 1)..];
        var lexer = new XTextLexer(body);
        var enc = XFile.ShiftJis;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        var ints = new List<uint>();
        var floats = new List<float>();
        void Flush()
        {
            if (ints.Count > 0)
            {
                w.Write((ushort)6); w.Write((uint)ints.Count);
                foreach (var i in ints) w.Write(i);
                ints.Clear();
            }
            if (floats.Count > 0)
            {
                w.Write((ushort)7); w.Write((uint)floats.Count);
                foreach (var f in floats) w.Write(f);
                floats.Clear();
            }
        }

        while (true)
        {
            var t = lexer.Next();
            if (t.Kind == XTokenKind.Eof) break;
            if (t.Kind == XTokenKind.Number)
            {
                bool isInt = t.Text!.IndexOfAny(['.', 'e', 'E']) < 0;
                if (isInt && floats.Count > 0) Flush();
                if (!isInt && ints.Count > 0) Flush();
                if (isInt) ints.Add((uint)(long)t.Number); else floats.Add((float)t.Number);
                continue;
            }
            if (t.Kind == XTokenKind.Separator) continue;
            Flush();
            switch (t.Kind)
            {
                case XTokenKind.Name:
                {
                    var b = enc.GetBytes(t.Text!);
                    w.Write((ushort)1); w.Write((uint)b.Length); w.Write(b);
                    break;
                }
                case XTokenKind.String:
                {
                    var b = enc.GetBytes(t.Text!);
                    w.Write((ushort)2); w.Write((uint)b.Length); w.Write(b); w.Write((ushort)20);
                    break;
                }
                case XTokenKind.Guid:
                    w.Write((ushort)5); w.Write(Guid.Parse(t.Text!).ToByteArray());
                    break;
                case XTokenKind.OBrace: w.Write((ushort)10); break;
                case XTokenKind.CBrace: w.Write((ushort)11); break;
                case XTokenKind.Template: w.Write((ushort)31); break;
            }
        }
        Flush();
        w.Flush();
        var payload = ms.ToArray();

        if (!compressed)
            return [.. Encoding.ASCII.GetBytes("xof 0303bin 0032"), .. payload];

        // チャンクごとに独立した deflate（後方参照はチャンク内だけ）。形式としては正しい MSZIP。
        using var outMs = new MemoryStream();
        using var ow = new BinaryWriter(outMs);
        ow.Write(Encoding.ASCII.GetBytes("xof 0303bzip0032"));
        ow.Write((uint)(payload.Length + 16));
        for (int i = 0; i < payload.Length; i += 32768)
        {
            int n = Math.Min(32768, payload.Length - i);
            using var cms = new MemoryStream();
            using (var ds = new DeflateStream(cms, CompressionLevel.Optimal, leaveOpen: true))
                ds.Write(payload, i, n);
            var comp = cms.ToArray();
            ow.Write((ushort)n);
            ow.Write((ushort)(comp.Length + 2));
            ow.Write((byte)'C'); ow.Write((byte)'K');
            ow.Write(comp);
        }
        ow.Flush();
        return outMs.ToArray();
    }
}
