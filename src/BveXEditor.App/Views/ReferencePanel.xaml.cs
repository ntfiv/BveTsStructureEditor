using System.Globalization;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BveXEditor.App.Document;
using BveXEditor.App.Geo;
using BveXEditor.App.Rendering;
using BveXEditor.Core.Editing;
using Microsoft.Win32;

namespace BveXEditor.App.Views;

/// <summary>「下絵」タブ。下絵の追加・削除と、置き方（面・幅・中心・回転・不透明度・縮尺合わせ）の編集。</summary>
public partial class ReferencePanel : UserControl
{
    public sealed record Item(ReferenceImage Ref)
    {
        public string Name => Ref.Name;
        public bool Visible => Ref.Visible;
        public string PlaneLabel => Ref.Plane switch
        {
            ReferencePlane.Ground => "上面",
            ReferencePlane.Front => "正面",
            _ => "側面",
        };
    }

    private IEditorHost? _host;
    private ViewportView? _viewport;
    private ReferenceRenderer? _renderer;
    private readonly ReferenceStore _store = new();
    private readonly List<ReferenceImage> _images = [];
    private string? _filePath;
    private bool _updating;

    public ReferencePanel()
    {
        InitializeComponent();
        UiState.Track(ObjectSection, "Reference.Objects");
    }

    public void Attach(IEditorHost host, ViewportView viewport, ReferenceRenderer renderer)
    {
        _host = host;
        _viewport = viewport;
        _renderer = renderer;
    }

    private ReferenceImage? Selected => (List.SelectedItem as Item)?.Ref;

    // ───────── ファイルとの対応 ─────────

    /// <summary>別のファイルを開いた・新規にしたとき。そのファイル用に保存してある下絵を読む。</summary>
    public void LoadForFile(string? path)
    {
        _filePath = path;
        _images.Clear();
        _images.AddRange(_store.Load(path));
        RefreshList(0);
        Apply(save: false);
    }

    /// <summary>今の下絵（作業ファイルに保存する）。</summary>
    public IReadOnlyList<ReferenceImage> Images => _images;

    /// <summary>作業ファイルから読んだ下絵に置き換える。</summary>
    public void ReplaceImages(IEnumerable<ReferenceImage> images)
    {
        _images.Clear();
        _images.AddRange(images);
        RefreshList(0);
        Apply(save: true, userEdit: false);
    }

    /// <summary>名前を付けて保存したとき。今の下絵を新しいパスに引き継ぐ。</summary>
    public void RetargetFile(string? path)
    {
        if (string.Equals(_filePath, path, StringComparison.OrdinalIgnoreCase)) return;
        _filePath = path;
        Apply(save: true, userEdit: false);
    }

    /// <summary>描き直して、ファイルに対応づけて保存する。<paramref name="userEdit"/> なら、作業ファイルを「未保存」にする。</summary>
    private void Apply(bool save = true, bool userEdit = true)
    {
        _renderer?.Rebuild(_images);
        if (save) _store.Save(_filePath, _images);
        if (save && userEdit) _host?.Document.MarkProjectChanged();
        if (_renderer is { Errors.Count: > 0 }) _host?.SetStatus("下絵: " + string.Join(" / ", _renderer.Errors));
    }

    private void RefreshList(int select)
    {
        _updating = true;
        List.Items.Clear();
        foreach (var r in _images) List.Items.Add(new Item(r));
        List.SelectedIndex = _images.Count == 0 ? -1 : Math.Clamp(select, 0, _images.Count - 1);
        _updating = false;
        // 出典が必要な下絵（地理院タイルなど）があれば一覧の下に出す
        var attributions = _images.Select(r => r.Attribution).Where(a => !string.IsNullOrEmpty(a)).Distinct().ToList();
        AttributionText.Text = string.Join(" / ", attributions);
        AttributionText.Visibility = attributions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FillEditor();
    }

    private string? _lastGsiPlace;

    private void OnAddGsi(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var dlg = new GsiMapDialog(_lastGsiPlace) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || dlg.Result is not { } result) return;
        _lastGsiPlace = dlg.PlaceBox.Text;
        var r = new ReferenceImage
        {
            Path = result.ImagePath,
            Plane = ReferencePlane.Ground,
            Width = (float)result.WidthMeters,
            Center = Vector3.Zero,
            Opacity = 0.7f,
            Label = string.Create(CultureInfo.InvariantCulture,
                $"地理院 {dlg.Layer.Label} ({dlg.Lat:0.0000}, {dlg.Lon:0.0000}) {dlg.SizeMeters:0}m"),
            Attribution = GsiTileFetcher.Attribution,
        };
        _images.Add(r);
        RefreshList(_images.Count - 1);
        Apply();
        _host.SetStatus($"地理院地図を下絵に追加しました（一辺 {r.Width:0.#} m、ズーム {result.Zoom}、北が +Z）。{GsiTileFetcher.Attribution}");
    }

    // ───────── 追加・削除 ─────────

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "下絵にする画像を選ぶ",
            Filter = "画像 (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.dds)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.dds",
            Multiselect = true,
        };
        if (dlg.ShowDialog() == true) AddImages(dlg.FileNames);
    }

    /// <summary>画像を下絵として足す。置く面は、選んでいる下絵と同じ（無ければ上面図）。</summary>
    public void AddImages(IReadOnlyList<string> files)
    {
        if (_host == null) return;
        var plane = Selected?.Plane ?? ReferencePlane.Ground;
        var problems = new List<string>();
        int added = 0;
        foreach (var file in files)
        {
            var full = Path.GetFullPath(file);
            var bmp = _host.Textures.Get(full, out var err);
            if (bmp == null)
            {
                problems.Add(err ?? Path.GetFileName(full));
                continue;
            }
            var r = new ReferenceImage { Path = full, Plane = plane, Width = 20f };
            float h = ReferencePlacement.HeightOf(r.Width, bmp.PixelWidth, bmp.PixelHeight);
            // 立てる面は下端を地面に合わせる
            r.Center = plane == ReferencePlane.Ground ? Vector3.Zero : new Vector3(0, h / 2, 0);
            _images.Add(r);
            added++;
        }
        RefreshList(_images.Count - 1);
        Apply();
        _host.SetStatus(added == 0
            ? "下絵にできる画像がありませんでした。" + string.Join(" / ", problems)
            : $"下絵を {added} 枚追加しました（幅 20 m の仮の大きさ）。「2 点で縮尺を合わせる」で実寸に合わせてください" +
              (problems.Count > 0 ? "  読めなかったもの: " + string.Join(" / ", problems) : ""));
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } r) return;
        int index = _images.IndexOf(r);
        _images.Remove(r);
        RefreshList(index);
        Apply();
        _host?.SetStatus($"下絵「{r.Name}」を外しました（画像ファイルは消していません）");
    }

    private void OnVisibleClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: Item item } cb)
        {
            item.Ref.Visible = cb.IsChecked == true;
            Apply();
        }
    }

    // ───────── 編集欄 ─────────

    private static string S(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private void OnSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating) FillEditor();
    }

    private void FillEditor()
    {
        _updating = true;
        try
        {
            var r = Selected;
            Editor.IsEnabled = r != null;
            // 見出しの "_" はアクセスキーの印として消えてしまうので "__" にする
            Editor.Header = r == null ? "選んだ下絵" : $"選んだ下絵: {r.Name.Replace("_", "__")}";
            if (r == null) return;
            PlaneBox.SelectedIndex = (int)r.Plane;
            WidthBox.Text = S(r.Width);
            CxBox.Text = S(r.CenterX);
            CyBox.Text = S(r.CenterY);
            CzBox.Text = S(r.CenterZ);
            RotationBox.Text = S(r.Rotation);
            OpacitySlider.Value = Math.Round(r.Opacity * 100);
            OpacityInfo.Text = $"{OpacitySlider.Value:0}%";
            UpdateHeightInfo(r);
        }
        finally { _updating = false; }
    }

    private void UpdateHeightInfo(ReferenceImage r)
    {
        var bmp = _host?.Textures.Get(r.Path, out _);
        HeightInfo.Text = bmp == null ? "（画像が見つかりません）"
            : $"高さ {S(ReferencePlacement.HeightOf(r.Width, bmp.PixelWidth, bmp.PixelHeight))} m（{bmp.PixelWidth}×{bmp.PixelHeight}px）";
    }

    private void OnFieldKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnFieldCommitted(sender, e);
    }

    private void OnFieldCommitted(object sender, RoutedEventArgs e)
    {
        if (_updating || Selected is not { } r) return;
        bool Read(TextBox box, out float v, bool positive = false)
        {
            if (float.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && (!positive || v > 0)) return true;
            _host?.SetStatus($"数値が読めません: \"{box.Text}\"");
            return false;
        }
        if (!Read(WidthBox, out var w, positive: true) || !Read(CxBox, out var x) || !Read(CyBox, out var y) ||
            !Read(CzBox, out var z) || !Read(RotationBox, out var rot))
        {
            FillEditor();
            return;
        }
        r.Width = w;
        r.Center = new Vector3(x, y, z);
        r.Rotation = rot;
        UpdateHeightInfo(r);
        Apply();
    }

    private void OnPlaneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || Selected is not { } r || PlaneBox.SelectedIndex < 0) return;
        var plane = (ReferencePlane)PlaneBox.SelectedIndex;
        if (plane == r.Plane) return;
        // 面を変えたら、置き直しやすい既定の位置（地面なら原点、立てるなら下端を地面）にする
        var bmp = _host?.Textures.Get(r.Path, out _);
        float h = bmp == null ? r.Width : ReferencePlacement.HeightOf(r.Width, bmp.PixelWidth, bmp.PixelHeight);
        r.Plane = plane;
        r.Center = plane == ReferencePlane.Ground ? Vector3.Zero : new Vector3(0, h / 2, 0);
        r.Rotation = 0;
        int index = _images.IndexOf(r);
        RefreshList(index);
        Apply();
    }

    private void OnRotateLeft(object sender, RoutedEventArgs e) => Rotate(-90);
    private void OnRotateRight(object sender, RoutedEventArgs e) => Rotate(90);

    private void Rotate(float deg)
    {
        if (Selected is not { } r) return;
        r.Rotation = ((r.Rotation + deg) % 360 + 360) % 360;
        FillEditor();
        Apply();
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || Selected is not { } r) return;
        r.Opacity = (float)(OpacitySlider.Value / 100);
        OpacityInfo.Text = $"{OpacitySlider.Value:0}%";
        Apply();
    }

    // ───────── 3D ビューで点を指定する操作 ─────────

    private bool BeginPick(out ReferenceImage r)
    {
        r = Selected!;
        if (_viewport == null || r == null) return false;
        if (!r.Visible)
        {
            r.Visible = true;
            RefreshList(_images.IndexOf(r));
            Apply();
        }
        return true;
    }

    private void OnScaleByTwoPoints(object sender, RoutedEventArgs e)
    {
        if (!BeginPick(out var r)) return;
        _host?.SetStatus($"縮尺合わせ: 3D ビューで「{r.Name}」の上の 1 点目をクリック（Esc で取り消し）");
        _viewport!.StartPointPick(r.Center, ReferencePlacement.Normal(r.Plane), 2, points =>
        {
            float measured = Vector3.Distance(points[0], points[1]);
            if (measured < 1e-4f)
            {
                _host?.SetStatus("2 点が同じ位置です。離れた 2 点を選んでください");
                return;
            }
            var dlg = new InputDialog("縮尺を合わせる",
                $"クリックした 2 点の、実際の距離を入れてください。\n（今の下絵の上では {measured:0.###} m）", "m", measured)
            { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;
            var (center, width) = ReferencePlacement.ScaleByTwoPoints(r.Center, r.Width, points[0], points[1], dlg.Result);
            r.Center = center;
            r.Width = width;
            FillEditor();
            Apply();
            _host?.SetStatus($"縮尺を合わせました（{dlg.Result / measured:0.###} 倍、幅 {width:0.###} m）");
        });
    }

    private void OnMovePointToOrigin(object sender, RoutedEventArgs e)
    {
        if (!BeginPick(out var r)) return;
        _host?.SetStatus($"原点合わせ: 3D ビューで「{r.Name}」の上の、原点にしたい点をクリック（Esc で取り消し）");
        _viewport!.StartPointPick(r.Center, ReferencePlacement.Normal(r.Plane), 1, points =>
        {
            // 面の上で、原点の真上（真横）にあたる位置へその点を持っていく
            var n = ReferencePlacement.Normal(r.Plane);
            var originOnPlane = n * Vector3.Dot(r.Center, n);
            r.Center += originOnPlane - points[0];
            FillEditor();
            Apply();
            _host?.SetStatus("クリックした点が原点に来るよう下絵を動かしました");
        });
    }
}
