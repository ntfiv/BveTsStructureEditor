using System.Globalization;
using System.IO;
using System.Numerics;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BveTsStructureEditor.Core.Editing;
using BveTsStructureEditor.Core.Model;

namespace BveTsStructureEditor.App.Views;

/// <summary>
/// 「形を追加」タブ。箱・板・円柱を新しいメッシュとして足す。
/// マウスを乗せた・入力中のグループの形を <see cref="PreviewChanged"/> で知らせ、3D ビューにゴースト表示させる。
/// </summary>
public partial class CreatePanel : UserControl
{
    private enum Shape { Box, Plane, Cylinder }

    private IEditorHost? _host;
    private Shape _current = Shape.Box;
    private readonly bool _ready;

    /// <summary>プレビューする形が変わった（null なら消す）。</summary>
    public event Action<XMesh?>? PreviewChanged;

    public CreatePanel()
    {
        InitializeComponent();
        UiState.Track(ProfileSection, "Create.Profile");
        UiState.Track(ImageSection, "Create.Image");
        _ready = true;
        HighlightCurrent();
    }

    public void Attach(IEditorHost host) => _host = host;

    // ───────── 入力の読み取り ─────────

    private static bool TryRead(out float[] values, params TextBox[] boxes)
    {
        values = new float[boxes.Length];
        for (int i = 0; i < boxes.Length; i++)
            if (!float.TryParse(boxes[i].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || values[i] <= 0)
                return false;
        return true;
    }

    /// <summary>今の入力値で形を作る。読めない値があれば null と、その理由。</summary>
    private (XMesh? Mesh, string Description) BuildShape(Shape shape)
    {
        switch (shape)
        {
            case Shape.Box:
                if (!TryRead(out var b, BoxW, BoxH, BoxD)) return (null, "箱: 幅・高さ・奥行に正の数を入れてください");
                return (Primitives.Box(b[0], b[1], b[2]), $"箱 {b[0]:0.###} × {b[1]:0.###} × {b[2]:0.###} m");
            case Shape.Plane:
                if (!TryRead(out var p, PlaneW, PlaneH)) return (null, "板: 幅・高さに正の数を入れてください");
                bool vertical = PlaneKind.SelectedIndex == 0;
                return (Primitives.Plane(p[0], p[1], vertical), $"板 {p[0]:0.###} × {p[1]:0.###} m（{(vertical ? "立てる" : "寝かせる")}）");
            default:
                return BuildCylinder();
        }
    }

    /// <summary>円柱の欄: 直線 / L 字 × 詰まった棒 / パイプ。</summary>
    private (XMesh? Mesh, string Description) BuildCylinder()
    {
        bool elbow = CylShape.SelectedIndex == 1;
        bool pipe = PipeCheck.IsChecked == true;
        string kind = (elbow ? "L字" : "") + (pipe ? "パイプ" : elbow ? "の棒" : "円柱");
        if (!TryRead(out var c, CylR, CylH, CylSeg))
            return (null, $"{kind}: 半径・高さ・分割数に正の数を入れてください");
        float radius = c[0], height = c[1];
        int seg = Math.Clamp((int)c[2], 3, 128);

        float thickness = 0;
        if (pipe)
        {
            if (!TryRead(out var t, PipeThickness) || t[0] >= radius)
                return (null, $"{kind}: 厚さは 0 より大きく、半径より小さくしてください");
            thickness = t[0];
        }
        string pipeText = pipe ? $"・厚さ {thickness:0.###} m" : "";

        if (!elbow)
        {
            var straight = pipe ? Primitives.Pipe(radius, thickness, height, seg) : Primitives.Cylinder(radius, height, seg);
            return (straight, $"{kind} 半径 {radius:0.###} m{pipeText}・高さ {height:0.###} m・{seg} 分割");
        }

        if (!TryRead(out var l, ElbowLength)) return (null, $"{kind}: 横の長さに正の数を入れてください");
        if (!float.TryParse(ElbowBend.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var bend) || bend < 0)
            return (null, $"{kind}: 曲げ半径に 0 以上の数を入れてください");
        var dir = ElbowDir.SelectedIndex switch
        {
            1 => Primitives.ElbowDirection.MinusZ,
            2 => Primitives.ElbowDirection.PlusX,
            3 => Primitives.ElbowDirection.MinusX,
            _ => Primitives.ElbowDirection.PlusZ,
        };
        try
        {
            var mesh = Primitives.Elbow(radius, thickness, height, l[0], bend, seg, dir);
            var dirText = ((ComboBoxItem)ElbowDir.SelectedItem).Content;
            return (mesh, $"{kind} 半径 {radius:0.###} m{pipeText}・高さ {height:0.###} m・横 {l[0]:0.###} m（{dirText}）・" +
                          (bend > 0 ? $"曲げ半径 {bend:0.###} m" : "角ばった継ぎ目"));
        }
        catch (ArgumentException ex)
        {
            return (null, $"{kind}: {ex.Message}");
        }
    }

    // ───────── プレビュー ─────────

    /// <summary>タブが表示されたときなどに、今の形を知らせ直す。</summary>
    public void RaisePreview()
    {
        if (!_ready) return;
        var (mesh, description) = BuildShape(_current);
        PreviewInfo.Text = mesh == null
            ? description
            : $"プレビュー: {description}（{mesh.Faces.Count} 面・{mesh.Positions.Count} 頂点）";
        PreviewChanged?.Invoke(PreviewCheck.IsChecked == true ? mesh : null);
    }

    private void SetCurrent(Shape shape)
    {
        if (_current == shape) return;
        _current = shape;
        HighlightCurrent();
        RaisePreview();
    }

    private void HighlightCurrent()
    {
        var accent = (Brush)FindResource("AccentBrush");
        var line = (Brush)FindResource("LineBrush");
        BoxGroup.BorderBrush = _current == Shape.Box ? accent : line;
        PlaneGroup.BorderBrush = _current == Shape.Plane ? accent : line;
        CylGroup.BorderBrush = _current == Shape.Cylinder ? accent : line;
    }

    private void OnGroupActivated(object sender, RoutedEventArgs e)
    {
        if (_ready && sender is GroupBox { Tag: string tag } && Enum.TryParse<Shape>(tag, out var shape))
            SetCurrent(shape);
    }

    private void OnParamChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        bool elbow = CylShape.SelectedIndex == 1;
        ElbowRow.Visibility = elbow ? Visibility.Visible : Visibility.Collapsed;
        CylAddButton.Content = CylinderLabel();
        // 入力したグループの形に切り替える
        if (sender is FrameworkElement fe)
        {
            if (BoxGroup.IsAncestorOf(fe)) _current = Shape.Box;
            else if (PlaneGroup.IsAncestorOf(fe)) _current = Shape.Plane;
            else if (CylGroup.IsAncestorOf(fe)) _current = Shape.Cylinder;
            HighlightCurrent();
        }
        RaisePreview();
    }

    private void OnPreviewToggled(object sender, RoutedEventArgs e) => RaisePreview();

    // ───────── 追加 ─────────

    private void Add(Shape shape, string label)
    {
        if (_host == null) return;
        SetCurrent(shape);
        var (mesh, description) = BuildShape(shape);
        if (mesh == null)
        {
            _host.SetStatus(description);
            return;
        }
        var doc = _host.Document;
        _host.Run(label, s =>
        {
            s.Meshes.Add(mesh);
            return $"{description} を追加しました";
        });
        doc.ActiveMesh = doc.Scene.Meshes.Count - 1;
        doc.Selection.SetMode(SelectMode.Object);
        doc.Selection.Set([new ElementRef(doc.ActiveMesh, -1)]);
    }

    // ───────── 画像から板 ─────────

    public static readonly string[] ImageExtensions = [".png", ".bmp", ".jpg", ".jpeg", ".gif", ".dds"];

    public static bool IsImageFile(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private void OnAddImageFromDialog(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "板にする画像を選ぶ",
            Filter = "画像 (*.png;*.bmp;*.jpg;*.jpeg;*.gif;*.dds)|*.png;*.bmp;*.jpg;*.jpeg;*.gif;*.dds",
            Multiselect = true,
            InitialDirectory = _host?.Document.Directory ?? "",
        };
        if (dlg.ShowDialog() == true) AddImagePlanes(dlg.FileNames, null);
    }

    /// <summary>
    /// 画像ごとにテクスチャ付きの板を作って 1 回の操作として追加する。
    /// <paramref name="basePoint"/> は最初の板を置く地面上の点（null なら原点）。2 枚目以降は X 方向に並べる。
    /// </summary>
    public void AddImagePlanes(IReadOnlyList<string> files, Vector3? basePoint)
    {
        if (_host == null) return;
        if (!float.TryParse(ImageSize.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var size) || size <= 0)
        {
            _host.SetStatus("「画像から板」の大きさに正の数を入れてください（形を追加タブ）");
            return;
        }
        var mode = (Primitives.ImageSizeMode)Math.Max(0, ImageSizeMode.SelectedIndex);
        bool vertical = ImageKind.SelectedIndex == 0;
        var doc = _host.Document;

        var meshes = new List<XMesh>();
        var problems = new List<string>();
        bool outside = false;
        float cursorX = 0;
        const float gap = 0.5f;
        foreach (var file in files)
        {
            var full = Path.GetFullPath(file);
            float? aspect = null;
            if (EditBeforeImport.IsChecked == true)
            {
                // 編集画面で決めた画像と縦横比を使う（2 の累乗に伸ばしても板の形は元の見た目のまま）
                if (TextureImport.Run(Window.GetWindow(this), full, doc.Directory, _host.Textures) is not { } imported) continue;
                full = Path.GetFullPath(imported.Path);
                aspect = imported.Aspect;
            }
            var image = _host.Textures.Get(full, out var error);
            if (image == null)
            {
                problems.Add(error ?? $"読めません: {Path.GetFileName(full)}");
                continue;
            }

            string texture;
            if (doc.Directory is { } dir)
            {
                texture = Path.GetRelativePath(dir, full);
                if (texture.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(texture)) outside = true;
            }
            else texture = full;

            int pixelHeight = aspect is { } a ? Math.Max(1, (int)MathF.Round(image.PixelWidth / a)) : image.PixelHeight;
            var mesh = Primitives.ImagePlane(Path.GetFileNameWithoutExtension(full), image.PixelWidth, pixelHeight,
                mode, size, vertical, texture);
            var min = mesh.Positions.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
            var max = mesh.Positions.Aggregate(new Vector3(float.MinValue), Vector3.Max);
            float width = max.X - min.X;
            // 1 枚目は置いた点が中心、以降はその右に隙間をあけて並べる
            if (meshes.Count == 0) cursorX = -width / 2;
            var offset = (basePoint ?? Vector3.Zero) with { Y = 0 } + new Vector3(cursorX + width / 2, 0, 0);
            for (int i = 0; i < mesh.Positions.Count; i++) mesh.Positions[i] += offset;
            cursorX += width + gap;
            meshes.Add(mesh);
        }

        if (meshes.Count == 0)
        {
            _host.SetStatus("板にできる画像がありませんでした。" + string.Join(" / ", problems));
            return;
        }

        int first = doc.Scene.Meshes.Count;
        _host.Run(meshes.Count == 1 ? "画像から板を追加" : $"画像から板を {meshes.Count} 枚追加", s =>
        {
            s.Meshes.AddRange(meshes);
            var names = string.Join("、", meshes.Select(m => m.Name));
            var msg = $"画像から板を追加しました: {names}";
            if (doc.Directory == null) msg += "（未保存なのでテクスチャは今は絶対パス。保存先のフォルダー以下にある画像なら、保存時に相対パスに直します）";
            else if (outside) msg += "（.x より上や別ドライブの画像です。配布するならフォルダーをまとめてください）";
            if (problems.Count > 0) msg += "  読めなかったもの: " + string.Join(" / ", problems);
            return msg;
        });
        doc.ActiveMesh = first;
        doc.Selection.SetMode(SelectMode.Object);
        doc.Selection.Set(Enumerable.Range(first, meshes.Count).Select(i => new ElementRef(i, -1)));
    }

    // ───────── 描いて作る（断面・建物・回転体） ─────────

    /// <summary>
    /// 多角形を描いて押し出す（<see cref="Lathe"/> = false）か、回転体の作図を始めてほしい。
    /// <see cref="Top"/> なら上面図で描いて上へ、そうでなければ正面図で描いて奥 (+Z) へ押し出す。
    /// </summary>
    public sealed record ProfileRequest(bool Lathe, bool Top, float Depth, int Segments, float Angle);

    public event Action<ProfileRequest>? ProfileRequested;

    /// <summary>描く面ごとに入れた長さ（奥行 / 高さ）を覚えておき、切り替えたら戻す。</summary>
    private readonly string[] _extrudeLengths = ["20", "10"];
    private int _extrudePlane;

    private bool ExtrudeFromTop => ExtrudePlane.SelectedIndex == 1;

    private void OnExtrudePlaneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileDepth == null) return; // InitializeComponent 中
        _extrudeLengths[_extrudePlane] = ProfileDepth.Text;
        _extrudePlane = ExtrudePlane.SelectedIndex;
        ProfileDepth.Text = _extrudeLengths[_extrudePlane];
        ExtrudeLengthLabel.Text = ExtrudeFromTop ? "高さ (Y)" : "奥行 (Z)";
    }

    private void OnDrawExtrude(object sender, RoutedEventArgs e)
    {
        if (!TryRead(out var v, ProfileDepth))
        {
            _host?.SetStatus((ExtrudeFromTop ? "高さ" : "奥行") + "に正の数を入れてください");
            return;
        }
        ProfileRequested?.Invoke(new ProfileRequest(false, ExtrudeFromTop, v[0], 0, 0));
    }

    private void OnDrawLathe(object sender, RoutedEventArgs e)
    {
        if (!TryRead(out var v, LatheSegments, LatheAngle)) { _host?.SetStatus("分割数と角度に正の数を入れてください"); return; }
        ProfileRequested?.Invoke(new ProfileRequest(true, false, 0, Math.Clamp((int)v[0], 3, 256), Math.Min(v[1], 360)));
    }

    private void OnAddBox(object sender, RoutedEventArgs e) => Add(Shape.Box, "箱を追加");
    private void OnAddPlane(object sender, RoutedEventArgs e) => Add(Shape.Plane, "板を追加");
    private void OnAddCylinder(object sender, RoutedEventArgs e) => Add(Shape.Cylinder, CylinderLabel());

    private string CylinderLabel() => (CylShape.SelectedIndex == 1, PipeCheck.IsChecked == true) switch
    {
        (true, true) => "L字パイプを追加",
        (true, false) => "L字の棒を追加",
        (false, true) => "パイプを追加",
        _ => "円柱を追加",
    };
}
