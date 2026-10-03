using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BveXEditor.Core.Editing;
using HelixToolkit.Wpf;

namespace BveXEditor.App.Views;

/// <summary>
/// 選択の辺の長さ・外形の寸法を、3D ビューの上に数字で出す（<see cref="EdgeDimensions"/>）。
/// 集めた寸法は形か選択が変わるまで使い回し、視点が変わったら位置だけ計算し直す。
/// </summary>
public partial class ViewportView
{
    /// <summary>画面上でこれより短い辺には数字を出さない (px)。</summary>
    private const double MinLabelEdgePixels = 26;

    private bool _showDimensions;
    private List<Dimension>? _dimensions;
    private bool _dimensionsTooMany;
    private Canvas? _dimensionLineLayer, _dimensionLabelLayer;
    private readonly List<TextBlock> _dimensionLabels = [];
    private readonly List<Line> _dimensionLines = [];

    /// <summary>寸法の表示。</summary>
    public bool ShowDimensions
    {
        get => _showDimensions;
        set
        {
            _showDimensions = value;
            _dimensions = null;
            UpdateDimensions();
        }
    }

    /// <summary>今の寸法の対象の説明（表示を切り替えたときのステータス用）。</summary>
    public string DescribeDimensions()
    {
        if (_doc == null) return "";
        EnsureDimensions();
        var sel = _doc.Selection;
        if (_dimensions!.Count == 0) return "寸法: 出せる辺がありません";
        if (_dimensionsTooMany) return $"寸法: 辺が {EdgeDimensions.MaxEdges} 本を超えるので、外形の寸法だけ出しています";
        if (sel.IsEmpty) return "寸法: モデル全体の外形（幅 X・高さ Y・奥行 Z）。面・頂点を選ぶと辺の長さを出します";
        return sel.Mode switch
        {
            SelectMode.Object => "寸法: 選んだメッシュの外形（幅 X・高さ Y・奥行 Z）。面・頂点を選ぶと辺の長さを出します",
            SelectMode.Face => "寸法: 選んだ面の辺の長さ (m)",
            _ => _dimensions[0].Kind == DimensionKind.Distance ? "寸法: 選んだ 2 頂点の距離 (m)" : "寸法: 両端を選んだ辺の長さ (m)",
        };
    }

    private void EnsureDimensions()
    {
        if (_dimensions != null || _doc == null) return;
        _dimensions = EdgeDimensions.Collect(_doc.Scene, _doc.Selection, _options?.HiddenMeshes, out _dimensionsTooMany);
        // 長い辺から数字を置く（重なったら短い方を省く）
        _dimensions.Sort((a, b) => b.Length.CompareTo(a.Length));
    }

    private void UpdateDimensions()
    {
        if (_dimensionLineLayer == null || _dimensionLabelLayer == null)
        {
            // 線の上に数字が来るよう層を分ける。どちらも範囲選択の枠などより下
            _dimensionLineLayer = new Canvas { IsHitTestVisible = false };
            _dimensionLabelLayer = new Canvas { IsHitTestVisible = false };
            Overlay.Children.Insert(0, _dimensionLabelLayer);
            Overlay.Children.Insert(0, _dimensionLineLayer);
        }
        int usedLabels = 0, usedLines = 0;
        if (_showDimensions && _doc != null)
        {
            EnsureDimensions();
            var m = Viewport3DHelper.GetTotalTransform(View.Viewport);
            var placed = new List<Rect>();
            var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
            foreach (var d in _dimensions!)
            {
                if (ProjectBve(m, d.A) is not { } a || ProjectBve(m, d.B) is not { } b) continue;
                bool box = d.Kind is DimensionKind.BoxX or DimensionKind.BoxY or DimensionKind.BoxZ;
                var brush = d.Kind switch
                {
                    DimensionKind.BoxX => AxisXBrush,
                    DimensionKind.BoxY => AxisYBrush,
                    DimensionKind.BoxZ => AxisZBrush,
                    DimensionKind.Distance => Brushes.Yellow,
                    _ => null,
                };
                // 外形と 2 点間の距離は線も引く（辺はモデルの線がそのまま見えている）
                if (brush != null)
                {
                    var line = Take(_dimensionLines, ref usedLines, NewLine, _dimensionLineLayer);
                    line.X1 = a.X; line.Y1 = a.Y; line.X2 = b.X; line.Y2 = b.Y;
                    line.Stroke = brush;
                    line.StrokeDashArray = d.Kind == DimensionKind.Distance ? DashPattern : null;
                }
                if (!box && (b - a).Length < MinLabelEdgePixels) continue;

                var text = d.Kind switch
                {
                    DimensionKind.BoxX => $"幅 {F(d.Length)} m",
                    DimensionKind.BoxY => $"高さ {F(d.Length)} m",
                    DimensionKind.BoxZ => $"奥行 {F(d.Length)} m",
                    _ => F(d.Length),
                };
                var label = Take(_dimensionLabels, ref usedLabels, NewLabel, _dimensionLabelLayer);
                label.Text = text;
                label.Foreground = box ? brush : d.Kind == DimensionKind.Distance ? Brushes.Yellow : Brushes.White;
                label.FontWeight = box ? FontWeights.SemiBold : FontWeights.Normal;
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var size = label.DesiredSize;
                var mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                var rect = new Rect(mid.X - size.Width / 2, mid.Y - size.Height / 2, size.Width, size.Height);
                // 画面外や、先に置いた（長い辺の）数字と重なるものは出さない
                if (!bounds.IntersectsWith(rect) || placed.Any(p => p.IntersectsWith(rect)))
                {
                    usedLabels--;
                    label.Visibility = Visibility.Collapsed;
                    continue;
                }
                placed.Add(rect);
                Canvas.SetLeft(label, rect.X);
                Canvas.SetTop(label, rect.Y);
            }
        }
        for (int i = usedLabels; i < _dimensionLabels.Count; i++) _dimensionLabels[i].Visibility = Visibility.Collapsed;
        for (int i = usedLines; i < _dimensionLines.Count; i++) _dimensionLines[i].Visibility = Visibility.Collapsed;
    }

    private static string F(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static readonly DoubleCollection DashPattern = Frozen([4, 2]);
    private static readonly Brush AxisXBrush = FrozenBrush(Color.FromRgb(255, 110, 100));
    private static readonly Brush AxisYBrush = FrozenBrush(Color.FromRgb(110, 220, 120));
    private static readonly Brush AxisZBrush = FrozenBrush(Color.FromRgb(110, 160, 255));
    private static readonly Brush LabelBackground = FrozenBrush(Color.FromArgb(200, 24, 27, 32));

    private static DoubleCollection Frozen(double[] values)
    {
        var c = new DoubleCollection(values);
        c.Freeze();
        return c;
    }

    private static Brush FrozenBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>使い回しの図形を 1 つ取り出す（足りなければ作って層に足す）。</summary>
    private static T Take<T>(List<T> pool, ref int used, Func<T> create, Canvas layer) where T : UIElement
    {
        if (used == pool.Count)
        {
            var item = create();
            pool.Add(item);
            layer.Children.Add(item);
        }
        var result = pool[used++];
        result.Visibility = Visibility.Visible;
        return result;
    }

    private static Line NewLine() => new() { StrokeThickness = 1.5, IsHitTestVisible = false };

    private static TextBlock NewLabel() => new()
    {
        Background = LabelBackground,
        Padding = new Thickness(3, 0, 3, 1),
        FontSize = 11,
        IsHitTestVisible = false,
    };
}
