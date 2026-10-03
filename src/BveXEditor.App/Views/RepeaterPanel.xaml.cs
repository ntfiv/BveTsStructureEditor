using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BveXEditor.App.Document;
using BveXEditor.App.Rendering;
using BveXEditor.Core.Editing;
using BveXEditor.Core.Model;
using Microsoft.Win32;

namespace BveXEditor.App.Views;

/// <summary>
/// BVE の Repeater 用に、長いストラクチャを Z 方向に切り分け、ピースごとに .x へ書き出し、
/// 曲線に並べたところを 3D ビューで確かめる。分割した結果はここに持つだけで、
/// 「モデルに反映」を押すまで編集中のモデルは変えない。
/// </summary>
public partial class RepeaterPanel : UserControl
{
    public sealed record ResultItem(string Message, bool Ok)
    {
        public string Mark => Ok ? "✓" : "×";
        public Brush MarkBrush => Ok ? Brushes.SeaGreen : Brushes.Firebrick;
    }

    private IEditorHost? _host;
    private RepeaterPreviewRenderer? _preview;
    private readonly ObservableCollection<ResultItem> _results = [];
    private List<RepeaterPiece> _pieces = [];
    private bool _ready;

    public RepeaterPanel()
    {
        InitializeComponent();
        Results.ItemsSource = _results;
    }

    public void Attach(IEditorHost host, RepeaterPreviewRenderer preview)
    {
        _host = host;
        _preview = preview;
        _ready = true;
        Refresh(ChangeKind.All);
    }

    private EditorDocument Doc => _host!.Document;

    /// <summary>ファイルや形が変わったら、分割をやり直して並べ直す。</summary>
    public void Refresh(ChangeKind kind)
    {
        if (!_ready) return;
        if (kind.HasFlag(ChangeKind.File))
        {
            BaseName.Text = Doc.FilePath is { } f ? Path.GetFileNameWithoutExtension(f) : "structure";
            if (string.IsNullOrWhiteSpace(OutputDir.Text)) OutputDir.Text = Doc.Directory ?? "";
        }
        UpdateRange();
        Split(announce: false);
    }

    // ───────── 入力 ─────────

    private static bool TryRead(TextBox box, out float value) =>
        float.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private void OnFieldCommitted(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (sender == PieceLength && TryRead(PieceLength, out var len) && len > 0) Span.Text = PieceLength.Text;
        Split(announce: false);
    }

    private void OnFieldKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OnFieldCommitted(sender, e);
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        ZStart.IsEnabled = ZEnd.IsEnabled = AutoRange.IsChecked != true;
        UpdateRange();
        Split(announce: false);
    }

    /// <summary>自動のときは、対象メッシュの前後の端を欄に入れておく（外したらそこから直せる）。</summary>
    private void UpdateRange()
    {
        if (AutoRange.IsChecked != true) return;
        if (RepeaterSplit.ZRange(Doc.Scene, Doc.Selection) is not { } range) return;
        ZStart.Text = range.Min.ToString("0.###", CultureInfo.InvariantCulture);
        ZEnd.Text = range.Max.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private RepeaterSplitOptions? ReadOptions()
    {
        if (!TryRead(PieceLength, out var length) || length <= 0)
        {
            _host!.SetStatus("ピースの長さに正の数を入れてください");
            return null;
        }
        if (!TryRead(Overlap, out var overlap) || overlap < 0)
        {
            _host!.SetStatus("重ね代に 0 以上の数を入れてください");
            return null;
        }
        var o = new RepeaterSplitOptions
        {
            Length = length,
            Overlap = overlap,
            WrapAround = WrapAround.IsChecked == true,
            AlignToZero = AlignZero.IsChecked == true,
            DropEmpty = DropEmpty.IsChecked == true,
        };
        if (AutoRange.IsChecked != true)
        {
            if (!TryRead(ZStart, out var zs) || !TryRead(ZEnd, out var ze) || ze - zs <= 0)
            {
                _host!.SetStatus("Z の範囲は、開始より終了を大きくしてください");
                return null;
            }
            o.ZStart = zs;
            o.ZEnd = ze;
        }
        return o;
    }

    // ───────── 分割 ─────────

    private void OnSplit(object sender, RoutedEventArgs e) => Split(announce: true);

    private void Split(bool announce)
    {
        if (ReadOptions() is not { } o)
        {
            _pieces = [];
            UpdatePreview();
            return;
        }
        _pieces = RepeaterSplit.Split(Doc.Scene, Doc.Selection, o);
        _preview?.SetPieces(_pieces.Select(p => p.Mesh), Doc.Directory);
        if (_pieces.Count == 0)
        {
            SplitInfo.Text = "分割できる面がありません（対象は選択のあるメッシュ、選択が空なら全体）";
            SplitInfo.Foreground = Brushes.Firebrick;
        }
        else
        {
            var last = _pieces[^1];
            var total = (o.ZEnd ?? last.ZEnd) - (o.ZStart ?? _pieces[0].ZStart);
            SplitInfo.Text = $"{total:0.###} m を {o.Length:0.###} m ずつ → {_pieces.Count} ピース" +
                             (MathF.Abs(last.Length - o.Length) > 1e-3f ? $"（最後の区間は {last.Length:0.###} m）" : "") +
                             (o.Overlap > 1e-4f ? $"、1 ピース {_pieces[0].CutLength:0.###} m（重ね代 {o.Overlap:0.###} m）" : "") +
                             $"、面 {_pieces.Sum(p => p.Mesh.Faces.Count)}";
            SplitInfo.Foreground = (Brush)FindResource("SubTextBrush");
        }
        if (announce) _host!.SetStatus("分割しました: " + SplitInfo.Text + "（モデルはまだ変えていません）");
        UpdatePreview();
    }

    // ───────── プレビュー ─────────

    /// <summary>BVE と同じ 25 m ブロック単位にするか（0 なら曲線そのものに沿わせる）。</summary>
    private float BlockLength => BlockMode.IsChecked == true ? RepeaterLayout.DefaultBlockLength : 0;

    private void UpdatePreview()
    {
        if (_preview == null) return;
        if (ShowPreview.IsChecked != true || _pieces.Count == 0)
        {
            _preview.Clear();
            PreviewInfo.Text = "";
            return;
        }
        if (!TryRead(Radius, out var radius) || !TryRead(Span, out var span) || !TryRead(Count, out var count))
        {
            _preview.Clear();
            PreviewInfo.Text = "半径・間隔・本数に数値を入れてください";
            return;
        }
        var placements = RepeaterLayout.Compute(radius, span, (int)count, _pieces.Count, BlockLength);
        _preview.Place(placements);
        PreviewInfo.Text = placements.Count == 0
            ? "間隔と本数は正の数にしてください"
            : RepeaterLayout.Describe(radius, span, placements.Count, BlockLength);
    }

    /// <summary>今の半径・間隔・並べ方と、モデルの左右の張り出しから、隙間が出ない最小の重ね代を入れる。</summary>
    private void OnAutoOverlap(object sender, RoutedEventArgs e)
    {
        if (!TryRead(Radius, out var radius) || !TryRead(Span, out var span) || span <= 0)
        {
            _host!.SetStatus("プレビューの半径と間隔に数値を入れてください");
            return;
        }
        if (RepeaterSplit.XRange(Doc.Scene, Doc.Selection) is not { } x)
        {
            _host!.SetStatus("対象のメッシュがありません");
            return;
        }
        // 1 ブロックぶん回るところまで見れば、いちばん大きい折れ角が入る
        int count = Math.Max(4, (int)MathF.Ceiling(RepeaterLayout.DefaultBlockLength * 2 / span) + 1);
        var placements = RepeaterLayout.Compute(radius, span, count, 1, BlockLength);
        float needed = RepeaterLayout.RequiredOverlap(placements, span, x.Min, x.Max);
        // 端数で隙間が残らないよう 1 mm 単位で切り上げる
        float rounded = MathF.Ceiling(needed * 1000f) / 1000f;
        Overlap.Text = rounded.ToString("0.###", CultureInfo.InvariantCulture);
        Split(announce: false);
        _host!.SetStatus(rounded <= 0
            ? "直線なので重ね代は要りません"
            : $"重ね代を {rounded:0.###} m にしました（張り出し {x.Min:0.##}〜{x.Max:0.##} m・{RepeaterLayout.Describe(radius, span, count, BlockLength)}）");
    }

    /// <summary>窓を閉じるときなど、重ねた表示を消す。</summary>
    public void ClearPreview() => _preview?.Clear();

    // ───────── 書き出し ─────────

    private void OnBrowseOutput(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "ピースの出力先",
            InitialDirectory = Directory.Exists(OutputDir.Text) ? OutputDir.Text : Doc.Directory ?? "",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) OutputDir.Text = dlg.FolderName;
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        Split(announce: false);
        _results.Clear();
        if (_pieces.Count == 0)
        {
            _host!.SetStatus("書き出すピースがありません。先に分割してください");
            return;
        }
        var dir = OutputDir.Text.Trim();
        var name = BaseName.Text.Trim();
        if (dir.Length == 0 || name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            _host!.SetStatus("出力先のフォルダーと、使える文字のファイル名を入れてください");
            return;
        }
        var existing = _pieces.Count(p => File.Exists(Path.Combine(dir, $"{name}_{p.Index:00}.x")));
        if (existing > 0 &&
            MessageBox.Show($"{existing} 個のファイルを上書きします。続けますか？", "BVE X Editor",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        var results = RepeaterSplit.Save(_pieces, Doc.Directory, dir, name, Doc.WriteOptions);
        foreach (var r in results) _results.Add(new ResultItem(r.Message, r.Ok));
        int ok = results.Count(r => r.Ok);
        _host!.SetStatus($"{dir} に {ok} 個のピースを書き出しました" +
                         (ok < results.Count ? $"（失敗 {results.Count - ok}）" : "") +
                         (Doc.Directory == null ? "。まだ保存していないので、テクスチャは絶対パスのままです" : ""));
    }

    // ───────── モデルに反映 ─────────

    private void OnApplyToScene(object sender, RoutedEventArgs e)
    {
        if (ReadOptions() is not { } o) return;
        // 反映するときは元の位置のまま重なりなく分ける（Z=0 揃えと重ね代は書き出し用）
        o.AlignToZero = false;
        o.Overlap = 0;
        var pieces = RepeaterSplit.Split(Doc.Scene, Doc.Selection, o);
        if (pieces.Count == 0)
        {
            _host!.SetStatus("分割できる面がありません");
            return;
        }
        var targets = SelectionQuery.Meshes(Doc.Scene, Doc.Selection).Order().ToList();
        Doc.Selection.Clear();
        _host!.Run("Repeater 用に分割", s =>
        {
            foreach (var m in targets.AsEnumerable().Reverse()) s.Meshes.RemoveAt(m);
            foreach (var piece in pieces) s.Meshes.Add(piece.Mesh.Clone());
            return $"{targets.Count} メッシュを {pieces.Count} ピースに分けました（元の位置のまま・重ね代なし）";
        });
    }
}
