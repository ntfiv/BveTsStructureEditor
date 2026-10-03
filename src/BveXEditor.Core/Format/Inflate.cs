namespace BveXEditor.Core.Format;

/// <summary>
/// RFC 1951 (raw deflate) の展開器。
///
/// .NET の DeflateStream はプリセット辞書を渡せないが、MSZIP ではチャンクをまたいで
/// 前のチャンクの出力を後方参照する。そこで出力先を 1 本のバッファにして、
/// 各チャンクを続けて展開することで参照が自然に届くようにしている。
/// </summary>
internal sealed class Inflater
{
    private readonly List<byte> _out;
    private byte[] _in = [];
    private int _pos;
    private int _bitBuf;
    private int _bitCnt;

    public Inflater(List<byte> output) => _out = output;

    /// <summary>`input[start..]` の deflate ストリームを最終ブロックまで展開し、消費した末尾位置を返す。</summary>
    public int Run(byte[] input, int start, int end)
    {
        _in = input;
        _pos = start;
        _limit = end;
        _bitBuf = 0;
        _bitCnt = 0;

        bool last;
        do
        {
            last = Bits(1) == 1;
            int type = Bits(2);
            switch (type)
            {
                case 0: Stored(); break;
                case 1: Codes(FixedLit, FixedDist); break;
                case 2: Dynamic(); break;
                default: throw new XFormatException("圧縮データが壊れています（不正なブロック型）");
            }
        } while (!last);
        return _pos;
    }

    private int _limit;

    private int Bits(int need)
    {
        int val = _bitBuf;
        while (_bitCnt < need)
        {
            if (_pos >= _limit) throw new XFormatException("圧縮データが途中で終わっています");
            val |= _in[_pos++] << _bitCnt;
            _bitCnt += 8;
        }
        _bitBuf = val >> need;
        _bitCnt -= need;
        return val & ((1 << need) - 1);
    }

    private void Stored()
    {
        _bitBuf = 0;
        _bitCnt = 0;
        if (_pos + 4 > _limit) throw new XFormatException("圧縮データが途中で終わっています");
        int len = _in[_pos] | (_in[_pos + 1] << 8);
        int nlen = _in[_pos + 2] | (_in[_pos + 3] << 8);
        _pos += 4;
        if (len != (~nlen & 0xffff)) throw new XFormatException("圧縮データが壊れています（stored 長）");
        if (_pos + len > _limit) throw new XFormatException("圧縮データが途中で終わっています");
        for (int i = 0; i < len; i++) _out.Add(_in[_pos++]);
    }

    private sealed class Huffman(short[] count, short[] symbol)
    {
        public readonly short[] Count = count;
        public readonly short[] Symbol = symbol;
    }

    private int Decode(Huffman h)
    {
        int code = 0, first = 0, index = 0;
        for (int len = 1; len <= 15; len++)
        {
            code |= Bits(1);
            int count = h.Count[len];
            if (code - count < first) return h.Symbol[index + (code - first)];
            index += count;
            first += count;
            first <<= 1;
            code <<= 1;
        }
        throw new XFormatException("圧縮データが壊れています（ハフマン符号）");
    }

    private static Huffman Build(ReadOnlySpan<short> lengths)
    {
        var count = new short[16];
        foreach (var l in lengths) count[l]++;
        var offs = new short[16];
        for (int len = 1; len < 15; len++) offs[len + 1] = (short)(offs[len] + count[len]);
        var symbol = new short[lengths.Length];
        for (short s = 0; s < lengths.Length; s++)
            if (lengths[s] != 0) symbol[offs[lengths[s]]++] = s;
        count[0] = 0;
        return new Huffman(count, symbol);
    }

    private static readonly short[] LenBase = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
    private static readonly short[] LenExtra = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
    private static readonly short[] DistBase = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
    private static readonly short[] DistExtra = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];

    private static readonly Huffman FixedLit;
    private static readonly Huffman FixedDist;

    static Inflater()
    {
        var l = new short[288];
        int i = 0;
        for (; i < 144; i++) l[i] = 8;
        for (; i < 256; i++) l[i] = 9;
        for (; i < 280; i++) l[i] = 7;
        for (; i < 288; i++) l[i] = 8;
        FixedLit = Build(l);
        var d = new short[30];
        Array.Fill(d, (short)5);
        FixedDist = Build(d);
    }

    private void Codes(Huffman lit, Huffman dist)
    {
        while (true)
        {
            int sym = Decode(lit);
            if (sym < 256) { _out.Add((byte)sym); continue; }
            if (sym == 256) return;
            sym -= 257;
            if (sym >= 29) throw new XFormatException("圧縮データが壊れています（長さ符号）");
            int len = LenBase[sym] + Bits(LenExtra[sym]);
            int ds = Decode(dist);
            if (ds >= 30) throw new XFormatException("圧縮データが壊れています（距離符号）");
            int d = DistBase[ds] + Bits(DistExtra[ds]);
            if (d > _out.Count) throw new XFormatException("圧縮データが壊れています（距離が遠すぎる）");
            int from = _out.Count - d;
            for (int k = 0; k < len; k++) _out.Add(_out[from + k]);
        }
    }

    private static readonly byte[] ClOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

    private void Dynamic()
    {
        int nlen = Bits(5) + 257;
        int ndist = Bits(5) + 1;
        int ncode = Bits(4) + 4;
        var lengths = new short[320];
        for (int i = 0; i < ncode; i++) lengths[ClOrder[i]] = (short)Bits(3);
        var lencode = Build(lengths.AsSpan(0, 19));
        Array.Clear(lengths);

        int index = 0;
        while (index < nlen + ndist)
        {
            int sym = Decode(lencode);
            if (sym < 16) { lengths[index++] = (short)sym; continue; }
            short prev = 0;
            int rep;
            if (sym == 16)
            {
                if (index == 0) throw new XFormatException("圧縮データが壊れています（反復符号）");
                prev = lengths[index - 1];
                rep = 3 + Bits(2);
            }
            else if (sym == 17) rep = 3 + Bits(3);
            else rep = 11 + Bits(7);
            if (index + rep > nlen + ndist) throw new XFormatException("圧縮データが壊れています（符号長）");
            while (rep-- > 0) lengths[index++] = prev;
        }
        var lit = Build(lengths.AsSpan(0, nlen));
        var dist = Build(lengths.AsSpan(nlen, ndist));
        Codes(lit, dist);
    }
}

/// <summary>
/// `tzip` / `bzip` の本体（ヘッダー 16 バイトの後ろ）を展開する。
///
/// レイアウト: DWORD 展開後の全体サイズ（ヘッダー込み）、以降チャンクの繰り返し
/// { WORD 展開後サイズ; WORD 圧縮サイズ; "CK"; deflate }。
/// 圧縮サイズが "CK" を含むかは書き出し元で揺れるので、どちらでも読めるようにしている。
/// </summary>
public static class MsZip
{
    public static byte[] Decompress(byte[] file)
    {
        int p = XHeader.Size;
        if (file.Length < p + 4) throw new XFormatException("圧縮ファイルが短すぎます");
        long total = BitConverter.ToUInt32(file, p) - XHeader.Size;
        p += 4;

        var output = new List<byte>(total is > 0 and < 256 * 1024 * 1024 ? (int)total : 4096);
        var inflater = new Inflater(output);
        while (p + 4 <= file.Length && (total <= 0 || output.Count < total))
        {
            int chunkRaw = BitConverter.ToUInt16(file, p);
            int chunkComp = BitConverter.ToUInt16(file, p + 2);
            int dataStart = p + 4;
            if (dataStart + 2 > file.Length || file[dataStart] != 'C' || file[dataStart + 1] != 'K')
                throw new XFormatException("圧縮チャンクの \"CK\" 署名が見つかりません");

            // チャンクの展開後サイズ (chunkRaw) は書き出し元で揺れるので照合しない。
            _ = chunkRaw;
            int end = inflater.Run(file, dataStart + 2, file.Length);

            // 次のチャンク位置: 圧縮サイズが CK を含む場合と含まない場合の両方を試す。
            int withCk = dataStart + chunkComp;
            int withoutCk = dataStart + 2 + chunkComp;
            p = PickNext(file, withCk, withoutCk, end);
        }
        return output.ToArray();
    }

    private static int PickNext(byte[] f, int a, int b, int consumed)
    {
        static bool LooksLikeChunk(byte[] f, int at) =>
            at + 6 <= f.Length && f[at + 4] == 'C' && f[at + 5] == 'K';

        if (a >= f.Length || LooksLikeChunk(f, a)) return a;
        if (b >= f.Length || LooksLikeChunk(f, b)) return b;
        return Math.Max(consumed, a);
    }
}
