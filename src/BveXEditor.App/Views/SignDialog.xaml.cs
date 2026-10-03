using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BveXEditor.Core.Editing;
using BveXEditor.Core.Model;
using Microsoft.Win32;

namespace BveXEditor.App.Views;

/// <summary>
/// 文字を画像にして、テクスチャ付きの板を作る。
/// 画像は文字の高さ 128px 相当で描き、テクスチャの大きさは 2 の累乗に伸ばす（板の縦横比は文字の見た目どおり）。
/// </summary>
public partial class SignDialog : Window
{
    private const double EmSize = 128;
    private const int MaxTexture = 2048;

    private readonly string? _modelDirectory;
    private readonly bool _ready;

    /// <summary>作った板（OK のとき）。</summary>
    public XMesh? Result { get; private set; }
    public string? ImagePath { get; private set; }

    public SignDialog(string? modelDirectory)
    {
        InitializeComponent();
        _modelDirectory = modelDirectory;
        var families = Fonts.SystemFontFamilies.Select(f => f.Source).Order(StringComparer.CurrentCulture).ToList();
        foreach (var f in families) FontBox.Items.Add(f);
        FontBox.SelectedItem = new[] { "BIZ UDPGothic", "Yu Gothic UI", "Meiryo UI", "Meiryo", "MS UI Gothic" }.FirstOrDefault(families.Contains) ?? families.FirstOrDefault();
        _ready = true;
        Render();
    }

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        if (_ready) Render();
    }

    private static Color? ParseColor(string text)
    {
        try
        {
            return ColorConverter.ConvertFromString(text.Trim()) is Color c ? c : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>今の入力で画像を描く。描けなければ null（理由は画面に出す）。</summary>
    private (BitmapSource Bitmap, int LayoutWidth, int LayoutHeight)? Render()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        string Fail(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
            Preview.Source = null;
            OkButton.IsEnabled = false;
            return message;
        }

        var text = SignText.Text.Replace("\r\n", "\n").TrimEnd('\n');
        if (text.Trim().Length == 0) { Fail("文字を入れてください"); return null; }
        if (ParseColor(ForeColor.Text) is not { } fore) { Fail("文字色は #RRGGBB で入れてください"); return null; }
        var back = ParseColor(BackColor.Text);
        bool transparent = TransparentCheck.IsChecked == true;
        if (!transparent && back == null) { Fail("背景色は #RRGGBB で入れてください"); return null; }
        if (!double.TryParse(PaddingBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var paddingPct) || paddingPct < 0)
        { Fail("余白は 0 以上の数で入れてください"); return null; }

        var typeface = new Typeface(new FontFamily((string?)FontBox.SelectedItem ?? "Meiryo"),
            FontStyles.Normal, BoldCheck.IsChecked == true ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        var foreBrush = new SolidColorBrush(fore);
        var alignment = AlignBox.SelectedIndex switch { 0 => TextAlignment.Left, 2 => TextAlignment.Right, _ => TextAlignment.Center };
        double pad = EmSize * paddingPct / 100;
        var lines = text.Split('\n');

        // 文字を並べる（横書きは FormattedText に任せ、縦書きは 1 文字ずつマス目に置く）
        var drawing = new DrawingGroup();
        double contentW, contentH;
        using (var dc = drawing.Open())
        {
            FormattedText Glyph(string s) => new(s, CultureInfo.GetCultureInfo("ja-JP"), FlowDirection.LeftToRight, typeface, EmSize, foreBrush, 1.0);
            if (VerticalCheck.IsChecked != true)
            {
                var ft = Glyph(text);
                ft.TextAlignment = alignment;
                contentW = Math.Ceiling(lines.Max(l => Glyph(l.Length == 0 ? " " : l).WidthIncludingTrailingWhitespace));
                ft.MaxTextWidth = contentW;
                contentH = Math.Ceiling(ft.Height);
                dc.DrawText(ft, new Point(pad, pad));
            }
            else
            {
                double cell = EmSize * 1.15;
                int rows = lines.Max(l => new StringInfo(l).LengthInTextElements);
                contentW = cell * lines.Length;
                contentH = cell * Math.Max(rows, 1);
                for (int col = 0; col < lines.Length; col++)
                {
                    var elements = StringInfo.GetTextElementEnumerator(lines[col]);
                    var chars = new List<string>();
                    while (elements.MoveNext()) chars.Add(elements.GetTextElement());
                    // 右の列から左へ。配置は縦方向（上寄せ・中央・下寄せ）
                    double x = pad + contentW - cell * (col + 1);
                    double offsetY = alignment switch
                    {
                        TextAlignment.Center => (rows - chars.Count) * cell / 2,
                        TextAlignment.Right => (rows - chars.Count) * cell,
                        _ => 0,
                    };
                    for (int row = 0; row < chars.Count; row++)
                    {
                        var ft = Glyph(chars[row]);
                        dc.DrawText(ft, new Point(x + (cell - ft.WidthIncludingTrailingWhitespace) / 2, pad + offsetY + row * cell + (cell - ft.Height) / 2));
                    }
                }
            }
        }

        int layoutW = (int)Math.Ceiling(contentW + pad * 2);
        int layoutH = (int)Math.Ceiling(contentH + pad * 2);
        if (layoutW <= 0 || layoutH <= 0) { Fail("文字の大きさを求められません"); return null; }
        int texW = NextPowerOfTwo(layoutW), texH = NextPowerOfTwo(layoutH);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform((double)texW / layoutW, (double)texH / layoutH));
            if (!transparent) dc.DrawRectangle(new SolidColorBrush(back!.Value), null, new Rect(0, 0, layoutW, layoutH));
            dc.DrawDrawing(drawing);
            dc.Pop();
        }
        var bmp = new RenderTargetBitmap(texW, texH, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();

        // プレビューは板の縦横比で見せる
        Preview.Source = new TransformedBitmap(bmp, new ScaleTransform((double)layoutW / texW, (double)layoutH / texH));
        OkButton.IsEnabled = true;
        string sizeText = double.TryParse(SizeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var size) && size > 0
            ? SizeMode.SelectedIndex == 0
                ? $"板 {size * layoutW / layoutH:0.###} × {size:0.###} m"
                : $"板 {size:0.###} × {size * layoutH / layoutW:0.###} m"
            : "大きさに正の数を入れてください";
        Info.Text = $"{sizeText}、テクスチャ {texW}×{texH}px" + (transparent ? "（背景は透明。BVE 側の透過設定も確認してください）" : "");
        return (bmp, layoutW, layoutH);
    }

    private static int NextPowerOfTwo(int v)
    {
        int p = 1;
        while (p < v && p < MaxTexture) p <<= 1;
        return p;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Render() is not { } r) return;
        if (!float.TryParse(SizeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var size) || size <= 0)
        {
            ErrorText.Text = "大きさに正の数を入れてください";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        var baseName = AsciiName(SignText.Text);
        string path;
        if (_modelDirectory != null)
        {
            path = Path.Combine(_modelDirectory, $"sign_{baseName}.png");
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(_modelDirectory, $"sign_{baseName}_{i}.png");
        }
        else
        {
            var dlg = new SaveFileDialog { Title = "看板の画像の保存先（未保存のファイルなので場所を選んでください）", FileName = $"sign_{baseName}.png", Filter = "PNG (*.png)|*.png" };
            if (dlg.ShowDialog(this) != true) return;
            path = dlg.FileName;
        }

        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(r.Bitmap));
            using var fs = File.Create(path);
            encoder.Save(fs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorText.Text = "画像を保存できませんでした: " + ex.Message;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        var texture = _modelDirectory != null ? Path.GetRelativePath(_modelDirectory, path) : path;
        var mode = SizeMode.SelectedIndex == 0 ? Primitives.ImageSizeMode.Height : Primitives.ImageSizeMode.Width;
        var mesh = Primitives.ImagePlane("Sign_" + baseName, r.LayoutWidth, r.LayoutHeight, mode, size, PlaneKind.SelectedIndex == 0, texture);
        if (DoubleSided.IsChecked == true)
        {
            int front = mesh.Positions.Count;
            var scene = new XScene();
            scene.Meshes.Add(mesh);
            FaceOps.DoubleSide(scene, new Selection());
            // 裏面は左右を反転して貼り、裏からも文字が読めるようにする
            for (int i = front; i < mesh.TexCoords.Count; i++) mesh.TexCoords[i] = mesh.TexCoords[i] with { X = 1 - mesh.TexCoords[i].X };
        }
        Result = mesh;
        ImagePath = path;
        DialogResult = true;
    }

    /// <summary>ファイル名に使える英数字だけの名前。なければ日時。</summary>
    private static string AsciiName(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
            if (sb.Length >= 24) break;
        }
        var name = sb.ToString().Trim('_');
        return name.Length > 0 ? name : DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
    }
}
