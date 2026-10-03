using System.ComponentModel;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BveXEditor.App.Document;
using BveXEditor.App.Rendering;
using BveXEditor.App.Views;
using BveXEditor.Core.Editing;
using BveXEditor.Core.Format;
using BveXEditor.Core.Model;
using Microsoft.Win32;

namespace BveXEditor.App;

public partial class MainWindow : Window, IEditorHost
{
    public sealed record MeshItem(int Index, string Name, string Stats, bool Visible);
    public sealed record MaterialVisItem(int Index, string Label, string Stats, Brush Swatch, ImageSource? Thumbnail, string Tooltip, bool Visible);

    private readonly EditorDocument _doc = new();
    private readonly TextureCache _textures = new();
    private readonly ViewOptions _view = new();
    private readonly SceneRenderer _renderer;
    private bool _updatingUi;

    public EditorDocument Document => _doc;
    public TextureCache Textures => _textures;

    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BveXEditor");
    private static readonly string RecentFile = Path.Combine(SettingsDir, "recent.txt");

    public MainWindow(string? openPath = null)
    {
        InitializeComponent();
        _renderer = new SceneRenderer(_textures);
        Viewport.Attach(_doc, _renderer, _view);
        // 参考オブジェクト（半透明のモデル）は下絵より先に描く
        var objectRenderer = new ReferenceObjectRenderer(_textures)
        {
            Loader = path =>
            {
                var (scene, _, format) = _doc.LoadModel(path);
                return (scene, format);
            },
        };
        Viewport.AttachReferences(objectRenderer.Visual);
        var referenceRenderer = new ReferenceRenderer(_textures);
        Viewport.AttachReferences(referenceRenderer.Visual);
        // Repeater で並べたプレビュー（窓を開いている間だけ中身が入る）
        _repeaterPreview = new RepeaterPreviewRenderer(_textures);
        Viewport.AttachReferences(_repeaterPreview.Visual);
        Viewport.AttachGuides(_guides.Visual);
        References.Attach(this, Viewport, referenceRenderer);
        References.Objects.Attach(this, Viewport, objectRenderer);
        Viewport.StatusRequested += SetStatus;
        Viewport.ProjectionChanged += OnProjectionChanged;
        Viewport.MoveSnapStep = () => Edit.NudgeStepValue;
        ApplyNavigation(announce: false);
        UvEditor.Attach(this);
        Edit.Attach(this);
        Create.Attach(this);
        Create.ProfileRequested += OnProfileRequested;
        // 「形を追加」タブを開いている間だけ、置く予定の形を 3D ビューに出す
        Create.PreviewChanged += mesh => Viewport.SetShapePreview(RightTabs.SelectedItem == CreateTab ? mesh : null);
        RightTabs.SelectionChanged += (_, e) =>
        {
            if (e.Source != RightTabs) return;
            if (RightTabs.SelectedItem == CreateTab) Create.RaisePreview();
            else Viewport.SetShapePreview(null);
        };
        TextView.Attach(this);

        _doc.Changed += OnDocChanged;
        _doc.CaptureEditorState = CaptureProjectState;
        PreviewKeyDown += OnPreviewKeyDown;
        Drop += OnDrop;
        DragOver += OnDragOver;
        Closing += OnClosing;
        CenterTabs.SelectionChanged += (_, e) =>
        {
            if (e.Source == CenterTabs) UvEditor.Refresh(ChangeKind.All);
        };

        BuildRecentMenu();
        UpdateExportMenu();
        OnDocChanged(ChangeKind.All);
        if (openPath != null) Loaded += (_, _) => OpenStartupPath(openPath);
    }

    // ───────── IEditorHost ─────────

    public void Run(string label, Func<XScene, string> operation, ChangeKind kind = ChangeKind.Geometry | ChangeKind.Structure)
    {
        try
        {
            SetStatus(_doc.Execute(label, operation, kind));
        }
        catch (Exception ex)
        {
            SetStatus($"{label} に失敗しました: {ex.Message}");
        }
    }

    public void SetStatus(string message) => StatusText.Text = message;

    // ───────── 変更の反映 ─────────

    private void OnDocChanged(ChangeKind kind)
    {
        bool sceneChanged = (kind & (ChangeKind.Geometry | ChangeKind.Materials | ChangeKind.Structure)) != 0;
        if (sceneChanged)
        {
            _view.Prune(_doc.Scene);
            _renderer.RebuildScene(_doc, _view);
            if (_renderer.TextureErrors.Count > 0 && kind.HasFlag(ChangeKind.File))
                SetStatus("テクスチャ: " + string.Join(" / ", _renderer.TextureErrors));
        }
        _renderer.RebuildSelection(_doc, _view);
        Viewport.UpdateGizmo();

        if (kind.HasFlag(ChangeKind.Live))
        {
            // ドラッグ中は 3D と UV だけ。テキストの作り直しなどは確定時にまとめて
            if (CenterTabs.SelectedIndex == 1) UvEditor.Refresh(kind);
            return;
        }

        if (kind != ChangeKind.Geometry)
        {
            RefreshMeshList();
            RefreshMaterialList();
        }
        RefreshModeButtons();
        Edit.Refresh(kind);
        _checkWindow?.Panel.Refresh(kind);
        _repeaterWindow?.Panel.Refresh(kind);
        TextView.Refresh(kind);
        if (CenterTabs.SelectedIndex == 1) UvEditor.Refresh(kind);

        Title = $"{(_doc.IsDirty ? "● " : "")}{_doc.DisplayName} - BVE X Editor";
        StatsText.Text = $"メッシュ {_doc.Scene.Meshes.Count}  頂点 {_doc.Scene.TotalVertices}  面 {_doc.Scene.TotalFaces}" +
                         (_doc.Selection.IsEmpty ? "" : $"  |  選択 {_doc.Selection.Count}");
        UndoMenu.Header = _doc.UndoLabel is { } u ? $"元に戻す: {u}(_U)" : "元に戻す(_U)";
        RedoMenu.Header = _doc.RedoLabel is { } r ? $"やり直し: {r}(_R)" : "やり直し(_R)";
        UndoMenu.IsEnabled = _doc.CanUndo;
        RedoMenu.IsEnabled = _doc.CanRedo;

        if (kind.HasFlag(ChangeKind.File))
        {
            FormatText.Text = _doc.SourceFormat is { } f ? $"読み込み形式: {f}" : "";
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Viewport.FitAll);
        }
    }

    private void RefreshMeshList()
    {
        _updatingUi = true;
        try
        {
            MeshList.Items.Clear();
            for (int i = 0; i < _doc.Scene.Meshes.Count; i++)
            {
                var m = _doc.Scene.Meshes[i];
                MeshList.Items.Add(new MeshItem(i, string.IsNullOrEmpty(m.Name) ? $"(無名 {i})" : m.Name,
                    $"{m.Faces.Count} 面", !_view.HiddenMeshes.Contains(i)));
            }
            MeshList.SelectedIndex = _doc.Scene.Meshes.Count == 0 ? -1 : _doc.ActiveMesh;
        }
        finally { _updatingUi = false; }
    }

    private void RefreshModeButtons()
    {
        _updatingUi = true;
        ModeObject.IsChecked = _doc.Selection.Mode == SelectMode.Object;
        ModeFace.IsChecked = _doc.Selection.Mode == SelectMode.Face;
        ModeVertex.IsChecked = _doc.Selection.Mode == SelectMode.Vertex;
        _updatingUi = false;
    }

    private void OnMeshListSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || MeshList.SelectedIndex < 0) return;
        _doc.ActiveMesh = MeshList.SelectedIndex;
        if (_doc.Selection.Mode == SelectMode.Object)
            _doc.Selection.Set([new ElementRef(_doc.ActiveMesh, -1)]);
        else
            OnDocChanged(ChangeKind.Selection);
    }

    private void OnMeshVisibleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: int index } cb) return;
        if (cb.IsChecked == true) _view.HiddenMeshes.Remove(index);
        else _view.HiddenMeshes.Add(index);
        ApplyVisibility();
    }

    /// <summary>表示・非表示を変えたときの描き直し。モデルは変わらないのでテキストなどは触らない。</summary>
    private void ApplyVisibility()
    {
        _renderer.RebuildScene(_doc, _view);
        _renderer.RebuildSelection(_doc, _view);
        Viewport.UpdateGizmo();
    }

    // ───────── マテリアルの表示切り替え ─────────

    private void RefreshMaterialList()
    {
        var mi = _doc.ActiveMesh;
        var mesh = mi < _doc.Scene.Meshes.Count ? _doc.Scene.Meshes[mi] : null;
        int keep = MaterialVisList.SelectedIndex;
        MaterialVisList.Items.Clear();
        MaterialHeader.Text = mesh == null ? "マテリアル" : $"マテリアル（{(string.IsNullOrEmpty(mesh.Name) ? $"無名 {mi}" : mesh.Name)}）";
        if (mesh == null) return;

        for (int i = 0; i < mesh.Materials.Count; i++)
        {
            var m = mesh.Materials[i];
            var c = m.FaceColor;
            byte B(float v) => (byte)Math.Clamp(MathF.Round(v * 255), 0, 255);
            var swatch = new SolidColorBrush(Color.FromRgb(B(c.X), B(c.Y), B(c.Z)));
            swatch.Freeze();
            ImageSource? thumb = null;
            string label;
            if (!string.IsNullOrEmpty(m.Texture))
            {
                thumb = _textures.Get(_doc.ResolveTexture(m.Texture), out _);
                label = Path.GetFileName(m.Texture.Replace('\\', '/'));
            }
            else label = $"#{B(c.X):X2}{B(c.Y):X2}{B(c.Z):X2}";
            int faces = mesh.Faces.Count(f => f.Material == i);
            var tip = $"マテリアル {i}\n{m.Describe()}\n{faces} 面";
            MaterialVisList.Items.Add(new MaterialVisItem(i, $"{i}: {label}", $"{faces} 面", swatch, thumb, tip,
                !_view.HiddenMaterials.Contains((mi, i))));
        }
        MaterialVisList.SelectedIndex = MaterialVisList.Items.Count == 0 ? -1 : Math.Clamp(keep, 0, MaterialVisList.Items.Count - 1);
    }

    private int MaterialCount => _doc.ActiveMesh < _doc.Scene.Meshes.Count ? _doc.Scene.Meshes[_doc.ActiveMesh].Materials.Count : 0;

    private void OnMaterialVisibleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: int index } cb) return;
        var key = (_doc.ActiveMesh, index);
        if (cb.IsChecked == true) _view.HiddenMaterials.Remove(key);
        else _view.HiddenMaterials.Add(key);
        ApplyVisibility();
        var hidden = _view.HiddenMaterials.Count(h => h.Mesh == _doc.ActiveMesh);
        SetStatus(hidden == 0 ? "すべてのマテリアルを表示中" : $"{hidden} 個のマテリアルを隠しています");
    }

    private void SetMaterialVisibility(Func<int, bool> visible, string message)
    {
        int mi = _doc.ActiveMesh;
        for (int i = 0; i < MaterialCount; i++)
        {
            if (visible(i)) _view.HiddenMaterials.Remove((mi, i));
            else _view.HiddenMaterials.Add((mi, i));
        }
        ApplyVisibility();
        RefreshMaterialList();
        SetStatus(message);
    }

    private void OnShowAllMaterials(object sender, RoutedEventArgs e) =>
        SetMaterialVisibility(_ => true, "すべてのマテリアルを表示しました");

    private void OnSoloMaterial(object sender, RoutedEventArgs e)
    {
        int solo = MaterialVisList.SelectedIndex;
        if (solo < 0) { SetStatus("表示するマテリアルを一覧で選んでください"); return; }
        SetMaterialVisibility(i => i == solo, $"マテリアル {solo} だけを表示しています");
    }

    private void OnInvertMaterials(object sender, RoutedEventArgs e)
    {
        int mi = _doc.ActiveMesh;
        var before = _view.HiddenMaterials.Where(h => h.Mesh == mi).Select(h => h.Material).ToHashSet();
        SetMaterialVisibility(before.Contains, "表示と非表示を入れ替えました");
    }

    /// <summary>ダブルクリックしたマテリアルの面を選ぶ（隠していたら表示に戻す）。</summary>
    private void OnMaterialDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MaterialVisList.SelectedItem is not MaterialVisItem item) return;
        int mi = _doc.ActiveMesh;
        if (mi >= _doc.Scene.Meshes.Count) return;
        if (_view.HiddenMaterials.Remove((mi, item.Index)))
        {
            ApplyVisibility();
            RefreshMaterialList();
        }
        var mesh = _doc.Scene.Meshes[mi];
        var faces = Enumerable.Range(0, mesh.Faces.Count).Where(f => mesh.Faces[f].Material == item.Index)
            .Select(f => new ElementRef(mi, f)).ToList();
        _doc.Selection.SetMode(SelectMode.Face);
        _doc.Selection.Set(faces);
        SetStatus($"マテリアル {item.Index} の {faces.Count} 面を選択しました");
    }

    // ───────── ファイル ─────────

    private bool ConfirmDiscard()
    {
        if (!_doc.IsDirty) return true;
        var r = MessageBox.Show($"{_doc.DisplayName} は保存されていません。保存しますか？", "BVE X Editor",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return r switch
        {
            MessageBoxResult.Yes => SaveInteractive(false),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    public void OpenFile(string path)
    {
        if (!ConfirmDiscard()) return;
        try
        {
            _view.HiddenMeshes.Clear();
            _view.HiddenMaterials.Clear();
            _doc.Open(path);
            References.LoadForFile(path);
            References.Objects.LoadForFile(path);
            if (EditorDocument.IsProject(path) && ProjectState.FromJson(_doc.LoadedEditorState) is { } state)
                ApplyProjectState(state, Path.GetDirectoryName(Path.GetFullPath(path))!);
            UpdateExportMenu();
            AddRecent(path);
            var msg = $"{Path.GetFileName(path)} を開きました（{_doc.SourceFormat}）";
            if (_doc.Warnings.Count > 0) msg += $"  警告 {_doc.Warnings.Count} 件（ファイル > 読み込み時の警告を表示）";
            if (_renderer.TextureErrors.Count > 0) msg += "  テクスチャ: " + string.Join(" / ", _renderer.TextureErrors);
            SetStatus(msg);
            // 作業ファイルはカメラを後から戻すので、そのとき出る表示の案内で上書きされないよう最後にもう一度出す
            if (EditorDocument.IsProject(path)) Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => SetStatus(msg));
        }
        catch (Exception ex) when (ex is XFormatException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"開けませんでした。\n\n{ex.Message}", "BVE X Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool SaveInteractive(bool saveAs, bool project = false)
    {
        string? path = _doc.FilePath;
        bool needDialog = saveAs || path == null;
        if (!needDialog && _doc.SourceHasPmxRig)
            needDialog = !Confirm("この PMX にはボーン・モーフ・物理などが含まれていますが、保存すると形とマテリアルだけになります。上書きしますか？\n（「いいえ」で別名保存）");
        else if (!needDialog && _doc.SourceHeader is { Kind: XEncodingKind.Binary } or { Compressed: true })
            needDialog = !ConfirmOverwriteBinary();
        if (needDialog)
        {
            // 作業ファイルとして保存するときや、作業ファイルを開いているときは .bvex を最初に選ぶ
            bool asProject = project || (path != null && EditorDocument.IsProject(path));
            string baseName = path != null ? Path.GetFileNameWithoutExtension(path) : "structure";
            var dlg = new SaveFileDialog
            {
                Title = project ? "作業ファイルとして保存（メッシュ・下絵・表示の状態をそのまま保存）" : "名前を付けて保存",
                Filter = "DirectX (*.x)|*.x|PMX モデル (*.pmx)|*.pmx|作業ファイル (*.bvex)|*.bvex",
                FilterIndex = asProject ? 3 : path != null && EditorDocument.IsPmx(path) ? 2 : 1,
                FileName = baseName + (asProject ? ".bvex" : path != null && EditorDocument.IsPmx(path) ? ".pmx" : ".x"),
                InitialDirectory = path != null ? Path.GetDirectoryName(path) : "",
            };
            if (dlg.ShowDialog() != true) return false;
            path = dlg.FileName;
        }
        try
        {
            _doc.Save(path!);
            References.RetargetFile(path!);
            References.Objects.RetargetFile(path!);
            AddRecent(path!);
            SetStatus(EditorDocument.IsProject(path!)
                ? $"{path} に作業ファイルとして保存しました（BVE で使うにはファイル > 書き出し）"
                : EditorDocument.IsPmx(path!)
                ? $"{path} に保存しました（PMX 2.0 静的モデル / 1 単位 = {_doc.PmxOptions.Scale:0.####} m）"
                : $"{path} に保存しました（テキスト形式 / {_doc.WriteOptions.Encoding.WebName}）");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"保存できませんでした。\n\n{ex.Message}", "BVE X Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    /// <summary>バイナリ・圧縮で開いたファイルをテキストで上書きしてよいか。</summary>
    private static bool Confirm(string message) =>
        MessageBox.Show(message, "BVE X Editor", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private bool ConfirmOverwriteBinary() =>
        MessageBox.Show("このファイルはバイナリ／圧縮形式で読み込みました。テキスト形式で上書きしますか？\n（「いいえ」で別名保存）",
            "BVE X Editor", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private void OnNew(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        _view.HiddenMeshes.Clear();
        _view.HiddenMaterials.Clear();
        _doc.New();
        References.LoadForFile(null);
        References.Objects.LoadForFile(null);
        UpdateExportMenu();
        SetStatus("新規ファイル。右の「形を追加」タブから始められます");
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "モデルを開く",
            Filter = "対応ファイル (*.bvex;*.x;*.pmx)|*.bvex;*.x;*.pmx|作業ファイル (*.bvex)|*.bvex|DirectX (*.x)|*.x|PMX モデル (*.pmx)|*.pmx|すべて (*.*)|*.*",
            InitialDirectory = _doc.Directory ?? "",
        };
        if (dlg.ShowDialog() == true) OpenFile(dlg.FileName);
    }

    private void OnSave(object sender, RoutedEventArgs e) => SaveInteractive(false);
    private void OnSaveAs(object sender, RoutedEventArgs e) => SaveInteractive(true);
    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnShowWarnings(object sender, RoutedEventArgs e)
    {
        var text = _doc.Warnings.Count == 0 ? "警告はありません。" : string.Join("\n", _doc.Warnings.Select(w => "・" + w));
        MessageBox.Show(text, "読み込み時の警告", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// モデル (.x / .pmx) は、今のファイルに追加するか新しく開くかを選ばせる（何も開いていなければそのまま開く）。
    /// 画像は板にして今のファイルに追加する。どちらも 3D ビューの地面に落とせばその位置を使える。
    /// </summary>
    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        var models = files.Where(EditorDocument.IsModelFile).ToList();
        var images = files.Where(CreatePanel.IsImageFile).ToList();
        int ignored = files.Length - models.Count - images.Count;

        System.Numerics.Vector3? ground = null;
        if (CenterTabs.SelectedIndex == 0)
        {
            var p = e.GetPosition(Viewport);
            if (p.X >= 0 && p.Y >= 0 && p.X <= Viewport.ActualWidth && p.Y <= Viewport.ActualHeight)
                ground = Viewport.GroundPointAt(e.GetPosition(Viewport.ViewportElement));
        }
        Activate();

        bool addedReferenceObjects = false;
        if (models.Count > 0 && RightTabs.SelectedItem == ReferenceTab)
        {
            References.Objects.AddFiles(models);
            models.Clear();
            addedReferenceObjects = true;
        }
        if (models.Count > 0)
        {
            bool empty = _doc.FilePath == null && !_doc.IsDirty && _doc.Scene.Meshes.Count == 0;
            if (empty && models.Count == 1)
            {
                OpenFile(models[0]);
            }
            else if (empty)
            {
                ImportModels(models, null);
            }
            else
            {
                var dialog = new DropChoiceDialog(models, _doc.DisplayName, ground != null) { Owner = this };
                if (dialog.ShowDialog() != true) return;
                if (dialog.Result == DropChoiceDialog.Choice.Open)
                {
                    OpenFile(models[0]);
                    if (_doc.FilePath == null || !string.Equals(Path.GetFullPath(_doc.FilePath), Path.GetFullPath(models[0]), StringComparison.OrdinalIgnoreCase))
                        return; // 開くのをやめた（未保存の確認でキャンセルなど）
                }
                else ImportModels(models, dialog.PlaceAtDropPoint ? ground : null);
            }
        }

        if (images.Count > 0)
        {
            // 「下絵」タブを開いているときは下絵として、それ以外は板メッシュとして追加する
            if (RightTabs.SelectedItem == ReferenceTab) References.AddImages(images);
            else Create.AddImagePlanes(images, ground);
        }
        if (ignored > 0 && models.Count + images.Count == 0 && !addedReferenceObjects)
            SetStatus("対応していないファイルです（.bvex / .x / .pmx / PNG・BMP・JPG・GIF・DDS 画像）");
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "今のファイルに追加するモデルを選ぶ",
            Filter = "対応ファイル (*.bvex;*.x;*.pmx)|*.bvex;*.x;*.pmx|作業ファイル (*.bvex)|*.bvex|DirectX (*.x)|*.x|PMX モデル (*.pmx)|*.pmx",
            Multiselect = true,
            InitialDirectory = _doc.Directory ?? "",
        };
        if (dlg.ShowDialog() == true) ImportModels(dlg.FileNames, null);
    }

    /// <summary>
    /// 別のモデルファイルのメッシュを今のファイルに足す（1 回の操作として元に戻せる）。
    /// テクスチャのパスは今のファイル基準に直す。<paramref name="ground"/> があれば、底面中心をそこへ移す。
    /// </summary>
    private void ImportModels(IReadOnlyList<string> files, System.Numerics.Vector3? ground)
    {
        var meshes = new List<XMesh>();
        var problems = new List<string>();
        int warningCount = 0;
        string? firstWarning = null;
        foreach (var file in files)
        {
            try
            {
                var (scene, warnings, _) = _doc.LoadModel(file);
                TexturePaths.Rebase(scene.Meshes, Path.GetDirectoryName(Path.GetFullPath(file))!, _doc.Directory);
                meshes.AddRange(scene.Meshes.Where(m => m.Faces.Count > 0));
                warningCount += warnings.Count;
                firstWarning ??= warnings.Count > 0 ? $"{Path.GetFileName(file)}: {warnings[0]}" : null;
            }
            catch (Exception ex) when (ex is XFormatException or IOException or UnauthorizedAccessException)
            {
                problems.Add($"{Path.GetFileName(file)}（{ex.Message}）");
            }
        }
        if (meshes.Count == 0)
        {
            SetStatus("追加できるメッシュがありませんでした。" + string.Join(" / ", problems));
            return;
        }

        if (ground is { } g)
        {
            var min = meshes.SelectMany(m => m.Positions).Aggregate(new System.Numerics.Vector3(float.MaxValue), System.Numerics.Vector3.Min);
            var max = meshes.SelectMany(m => m.Positions).Aggregate(new System.Numerics.Vector3(float.MinValue), System.Numerics.Vector3.Max);
            var offset = new System.Numerics.Vector3(g.X - (min.X + max.X) / 2, -min.Y, g.Z - (min.Z + max.Z) / 2);
            foreach (var m in meshes)
                for (int i = 0; i < m.Positions.Count; i++) m.Positions[i] += offset;
        }

        int first = _doc.Scene.Meshes.Count;
        var label = files.Count == 1 ? $"{Path.GetFileName(files[0])} を追加" : $"{files.Count} ファイルを追加";
        Run(label, s =>
        {
            s.Meshes.AddRange(meshes);
            var msg = $"{label}しました（メッシュ {meshes.Count}）";
            if (warningCount > 0) msg += $"  警告 {warningCount} 件: {firstWarning}";
            if (problems.Count > 0) msg += "  読めなかったもの: " + string.Join(" / ", problems);
            if (_doc.Directory == null) msg += "  ※未保存なのでテクスチャは絶対パス（保存時に保存先以下なら相対パスへ直します）";
            return msg;
        });
        _doc.ActiveMesh = first;
        _doc.Selection.SetMode(SelectMode.Object);
        _doc.Selection.Set(Enumerable.Range(first, meshes.Count).Select(i => new ElementRef(i, -1)));
    }

    /// <summary>起動時の引数が画像なら、新規ファイルに板として追加する。</summary>
    private void OpenStartupPath(string path)
    {
        if (CreatePanel.IsImageFile(path)) Create.AddImagePlanes([path], null);
        else OpenFile(path);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmDiscard()) e.Cancel = true;
    }

    private void AddRecent(string path)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var list = LoadRecent().Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).Prepend(path).Take(10);
            File.WriteAllLines(RecentFile, list);
        }
        catch (IOException) { }
        BuildRecentMenu();
    }

    private static List<string> LoadRecent()
    {
        try { return File.Exists(RecentFile) ? File.ReadAllLines(RecentFile).Where(l => l.Length > 0).ToList() : []; }
        catch (IOException) { return []; }
    }

    private void BuildRecentMenu()
    {
        RecentMenu.Items.Clear();
        foreach (var p in LoadRecent())
        {
            var item = new MenuItem { Header = p.Replace("_", "__") };
            item.Click += (_, _) => OpenFile(p);
            RecentMenu.Items.Add(item);
        }
        RecentMenu.IsEnabled = RecentMenu.Items.Count > 0;
    }

    // ───────── 編集 ─────────

    private void OnUndo(object sender, RoutedEventArgs e) => SetStatus(_doc.Undo() is { } l ? $"元に戻しました: {l}" : "これ以上戻せません");
    private void OnRedo(object sender, RoutedEventArgs e) => SetStatus(_doc.Redo() is { } l ? $"やり直しました: {l}" : "やり直す操作がありません");

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        var s = _doc.Scene;
        var sel = _doc.Selection;
        IEnumerable<ElementRef> all = sel.Mode switch
        {
            SelectMode.Object => Enumerable.Range(0, s.Meshes.Count).Select(m => new ElementRef(m, -1)),
            SelectMode.Face => Enumerable.Range(0, s.Meshes.Count).SelectMany(m => Enumerable.Range(0, s.Meshes[m].Faces.Count).Select(f => new ElementRef(m, f))),
            _ => Enumerable.Range(0, s.Meshes.Count).SelectMany(m => Enumerable.Range(0, s.Meshes[m].Positions.Count).Select(v => new ElementRef(m, v))),
        };
        // 隠しているメッシュ・マテリアルの面や頂点は含めない
        var visibleVerts = new Dictionary<int, HashSet<int>?>();
        sel.Set(all.Where(r =>
        {
            if (_view.HiddenMeshes.Contains(r.Mesh)) return false;
            var mesh = s.Meshes[r.Mesh];
            return sel.Mode switch
            {
                SelectMode.Face => _view.IsFaceVisible(r.Mesh, mesh.Faces[r.Index]),
                SelectMode.Vertex => (visibleVerts.TryGetValue(r.Mesh, out var vv) ? vv : visibleVerts[r.Mesh] = _view.VisibleVertices(r.Mesh, mesh)) is not { } set || set.Contains(r.Index),
                _ => true,
            };
        }));
    }

    private void OnSelectNone(object sender, RoutedEventArgs e) => _doc.Selection.Clear();
    private void OnDelete(object sender, RoutedEventArgs e) => Edit.DeleteSelection();

    private void OnModeChecked(object sender, RoutedEventArgs e)
    {
        // XAML 読み込み中（IsChecked="True" の適用時）はまだ他のボタンが無い
        if (_updatingUi || ModeFace == null || ModeVertex == null) return;
        var mode = ModeFace.IsChecked == true ? SelectMode.Face : ModeVertex.IsChecked == true ? SelectMode.Vertex : SelectMode.Object;
        SetMode(mode);
    }

    private void SetMode(SelectMode mode)
    {
        _doc.Selection.SetMode(mode);
        SetStatus(mode switch
        {
            SelectMode.Object => "オブジェクト選択: クリックでメッシュ単位",
            SelectMode.Face => "面選択: クリックで面、ドラッグで範囲（面の中心が入ったもの）",
            _ => "頂点選択: 面を張るときはクリックした順番が使われます",
        });
    }

    private void OnGizmoChecked(object sender, RoutedEventArgs e)
    {
        if (_updatingUi || GizmoNone == null || GizmoMove == null || GizmoRotate == null || GizmoScale == null) return;
        SetGizmo(GizmoMove.IsChecked == true ? GizmoMode.Move
            : GizmoRotate.IsChecked == true ? GizmoMode.Rotate
            : GizmoScale.IsChecked == true ? GizmoMode.Scale
            : GizmoMode.None);
    }

    private void SetGizmo(GizmoMode mode)
    {
        Viewport.GizmoMode = mode;
        _updatingUi = true;
        GizmoNone.IsChecked = mode == GizmoMode.None;
        GizmoMove.IsChecked = mode == GizmoMode.Move;
        GizmoRotate.IsChecked = mode == GizmoMode.Rotate;
        GizmoScale.IsChecked = mode == GizmoMode.Scale;
        _updatingUi = false;
        SetStatus(mode switch
        {
            GizmoMode.Move => "移動ギズモ: 矢印で軸方向、四角で平面内、中心の白い箱で画面と平行に動かす（Ctrl でスナップ）",
            GizmoMode.Rotate => "回転ギズモ: リングをドラッグして回す（Ctrl で 15° 刻み）",
            GizmoMode.Scale => "拡大ギズモ: 立方体の軸で 1 方向、軸の間の斜めの帯で 2 方向、中心の箱を左右にドラッグで全体を拡大縮小（Ctrl で 0.1 倍刻み）",
            _ => "ギズモを隠しました",
        });
    }

    /// <summary>矢印キーで選択を動かす。画面の向きに一番近いワールド軸に沿わせる。</summary>
    private void Nudge(Key key)
    {
        var step = Edit.NudgeStepValue;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) step *= 10;
        var (right, up, forward) = Viewport.CameraAxes();
        static Vector3 Snap(Vector3 v)
        {
            v.Y = 0;
            if (v.LengthSquared() < 1e-6f) return Vector3.UnitZ;
            return MathF.Abs(v.X) > MathF.Abs(v.Z) ? new Vector3(MathF.Sign(v.X), 0, 0) : new Vector3(0, 0, MathF.Sign(v.Z));
        }
        // 見下ろしているときは画面の上方向を「前」とみなす
        var fwd = MathF.Abs(forward.Y) > 0.9f ? Snap(up) : Snap(forward);
        var dir = key switch
        {
            Key.Left => -Snap(right),
            Key.Right => Snap(right),
            Key.Up => fwd,
            Key.Down => -fwd,
            Key.PageUp => Vector3.UnitY,
            _ => -Vector3.UnitY,
        };
        var m = Matrix4x4.CreateTranslation(dir * step);
        Run("矢印キーで移動", s => _doc.TransformSelection(s, m), ChangeKind.Geometry);
        SetStatus($"({dir.X * step:0.###}, {dir.Y * step:0.###}, {dir.Z * step:0.###}) m 移動");
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        bool typing = Keyboard.FocusedElement is TextBox or ICSharpCode.AvalonEdit.Editing.TextArea;

        if (Viewport.IsPickingPoints && e.Key is Key.Escape or Key.Enter or Key.Back)
        {
            if (e.Key == Key.Escape) Viewport.CancelPointPick();
            else if (e.Key == Key.Back) Viewport.UndoPickPoint();
            else Viewport.FinishPointPick();
            e.Handled = true;
            return;
        }

        if (mods == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.S: SaveInteractive(false); e.Handled = true; return;
                case Key.E: ExportInteractive(useLast: false); e.Handled = true; return;
                case Key.O: OnOpen(this, e); e.Handled = true; return;
                case Key.I: OnImport(this, e); e.Handled = true; return;
                case Key.N: OnNew(this, e); e.Handled = true; return;
            }
            if (typing) return;
            switch (e.Key)
            {
                case Key.Z: OnUndo(this, e); e.Handled = true; return;
                case Key.Y: OnRedo(this, e); e.Handled = true; return;
                case Key.A: OnSelectAll(this, e); e.Handled = true; return;
                case Key.D1: Viewport.SetView(ViewportView.Preset.Back); e.Handled = true; return;
                case Key.D3: Viewport.SetView(ViewportView.Preset.Left); e.Handled = true; return;
            }
            return;
        }
        if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key is Key.S or Key.E)
        {
            if (e.Key == Key.S) SaveInteractive(true);
            else ExportInteractive(useLast: true);
            e.Handled = true;
            return;
        }
        if (Viewport.IsGizmoDragging)
        {
            // ドラッグ中はほかの操作をさせない（Esc だけ取り消しに使う）
            if (e.Key == Key.Escape) Viewport.CancelGizmoDrag();
            e.Handled = e.Key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift);
            return;
        }
        if (typing || (mods & ~ModifierKeys.Shift) != 0) return;

        switch (e.Key)
        {
            case Key.Escape:
                Viewport.ClearMeasurement();
                _doc.Selection.Clear();
                break;
            case Key.Delete: Edit.DeleteSelection(); break;
            case Key.Q: SetMode(SelectMode.Object); break;
            case Key.E: SetMode(SelectMode.Face); break;
            case Key.R: SetMode(SelectMode.Vertex); break;
            case Key.G: SetGizmo(Viewport.GizmoMode == GizmoMode.Move ? GizmoMode.None : GizmoMode.Move); break;
            case Key.T: SetGizmo(Viewport.GizmoMode == GizmoMode.Rotate ? GizmoMode.None : GizmoMode.Rotate); break;
            case Key.S: SetGizmo(Viewport.GizmoMode == GizmoMode.Scale ? GizmoMode.None : GizmoMode.Scale); break;
            case Key.M: SetMirror(!_doc.MirrorX); break;
            case Key.O: Viewport.Orthographic = !Viewport.Orthographic; break;
            case Key.D: SetDimensions(!Viewport.ShowDimensions); break;
            case Key.W:
                WireMenu.IsChecked = !WireMenu.IsChecked;
                ApplyViewOptions();
                break;
            case Key.F: Viewport.FitAll(); break;
            case Key.D1 or Key.NumPad1: Viewport.SetView(ViewportView.Preset.Front); break;
            case Key.D3 or Key.NumPad3: Viewport.SetView(ViewportView.Preset.Right); break;
            case Key.D7 or Key.NumPad7: Viewport.SetView(ViewportView.Preset.Top); break;
            case Key.D5 or Key.NumPad5: Viewport.SetView(ViewportView.Preset.Perspective); break;
            case Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown:
                if (CenterTabs.SelectedIndex != 0) return;
                Nudge(e.Key);
                break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnMirrorCheck(object sender, RoutedEventArgs e)
    {
        // SetMirror からチェックを付け直したときにも呼ばれるので、変わったときだけ
        if (_doc.MirrorX != (MirrorCheck.IsChecked == true)) SetMirror(MirrorCheck.IsChecked == true);
    }

    // ───────── BVE メニュー・保存設定 ─────────

    private CheckWindow? _checkWindow;

    private void OnCheck(object sender, RoutedEventArgs e)
    {
        if (_checkWindow == null)
        {
            _checkWindow = new CheckWindow(this) { Owner = this };
            _checkWindow.Closed += (_, _) => _checkWindow = null;
            _checkWindow.Show();
        }
        else _checkWindow.Activate();
    }

    private RepeaterWindow? _repeaterWindow;
    private readonly RepeaterPreviewRenderer _repeaterPreview;

    private void OnRepeater(object sender, RoutedEventArgs e)
    {
        if (_repeaterWindow == null)
        {
            _repeaterWindow = new RepeaterWindow(this, _repeaterPreview) { Owner = this };
            _repeaterWindow.Closed += (_, _) => _repeaterWindow = null;
            _repeaterWindow.Show();
        }
        else _repeaterWindow.Activate();
    }

    private void OnAddSign(object sender, RoutedEventArgs e)
    {
        var dlg = new SignDialog(_doc.Directory) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is not { } mesh) return;
        AddMeshAndSelect(mesh, $"看板（{Path.GetFileName(dlg.ImagePath)}）");
    }

    private void OnBatch(object sender, RoutedEventArgs e)
    {
        new BatchDialog(_doc.Directory, _doc.WriteOptions) { Owner = this }.ShowDialog();
    }

    /// <summary>画像を選んで編集し、PNG で保存するだけ（モデルは変えない）。</summary>
    private void OnEditTextureImage(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "編集する画像を選ぶ",
            Filter = "画像 (*.png;*.bmp;*.jpg;*.jpeg;*.gif;*.dds)|*.png;*.bmp;*.jpg;*.jpeg;*.gif;*.dds",
            InitialDirectory = _doc.Directory ?? "",
        };
        if (dlg.ShowDialog(this) != true) return;
        if (TextureImport.Run(this, dlg.FileName, _doc.Directory, _textures) is not { } result) return;
        SetStatus(result.Edited
            ? $"編集した画像を保存しました: {result.Path}（マテリアルの「参照…」から使えます）"
            : "画像は編集しませんでした");
    }

    private void OnSaveOptions(object sender, RoutedEventArgs e)
    {
        new SaveOptionsWindow(this) { Owner = this }.ShowDialog();
    }

    // ───────── 計測・作図 ─────────

    private void OnMeasure(object sender, RoutedEventArgs e)
    {
        var (planePoint, normal) = Viewport.MeasurePlane();
        Viewport.StartPointPick(new PointPickOptions
        {
            PlanePoint = planePoint,
            Normal = normal,
            MaxPoints = 2,
            MinPoints = 2,
            AlwaysSnapToVertices = true,
            ProjectSnapsToPlane = false,
            Prompt = "計測: 1 点目をクリック（頂点の近くでは頂点に吸着。頂点がない所は" +
                     (normal == Vector3.UnitY ? "地面" : "原点を通る縦の面") + "の上。Esc で取り消し）",
        }, points =>
        {
            var d = points[1] - points[0];
            var label = $"{d.Length():0.###} m";
            Viewport.ShowMeasurement(points[0], points[1], label);
            SetStatus($"距離 {d.Length():0.###} m（ΔX {d.X:0.###} / ΔY {d.Y:0.###} / ΔZ {d.Z:0.###}、水平 {new Vector2(d.X, d.Z).Length():0.###} m）");
        });
    }

    /// <summary>
    /// 正面図で断面を描いて Z 方向に押し出すか Y 軸で回す。上面図なら輪郭を描いて上へ押し出す（下絵をなぞった建物）。
    /// </summary>
    private void OnProfileRequested(CreatePanel.ProfileRequest request)
    {
        bool lathe = request.Lathe;
        bool top = request.Top && !lathe;
        Viewport.SetView(top ? ViewportView.Preset.Top : ViewportView.Preset.Front);
        Viewport.Orthographic = true;
        Viewport.StartPointPick(new PointPickOptions
        {
            PlanePoint = Vector3.Zero,
            Normal = top ? Vector3.UnitY : Vector3.UnitZ,
            MinPoints = lathe ? 2 : 3,
            Closed = !lathe,
            Prompt = lathe
                ? "回転体: 正面図で輪郭の点を順にクリック（X が軸 = 画面中央の縦線からの距離。Enter で確定、Ctrl でグリッド、Shift で頂点に吸着）"
                : top
                    ? "上面図で建物の角を順にクリック（Enter かダブルクリックで確定、Ctrl でグリッド、Shift で頂点に吸着、Backspace で 1 点戻す）"
                    : "断面の押し出し: 正面図で断面の角を順にクリック（Enter かダブルクリックで確定、Ctrl でグリッド、Shift で頂点に吸着）",
        }, points =>
        {
            XMesh mesh;
            string what;
            try
            {
                if (lathe)
                {
                    mesh = ProfileOps.Lathe(points.Select(p => new Vector2(p.X, p.Y)).ToList(), request.Segments, request.Angle, "Lathe");
                    what = $"回転体（{points.Count} 点・{request.Segments} 分割・{request.Angle:0.#}°）";
                }
                else if (top)
                {
                    mesh = ProfileOps.ExtrudePolygon(points, new Vector3(0, request.Depth, 0), "Building");
                    what = $"建物（{points.Count} 角形・高さ {request.Depth:0.###} m）";
                }
                else
                {
                    mesh = ProfileOps.ExtrudePolygon(points, new Vector3(0, 0, request.Depth), "Profile");
                    what = $"断面の押し出し（{points.Count} 角形・奥行き {request.Depth:0.###} m）";
                }
            }
            catch (InvalidOperationException ex)
            {
                SetStatus("作れませんでした: " + ex.Message);
                return;
            }
            AddMeshAndSelect(mesh, what);
        });
    }

    /// <summary>作ったメッシュを 1 回の操作として足し、それを選ぶ。</summary>
    public void AddMeshAndSelect(XMesh mesh, string description)
    {
        Run($"{description}を追加", s =>
        {
            s.Meshes.Add(mesh);
            return $"{description}を追加しました（{mesh.Faces.Count} 面）";
        });
        _doc.ActiveMesh = _doc.Scene.Meshes.Count - 1;
        _doc.Selection.SetMode(SelectMode.Object);
        _doc.Selection.Set([new ElementRef(_doc.ActiveMesh, -1)]);
    }

    private readonly GuideRenderer _guides = new();

    private void OnGuideGauge(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !Enum.TryParse<GuideGauge>(tag, out var gauge)) return;
        CheckGuideMenu(gauge);
        ApplyGuides(gauge, _guides.Platform);
    }

    private void CheckGuideMenu(GuideGauge gauge)
    {
        foreach (var item in GuideMenu.Items.OfType<MenuItem>())
            if (item.Tag is string t) item.IsChecked = t == gauge.ToString();
    }

    // ───────── 視点の操作 ─────────

    private readonly NavigationSettings _navigation = NavigationSettings.Load();

    private void OnNavigationStyle(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && Enum.TryParse<NavigationStyle>(tag, out var style))
            _navigation.Style = style;
        _navigation.Save();
        ApplyNavigation(announce: true);
    }

    private void OnNavigationOption(object sender, RoutedEventArgs e)
    {
        _navigation.InvertZoom = InvertZoomMenu.IsChecked;
        _navigation.AroundMouse = AroundMouseMenu.IsChecked;
        _navigation.Save();
        ApplyNavigation(announce: true);
    }

    private void ApplyNavigation(bool announce)
    {
        Viewport.ApplyNavigation(_navigation);
        foreach (var item in NavigationMenu.Items.OfType<MenuItem>())
            if (item.Tag is string t) item.IsChecked = t == _navigation.Style.ToString();
        InvertZoomMenu.IsChecked = _navigation.InvertZoom;
        AroundMouseMenu.IsChecked = _navigation.AroundMouse;
        if (announce)
            SetStatus($"視点の操作: {NavigationSettings.DisplayName(_navigation.Style)}（{NavigationSettings.Describe(_navigation.Style)} / ホイール: ズーム" +
                      (_navigation.InvertZoom ? "・向きを反転" : "") +
                      (_navigation.AroundMouse ? "）" : "・画面の中心を軸に）"));
    }

    // ───────── 作業ファイル (.bvex) の画面の状態 ─────────

    private static string? RelativeTo(string dir, string? path) =>
        string.IsNullOrEmpty(path) ? path : Path.GetRelativePath(dir, Path.GetFullPath(path));

    private static string? ResolveFrom(string dir, string? path) =>
        string.IsNullOrEmpty(path) ? path : Path.GetFullPath(Path.Combine(dir, path));

    /// <summary>作業ファイルに入れる画面の状態を集める。パスは <paramref name="projectDir"/> からの相対パスにする。</summary>
    private System.Text.Json.Nodes.JsonObject CaptureProjectState(string projectDir)
    {
        var state = new ProjectState
        {
            Camera = Viewport.GetCamera(),
            View = new ViewState
            {
                ShowTextures = TexturesMenu.IsChecked,
                Wireframe = WireMenu.IsChecked,
                BackFaces = BackMenu.IsChecked,
                Grid = GridMenu.IsChecked,
                Dimensions = Viewport.ShowDimensions,
                HiddenMeshes = _view.HiddenMeshes.Order().ToList(),
                HiddenMaterials = _view.HiddenMaterials.Select(h => new[] { h.Mesh, h.Material }).ToList(),
                Gizmo = Viewport.GizmoMode.ToString(),
                MirrorX = _doc.MirrorX,
                CenterTab = CenterTabs.SelectedIndex,
                RightTab = RightTabs.SelectedIndex,
            },
            Selection = new SelectionState
            {
                Mode = _doc.Selection.Mode.ToString(),
                Items = _doc.Selection.Items.Select(r => new[] { r.Mesh, r.Index }).ToList(),
                ActiveMesh = _doc.ActiveMesh,
            },
            Guides = new GuideState { Gauge = _guides.Gauge.ToString(), Platform = _guides.Platform },
            ReferenceObjects = References.Objects.Items.Select(o => new ReferenceObject
            {
                Path = RelativeTo(projectDir, o.Path) ?? "",
                OffsetX = o.OffsetX, OffsetY = o.OffsetY, OffsetZ = o.OffsetZ,
                RotationX = o.RotationX, RotationY = o.RotationY, RotationZ = o.RotationZ,
                Opacity = o.Opacity, Visible = o.Visible,
            }).ToList(),
            References = References.Images.Select(r => new ReferenceImage
            {
                Path = RelativeTo(projectDir, r.Path) ?? "",
                Plane = r.Plane, Width = r.Width, CenterX = r.CenterX, CenterY = r.CenterY, CenterZ = r.CenterZ,
                Rotation = r.Rotation, Opacity = r.Opacity, Visible = r.Visible, Label = r.Label, Attribution = r.Attribution,
            }).ToList(),
            Save = new SaveState
            {
                Encoding = _doc.WriteOptions.Encoding.CodePage == 932 ? "shift_jis" : "utf-8",
                Precision = _doc.WriteOptions.Precision,
                WriteTemplates = _doc.WriteOptions.WriteTemplates,
                SmoothAngle = _doc.WriteOptions.SmoothAngle,
                PmxScale = _doc.PmxOptions.Scale,
                ExportPath = RelativeTo(projectDir, _doc.ExportPath),
            },
        };
        return state.ToJson();
    }

    /// <summary>作業ファイルを開いた直後に、保存してあった画面の状態へ戻す。読めない項目は飛ばす。</summary>
    private void ApplyProjectState(ProjectState state, string projectDir)
    {
        if (state.Save is { } save)
        {
            _doc.WriteOptions.Encoding = save.Encoding == "utf-8" ? new System.Text.UTF8Encoding(false) : XFile.ShiftJis;
            _doc.WriteOptions.Precision = Math.Clamp(save.Precision, 1, 9);
            _doc.WriteOptions.WriteTemplates = save.WriteTemplates;
            if (save.SmoothAngle > 0) _doc.WriteOptions.SmoothAngle = save.SmoothAngle;
            if (save.PmxScale > 0) _doc.PmxOptions.Scale = save.PmxScale;
            _doc.ExportPath = ResolveFrom(projectDir, save.ExportPath);
        }
        if (state.References is { } refs)
            References.ReplaceImages(refs.Select(r => { r.Path = ResolveFrom(projectDir, r.Path) ?? ""; return r; }));
        if (state.ReferenceObjects is { } objects)
            References.Objects.ReplaceItems(objects.Select(o => { o.Path = ResolveFrom(projectDir, o.Path) ?? ""; return o; }));
        if (state.Guides is { } guides && Enum.TryParse<GuideGauge>(guides.Gauge, out var gauge))
        {
            CheckGuideMenu(gauge);
            GuidePlatformMenu.IsChecked = guides.Platform;
            _guides.Set(gauge, guides.Platform);
            Viewport.SetGuideLegend(_guides.Legend);
        }
        if (state.View is { } view)
        {
            TexturesMenu.IsChecked = view.ShowTextures;
            WireMenu.IsChecked = view.Wireframe;
            BackMenu.IsChecked = view.BackFaces;
            GridMenu.IsChecked = view.Grid;
            _view.HiddenMeshes.Clear();
            foreach (var m in view.HiddenMeshes) _view.HiddenMeshes.Add(m);
            _view.HiddenMaterials.Clear();
            foreach (var h in view.HiddenMaterials.Where(h => h.Length == 2)) _view.HiddenMaterials.Add((h[0], h[1]));
            _view.Prune(_doc.Scene);
            ApplyViewOptions();
            RefreshMeshList();
            RefreshMaterialList();
            if (Enum.TryParse<GizmoMode>(view.Gizmo, out var gizmo)) SetGizmo(gizmo);
            if (_doc.MirrorX != view.MirrorX) SetMirror(view.MirrorX);
            if (Viewport.ShowDimensions != view.Dimensions) SetDimensions(view.Dimensions);
            if (view.CenterTab >= 0 && view.CenterTab < CenterTabs.Items.Count) CenterTabs.SelectedIndex = view.CenterTab;
            if (view.RightTab >= 0 && view.RightTab < RightTabs.Items.Count) RightTabs.SelectedIndex = view.RightTab;
        }
        if (state.Selection is { } sel && Enum.TryParse<SelectMode>(sel.Mode, out var mode))
        {
            _doc.ActiveMesh = Math.Clamp(sel.ActiveMesh, 0, Math.Max(0, _doc.Scene.Meshes.Count - 1));
            _doc.Selection.SetMode(mode);
            _doc.Selection.Set(sel.Items.Where(i => i.Length == 2).Select(i => new ElementRef(i[0], i[1])));
            _doc.Selection.Validate(_doc.Scene);
        }
        // 全体表示（ファイルを開いたときに予約される）より後にカメラを戻す
        if (state.Camera is { } camera)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Viewport.SetCamera(camera));
    }

    // ───────── 書き出し ─────────

    private void OnExport(object sender, RoutedEventArgs e) => ExportInteractive(useLast: false);
    private void OnExportLast(object sender, RoutedEventArgs e) => ExportInteractive(useLast: true);

    private bool ExportInteractive(bool useLast)
    {
        var path = useLast ? _doc.ExportPath : null;
        if (path == null)
        {
            var last = _doc.ExportPath;
            var dlg = new SaveFileDialog
            {
                Title = "書き出し（BVE で使う .x / .pmx）",
                Filter = "DirectX (*.x)|*.x|PMX モデル (*.pmx)|*.pmx",
                FilterIndex = last != null && EditorDocument.IsPmx(last) ? 2 : 1,
                FileName = last != null ? Path.GetFileName(last) : Path.ChangeExtension(_doc.FilePath is { } f ? Path.GetFileName(f) : "structure", ".x"),
                InitialDirectory = last != null ? Path.GetDirectoryName(last) : _doc.Directory ?? "",
            };
            if (dlg.ShowDialog(this) != true) return false;
            path = dlg.FileName;
        }
        try
        {
            _doc.Export(path);
            UpdateExportMenu();
            SetStatus($"{path} に書き出しました（開いているファイルはそのまま。次からは Ctrl+Shift+E で同じ場所に書き出せます）");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"書き出せませんでした。\n\n{ex.Message}", "BVE X Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void UpdateExportMenu()
    {
        ExportLastMenu.IsEnabled = _doc.ExportPath != null;
        ExportLastMenu.Header = _doc.ExportPath is { } p ? $"前回の場所に書き出す: {Path.GetFileName(p)}" : "前回の場所に書き出す";
    }

    private void OnSaveProject(object sender, RoutedEventArgs e) => SaveInteractive(saveAs: true, project: true);

    private void OnGuidePlatform(object sender, RoutedEventArgs e) => ApplyGuides(_guides.Gauge, GuidePlatformMenu.IsChecked);

    private void ApplyGuides(GuideGauge gauge, bool platform)
    {
        _guides.Set(gauge, platform);
        Viewport.SetGuideLegend(_guides.Legend);
        SetStatus(gauge == GuideGauge.None
            ? "線路・限界のガイドを消しました"
            : $"{TrackGuides.Describe(gauge)} のガイドを表示しています（原点 = 軌道中心・レール面。限界の形は代表的な寸法の目安です）");
    }

    private void OnOrthoCheck(object sender, RoutedEventArgs e) => Viewport.Orthographic = OrthoCheck.IsChecked == true;
    private void OnOrthoMenu(object sender, RoutedEventArgs e) => Viewport.Orthographic = OrthoMenu.IsChecked;

    /// <summary>ビューの投影が変わったら（キー・メニュー・ツールバーのどこからでも）表示をそろえる。</summary>
    private void OnProjectionChanged(bool ortho)
    {
        OrthoCheck.IsChecked = ortho;
        OrthoMenu.IsChecked = ortho;
        SetStatus(ortho ? "平行投影で表示しています（O で透視投影に戻す）" : "透視投影で表示しています");
    }

    private void OnDimensionCheck(object sender, RoutedEventArgs e)
    {
        // SetDimensions からチェックを付け直したときにも呼ばれるので、変わったときだけ
        if (Viewport.ShowDimensions != (DimensionCheck.IsChecked == true)) SetDimensions(DimensionCheck.IsChecked == true);
    }

    private void OnDimensionMenu(object sender, RoutedEventArgs e) => SetDimensions(DimensionMenu.IsChecked);

    private void SetDimensions(bool on)
    {
        Viewport.ShowDimensions = on;
        DimensionCheck.IsChecked = on;
        DimensionMenu.IsChecked = on;
        SetStatus(on ? Viewport.DescribeDimensions() : "寸法の表示を消しました");
    }

    private void SetMirror(bool on)
    {
        _doc.MirrorX = on;
        MirrorCheck.IsChecked = on;
        Viewport.UpdateGizmo();
        SetStatus(on
            ? "左右対称 ON: 選択を変形すると、X=0 をはさんだ反対側の頂点も鏡像で動きます（紫の印が連動する頂点）"
            : "左右対称 OFF");
    }

    // ───────── 表示 ─────────

    private void OnViewOption(object sender, RoutedEventArgs e) => ApplyViewOptions();

    private void OnWireCheck(object sender, RoutedEventArgs e)
    {
        // メニューや W キーからチェックを付け直したときにも呼ばれるので、変わったときだけ描き直す
        if (WireMenu.IsChecked == (WireCheck.IsChecked == true)) return;
        WireMenu.IsChecked = WireCheck.IsChecked == true;
        ApplyViewOptions();
    }

    private void ApplyViewOptions()
    {
        _view.ShowTextures = TexturesMenu.IsChecked;
        _view.ShowWireframe = WireMenu.IsChecked;
        _view.ShowBackFaces = BackMenu.IsChecked;
        WireCheck.IsChecked = WireMenu.IsChecked;
        Viewport.ShowGrid = GridMenu.IsChecked;
        _renderer.RebuildScene(_doc, _view);
        _renderer.RebuildSelection(_doc, _view);
    }

    private void OnFit(object sender, RoutedEventArgs e) => Viewport.FitAll();

    private void OnPreset(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<ViewportView.Preset>(tag, out var p))
            Viewport.SetView(p);
    }

    private void OnReloadTextures(object sender, RoutedEventArgs e)
    {
        _renderer.RebuildScene(_doc, _view);
        UvEditor.Refresh(ChangeKind.Materials);
        SetStatus(_renderer.TextureErrors.Count == 0 ? "テクスチャを読み直しました" : "テクスチャ: " + string.Join(" / ", _renderer.TextureErrors));
    }
}
