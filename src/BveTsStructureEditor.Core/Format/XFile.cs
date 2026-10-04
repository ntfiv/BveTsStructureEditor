using System.Text;
using BveTsStructureEditor.Core.Model;

namespace BveTsStructureEditor.Core.Format;

public sealed record XLoadResult(XScene Scene, XHeader Header, Encoding TextEncoding, IReadOnlyList<string> Warnings, string? DecodedText);

/// <summary>.x の読み込み・書き出しの入口。</summary>
public static class XFile
{
    static XFile() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Encoding ShiftJis => Encoding.GetEncoding(932);

    public static XLoadResult Load(string path) => Load(File.ReadAllBytes(path));

    public static XLoadResult Load(byte[] data)
    {
        var header = XHeader.Parse(data);
        byte[] body = data;
        int start = XHeader.Size;
        if (header.Compressed)
        {
            body = MsZip.Decompress(data);
            start = 0;
        }

        var warnings = new List<string>();
        IXLexer lexer;
        Encoding enc;
        string? text = null;
        if (header.Kind == XEncodingKind.Text)
        {
            enc = DetectEncoding(body.AsSpan(start));
            text = enc.GetString(body, start, body.Length - start);
            lexer = new XTextLexer(text);
        }
        else
        {
            enc = DetectEncoding(body.AsSpan(start));
            lexer = new XBinaryLexer(body, start, header.FloatBits, enc);
        }

        var roots = new XParser(lexer).ParseFile();
        var scene = new XSceneReader(warnings).Read(roots);
        if (scene.Meshes.Count == 0) warnings.Add("メッシュが 1 つも見つかりませんでした");
        return new XLoadResult(scene, header, enc, warnings, text is null ? null : "xof " + header.Version + "txt 0032\n" + text);
    }

    /// <summary>テキスト（ヘッダー込みの .x 本文）から読む。テキストエディタからの反映用。</summary>
    public static XLoadResult LoadText(string text)
    {
        if (!text.StartsWith("xof ", StringComparison.Ordinal))
            throw new XFormatException("先頭行は \"xof 0303txt 0032\" で始めてください");
        var nl = text.IndexOf('\n');
        var body = nl >= 0 ? text[(nl + 1)..] : "";
        var headerLine = (nl >= 0 ? text[..nl] : text).TrimEnd('\r').PadRight(XHeader.Size);
        var header = XHeader.Parse(Encoding.ASCII.GetBytes(headerLine[..XHeader.Size]));
        if (header.Kind != XEncodingKind.Text || header.Compressed)
            throw new XFormatException("テキストで編集できるのは \"txt \" 形式だけです");

        var warnings = new List<string>();
        var roots = new XParser(new XTextLexer(body)).ParseFile();
        var scene = new XSceneReader(warnings).Read(roots);
        if (scene.Meshes.Count == 0) warnings.Add("メッシュが 1 つも見つかりませんでした");
        return new XLoadResult(scene, header, Encoding.UTF8, warnings, text);
    }

    /// <summary>UTF-8 として正しく読めれば UTF-8、そうでなければ Shift_JIS（BVE 界隈の既定）。</summary>
    public static Encoding DetectEncoding(ReadOnlySpan<byte> bytes)
    {
        bool nonAscii = false;
        foreach (var b in bytes)
            if (b >= 0x80) { nonAscii = true; break; }
        if (!nonAscii) return ShiftJis;
        try
        {
            new UTF8Encoding(false, true).GetCharCount(bytes);
            return new UTF8Encoding(false);
        }
        catch (DecoderFallbackException)
        {
            return ShiftJis;
        }
    }

    public static void Save(string path, XScene scene, XWriteOptions? options = null)
    {
        options ??= new XWriteOptions();
        var text = XTextWriter.Write(scene, options);
        File.WriteAllBytes(path, options.Encoding.GetBytes(text));
    }
}
