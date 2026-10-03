namespace BveXEditor.Core.Format;

/// <summary>
/// テンプレートの定義を知らなくても読める、汎用のデータオブジェクト。
///
/// 値（数値・文字列）は出現順に平らに並べ、子オブジェクトと参照 `{ name }` は
/// 別のリストに出現順で持つ。Mesh などの意味づけは <see cref="XSceneReader"/> がする。
/// テキストとバイナリで区切り記号の出方が違っても、この形にすれば同じに扱える。
/// </summary>
public sealed class XDataObject
{
    public required string Template { get; init; }
    public string? Name { get; init; }
    public int Line { get; init; }
    public List<double> Numbers { get; } = [];
    public List<string> Strings { get; } = [];
    /// <summary>子オブジェクト (<see cref="XDataObject"/>) か参照名 (<see cref="string"/>) を出現順に。</summary>
    public List<object> Items { get; } = [];

    public IEnumerable<XDataObject> Children => Items.OfType<XDataObject>();

    public override string ToString() => Name is null ? Template : $"{Template} {Name}";
}

internal sealed class XParser
{
    private readonly IXLexer _lex;
    private XToken _cur;
    private bool _hasCur;

    public XParser(IXLexer lexer) => _lex = lexer;

    private XToken Peek()
    {
        if (!_hasCur) { _cur = _lex.Next(); _hasCur = true; }
        return _cur;
    }

    private XToken Take()
    {
        var t = Peek();
        _hasCur = false;
        return t;
    }

    public List<XDataObject> ParseFile()
    {
        var result = new List<XDataObject>();
        while (true)
        {
            var t = Take();
            switch (t.Kind)
            {
                case XTokenKind.Eof: return result;
                case XTokenKind.Template: SkipTemplate(); break;
                case XTokenKind.Name:
                    var obj = ParseObject(t);
                    if (obj != null) result.Add(obj);
                    break;
            }
        }
    }

    private void SkipTemplate()
    {
        // template 名 { ... } をまるごと読み飛ばす
        while (true)
        {
            var t = Take();
            if (t.Kind == XTokenKind.Eof) return;
            if (t.Kind == XTokenKind.OBrace) break;
        }
        int depth = 1;
        while (depth > 0)
        {
            var t = Take();
            if (t.Kind == XTokenKind.Eof) return;
            if (t.Kind == XTokenKind.OBrace) depth++;
            else if (t.Kind == XTokenKind.CBrace) depth--;
        }
    }

    /// <summary>テンプレート名のトークンを読んだ直後から、対応する `}` までを読む。</summary>
    private XDataObject? ParseObject(XToken templateToken)
    {
        string? name = null;
        if (Peek().Kind == XTokenKind.Name) name = Take().Text;
        if (Peek().Kind == XTokenKind.Guid) Take();
        if (Peek().Kind != XTokenKind.OBrace) return null;
        Take();

        var obj = new XDataObject { Template = templateToken.Text!, Name = name, Line = templateToken.Line };
        if (Peek().Kind == XTokenKind.Guid) Take();

        while (true)
        {
            var t = Take();
            switch (t.Kind)
            {
                case XTokenKind.Eof:
                case XTokenKind.CBrace:
                    return obj;
                case XTokenKind.Number:
                    obj.Numbers.Add(t.Number);
                    break;
                case XTokenKind.NumberList:
                    obj.Numbers.AddRange(t.List!);
                    break;
                case XTokenKind.String:
                    obj.Strings.Add(t.Text!);
                    break;
                case XTokenKind.OBrace:
                    var reference = ParseReference();
                    if (reference != null) obj.Items.Add(reference);
                    break;
                case XTokenKind.Name:
                    var next = Peek().Kind;
                    if (next is XTokenKind.Name or XTokenKind.OBrace or XTokenKind.Guid)
                    {
                        var child = ParseObject(t);
                        if (child != null) obj.Items.Add(child);
                    }
                    break;
            }
        }
    }

    private string? ParseReference()
    {
        string? name = null;
        while (true)
        {
            var t = Take();
            if (t.Kind is XTokenKind.Eof or XTokenKind.CBrace) return name;
            if (t.Kind == XTokenKind.Name && name == null) name = t.Text;
        }
    }
}
