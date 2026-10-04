namespace BveTsStructureEditor.Core.Imaging;

/// <summary>
/// プレビュー表示用の最小限の DDS デコーダー（DXT1 / DXT3 / DXT5 / 非圧縮 24・32bit）。
/// 先頭のミップマップだけを BGRA32 で返す。
/// </summary>
public static class DdsDecoder
{
    public sealed record Image(int Width, int Height, byte[] Bgra);

    public static Image Decode(byte[] data)
    {
        if (data.Length < 128 || data[0] != 'D' || data[1] != 'D' || data[2] != 'S' || data[3] != ' ')
            throw new InvalidDataException("DDS ファイルではありません");
        int height = BitConverter.ToInt32(data, 12);
        int width = BitConverter.ToInt32(data, 16);
        int pfFlags = BitConverter.ToInt32(data, 80);
        string fourCC = System.Text.Encoding.ASCII.GetString(data, 84, 4);
        int bitCount = BitConverter.ToInt32(data, 88);
        uint rMask = BitConverter.ToUInt32(data, 92), gMask = BitConverter.ToUInt32(data, 96),
             bMask = BitConverter.ToUInt32(data, 100), aMask = BitConverter.ToUInt32(data, 104);
        int offset = 128;
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384) throw new InvalidDataException("DDS の寸法が不正です");

        var dst = new byte[width * height * 4];
        if ((pfFlags & 0x4) != 0)
        {
            int mode = fourCC switch { "DXT1" => 1, "DXT3" => 3, "DXT5" => 5, _ => 0 };
            if (mode == 0) throw new InvalidDataException($"未対応の DDS 形式です ({fourCC})");
            DecodeBlocks(data, offset, width, height, mode, dst);
        }
        else
        {
            int bpp = bitCount / 8;
            if (bpp is not (3 or 4)) throw new InvalidDataException($"未対応の DDS 形式です ({bitCount}bit)");
            if (offset + width * height * bpp > data.Length) throw new InvalidDataException("DDS のデータが足りません");
            bool hasAlpha = (pfFlags & 0x1) != 0 && aMask != 0;
            for (int i = 0; i < width * height; i++)
            {
                uint px = bpp == 4 ? BitConverter.ToUInt32(data, offset + i * 4)
                    : (uint)(data[offset + i * 3] | data[offset + i * 3 + 1] << 8 | data[offset + i * 3 + 2] << 16);
                dst[i * 4] = Channel(px, bMask);
                dst[i * 4 + 1] = Channel(px, gMask);
                dst[i * 4 + 2] = Channel(px, rMask);
                dst[i * 4 + 3] = hasAlpha ? Channel(px, aMask) : (byte)255;
            }
        }
        return new Image(width, height, dst);
    }

    private static byte Channel(uint px, uint mask)
    {
        if (mask == 0) return 0;
        int shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        uint max = mask >> shift;
        return (byte)(((px & mask) >> shift) * 255 / max);
    }

    private static void DecodeBlocks(byte[] src, int offset, int width, int height, int mode, byte[] dst)
    {
        int blockSize = mode == 1 ? 8 : 16;
        Span<byte> colors = stackalloc byte[16];
        Span<byte> alphas = stackalloc byte[8];
        for (int by = 0; by < (height + 3) / 4; by++)
        for (int bx = 0; bx < (width + 3) / 4; bx++)
        {
            if (offset + blockSize > src.Length) return;
            int colorOff = mode == 1 ? offset : offset + 8;
            ushort c0 = BitConverter.ToUInt16(src, colorOff);
            ushort c1 = BitConverter.ToUInt16(src, colorOff + 2);
            Rgb565(c0, colors[..4]);
            Rgb565(c1, colors[4..8]);
            if (c0 > c1 || mode != 1)
            {
                for (int k = 0; k < 3; k++)
                {
                    colors[8 + k] = (byte)((2 * colors[k] + colors[4 + k]) / 3);
                    colors[12 + k] = (byte)((colors[k] + 2 * colors[4 + k]) / 3);
                }
                colors[11] = 255;
                colors[15] = 255;
            }
            else
            {
                for (int k = 0; k < 3; k++) colors[8 + k] = (byte)((colors[k] + colors[4 + k]) / 2);
                colors[11] = 255;
                colors[12] = colors[13] = colors[14] = colors[15] = 0;
            }
            uint bits = BitConverter.ToUInt32(src, colorOff + 4);

            if (mode == 5)
            {
                byte a0 = src[offset], a1 = src[offset + 1];
                alphas[0] = a0;
                alphas[1] = a1;
                if (a0 > a1)
                    for (int k = 1; k < 7; k++) alphas[k + 1] = (byte)(((7 - k) * a0 + k * a1) / 7);
                else
                {
                    for (int k = 1; k < 5; k++) alphas[k + 1] = (byte)(((5 - k) * a0 + k * a1) / 5);
                    alphas[6] = 0;
                    alphas[7] = 255;
                }
            }
            ulong alphaBits = mode == 5 ? BitConverter.ToUInt64(src, offset + 2) & 0xFFFFFFFFFFFFUL : 0;

            for (int py = 0; py < 4; py++)
            for (int px = 0; px < 4; px++)
            {
                int x = bx * 4 + px, y = by * 4 + py;
                if (x >= width || y >= height) continue;
                int i = py * 4 + px;
                int ci = (int)(bits >> (2 * i)) & 3;
                int o = (y * width + x) * 4;
                // colors は RGBA で持っているので BGRA に並べ替える
                dst[o] = colors[ci * 4 + 2];
                dst[o + 1] = colors[ci * 4 + 1];
                dst[o + 2] = colors[ci * 4];
                dst[o + 3] = mode switch
                {
                    3 => (byte)(((BitConverter.ToUInt64(src, offset) >> (4 * i)) & 0xF) * 17),
                    5 => alphas[(int)(alphaBits >> (3 * i)) & 7],
                    _ => colors[ci * 4 + 3],
                };
            }
            offset += blockSize;
        }
    }

    private static void Rgb565(ushort c, Span<byte> rgba)
    {
        rgba[0] = (byte)(((c >> 11) & 31) * 255 / 31);
        rgba[1] = (byte)(((c >> 5) & 63) * 255 / 63);
        rgba[2] = (byte)((c & 31) * 255 / 31);
        rgba[3] = 255;
    }
}
