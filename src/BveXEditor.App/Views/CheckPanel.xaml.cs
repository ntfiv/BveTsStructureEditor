using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BveXEditor.App.Document;
using BveXEditor.Core.Editing;

namespace BveXEditor.App.Views;

/// <summary>BVE 互換チェックの結果一覧。タブが見えている間だけ、形が変わるたびに調べ直す。</summary>
public partial class CheckPanel : UserControl
{
    public sealed record Item(CheckIssue Issue, Brush Badge, string FixLabel);

    private static readonly Brush ErrorBrush = Frozen(Color.FromRgb(200, 60, 60));
    private static readonly Brush WarningBrush = Frozen(Color.FromRgb(210, 140, 30));
    private static readonly Brush InfoBrush = Frozen(Color.FromRgb(110, 120, 135));

    private IEditorHost? _host;
    private bool _stale = true;

    public CheckPanel()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => { if (IsVisible && _stale) Recheck(); };
    }

    public void Attach(IEditorHost host) => _host = host;

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public void Refresh(ChangeKind kind)
    {
        if ((kind & (ChangeKind.Geometry | ChangeKind.Materials | ChangeKind.Structure | ChangeKind.File)) == 0) return;
        _stale = true;
        if (IsVisible) Recheck();
    }

    private void Recheck()
    {
        if (_host == null) return;
        var doc = _host.Document;
        var issues = BveCheck.Run(doc.Scene, doc.Directory, doc.Warnings);
        List.ItemsSource = issues.Select(i => new Item(i,
            i.Severity switch { CheckSeverity.Error => ErrorBrush, CheckSeverity.Warning => WarningBrush, _ => InfoBrush },
            i.Fix == CheckFix.None ? "" : "直せる")).ToList();
        int errors = issues.Count(i => i.Severity == CheckSeverity.Error);
        int warnings = issues.Count(i => i.Severity == CheckSeverity.Warning);
        Summary.Text = issues.Count == 0 ? "問題は見つかりませんでした" : $"エラー {errors}・注意 {warnings}・情報 {issues.Count - errors - warnings}";
        _stale = false;
        OnSelectionChanged(this, null!);
    }

    private Item? Current => List.SelectedItem as Item;

    private void OnRecheck(object sender, RoutedEventArgs e) => Recheck();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var issue = Current?.Issue;
        SelectButton.IsEnabled = issue is { Mesh: >= 0 };
        FixButton.IsEnabled = issue is { Fix: not CheckFix.None };
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e) => OnSelect(sender, e);

    private void OnSelect(object sender, RoutedEventArgs e)
    {
        if (_host == null || Current?.Issue is not { Mesh: >= 0 } issue) return;
        var doc = _host.Document;
        if (issue.Mesh >= doc.Scene.Meshes.Count) return;
        doc.ActiveMesh = issue.Mesh;
        if (issue.Faces is { Length: > 0 } faces)
        {
            doc.Selection.SetMode(SelectMode.Face);
            doc.Selection.Set(faces.Select(f => new ElementRef(issue.Mesh, f)));
        }
        else if (issue.Vertices is { Length: > 0 } verts)
        {
            doc.Selection.SetMode(SelectMode.Vertex);
            doc.Selection.Set(verts.Select(v => new ElementRef(issue.Mesh, v)));
        }
        else
        {
            doc.Selection.SetMode(SelectMode.Object);
            doc.Selection.Set([new ElementRef(issue.Mesh, -1)]);
        }
        _host.SetStatus($"該当箇所を選択しました: {issue.Message}");
    }

    private void OnFix(object sender, RoutedEventArgs e)
    {
        if (_host == null || Current?.Issue is not { Fix: not CheckFix.None } issue) return;
        _host.Document.Selection.Clear();
        _host.Run("チェック: " + issue.Message, s => BveCheck.Fix(s, issue));
    }
}
