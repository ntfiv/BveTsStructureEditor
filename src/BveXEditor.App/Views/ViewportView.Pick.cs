using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using BveXEditor.App.Rendering;
using BveXEditor.Core.Editing;
using HelixToolkit.Wpf;

namespace BveXEditor.App.Views;

/// <summary>3D ビューで点を置いていく操作の設定。</summary>
public sealed class PointPickOptions
{
    /// <summary>点を置く面（この点を通り、<see cref="Normal"/> に垂直）。</summary>
    public Vector3 PlanePoint { get; init; }
    public Vector3 Normal { get; init; } = Vector3.UnitY;
    /// <summary>この数に達したら自動で確定。0 なら Enter / ダブルクリックで確定。</summary>
    public int MaxPoints { get; init; }
    /// <summary>確定に必要な最小の点数。</summary>
    public int MinPoints { get; init; } = 1;
    /// <summary>Shift を押さなくても頂点に吸着する（計測用）。</summary>
    public bool AlwaysSnapToVertices { get; init; }
    /// <summary>頂点に吸着した点を面の上に投影する（作図用）。false なら頂点の位置そのまま（計測用）。</summary>
    public bool ProjectSnapsToPlane { get; init; } = true;
    /// <summary>描いている線を閉じた多角形として見せる。</summary>
    public bool Closed { get; init; }
    /// <summary>ステータスバーに出す、次にすることの説明。</summary>
    public string Prompt { get; init; } = "";
}

/// <summary>
/// 点を置いていくモード（下絵の縮尺合わせ・計測・断面や建物の作図）と、頂点への吸着。
/// クリック: 点を置く / Shift: 頂点に吸着 / Ctrl: グリッドに吸着 / Backspace: 1 点戻す / Enter・ダブルクリック: 確定 / Esc: 取り消し
/// </summary>
public partial class ViewportView
{
    private sealed class PointPick(PointPickOptions options, Action<IReadOnlyList<Vector3>> done)
    {
        public PointPickOptions Options { get; } = options;
        public List<Vector3> Points { get; } = [];
        public Action<IReadOnlyList<Vector3>> Done { get; } = done;
    }

    /// <summary>吸着する距離（画面上の px）。</summary>
    public const double SnapPixels = 12;

    private PointPick? _pointPick;
    private Point? _pickMouse;
    private Polyline? _pickLine;
    private Ellipse? _snapMarker;
    private Polyline? _measureLine;
    private TextBlock? _measureLabel;
    private (Vector3 A, Vector3 B)? _measurement;

    public bool IsPickingPoints => _pointPick != null;

    /// <summary>次の左クリック <paramref name="count"/> 回ぶん、面の上の点を拾う（下絵の縮尺合わせ用）。</summary>
    public void StartPointPick(Vector3 planePoint, Vector3 normal, int count, Action<IReadOnlyList<Vector3>> done) =>
        StartPointPick(new PointPickOptions { PlanePoint = planePoint, Normal = normal, MaxPoints = count, MinPoints = count }, done);

    public void StartPointPick(PointPickOptions options, Action<IReadOnlyList<Vector3>> done)
    {
        if (IsGizmoDragging) CancelGizmoDrag();
        ClearMeasurement();
        _pointPick = new PointPick(options, done);
        View.Cursor = Cursors.Cross;
        Focus();
        UpdateGizmo();
        UpdatePickOverlay();
        if (options.Prompt.Length > 0) StatusRequested?.Invoke(options.Prompt);
    }

    public void CancelPointPick()
    {
        if (_pointPick == null) return;
        EndPointPick();
        StatusRequested?.Invoke("点の指定を取り消しました");
    }

    /// <summary>Enter / ダブルクリック。点数が足りていれば確定する。</summary>
    public void FinishPointPick()
    {
        if (_pointPick is not { } pick) return;
        if (pick.Points.Count < pick.Options.MinPoints)
        {
            StatusRequested?.Invoke($"点が足りません（あと {pick.Options.MinPoints - pick.Points.Count} 点）。Esc で取り消し");
            return;
        }
        EndPointPick();
        pick.Done(pick.Points);
    }

    /// <summary>Backspace。最後に置いた点を消す。</summary>
    public void UndoPickPoint()
    {
        if (_pointPick is not { Points.Count: > 0 } pick) return;
        pick.Points.RemoveAt(pick.Points.Count - 1);
        StatusRequested?.Invoke($"1 点戻しました（{pick.Points.Count} 点）");
        UpdateGizmo();
        UpdatePickOverlay();
    }

    private void EndPointPick()
    {
        _pointPick = null;
        _pickMouse = null;
        View.Cursor = null;
        UpdateGizmo();
        UpdatePickOverlay();
    }

    /// <summary>拾い済みの点（画面に目印を出すため）。</summary>
    private IReadOnlyList<Vector3> PickedPoints => _pointPick?.Points ?? (IReadOnlyList<Vector3>)[];

    private bool HandlePointPick(Point p, int clickCount)
    {
        if (_pointPick is not { } pick) return false;
        if (clickCount >= 2 && pick.Options.MaxPoints == 0)
        {
            FinishPointPick();
            return true;
        }
        if (PickPosition(pick.Options, p, out _) is not { } hit)
        {
            StatusRequested?.Invoke("その位置では点を置く面に当たりません。面が見える向きでクリックしてください");
            return true;
        }
        pick.Points.Add(hit);
        if (pick.Options.MaxPoints > 0 && pick.Points.Count >= pick.Options.MaxPoints)
        {
            EndPointPick();
            pick.Done(pick.Points);
            return true;
        }
        string next = pick.Options.MaxPoints > 0
            ? "次の点をクリック"
            : "次の点をクリック（Enter かダブルクリックで確定、Backspace で 1 点戻す）";
        StatusRequested?.Invoke($"{pick.Points.Count} 点目（{hit.X:0.###}, {hit.Y:0.###}, {hit.Z:0.###}）。{next}  Shift: 頂点に吸着 / Ctrl: グリッド / Esc: 取り消し");
        UpdateGizmo();
        UpdatePickOverlay();
        return true;
    }

    /// <summary>画面の点から、置く点の位置を決める。頂点に吸着したら <paramref name="snapped"/> が true。</summary>
    private Vector3? PickPosition(PointPickOptions o, Point p, out bool snapped)
    {
        snapped = false;
        var mods = Keyboard.Modifiers;
        if ((o.AlwaysSnapToVertices || mods.HasFlag(ModifierKeys.Shift)) && FindSnapVertex(p, null) is { } v)
        {
            snapped = true;
            return o.ProjectSnapsToPlane ? v - o.Normal * Vector3.Dot(v - o.PlanePoint, o.Normal) : v;
        }
        if (!TryRay(p, out var origin, out var dir) || GizmoMath.RayPlane(o.PlanePoint, o.Normal, origin, dir) is not { } hit)
            return null;
        if (mods.HasFlag(ModifierKeys.Control))
        {
            hit = GizmoMath.Snap(hit, MoveSnapStep());
            hit -= o.Normal * Vector3.Dot(hit - o.PlanePoint, o.Normal);
        }
        return hit;
    }

    /// <summary>
    /// 画面の点から <see cref="SnapPixels"/> 以内で一番近い、表示中の頂点の位置。
    /// <paramref name="exclude"/> が true を返す頂点は候補にしない（ギズモで動かしている頂点など）。
    /// </summary>
    private Vector3? FindSnapVertex(Point p, Func<ElementRef, bool>? exclude)
    {
        if (_doc == null) return null;
        ElementRef? best = null;
        double bestDist = SnapPixels, bestDepth = double.MaxValue;
        ForEachProjectedVertex((r, sp, depth) =>
        {
            if (exclude?.Invoke(r) == true) return;
            double d = (sp - p).Length;
            if (d > SnapPixels) return;
            // ほぼ同じ距離ならカメラに近いほう
            if (d < bestDist - 2 || (Math.Abs(d - bestDist) <= 2 && depth < bestDepth))
            {
                best = r;
                bestDist = d;
                bestDepth = depth;
            }
        });
        return best is { } b ? _doc.Scene.Meshes[b.Mesh].Positions[b.Index] : null;
    }

    // ───────── オーバーレイ（描いている線・吸着の目印・計測結果） ─────────

    private void EnsureOverlayShapes()
    {
        if (_pickLine != null) return;
        _pickLine = new Polyline { Stroke = new SolidColorBrush(Color.FromRgb(60, 220, 255)), StrokeThickness = 1.5, IsHitTestVisible = false };
        _snapMarker = new Ellipse { Width = 12, Height = 12, Stroke = Brushes.Yellow, StrokeThickness = 2, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _measureLine = new Polyline { Stroke = Brushes.Yellow, StrokeThickness = 1.5, StrokeDashArray = [4, 2], IsHitTestVisible = false };
        _measureLabel = new TextBlock
        {
            Foreground = Brushes.Black,
            Background = new SolidColorBrush(Color.FromArgb(230, 255, 230, 120)),
            Padding = new Thickness(4, 1, 4, 1),
            FontSize = 12,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        Overlay.Children.Add(_pickLine);
        Overlay.Children.Add(_measureLine);
        Overlay.Children.Add(_snapMarker);
        Overlay.Children.Add(_measureLabel);
    }

    private Point? Screen(Vector3 p)
    {
        var m = Viewport3DHelper.GetTotalTransform(View.Viewport);
        return Project(m, SceneRenderer.ToWpf(p), out var s, out _) ? s : null;
    }

    private void UpdatePickOverlay()
    {
        EnsureOverlayShapes();
        _pickLine!.Points.Clear();
        _snapMarker!.Visibility = Visibility.Collapsed;
        if (_pointPick is { } pick)
        {
            foreach (var p in pick.Points)
                if (Screen(p) is { } s) _pickLine.Points.Add(s);
            if (_pickMouse is { } mouse && PickPosition(pick.Options, mouse, out bool snapped) is { } cur && Screen(cur) is { } cs)
            {
                if (pick.Points.Count > 0) _pickLine.Points.Add(cs);
                if (snapped)
                {
                    Canvas.SetLeft(_snapMarker, cs.X - 6);
                    Canvas.SetTop(_snapMarker, cs.Y - 6);
                    _snapMarker.Visibility = Visibility.Visible;
                }
            }
            if (pick.Options.Closed && _pickLine.Points.Count >= 3) _pickLine.Points.Add(_pickLine.Points[0]);
        }

        _measureLine!.Points.Clear();
        _measureLabel!.Visibility = Visibility.Collapsed;
        if (_measurement is { } mm && Screen(mm.A) is { } a && Screen(mm.B) is { } b)
        {
            _measureLine.Points.Add(a);
            _measureLine.Points.Add(b);
            Canvas.SetLeft(_measureLabel, (a.X + b.X) / 2 + 8);
            Canvas.SetTop(_measureLabel, (a.Y + b.Y) / 2 - 10);
            _measureLabel.Visibility = Visibility.Visible;
        }
    }

    /// <summary>計測した 2 点と結果の吹き出しを出す。次にビューをクリックすると消える。</summary>
    public void ShowMeasurement(Vector3 a, Vector3 b, string label)
    {
        EnsureOverlayShapes();
        _measurement = (a, b);
        _measureLabel!.Text = label;
        UpdatePickOverlay();
    }

    public void ClearMeasurement()
    {
        if (_measurement == null) return;
        _measurement = null;
        UpdatePickOverlay();
    }

    /// <summary>計測用に、今の視点で点を置きやすい面（見下ろしていれば地面、横からなら視線に垂直な縦の面）。</summary>
    public (Vector3 Point, Vector3 Normal) MeasurePlane()
    {
        var (_, _, forward) = CameraAxes();
        if (MathF.Abs(forward.Y) > 0.35f) return (Vector3.Zero, Vector3.UnitY);
        return MathF.Abs(forward.X) > MathF.Abs(forward.Z) ? (Vector3.Zero, Vector3.UnitX) : (Vector3.Zero, Vector3.UnitZ);
    }
}
