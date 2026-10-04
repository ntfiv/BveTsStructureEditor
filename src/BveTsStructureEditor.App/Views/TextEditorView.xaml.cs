using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;
using BveTsStructureEditor.App.Document;
using BveTsStructureEditor.Core.Format;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Search;

namespace BveTsStructureEditor.App.Views;

/// <summary>
/// .x のテキストを直接書き換えて 3D に反映するタブ。
///
/// テキスト側に未反映の変更があるあいだは、3D 側の編集でテキストを上書きしない
/// （せっかく打った内容が消えないように）。代わりに「テキストが古い」と表示する。
/// </summary>
public partial class TextEditorView : UserControl
{
    private IEditorHost? _host;
    private bool _dirty;
    private bool _stale = true;
    private bool _settingText;
    private bool _applying;
    private readonly DispatcherTimer _autoTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };

    public TextEditorView()
    {
        InitializeComponent();
        Editor.SyntaxHighlighting = LoadHighlighting();
        SearchPanel.Install(Editor);
        Editor.TextChanged += OnTextChanged;
        Editor.InputBindings.Add(new KeyBinding(new RelayCommand(Apply), Key.Enter, ModifierKeys.Control));
        _autoTimer.Tick += (_, _) =>
        {
            _autoTimer.Stop();
            if (AutoApply.IsChecked == true) Apply(quiet: true);
        };
        IsVisibleChanged += (_, _) => { if (IsVisible && _stale && !_dirty) Regenerate(); };
    }

    public void Attach(IEditorHost host) => _host = host;

    public void Refresh(ChangeKind kind)
    {
        if (_host == null || _applying) return;
        if (kind.HasFlag(ChangeKind.File) && _host.Document.OriginalText is { } original && !_host.Document.IsDirty)
        {
            SetText(original);
            _dirty = false;
            _stale = false;
            HideMessage();
            return;
        }
        if ((kind & (ChangeKind.Geometry | ChangeKind.Materials | ChangeKind.Structure)) == 0) return;

        _stale = true;
        if (_dirty) ShowMessage("3D 側で編集されたため、このテキストは古くなっています。「モデルから作り直す」で最新にできます。", warn: true);
        else if (IsVisible) Regenerate();
    }

    private void Regenerate()
    {
        if (_host == null) return;
        SetText(XTextWriter.Write(_host.Document.Scene, _host.Document.WriteOptions));
        _dirty = false;
        _stale = false;
        HideMessage();
    }

    private void SetText(string text)
    {
        _settingText = true;
        var caret = Editor.CaretOffset;
        var offset = Editor.TextArea.TextView.ScrollOffset;
        Editor.Text = text;
        Editor.CaretOffset = Math.Min(caret, text.Length);
        Editor.ScrollToVerticalOffset(offset.Y);
        _settingText = false;
    }

    private void OnTextChanged(object? sender, EventArgs e)
    {
        if (_settingText) return;
        _dirty = true;
        if (AutoApply.IsChecked == true)
        {
            _autoTimer.Stop();
            _autoTimer.Start();
        }
    }

    private void OnApply(object sender, RoutedEventArgs e) => Apply();

    private void OnRegenerate(object sender, RoutedEventArgs e) => Regenerate();

    private void Apply() => Apply(quiet: false);

    private void Apply(bool quiet)
    {
        if (_host == null) return;
        XLoadResult result;
        try
        {
            result = XFile.LoadText(Editor.Text);
        }
        catch (XFormatException ex)
        {
            ShowMessage("読めません: " + ex.Message, warn: false);
            return;
        }
        if (result.Scene.Meshes.Count == 0)
        {
            ShowMessage("メッシュが 1 つもないので反映しませんでした。" + string.Join(" / ", result.Warnings), warn: false);
            return;
        }

        _applying = true;
        try
        {
            _host.Document.ReplaceScene("テキストから反映", result.Scene, result.Warnings);
        }
        finally { _applying = false; }
        _dirty = false;
        _stale = false;
        if (result.Warnings.Count > 0) ShowMessage("反映しました（警告）: " + string.Join(" / ", result.Warnings), warn: true);
        else HideMessage();
        if (!quiet || result.Warnings.Count == 0)
            _host.SetStatus($"テキストを反映しました（メッシュ {result.Scene.Meshes.Count}、面 {result.Scene.TotalFaces}）");
    }

    private void ShowMessage(string text, bool warn)
    {
        MessageText.Text = text;
        MessageBar.Background = new SolidColorBrush(warn ? Color.FromRgb(58, 52, 30) : Color.FromRgb(70, 34, 34));
        MessageText.Foreground = new SolidColorBrush(warn ? Color.FromRgb(255, 230, 160) : Color.FromRgb(255, 200, 200));
        MessageBar.Visibility = Visibility.Visible;
    }

    private void HideMessage() => MessageBar.Visibility = Visibility.Collapsed;

    private static IHighlightingDefinition LoadHighlighting()
    {
        const string xshd = """
            <SyntaxDefinition name="DirectX" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Comment" foreground="#6A9955" />
              <Color name="String" foreground="#CE9178" />
              <Color name="Template" foreground="#4FC1FF" fontWeight="bold" />
              <Color name="Keyword" foreground="#C586C0" />
              <Color name="Number" foreground="#B5CEA8" />
              <Color name="Guid" foreground="#808080" />
              <Color name="Header" foreground="#DCDCAA" fontWeight="bold" />
              <RuleSet>
                <Span color="Comment" begin="//" />
                <Span color="Comment" begin="\#" />
                <Span color="String" begin="&quot;" end="&quot;" />
                <Span color="Guid" begin="&lt;" end="&gt;" />
                <Rule color="Header">^xof\s+\S+</Rule>
                <Keywords color="Keyword">
                  <Word>template</Word><Word>array</Word><Word>DWORD</Word><Word>FLOAT</Word><Word>STRING</Word>
                  <Word>WORD</Word><Word>CHAR</Word><Word>UCHAR</Word><Word>DOUBLE</Word>
                </Keywords>
                <Keywords color="Template">
                  <Word>Mesh</Word><Word>MeshMaterialList</Word><Word>Material</Word><Word>TextureFilename</Word>
                  <Word>TextureFileName</Word><Word>MeshNormals</Word><Word>MeshTextureCoords</Word>
                  <Word>MeshVertexColors</Word><Word>Frame</Word><Word>FrameTransformMatrix</Word><Word>Header</Word>
                </Keywords>
                <Rule color="Number">-?\b\d+(\.\d+)?([eE][-+]?\d+)?\b</Rule>
              </RuleSet>
            </SyntaxDefinition>
            """;
        using var reader = XmlReader.Create(new StringReader(xshd));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}

internal sealed class RelayCommand(Action action) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action();
}
