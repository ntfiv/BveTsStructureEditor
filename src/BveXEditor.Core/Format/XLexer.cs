using System.Globalization;
using System.Text;

namespace BveXEditor.Core.Format;

public enum XTokenKind
{
    Eof,
    Name,
    String,
    Number,
    /// <summary>バイナリの INTEGER_LIST / FLOAT_LIST。<see cref="XToken.List"/> に値が入る。</summary>
    NumberList,
    Guid,
    OBrace,
    CBrace,
    Separator,
    /// <summary>`template` 宣言の始まり。</summary>
    Template,
    /// <summary>テンプレート宣言の中にだけ出る型キーワードや括弧など。値としては無視する。</summary>
    Other,
}

public readonly record struct XToken(XTokenKind Kind, string? Text = null, double Number = 0, double[]? List = null, int Line = 0);

internal interface IXLexer
{
    XToken Next();
}

/// <summary>テキスト形式 (`txt `) の字句解析。</summary>
internal sealed class XTextLexer : IXLexer
{
    private readonly string _s;
    private int _i;
    private int _line = 1;

    public XTextLexer(string source) => _s = source;

    public XToken Next()
    {
        while (_i < _s.Length)
        {
            char c = _s[_i];
            if (c == '\n') { _line++; _i++; continue; }
            if (char.IsWhiteSpace(c)) { _i++; continue; }
            if (c == '/' && _i + 1 < _s.Length && _s[_i + 1] == '/') { SkipLine(); continue; }
            if (c == '#') { SkipLine(); continue; }
            break;
        }
        if (_i >= _s.Length) return new XToken(XTokenKind.Eof, Line: _line);

        char ch = _s[_i];
        int line = _line;
        switch (ch)
        {
            case '{': _i++; return new XToken(XTokenKind.OBrace, Line: line);
            case '}': _i++; return new XToken(XTokenKind.CBrace, Line: line);
            case ';':
            case ',': _i++; return new XToken(XTokenKind.Separator, Line: line);
            case '[': case ']': case '(': case ')': _i++; return new XToken(XTokenKind.Other, Line: line);
            case '"':
            {
                int start = ++_i;
                while (_i < _s.Length && _s[_i] != '"')
                {
                    if (_s[_i] == '\n') _line++;
                    _i++;
                }
                var text = _s[start.._i];
                if (_i < _s.Length) _i++;
                return new XToken(XTokenKind.String, text, Line: line);
            }
            case '<':
            {
                int start = ++_i;
                while (_i < _s.Length && _s[_i] != '>') _i++;
                var text = _s[start.._i];
                if (_i < _s.Length) _i++;
                return new XToken(XTokenKind.Guid, text.Trim(), Line: line);
            }
        }

        if (char.IsDigit(ch) || ch == '-' || ch == '+' || ch == '.')
        {
            int start = _i;
            _i++;
            while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] is '.' or 'e' or 'E' or '-' or '+')) _i++;
            var text = _s[start.._i];
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                return new XToken(XTokenKind.Number, text, v, Line: line);
            // `1.#QNAN0` のような壊れた値は 0 として扱う
            while (_i < _s.Length && !IsDelimiter(_s[_i])) _i++;
            return new XToken(XTokenKind.Number, text, 0, Line: line);
        }

        if (IsNameStart(ch))
        {
            int start = _i;
            while (_i < _s.Length && IsNamePart(_s[_i])) _i++;
            var name = _s[start.._i];
            if (name == "template") return new XToken(XTokenKind.Template, name, Line: line);
            return new XToken(XTokenKind.Name, name, Line: line);
        }

        _i++;
        return new XToken(XTokenKind.Other, ch.ToString(), Line: line);
    }

    private void SkipLine()
    {
        while (_i < _s.Length && _s[_i] != '\n') _i++;
    }

    private static bool IsDelimiter(char c) => char.IsWhiteSpace(c) || c is ';' or ',' or '{' or '}';
    private static bool IsNameStart(char c) => char.IsLetter(c) || c == '_' || c > 127;
    private static bool IsNamePart(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' || c > 127;
}

/// <summary>バイナリ形式 (`bin `) の字句解析。</summary>
internal sealed class XBinaryLexer : IXLexer
{
    private readonly byte[] _b;
    private int _p;
    private readonly int _floatBytes;
    private readonly Encoding _enc;

    public XBinaryLexer(byte[] data, int start, int floatBits, Encoding enc)
    {
        _b = data;
        _p = start;
        _floatBytes = floatBits == 64 ? 8 : 4;
        _enc = enc;
    }

    public XToken Next()
    {
        if (_p + 2 > _b.Length) return new XToken(XTokenKind.Eof);

        int tok = U16();
        switch (tok)
        {
            case 1: // NAME
            {
                int n = (int)U32();
                var s = Str(n);
                return new XToken(XTokenKind.Name, s);
            }
            case 2: // STRING
            {
                int n = (int)U32();
                var s = Str(n);
                if (_p + 2 <= _b.Length) U16(); // 終端トークン (; か ,)
                return new XToken(XTokenKind.String, s.TrimEnd('\0'));
            }
            case 3: return new XToken(XTokenKind.Number, Number: U32());
            case 5:
            {
                Need(16);
                var g = new Guid(_b.AsSpan(_p, 16));
                _p += 16;
                return new XToken(XTokenKind.Guid, g.ToString());
            }
            case 6:
            {
                int n = (int)U32();
                Need((long)n * 4);
                var list = new double[n];
                for (int i = 0; i < n; i++) list[i] = U32();
                return new XToken(XTokenKind.NumberList, List: list);
            }
            case 7:
            {
                int n = (int)U32();
                Need((long)n * _floatBytes);
                var list = new double[n];
                for (int i = 0; i < n; i++)
                {
                    list[i] = _floatBytes == 8 ? BitConverter.ToDouble(_b, _p) : BitConverter.ToSingle(_b, _p);
                    _p += _floatBytes;
                }
                return new XToken(XTokenKind.NumberList, List: list);
            }
            case 10: return new XToken(XTokenKind.OBrace);
            case 11: return new XToken(XTokenKind.CBrace);
            case 19:
            case 20: return new XToken(XTokenKind.Separator);
            case 31: return new XToken(XTokenKind.Template);
            case >= 12 and <= 18:
            case >= 40 and <= 53:
                return new XToken(XTokenKind.Other);
            default:
                throw new XFormatException($"バイナリデータが壊れています（未知のトークン {tok}、位置 0x{_p - 2:X}）");
        }
    }

    private void Need(long n)
    {
        if (_p + n > _b.Length) throw new XFormatException("バイナリデータが途中で終わっています");
    }

    private int U16()
    {
        Need(2);
        int v = BitConverter.ToUInt16(_b, _p);
        _p += 2;
        return v;
    }

    private uint U32()
    {
        Need(4);
        uint v = BitConverter.ToUInt32(_b, _p);
        _p += 4;
        return v;
    }

    private string Str(int n)
    {
        Need(n);
        var s = _enc.GetString(_b, _p, n);
        _p += n;
        return s;
    }
}
