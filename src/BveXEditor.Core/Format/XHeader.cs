using System.Text;

namespace BveXEditor.Core.Format;

public enum XEncodingKind { Text, Binary }

/// <summary>
/// `xof 0303txt 0032` の 16 バイトヘッダー。
/// </summary>
public sealed record XHeader(string Version, XEncodingKind Kind, bool Compressed, int FloatBits)
{
    public const int Size = 16;

    public string Describe() =>
        (Kind == XEncodingKind.Text ? "テキスト" : "バイナリ") + (Compressed ? " (MSZIP 圧縮)" : "") +
        $" / ver {Version} / float{FloatBits}";

    public static XHeader Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size) throw new XFormatException("ファイルが短すぎます（ヘッダーがありません）");
        var s = Encoding.ASCII.GetString(data[..Size]);
        if (!s.StartsWith("xof ", StringComparison.Ordinal))
            throw new XFormatException("DirectX .x ファイルではありません（先頭が \"xof \" ではない）");

        var version = s.Substring(4, 4);
        var format = s.Substring(8, 4);
        (XEncodingKind kind, bool compressed) = format switch
        {
            "txt " => (XEncodingKind.Text, false),
            "bin " => (XEncodingKind.Binary, false),
            "tzip" => (XEncodingKind.Text, true),
            "bzip" => (XEncodingKind.Binary, true),
            _ => throw new XFormatException($"未知の形式です: \"{format}\""),
        };
        int floatBits = s.Substring(12, 4) switch
        {
            "0064" => 64,
            _ => 32,
        };
        return new XHeader(version, kind, compressed, floatBits);
    }
}

public sealed class XFormatException(string message) : Exception(message);
