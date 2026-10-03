using System.Globalization;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BveXEditor.App.Document;
using BveXEditor.Core.Model;
using Microsoft.Win32;

namespace BveXEditor.App.Views;

/// <summary>
/// 1 つのマテリアルの色・テクスチャを編集する、閉じずに作業を続けられるウィンドウ。
/// 編集タブのマテリアル一覧で選んでいるものを対象にし、選び直すと対象も切り替わる。
/// </summary>
public partial class MaterialEditorWindow : Window
{
    private readonly IEditorHost _host;
    private int _mesh = -1, _material = -1;

    public MaterialEditorWindow(IEditorHost host)
    {
        InitializeComponent();
        _host = host;
        Palette.ColorChanged += OnPaletteColorChanged;
        Palette.Committed += OnPaletteCommitted;
        Palette.Canceled += () => PalettePopup.IsOpen = false;
    }

    private XMesh? Mesh => _mesh >= 0 && _mesh < _host.Document.Scene.Meshes.Count ? _host.Document.Scene.Meshes[_mesh] : null;

    private XMaterial? Material => Mesh is { } m && _material >= 0 && _material < m.Materials.Count ? m.Materials[_material] : null;

    /// <summary>編集するマテリアルを決めて、欄を今の値で埋め直す。</summary>
    public void SetTarget(int mesh, int material)
    {
        _mesh = mesh;
        _material = material;
        Fill();
    }

    private static float F(string s, float fallback) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static string S(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static byte B(float v) => (byte)Math.Clamp(MathF.Round(v * 255), 0, 255);

    private void Fill()
    {
        var m = Material;
        Fields.IsEnabled = ApplyButton.IsEnabled = m != null;
        if (m == null)
        {
            TargetText.Text = "マテリアルが選ばれていません";
            TargetInfo.Text = "編集タブのマテリアル一覧で選んでください";
            foreach (var b in new[] { DR, DG, DB, DA, SR, SG, SB, Power, ER, EG, EB, TexturePath, HexColor }) b.Text = "";
            Swatch.Background = null;
            UpdatePreview(null, Vector4.Zero);
            return;
        }
        var mesh = Mesh!;
        TargetText.Text = $"{(string.IsNullOrEmpty(mesh.Name) ? $"メッシュ {_mesh}" : mesh.Name)} ／ マテリアル {_material}";
        TargetInfo.Text = $"{mesh.Faces.Count(f => f.Material == _material)} 面で使用";
        DR.Text = S(m.FaceColor.X); DG.Text = S(m.FaceColor.Y); DB.Text = S(m.FaceColor.Z); DA.Text = S(m.FaceColor.W);
        SR.Text = S(m.Specular.X); SG.Text = S(m.Specular.Y); SB.Text = S(m.Specular.Z); Power.Text = S(m.Power);
        ER.Text = S(m.Emissive.X); EG.Text = S(m.Emissive.Y); EB.Text = S(m.Emissive.Z);
        TexturePath.Text = m.Texture ?? "";
        UpdateSwatch(m.FaceColor);
        UpdatePreview(m.Texture, m.FaceColor);
    }

    private void UpdateSwatch(Vector4 c)
    {
        HexColor.Text = $"#{B(c.X):X2}{B(c.Y):X2}{B(c.Z):X2}";
        Swatch.Background = new SolidColorBrush(Color.FromRgb(B(c.X), B(c.Y), B(c.Z)));
    }

    /// <summary>見本: テクスチャがあればその画像、なければ拡散色（不透明度つき）。</summary>
    private void UpdatePreview(string? texture, Vector4 color)
    {
        PreviewImage.Source = null;
        PreviewColor.Background = null;
        if (Material == null) return;
        if (!string.IsNullOrEmpty(texture))
        {
            var image = _host.Textures.Get(_host.Document.ResolveTexture(texture), out var error);
            PreviewImage.Source = image;
            TargetInfo.Text += image == null ? $"\nテクスチャを読めません: {error}" : $"\n{image.PixelWidth} × {image.PixelHeight} px";
        }
        else
        {
            PreviewColor.Background = new SolidColorBrush(Color.FromArgb(B(color.W), B(color.X), B(color.Y), B(color.Z)));
        }
    }

    // ───────── 適用 ─────────

    private bool TryFloats(out float[] values, params TextBox[] boxes)
    {
        values = new float[boxes.Length];
        for (int i = 0; i < boxes.Length; i++)
        {
            if (!float.TryParse(boxes[i].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
            {
                _host.SetStatus($"数値が読めません: \"{boxes[i].Text}\"");
                boxes[i].Focus();
                boxes[i].SelectAll();
                return false;
            }
        }
        return true;
    }

    private void Apply()
    {
        if (Material == null) return;
        if (!TryFloats(out var v, DR, DG, DB, DA, SR, SG, SB, Power, ER, EG, EB)) return;
        int mesh = _mesh, index = _material;
        var tex = TexturePath.Text.Trim();
        _host.Run("マテリアルの変更", s =>
        {
            var m = s.Meshes[mesh].Materials[index];
            m.FaceColor = new Vector4(v[0], v[1], v[2], v[3]);
            m.Specular = new Vector3(v[4], v[5], v[6]);
            m.Power = v[7];
            m.Emissive = new Vector3(v[8], v[9], v[10]);
            m.Texture = tex.Length == 0 ? null : tex;
            return $"マテリアル {index} を変更しました";
        }, ChangeKind.Materials);
    }

    private void OnApply(object sender, RoutedEventArgs e) => Apply();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnFieldKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (sender == HexColor && !ReadHex()) return;
        Apply();
    }

    private bool ReadHex()
    {
        var hex = HexColor.Text.Trim().TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            _host.SetStatus("色コードは #RRGGBB で入力してください");
            return false;
        }
        DR.Text = S(((rgb >> 16) & 255) / 255f);
        DG.Text = S(((rgb >> 8) & 255) / 255f);
        DB.Text = S((rgb & 255) / 255f);
        return true;
    }

    // ───────── パレット ─────────

    /// <summary>パレットを開いたときの欄の値（キャンセルしたら戻す）。</summary>
    private (string R, string G, string B)? _paletteBackup;
    private bool _paletteCommitted;

    private void OnSwatchClick(object sender, MouseButtonEventArgs e) => OnOpenPalette(sender, e);

    private void OnOpenPalette(object sender, RoutedEventArgs e)
    {
        if (Material is not { } m || Mesh is not { } mesh) return;
        var current = Color.FromRgb(B(F(DR.Text, m.FaceColor.X)), B(F(DG.Text, m.FaceColor.Y)), B(F(DB.Text, m.FaceColor.Z)));
        var meshColors = mesh.Materials.Where(x => x != m)
            .Select(x => Color.FromRgb(B(x.FaceColor.X), B(x.FaceColor.Y), B(x.FaceColor.Z)));
        _paletteBackup = (DR.Text, DG.Text, DB.Text);
        _paletteCommitted = false;
        Palette.Start(current, meshColors);
        PalettePopup.IsOpen = true;
    }

    /// <summary>パレットで選んでいる途中の色を欄と見本に出す（まだマテリアルには入れない）。</summary>
    private void OnPaletteColorChanged(Color c)
    {
        DR.Text = S(c.R / 255f);
        DG.Text = S(c.G / 255f);
        DB.Text = S(c.B / 255f);
        UpdateSwatch(new Vector4(c.R / 255f, c.G / 255f, c.B / 255f, 1));
    }

    private void OnPaletteCommitted(Color c)
    {
        OnPaletteColorChanged(c);
        _paletteCommitted = true;
        PalettePopup.IsOpen = false;
        Apply();
    }

    private void OnPaletteClosed(object? sender, EventArgs e)
    {
        // 決定せずに閉じた（キャンセル・外をクリック）ら、開く前の値に戻す
        if (_paletteCommitted || _paletteBackup is not { } b) return;
        DR.Text = b.R;
        DG.Text = b.G;
        DB.Text = b.B;
        UpdateSwatch(new Vector4(F(b.R, 1), F(b.G, 1), F(b.B, 1), 1));
        _paletteBackup = null;
    }

    // ───────── テクスチャ ─────────

    private void OnBrowseTexture(object sender, RoutedEventArgs e)
    {
        var doc = _host.Document;
        var dlg = new OpenFileDialog
        {
            Title = "テクスチャを選ぶ",
            Filter = "画像 (*.png;*.bmp;*.jpg;*.jpeg;*.gif;*.dds)|*.png;*.bmp;*.jpg;*.jpeg;*.gif;*.dds|すべて (*.*)|*.*",
            InitialDirectory = doc.Directory ?? "",
        };
        if (dlg.ShowDialog(this) != true) return;
        var file = dlg.FileName;
        if (sender is FrameworkElement { Tag: "Edit" })
        {
            if (TextureImport.Run(this, file, doc.Directory, _host.Textures) is not { } imported) return;
            file = imported.Path;
        }
        if (doc.Directory is { } dir)
        {
            var rel = Path.GetRelativePath(dir, file);
            TexturePath.Text = rel;
            if (rel.StartsWith("..", StringComparison.Ordinal))
                _host.SetStatus(".x より上のフォルダーの画像です。配布するときはパスに注意してください");
        }
        else
        {
            TexturePath.Text = file;
            _host.SetStatus("まだ保存していないので絶対パスにしました。先に .x を保存してから選び直すと相対パスになります");
        }
        Apply();
    }

    private void OnClearTexture(object sender, RoutedEventArgs e)
    {
        TexturePath.Text = "";
        Apply();
    }
}
