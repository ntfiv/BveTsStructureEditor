namespace BveTsStructureEditor.Core.Imaging;

/// <summary>画像ファイルの先頭だけを読んで幅と高さを返す（PNG / BMP / GIF / JPEG / DDS）。読めなければ null。</summary>
public static class ImageHeader
{
    public static (int Width, int Height)? ReadSize(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var head = new byte[Math.Min(fs.Length, 64 * 1024)];
            int n = fs.Read(head, 0, head.Length);
            return ReadSize(head.AsSpan(0, n));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static (int Width, int Height)? ReadSize(ReadOnlySpan<byte> d)
    {
        if (d.Length >= 24 && d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G')
            return (BigEndian32(d, 16), BigEndian32(d, 20));
        if (d.Length >= 26 && d[0] == 'B' && d[1] == 'M')
            return (LittleEndian32(d, 18), Math.Abs(LittleEndian32(d, 22)));
        if (d.Length >= 10 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F')
            return (d[6] | d[7] << 8, d[8] | d[9] << 8);
        if (d.Length >= 20 && d[0] == 'D' && d[1] == 'D' && d[2] == 'S' && d[3] == ' ')
            return (LittleEndian32(d, 16), LittleEndian32(d, 12));
        if (d.Length >= 4 && d[0] == 0xFF && d[1] == 0xD8)
            return Jpeg(d);
        return null;
    }

    private static (int, int)? Jpeg(ReadOnlySpan<byte> d)
    {
        int i = 2;
        while (i + 9 < d.Length)
        {
            if (d[i] != 0xFF) { i++; continue; }
            byte marker = d[i + 1];
            if (marker == 0xFF) { i++; continue; }
            int len = d[i + 2] << 8 | d[i + 3];
            // SOF0〜SOF15（DHT=C4, JPG=C8, DAC=CC は除く）
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return (d[i + 7] << 8 | d[i + 8], d[i + 5] << 8 | d[i + 6]);
            i += 2 + len;
        }
        return null;
    }

    private static int BigEndian32(ReadOnlySpan<byte> d, int o) => d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3];
    private static int LittleEndian32(ReadOnlySpan<byte> d, int o) => d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24;

    public static bool IsPowerOfTwo(int v) => v > 0 && (v & (v - 1)) == 0;
}
