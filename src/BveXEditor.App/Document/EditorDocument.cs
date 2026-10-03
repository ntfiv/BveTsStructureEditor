using System.IO;
using BveXEditor.Core.Editing;
using BveXEditor.Core.Format;
using BveXEditor.Core.Format.Pmx;
using BveXEditor.Core.Model;

namespace BveXEditor.App.Document;

/// <summary>何が変わったか。表示側はこれで作り直す範囲を決める。</summary>
[Flags]
public enum ChangeKind
{
    None = 0,
    Geometry = 1,
    Materials = 2,
    Structure = 4,
    Selection = 8,
    File = 16,
    /// <summary>ドラッグ途中の更新。3D 表示だけ描き直し、テキストやパネルは確定時まで待つ。</summary>
    Live = 32,
    All = Geometry | Materials | Structure | Selection | File,
}

/// <summary>
/// 開いている 1 ファイルの状態。元に戻すはシーン丸ごとのスナップショット。
/// BVE のストラクチャは大きくても数万頂点なので、この単純さで足りる。
/// </summary>
public sealed class EditorDocument
{
    private const int MaxHistory = 100;

    private readonly List<(string Label, XScene Scene)> _undo = [];
    private readonly List<(string Label, XScene Scene)> _redo = [];
    private (string Label, XScene Scene)? _pendingEdit;

    public XScene Scene { get; private set; } = new();
    public Selection Selection { get; } = new();
    public string? FilePath { get; private set; }
    public XHeader? SourceHeader { get; private set; }
    /// <summary>読み込んだ形式の説明（画面表示用）。</summary>
    public string? SourceFormat { get; private set; }
    /// <summary>ボーン・モーフ・物理などを含む PMX を開いた（保存するとそれらは失われる）。</summary>
    public bool SourceHasPmxRig { get; private set; }
    public IReadOnlyList<string> Warnings { get; private set; } = [];
    /// <summary>開いたときのテキスト（テキスト形式のときだけ）。テキストタブの初期表示に使う。</summary>
    public string? OriginalText { get; private set; }
    public bool IsDirty { get; private set; }

    /// <summary>作業中のメッシュ（マテリアル一覧・UV 表示の対象）。</summary>
    public int ActiveMesh { get; set; }

    /// <summary>
    /// 左右対称モード。選択を変形すると、X=0 をはさんだ反対側の頂点も鏡像で動く。
    /// 編集のしかたの設定なので元に戻すの対象にはしない。
    /// </summary>
    public bool MirrorX
    {
        get => _mirrorX;
        set
        {
            if (_mirrorX == value) return;
            _mirrorX = value;
            Changed?.Invoke(ChangeKind.Selection); // 連動する頂点の目印を描き直す
        }
    }
    private bool _mirrorX;

    /// <summary>選択に行列を掛ける。左右対称モードなら反対側も鏡像で動かす。</summary>
    public string TransformSelection(XScene scene, System.Numerics.Matrix4x4 matrix) =>
        MirrorX
            ? Symmetry.Transform(scene, Selection, matrix, Symmetry.Plan(scene, Selection))
            : MeshOps.Transform(scene, Selection, matrix);

    public XWriteOptions WriteOptions { get; } = new();
    public PmxOptions PmxOptions { get; } = new();

    public static bool IsPmx(string path) => string.Equals(Path.GetExtension(path), ".pmx", StringComparison.OrdinalIgnoreCase);

    public event Action<ChangeKind>? Changed;

    public EditorDocument()
    {
        Selection.Changed += () => Changed?.Invoke(ChangeKind.Selection);
    }

    public string DisplayName => FilePath is null ? "無題" : Path.GetFileName(FilePath);
    public string? Directory => FilePath is null ? null : Path.GetDirectoryName(FilePath);

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;
    public string? RedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;

    public void New()
    {
        Scene = new XScene();
        LoadedEditorState = null;
        ExportPath = null;
        FilePath = null;
        SourceHeader = null;
        SourceFormat = null;
        SourceHasPmxRig = false;
        Warnings = [];
        OriginalText = null;
        ResetHistory();
        Changed?.Invoke(ChangeKind.All);
    }

    public static bool IsModelFile(string path) =>
        IsPmx(path) || IsProject(path) || string.Equals(Path.GetExtension(path), ".x", StringComparison.OrdinalIgnoreCase);

    /// <summary>作業ファイル (.bvex)。</summary>
    public static bool IsProject(string path) => BvexFile.IsBvex(path);

    /// <summary>作業ファイルを開いたときの画面の状態（開いた直後に画面へ反映する。ほかの形式では null）。</summary>
    public System.Text.Json.Nodes.JsonObject? LoadedEditorState { get; private set; }

    /// <summary>作業ファイルに保存する画面の状態を作る（引数は作業ファイルのフォルダー）。画面側が設定する。</summary>
    public Func<string, System.Text.Json.Nodes.JsonObject>? CaptureEditorState { get; set; }

    /// <summary>最後に .x / .pmx を書き出した場所（作業ファイルに保存する）。</summary>
    public string? ExportPath { get; set; }

    /// <summary>
    /// モデル以外（下絵・参考オブジェクトなど、作業ファイルに入るもの）を変えたとき。
    /// 作業ファイルを開いているときだけ「未保存」にする（.x には入らないので、.x では印を付けない）。
    /// </summary>
    public void MarkProjectChanged()
    {
        if (IsDirty || FilePath == null || !IsProject(FilePath)) return;
        IsDirty = true;
        Changed?.Invoke(ChangeKind.None);
    }

    /// <summary>ファイルを読むだけ（今のドキュメントは変えない）。「追加で読み込み」用。</summary>
    public (XScene Scene, IReadOnlyList<string> Warnings, string Format) LoadModel(string path)
    {
        if (IsProject(path))
        {
            var project = BvexFile.Load(path);
            return (project.Scene, project.Warnings, $"作業ファイル v{project.Version}");
        }
        if (IsPmx(path))
        {
            var pmx = PmxReader.Load(path, PmxOptions);
            return (pmx.Scene, pmx.Warnings, pmx.Describe());
        }
        var x = XFile.Load(path);
        return (x.Scene, x.Warnings, x.Header.Describe());
    }

    public void Open(string path)
    {
        LoadedEditorState = null;
        ExportPath = null;
        if (IsProject(path))
        {
            var project = BvexFile.Load(path);
            Scene = project.Scene;
            FilePath = path;
            SourceHeader = null;
            SourceFormat = $"作業ファイル (.bvex v{project.Version})";
            SourceHasPmxRig = false;
            Warnings = project.Warnings;
            OriginalText = null;
            LoadedEditorState = project.Editor;
            ResetHistory();
            Changed?.Invoke(ChangeKind.All);
            return;
        }
        if (IsPmx(path))
        {
            var pmx = PmxReader.Load(path, PmxOptions);
            Scene = pmx.Scene;
            FilePath = path;
            SourceHeader = null;
            SourceFormat = pmx.Describe();
            SourceHasPmxRig = pmx.HasRigData;
            Warnings = pmx.Warnings;
            OriginalText = null;
            ResetHistory();
            Changed?.Invoke(ChangeKind.All);
            return;
        }

        var result = XFile.Load(path);
        Scene = result.Scene;
        FilePath = path;
        SourceHeader = result.Header;
        SourceFormat = result.Header.Describe();
        SourceHasPmxRig = false;
        Warnings = result.Warnings;
        OriginalText = result.DecodedText;
        WriteOptions.Encoding = result.TextEncoding;
        ResetHistory();
        Changed?.Invoke(ChangeKind.All);
    }

    public void Save(string path)
    {
        // 未保存のうちに作った絶対パスのテクスチャを、保存先のフォルダー基準の相対パスにする
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        // 別のフォルダーへ保存し直すときは、相対パスのテクスチャが同じ画像を指すよう書き換える（.x / .pmx / .bvex 共通）
        // （Rebase は絶対パスも保存先からの相対パスにする）
        if (Directory is { } oldDir && dir != null &&
            !string.Equals(Path.GetFullPath(oldDir), dir, StringComparison.OrdinalIgnoreCase))
            TexturePaths.Rebase(Scene.Meshes, oldDir, dir);
        if (dir != null) TexturePaths.MakeRelative(Scene, dir);
        if (IsProject(path))
        {
            BvexFile.Save(path, Scene, CaptureEditorState?.Invoke(dir!));
            SourceHeader = null;
            SourceFormat = $"作業ファイル (.bvex v{BvexFile.CurrentVersion})";
            SourceHasPmxRig = false;
            FilePath = path;
            IsDirty = false;
            OriginalText = null;
            Changed?.Invoke(ChangeKind.File);
            return;
        }
        if (IsPmx(path))
        {
            PmxWriter.Save(path, Scene, PmxOptions, WriteOptions.SmoothAngle);
            SourceHeader = null;
            SourceFormat = "PMX 2.0（静的モデル）";
        }
        else
        {
            XFile.Save(path, Scene, WriteOptions);
            SourceHeader = new XHeader("0303", XEncodingKind.Text, false, 32);
            SourceFormat = SourceHeader.Describe();
        }
        SourceHasPmxRig = false;
        FilePath = path;
        IsDirty = false;
        OriginalText = null;
        Changed?.Invoke(ChangeKind.File);
    }

    /// <summary>
    /// 今のモデルを .x / .pmx に書き出す。開いているファイル（作業ファイルなど）は変えず、未保存の状態も変えない。
    /// テクスチャの相対パスは、書き出し先から同じ画像を指すように直した写しで書く。
    /// </summary>
    public void Export(string path)
    {
        var full = Path.GetFullPath(path);
        var outDir = Path.GetDirectoryName(full)!;
        var copy = Scene.Clone();
        if (Directory is { } dir && !string.Equals(Path.GetFullPath(dir), outDir, StringComparison.OrdinalIgnoreCase))
            TexturePaths.Rebase(copy.Meshes, dir, outDir);
        TexturePaths.MakeRelative(copy, outDir);
        if (IsPmx(full)) PmxWriter.Save(full, copy, PmxOptions, WriteOptions.SmoothAngle);
        else XFile.Save(full, copy, WriteOptions);
        ExportPath = full;
    }

    private void ResetHistory()
    {
        _undo.Clear();
        _redo.Clear();
        _pendingEdit = null;
        IsDirty = false;
        ActiveMesh = 0;
        Selection.SetMode(Selection.Mode);
        Selection.Clear();
    }

    /// <summary>1 回の操作として実行し、元に戻せるようにする。戻り値は操作のメッセージ。</summary>
    public string Execute(string label, Func<XScene, string> action, ChangeKind kind = ChangeKind.Geometry | ChangeKind.Structure)
    {
        var snapshot = Scene.Clone();
        string message;
        try
        {
            message = action(Scene);
        }
        catch
        {
            Scene = snapshot;
            throw;
        }
        Push(label, snapshot);
        AfterChange(kind);
        return message;
    }

    /// <summary>ドラッグなど、何度も書き換えてから 1 回ぶんとして確定する操作の開始。</summary>
    public void BeginEdit(string label)
    {
        _pendingEdit ??= (label, Scene.Clone());
    }

    /// <summary>途中経過の表示更新（履歴には積まない）。</summary>
    public void PreviewEdit(ChangeKind kind) => Changed?.Invoke(kind);

    public void EndEdit(ChangeKind kind)
    {
        if (_pendingEdit is not { } p) return;
        _pendingEdit = null;
        Push(p.Label, p.Scene);
        AfterChange(kind);
    }

    /// <summary>何も変えずに終わった編集を、履歴に積まずに捨てる（シーンはそのまま）。</summary>
    public void DropEdit() => _pendingEdit = null;

    /// <summary>編集開始時のシーン（ドラッグ中に元の形から計算し直すため）。</summary>
    public XScene? EditStartScene => _pendingEdit?.Scene;

    public void CancelEdit()
    {
        if (_pendingEdit is not { } p) return;
        _pendingEdit = null;
        Scene = p.Scene;
        AfterChange(ChangeKind.All);
    }

    /// <summary>テキストエディタから反映したシーンに置き換える。</summary>
    public void ReplaceScene(string label, XScene scene, IReadOnlyList<string> warnings)
    {
        Push(label, Scene);
        Scene = scene;
        Warnings = warnings;
        AfterChange(ChangeKind.All & ~ChangeKind.File);
    }

    private void Push(string label, XScene snapshot)
    {
        _undo.Add((label, snapshot));
        if (_undo.Count > MaxHistory) _undo.RemoveAt(0);
        _redo.Clear();
        IsDirty = true;
    }

    private void AfterChange(ChangeKind kind)
    {
        Selection.Validate(Scene);
        if (ActiveMesh >= Scene.Meshes.Count) ActiveMesh = Math.Max(0, Scene.Meshes.Count - 1);
        Changed?.Invoke(kind);
    }

    public string? Undo()
    {
        if (_undo.Count == 0) return null;
        var (label, scene) = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add((label, Scene));
        Scene = scene;
        IsDirty = true;
        AfterChange(ChangeKind.All & ~ChangeKind.File);
        return label;
    }

    public string? Redo()
    {
        if (_redo.Count == 0) return null;
        var (label, scene) = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add((label, Scene));
        Scene = scene;
        IsDirty = true;
        AfterChange(ChangeKind.All & ~ChangeKind.File);
        return label;
    }

    /// <summary>テクスチャのフルパス（.x の場所から相対）。</summary>
    public string? ResolveTexture(string? texture)
    {
        if (string.IsNullOrWhiteSpace(texture)) return null;
        var rel = texture.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(rel)) return rel;
        var dir = Directory ?? Environment.CurrentDirectory;
        return Path.GetFullPath(Path.Combine(dir, rel));
    }
}
