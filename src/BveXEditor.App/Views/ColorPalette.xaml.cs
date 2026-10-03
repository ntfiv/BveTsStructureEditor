using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BveXEditor.App.Views;

/// <summary>
/// 色を選ぶパレット。色相・鮮やかさ・明るさのドラッグ、基本の色、同じメッシュの色、最近使った色から選べる。
/// 選んでいる途中は <see cref="ColorChanged"/>、決定で <see cref="Committed"/>、キャンセルで <see cref="Canceled"/> を出す。
/// </summary>
public partial class ColorPalette : UserControl
{
    /// <summary>ストラクチャでよく使う色（名前はツールチップに出す）。</summary>
    private static readonly (string Name, Color Color)[] Basic =
    [
        ("白", Color.FromRgb(0xFF, 0xFF, 0xFF)),
        ("明るい灰", Color.FromRgb(0xCC, 0xCC, 0xCC)),
        ("灰", Color.FromRgb(0x88, 0x88, 0x88)),
        ("暗い灰", Color.FromRgb(0x44, 0x44, 0x44)),
        ("黒", Color.FromRgb(0x10, 0x10, 0x10)),
        ("コンクリート", Color.FromRgb(0xB8, 0xB4, 0xAA)),
        ("古いコンクリート", Color.FromRgb(0x8F, 0x8C, 0x84)),
        ("アスファルト", Color.FromRgb(0x4A, 0x4A, 0x4C)),
        ("バラスト", Color.FromRgb(0x7D, 0x77, 0x6E)),
        ("枕木・木", Color.FromRgb(0x6E, 0x55, 0x3E)),
        ("明るい木", Color.FromRgb(0xB0, 0x8A, 0x5E)),
        ("レンガ", Color.FromRgb(0x9C, 0x4A, 0x2F)),
        ("赤さび", Color.FromRgb(0x7A, 0x4B, 0x2A)),
        ("トタン屋根の青", Color.FromRgb(0x3F, 0x6B, 0x8A)),
        ("瓦", Color.FromRgb(0x5A, 0x5F, 0x66)),
        ("ガラス", Color.FromRgb(0x6F, 0x8F, 0xA6)),
        ("警戒色の黄", Color.FromRgb(0xE8, 0xC2, 0x1A)),
        ("信号の赤", Color.FromRgb(0xC8, 0x32, 0x2B)),
        ("信号の緑", Color.FromRgb(0x2E, 0x9E, 0x5B)),
        ("案内の青", Color.FromRgb(0x1F, 0x4E, 0x9A)),
        ("草", Color.FromRgb(0x5E, 0x7F, 0x3A)),
        ("植え込み", Color.FromRgb(0x3C, 0x5A, 0x2E)),
        ("土", Color.FromRgb(0x8B, 0x73, 0x55)),
        ("クリーム", Color.FromRgb(0xEF, 0xE6, 0xCC)),
    ];

    private const int MaxRecent = 12;
    private static readonly string RecentFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BveXEditor", "recent_colors.json");

    private double _hue, _saturation, _value;
    private Color _original;
    private bool _draggingSv, _draggingHue, _updating;

    public Color SelectedColor { get; private set; }

    public event Action<Color>? ColorChanged;
    public event Action<Color>? Committed;
    public event Action? Canceled;

    public ColorPalette()
    {
        InitializeComponent();
        foreach (var (name, color) in Basic) BasicColors.Children.Add(MakeSwatch(color, name));
        SvArea.SizeChanged += (_, _) => UpdateMarkers();
    }

    /// <summary>開くたびに呼ぶ。<paramref name="meshColors"/> は同じメッシュの他のマテリアルの色。</summary>
    public void Start(Color current, IEnumerable<Color> meshColors)
    {
        _original = current;
        OldPreview.Background = Frozen(current);
        FillSwatches(MeshColors, MeshColorsHeading, meshColors.Distinct());
        FillSwatches(RecentColors, RecentHeading, LoadRecent());
        SetColor(current, raise: false);
    }

    // ───────── 色の設定 ─────────

    private void SetColor(Color c, bool raise)
    {
        var (h, s, v) = ToHsv(c);
        // 灰色（鮮やかさ 0）や黒（明るさ 0）では色相が決まらないので、今の色相を保つ
        _hue = s < 1e-6 || v < 1e-6 ? _hue : h;
        _saturation = s;
        _value = v;
        Show(c, raise);
    }

    private void SetHsv(double h, double s, double v, bool raise)
    {
        _hue = h;
        _saturation = Math.Clamp(s, 0, 1);
        _value = Math.Clamp(v, 0, 1);
        Show(FromHsv(_hue, _saturation, _value), raise);
    }

    private void Show(Color c, bool raise)
    {
        SelectedColor = c;
        _updating = true;
        HexBox.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        _updating = false;
        RgbText.Text = $"R {c.R}  G {c.G}  B {c.B}　（{c.R / 255.0:0.###}, {c.G / 255.0:0.###}, {c.B / 255.0:0.###}）";
        NewPreview.Background = Frozen(c);
        HueFill.Fill = Frozen(FromHsv(_hue, 1, 1));
        UpdateMarkers();
        if (raise) ColorChanged?.Invoke(c);
    }

    private void UpdateMarkers()
    {
        double w = SvArea.ActualWidth, h = SvArea.ActualHeight;
        if (w <= 0 || h <= 0) return;
        Canvas.SetLeft(SvMarker, _saturation * w - SvMarker.Width / 2);
        Canvas.SetTop(SvMarker, (1 - _value) * h - SvMarker.Height / 2);
        Canvas.SetTop(HueMarker, _hue / 360 * HueArea.ActualHeight - HueMarker.Height / 2);
    }

    // ───────── ドラッグ ─────────

    private void OnSvDown(object sender, MouseButtonEventArgs e)
    {
        _draggingSv = SvArea.CaptureMouse();
        PickSv(e.GetPosition(SvArea));
    }

    private void OnSvMove(object sender, MouseEventArgs e)
    {
        if (_draggingSv) PickSv(e.GetPosition(SvArea));
    }

    private void OnSvUp(object sender, MouseButtonEventArgs e)
    {
        _draggingSv = false;
        SvArea.ReleaseMouseCapture();
    }

    private void PickSv(Point p) =>
        SetHsv(_hue, p.X / SvArea.ActualWidth, 1 - p.Y / SvArea.ActualHeight, raise: true);

    private void OnHueDown(object sender, MouseButtonEventArgs e)
    {
        _draggingHue = HueArea.CaptureMouse();
        PickHue(e.GetPosition(HueArea));
    }

    private void OnHueMove(object sender, MouseEventArgs e)
    {
        if (_draggingHue) PickHue(e.GetPosition(HueArea));
    }

    private void OnHueUp(object sender, MouseButtonEventArgs e)
    {
        _draggingHue = false;
        HueArea.ReleaseMouseCapture();
    }

    private void PickHue(Point p) =>
        SetHsv(Math.Clamp(p.Y / HueArea.ActualHeight, 0, 0.9999) * 360, _saturation, _value, raise: true);

    // ───────── 色コード・見本 ─────────

    private void OnHexKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnHexCommitted(sender, e);
    }

    private void OnHexCommitted(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        if (TryParseHex(HexBox.Text, out var c))
        {
            if (c != SelectedColor) SetColor(c, raise: true);
        }
        else Show(SelectedColor, raise: false); // 読めない入力は今の色に戻す
    }

    public static bool TryParseHex(string text, out Color color)
    {
        color = default;
        var hex = text.Trim().TrimStart('#');
        if (hex.Length == 3) hex = string.Concat(hex.Select(ch => $"{ch}{ch}"));
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return false;
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    private void OnOldPreviewClick(object sender, MouseButtonEventArgs e) => SetColor(_original, raise: true);

    private Button MakeSwatch(Color c, string? name = null)
    {
        var b = new Button
        {
            Style = (Style)FindResource("SwatchButton"),
            Background = Frozen(c),
            ToolTip = (name != null ? name + "　" : "") + $"#{c.R:X2}{c.G:X2}{c.B:X2}",
        };
        b.Click += (_, _) => SetColor(c, raise: true);
        return b;
    }

    private void FillSwatches(WrapPanel panel, TextBlock heading, IEnumerable<Color> colors)
    {
        panel.Children.Clear();
        foreach (var c in colors) panel.Children.Add(MakeSwatch(c));
        var visibility = panel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        panel.Visibility = visibility;
        heading.Visibility = visibility;
    }

    // ───────── 決定・キャンセル ─────────

    private void OnOk(object sender, RoutedEventArgs e)
    {
        OnHexCommitted(sender, e); // 入力途中の色コードも取り込む
        SaveRecent(SelectedColor);
        Committed?.Invoke(SelectedColor);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Canceled?.Invoke();

    private static List<Color> LoadRecent()
    {
        try
        {
            if (!File.Exists(RecentFile)) return [];
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(RecentFile)) ?? [];
            return list.Select(s => TryParseHex(s, out var c) ? (Color?)c : null).OfType<Color>().Take(MaxRecent).ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void SaveRecent(Color c)
    {
        try
        {
            var list = LoadRecent().Where(x => x != c).Prepend(c).Take(MaxRecent)
                .Select(x => $"#{x.R:X2}{x.G:X2}{x.B:X2}").ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(RecentFile)!);
            File.WriteAllText(RecentFile, JsonSerializer.Serialize(list));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 覚えられなくても色は決められる
        }
    }

    // ───────── HSV ─────────

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = 0;
        if (d > 1e-9)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }
        return (h, max <= 0 ? 0 : d / max, max);
    }

    public static Color FromHsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        byte B(double t) => (byte)Math.Clamp(Math.Round((t + m) * 255), 0, 255);
        return Color.FromRgb(B(r), B(g), B(b));
    }
}
