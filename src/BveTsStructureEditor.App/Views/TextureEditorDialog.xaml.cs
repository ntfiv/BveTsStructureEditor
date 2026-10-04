using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BveTsStructureEditor.Core.Imaging;

namespace BveTsStructureEditor.App.Views;

/// <summary>
/// 取り込むテクスチャの簡易編集（歪み補正・回転反転・シームレス化・色調整・透過色・出力サイズ）。
/// 操作中は縮小した画像でプレビューし、「編集した画像を使う」で元の解像度から作り直す。
/// </summary>
public partial class TextureEditorDialog : Window
{
    private const int PreviewMax = 768;
    private const double HandleRadius = 7;

    private readonly RgbaImage _source;
    private readonly RgbaImage _preview;
    private readonly float _previewScale;
    private readonly Vector2[] _corners;
    private readonly Ellipse[] _handles = new Ellipse[4];
    private readonly DispatcherTimer _timer;
    private readonly bool _ready;
    private int _turns;
    private int _dragging = -1;
    private int _version;
    private TextureEditResult? _lastPreview;

    /// <summary>「編集した画像を使う」で作った画像。</summary>
    public TextureEditResult? Result { get; private set; }
    /// <summary>「そのまま使う」を押した。</summary>
    public bool UseOriginal { get; private set; }

    public TextureEditorDialog(RgbaImage source, string name)
    {
        InitializeComponent();
        _source = source;
        _previewScale = MathF.Min(1, (float)PreviewMax / Math.Max(source.Width, source.Height));
        _preview = _previewScale < 1
            ? TextureEdit.Resize(source, Math.Max(1, (int)(source.Width * _previewScale)), Math.Max(1, (int)(source.Height * _previewScale)))
            : source;
        _corners = FullCorners();
        SourceImage.Source = TextureImport.ToBitmap(_preview);
        SourceTitle.Text = $"元の画像: {name}（{source.Width}×{source.Height}）";

        for (int i = 0; i < 4; i++)
        {
            _handles[i] = new Ellipse
            {
                Width = HandleRadius * 2, Height = HandleRadius * 2,
                Fill = new SolidColorBrush(Color.FromArgb(200, 255, 160, 48)), Stroke = Brushes.White, StrokeThickness = 1.5,
                IsHitTestVisible = false,
            };
            Handles.Children.Add(_handles[i]);
        }
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += (_, _) => { _timer.Stop(); RenderPreview(); };
        _ready = true;
        UpdateSliderTexts();
        Loaded += (_, _) => { UpdateHandles(); Schedule(); };
    }

    private Vector2[] FullCorners() => [new(0, 0), new(_source.Width, 0), new(_source.Width, _source.Height), new(0, _source.Height)];

    // ───────── 設定の読み取り ─────────

    private TextureEditSettings ReadSettings()
    {
        float aspect = 0;
        if (float.TryParse(RealWidth.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var rw) &&
            float.TryParse(RealHeight.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var rh) && rw > 0 && rh > 0)
            aspect = rw / rh;
        int Size(ComboBox box) => box.SelectedIndex <= 0 ? 0 : int.Parse((string)((ComboBoxItem)box.SelectedItem).Content, CultureInfo.InvariantCulture);
        int autoMax = int.Parse((string)((ComboBoxItem)AutoMax.SelectedItem).Content, CultureInfo.InvariantCulture);
        var key = ((SolidColorBrush)KeySwatch.Background).Color;
        return new TextureEditSettings
        {
            Corners = (Vector2[])_corners.Clone(),
            Aspect = aspect,
            RotateQuarterTurns = _turns,
            FlipHorizontal = FlipH.IsChecked == true,
            FlipVertical = FlipV.IsChecked == true,
            UseColorKey = KeyCheck.IsChecked == true,
            KeyColor = new Vector3(key.R, key.G, key.B) / 255f,
            KeyTolerance = (float)KeyTolerance.Value / 100f,
            KeySoftness = (float)KeySoftness.Value / 100f,
            SeamlessHorizontal = SeamH.IsChecked == true,
            SeamlessVertical = SeamV.IsChecked == true,
            SeamlessOverlap = (float)Overlap.Value / 100f,
            Brightness = (float)Brightness.Value / 100f,
            Contrast = (float)Contrast.Value / 100f,
            Saturation = (float)Saturation.Value / 100f,
            OutputWidth = Size(OutW),
            OutputHeight = Size(OutH),
            MaxAutoSize = autoMax,
        };
    }

    // ───────── プレビュー ─────────

    private void Schedule()
    {
        if (!_ready) return;
        _timer.Stop();
        _timer.Start();
    }

    private async void RenderPreview()
    {
        var settings = ReadSettings();
        // 縮小画像で作る。四隅も同じ比率で縮め、出力は軽い大きさにする
        var previewSettings = settings with
        {
            Corners = settings.Corners!.Select(c => c * _previewScale).ToArray(),
            OutputWidth = 0,
            OutputHeight = 0,
            MaxAutoSize = 512,
        };
        int version = ++_version;
        TextureEditResult result;
        try
        {
            result = await Task.Run(() => TextureEdit.Apply(_preview, previewSettings));
        }
        catch (ArgumentException ex)
        {
            ResultInfo.Text = "作れません: " + ex.Message;
            return;
        }
        if (version != _version) return; // もっと新しい操作がある
        _lastPreview = result;
        ShowResult();
        var (w, h) = TextureEdit.OutputSize(result.DisplayAspect, settings);
        ResultInfo.Text = $"出力 {w}×{h}px（PNG）、見た目の縦横比 {result.DisplayAspect:0.###}" +
                          (settings.SeamlessHorizontal || settings.SeamlessVertical ? "。シームレス化した分、端が少し削れます" : "");
    }

    private void ShowResult()
    {
        if (_lastPreview is not { } r) return;
        bool tile = TilePreview.IsChecked == true;
        var brush = new ImageBrush(TextureImport.ToBitmap(r.Image)) { Stretch = Stretch.Fill };
        if (tile)
        {
            brush.TileMode = TileMode.Tile;
            brush.Viewport = new Rect(0, 0, 0.5, 0.5);
        }
        ResultRect.Fill = brush;
        const double size = 300;
        double aspect = r.DisplayAspect;
        ResultRect.Width = aspect >= 1 ? size : size * aspect;
        ResultRect.Height = aspect >= 1 ? size / aspect : size;
    }

    private void OnTileChanged(object sender, RoutedEventArgs e) => ShowResult();

    // ───────── 四隅のハンドル ─────────

    /// <summary>元画像が表示されている範囲（Canvas 上の左上と、画素 1 つあたりの表示の大きさ）。</summary>
    private (double Left, double Top, double Scale) ImageRect()
    {
        double hw = SourceHost.ActualWidth, hh = SourceHost.ActualHeight;
        if (hw <= 0 || hh <= 0) return (0, 0, 1);
        double scale = Math.Min(hw / _source.Width, hh / _source.Height);
        // Canvas は SourceHost より余白ぶん外に広がっているので、その分ずらす
        double pad = -Handles.Margin.Left;
        return (pad + (hw - _source.Width * scale) / 2, pad + (hh - _source.Height * scale) / 2, scale);
    }

    private Point ToScreen(Vector2 p)
    {
        var (l, t, s) = ImageRect();
        return new Point(l + p.X * s, t + p.Y * s);
    }

    private Vector2 ToImage(Point p)
    {
        var (l, t, s) = ImageRect();
        return new Vector2((float)((p.X - l) / s), (float)((p.Y - t) / s));
    }

    private void UpdateHandles()
    {
        QuadShape.Points = new PointCollection(_corners.Select(ToScreen));
        for (int i = 0; i < 4; i++)
        {
            var p = ToScreen(_corners[i]);
            Canvas.SetLeft(_handles[i], p.X - HandleRadius);
            Canvas.SetTop(_handles[i], p.Y - HandleRadius);
        }
    }

    private void OnSourceSizeChanged(object sender, SizeChangedEventArgs e) => UpdateHandles();

    private void OnSourceMouseDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(Handles);
        if (PickButton.IsChecked == true)
        {
            var ip = ToImage(pos);
            int x = Math.Clamp((int)ip.X, 0, _source.Width - 1), y = Math.Clamp((int)ip.Y, 0, _source.Height - 1);
            var c = _source.Get(x, y);
            KeySwatch.Background = new SolidColorBrush(Color.FromRgb((byte)(c.X * 255), (byte)(c.Y * 255), (byte)(c.Z * 255)));
            PickButton.IsChecked = false;
            KeyCheck.IsChecked = true;
            Schedule();
            return;
        }
        double best = HandleRadius * 2.5;
        _dragging = -1;
        for (int i = 0; i < 4; i++)
        {
            double d = (ToScreen(_corners[i]) - pos).Length;
            if (d < best) { best = d; _dragging = i; }
        }
        if (_dragging >= 0) Handles.CaptureMouse();
    }

    private void OnSourceMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging < 0) return;
        var ip = ToImage(e.GetPosition(Handles));
        _corners[_dragging] = Vector2.Clamp(ip, Vector2.Zero, new Vector2(_source.Width, _source.Height));
        UpdateHandles();
        Schedule();
    }

    private void OnSourceMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging < 0) return;
        _dragging = -1;
        Handles.ReleaseMouseCapture();
    }

    private void OnResetCorners(object sender, RoutedEventArgs e)
    {
        var full = FullCorners();
        for (int i = 0; i < 4; i++) _corners[i] = full[i];
        UpdateHandles();
        Schedule();
    }

    // ───────── そのほかの操作 ─────────

    private void OnChanged(object sender, RoutedEventArgs e) => Schedule();

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        UpdateSliderTexts();
        Schedule();
    }

    private void UpdateSliderTexts()
    {
        OverlapText.Text = $"{Overlap.Value:0}%";
        BrightnessText.Text = $"{Brightness.Value:0}";
        ContrastText.Text = $"{Contrast.Value:0}";
        SaturationText.Text = $"{Saturation.Value:0}";
        KeyToleranceText.Text = $"{KeyTolerance.Value:0}";
        KeySoftnessText.Text = $"{KeySoftness.Value:0}";
    }

    private void OnRotateLeft(object sender, RoutedEventArgs e) { _turns = (_turns + 3) % 4; Schedule(); }
    private void OnRotateRight(object sender, RoutedEventArgs e) { _turns = (_turns + 1) % 4; Schedule(); }

    private void OnResetColor(object sender, RoutedEventArgs e)
    {
        Brightness.Value = 0;
        Contrast.Value = 0;
        Saturation.Value = 0;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var settings = ReadSettings();
        try
        {
            Cursor = Cursors.Wait;
            Info.Text = "元の解像度で作っています…";
            Result = TextureEdit.Apply(_source, settings);
        }
        catch (ArgumentException ex)
        {
            Info.Text = "作れません: " + ex.Message;
            return;
        }
        finally
        {
            Cursor = null;
        }
        DialogResult = true;
    }

    private void OnUseOriginal(object sender, RoutedEventArgs e)
    {
        UseOriginal = true;
        DialogResult = true;
    }
}
