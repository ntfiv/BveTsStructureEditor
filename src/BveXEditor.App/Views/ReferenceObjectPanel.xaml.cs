using System.Globalization;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BveXEditor.App.Document;
using BveXEditor.App.Rendering;
using Microsoft.Win32;

namespace BveXEditor.App.Views;

/// <summary>参考オブジェクト（見比べるだけのモデル）の追加・削除と、位置・不透明度の変更。</summary>
public partial class ReferenceObjectPanel : UserControl
{
    public sealed record Item(ReferenceObject Ref, string Info)
    {
        public string Name => Ref.Name;
        public string FullPath => Ref.Path;
        public bool Visible => Ref.Visible;
    }

    private IEditorHost? _host;
    private ViewportView? _viewport;
    private ReferenceObjectRenderer? _renderer;
    private readonly ReferenceObjectStore _store = new();
    private readonly List<ReferenceObject> _items = [];
    private string? _filePath;
    private bool _updating;

    public ReferenceObjectPanel()
    {
        InitializeComponent();
    }

    public void Attach(IEditorHost host, ViewportView viewport, ReferenceObjectRenderer renderer)
    {
        _host = host;
        _viewport = viewport;
        _renderer = renderer;
    }

    private ReferenceObject? Selected => (List.SelectedItem as Item)?.Ref;

    // ───────── ファイルとの対応 ─────────

    /// <summary>今の参考オブジェクト（作業ファイルに保存する）。</summary>
    public IReadOnlyList<ReferenceObject> Items => _items;

    /// <summary>別のファイルを開いた・新規にしたとき。そのファイル用に覚えてある参考オブジェクトを読む。</summary>
    public void LoadForFile(string? path)
    {
        _filePath = path;
        _items.Clear();
        _items.AddRange(_store.Load(path));
        RefreshList(0);
        Apply(save: false);
    }

    /// <summary>名前を付けて保存したとき。今の参考オブジェクトを新しいパスに引き継ぐ。</summary>
    public void RetargetFile(string? path)
    {
        if (string.Equals(_filePath, path, StringComparison.OrdinalIgnoreCase)) return;
        _filePath = path;
        Apply(save: true, userEdit: false);
    }

    /// <summary>作業ファイルから読んだ参考オブジェクトに置き換える。</summary>
    public void ReplaceItems(IEnumerable<ReferenceObject> items)
    {
        _items.Clear();
        _items.AddRange(items);
        RefreshList(0);
        Apply(save: true, userEdit: false);
    }

    /// <summary>描き直して保存する。<paramref name="userEdit"/> なら、作業ファイルを「未保存」にする。</summary>
    private void Apply(bool save = true, bool userEdit = true)
    {
        _renderer?.Rebuild(_items);
        if (save) _store.Save(_filePath, _items);
        if (save && userEdit) _host?.Document.MarkProjectChanged();
    }

    private void RefreshList(int select)
    {
        _updating = true;
        List.Items.Clear();
        foreach (var r in _items) List.Items.Add(new Item(r, Describe(r)));
        List.SelectedIndex = _items.Count == 0 ? -1 : Math.Clamp(select, 0, _items.Count - 1);
        _updating = false;
        FillEditor();
    }

    private string Describe(ReferenceObject r)
    {
        if (_renderer == null) return "";
        var (faces, _, error) = _renderer.Describe(r.Path);
        return error != null ? "読めません" : $"{faces} 面";
    }

    // ───────── 追加・削除 ─────────

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "参考オブジェクトにするモデルを選ぶ",
            Filter = "対応ファイル (*.x;*.pmx;*.bvex)|*.x;*.pmx;*.bvex|DirectX (*.x)|*.x|PMX モデル (*.pmx)|*.pmx|作業ファイル (*.bvex)|*.bvex",
            Multiselect = true,
            InitialDirectory = _host?.Document.Directory ?? "",
        };
        if (dlg.ShowDialog() == true) AddFiles(dlg.FileNames);
    }

    /// <summary>モデルファイルを参考オブジェクトとして足す（原点に置く）。</summary>
    public void AddFiles(IReadOnlyList<string> files)
    {
        if (_host == null || _renderer == null) return;
        var problems = new List<string>();
        int added = 0;
        foreach (var file in files)
        {
            var r = new ReferenceObject { Path = Path.GetFullPath(file) };
            var (_, _, error) = _renderer.Describe(r.Path);
            if (error != null)
            {
                problems.Add($"{r.Name}（{error}）");
                continue;
            }
            _items.Add(r);
            added++;
        }
        RefreshList(_items.Count - 1);
        Apply();
        _host.SetStatus(added == 0
            ? "参考オブジェクトにできるモデルがありませんでした。" + string.Join(" / ", problems)
            : $"参考オブジェクトを {added} 個追加しました（表示だけで、選択・編集・.x への書き出しには含まれません）" +
              (problems.Count > 0 ? "  読めなかったもの: " + string.Join(" / ", problems) : ""));
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } r) return;
        int index = _items.IndexOf(r);
        _items.Remove(r);
        RefreshList(index);
        Apply();
        _host?.SetStatus($"参考オブジェクト「{r.Name}」を外しました（ファイルは消していません）");
    }

    private void OnReload(object sender, RoutedEventArgs e)
    {
        if (_renderer == null) return;
        foreach (var r in _items) _renderer.Forget(r.Path);
        int index = Selected is { } s ? _items.IndexOf(s) : 0;
        Apply(save: false);
        RefreshList(index);
        _host?.SetStatus("参考オブジェクトを読み直しました");
    }

    private void OnVisibleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: Item item } cb) return;
        item.Ref.Visible = cb.IsChecked == true;
        Apply();
    }

    // ───────── 位置・不透明度 ─────────

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
            Editor.Header = r == null ? "選んだ参考オブジェクト" : $"選んだ参考オブジェクト: {r.Name.Replace("_", "__")}";
            if (r == null)
            {
                FileInfo.Text = "";
                return;
            }
            OxBox.Text = S(r.OffsetX);
            OyBox.Text = S(r.OffsetY);
            OzBox.Text = S(r.OffsetZ);
            RxBox.Text = S(r.RotationX);
            RyBox.Text = S(r.RotationY);
            RzBox.Text = S(r.RotationZ);
            OpacitySlider.Value = Math.Round(r.Opacity * 100);
            OpacityInfo.Text = $"{OpacitySlider.Value:0}%";
            if (_renderer != null)
            {
                var (faces, format, error) = _renderer.Describe(r.Path);
                FileInfo.Text = error != null ? $"読めません: {error}" : $"{format}・{faces} 面　{r.Path}";
            }
        }
        finally { _updating = false; }
    }

    private void OnPositionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnPositionCommitted(sender, e);
    }

    private void OnPositionCommitted(object sender, RoutedEventArgs e)
    {
        if (_updating || Selected is not { } r) return;
        static bool Read(TextBox box, out float v) =>
            float.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        if (!Read(OxBox, out var x) || !Read(OyBox, out var y) || !Read(OzBox, out var z) ||
            !Read(RxBox, out var rx) || !Read(RyBox, out var ry) || !Read(RzBox, out var rz))
        {
            _host?.SetStatus("位置・回転の数値が読めません");
            FillEditor();
            return;
        }
        var offset = new Vector3(x, y, z);
        var rotation = new Vector3(Normalize(rx), Normalize(ry), Normalize(rz));
        if (r.Offset == offset && r.Rotation == rotation) return;
        r.Offset = offset;
        r.Rotation = rotation;
        FillEditor();
        Apply();
    }

    /// <summary>角度を -180〜180 に収める（360° 回したら 0° に戻るように）。</summary>
    private static float Normalize(float deg)
    {
        deg %= 360;
        if (deg > 180) deg -= 360;
        if (deg <= -180) deg += 360;
        return deg;
    }

    private void OnRotateLeft(object sender, RoutedEventArgs e) => RotateY(-90);
    private void OnRotateRight(object sender, RoutedEventArgs e) => RotateY(90);

    private void RotateY(float deg)
    {
        if (Selected is not { } r) return;
        r.RotationY = Normalize(r.RotationY + deg);
        FillEditor();
        Apply();
        _host?.SetStatus($"「{r.Name}」を縦軸まわりに {S(r.RotationY)}° にしました");
    }

    private void OnResetRotation(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } r) return;
        r.Rotation = Vector3.Zero;
        FillEditor();
        Apply();
    }

    private void OnResetPosition(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } r) return;
        r.Offset = Vector3.Zero;
        FillEditor();
        Apply();
    }

    private void OnPlaceOnGround(object sender, RoutedEventArgs e)
    {
        if (_viewport == null || Selected is not { } r) return;
        if (!r.Visible)
        {
            r.Visible = true;
            RefreshList(_items.IndexOf(r));
            Apply();
        }
        _host?.SetStatus($"「{r.Name}」を置く位置を、3D ビューの地面でクリックしてください（Ctrl でグリッド、Shift で頂点に吸着、Esc で取り消し）");
        _viewport.StartPointPick(Vector3.Zero, Vector3.UnitY, 1, points =>
        {
            r.Offset = new Vector3(points[0].X, r.OffsetY, points[0].Z);
            FillEditor();
            Apply();
            _host?.SetStatus($"「{r.Name}」を ({S(r.OffsetX)}, {S(r.OffsetY)}, {S(r.OffsetZ)}) に置きました");
        });
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || Selected is not { } r) return;
        r.Opacity = (float)(OpacitySlider.Value / 100);
        OpacityInfo.Text = $"{OpacitySlider.Value:0}%";
        Apply();
    }
}
