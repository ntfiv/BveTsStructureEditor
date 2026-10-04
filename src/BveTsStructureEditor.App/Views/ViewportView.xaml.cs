using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using BveTsStructureEditor.App.Document;
using BveTsStructureEditor.App.Rendering;
using BveTsStructureEditor.Core.Editing;
using HelixToolkit.Wpf;

namespace BveTsStructureEditor.App.Views;

public partial class ViewportView : UserControl
{
    private EditorDocument? _doc;
    private SceneRenderer? _renderer;
    private ViewOptions? _options;
    private Point _down;
    private bool _leftDown;
    private bool _dragging;

    public ViewportView()
    {
        InitializeComponent();
        Grid.Fill = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));

        // Viewport3D は何か描かれている所でしかマウスを受け取らないので、背景を持つ親 (HelixViewport3D) で受ける。
        // こうしないと、何もない所から始めた範囲選択や、空白クリックでの選択解除が効かない。
        View.PreviewMouseLeftButtonDown += OnLeftDown;
        View.PreviewMouseMove += OnMove;
        View.PreviewMouseLeftButtonUp += OnLeftUp;
        View.PreviewMouseDown += (_, _) => Focus();
        InitGizmo();
    }

    public void Attach(EditorDocument doc, SceneRenderer renderer, ViewOptions options)
    {
        _doc = doc;
        _renderer = renderer;
        _options = options;
        View.Children.Add(renderer.Root);
        doc.Changed += _ =>
        {
            // 形か選択が変わったら、連動する頂点と寸法を探し直す
            _markerPlan = null;
            _dimensions = null;
        };
    }

    /// <summary>下絵の層を足す。モデルより後に描くので、半透明がモデルの上にも正しく重なる。</summary>
    public void AttachReferences(Visual3D visual) => View.Children.Add(visual);

    /// <summary>線路・限界のガイドの層を足す。</summary>
    public void AttachGuides(Visual3D visual) => View.Children.Add(visual);

    /// <summary>ガイドの凡例（空なら隠す）。</summary>
    public void SetGuideLegend(string text)
    {
        GuideLegend.Text = text;
        GuideLegend.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>3D を描いている要素（マウス座標の基準）。</summary>
    public IInputElement ViewportElement => View.Viewport;

    public bool ShowGrid
    {
        get => View.Children.Contains(Grid);
        set
        {
            if (value && !View.Children.Contains(Grid)) View.Children.Add(Grid);
            if (!value) View.Children.Remove(Grid);
            UpdateOriginMarker();
        }
    }

    /// <summary>モデルの外接箱に合わせる（グリッドは含めない）。</summary>
    public void FitAll()
    {
        if (_doc?.Scene.Bounds() is not { } b)
        {
            View.ZoomExtents(0);
            return;
        }
        var a = SceneRenderer.ToWpf(b.Min);
        var c = SceneRenderer.ToWpf(b.Max);
        var min = new Point3D(Math.Min(a.X, c.X), Math.Min(a.Y, c.Y), Math.Min(a.Z, c.Z));
        var size = new Size3D(Math.Max(Math.Abs(c.X - a.X), 0.1), Math.Max(Math.Abs(c.Y - a.Y), 0.1), Math.Max(Math.Abs(c.Z - a.Z), 0.1));
        View.ZoomExtents(new Rect3D(min, size), 0);
    }

    // ───────── 投影 ─────────

    /// <summary>平行投影にしたときの、注視点からカメラまでの距離 (m)。手前の物が切れないよう十分遠くに置く。</summary>
    private const double OrthoDistance = 5000;

    private bool _adjustingCamera;
    private bool? _lastOrtho;

    /// <summary>平行投影か透視投影かが変わった（引数は平行投影なら true）。</summary>
    public event Action<bool>? ProjectionChanged;

    /// <summary>平行投影（遠近感なし）で表示するか。見ている範囲と注視点はそのまま引き継ぐ。</summary>
    public bool Orthographic
    {
        get => View.Camera is OrthographicCamera;
        set
        {
            if (value == Orthographic || View.Camera is not ProjectionCamera old) return;
            var look = old.LookDirection;
            double dist = look.Length;
            if (dist < 1e-9) return;
            var target = old.Position + look;
            look /= dist;
            const double fov = 45;
            double tan = Math.Tan(fov * Math.PI / 360);

            ProjectionCamera cam;
            if (value)
            {
                // 注視点の位置で、透視投影と同じ幅が見えるようにする
                double width = old is PerspectiveCamera pc ? 2 * dist * Math.Tan(pc.FieldOfView * Math.PI / 360) : 20;
                cam = new OrthographicCamera { Width = width, Position = target - look * OrthoDistance, LookDirection = look * OrthoDistance };
            }
            else
            {
                double width = old is OrthographicCamera oc ? oc.Width : 20;
                double d = Math.Max(width / (2 * tan), 0.5);
                cam = new PerspectiveCamera { FieldOfView = fov, Position = target - look * d, LookDirection = look * d };
            }
            cam.UpDirection = old.UpDirection;
            cam.NearPlaneDistance = value ? 1 : 0.05;
            _adjustingCamera = true;
            try
            {
                View.Camera = cam;
            }
            finally
            {
                _adjustingCamera = false;
            }
            OnCameraChangedForProjection();
            UpdateGizmo();
            UpdateShapePreview();
        }
    }

    /// <summary>
    /// カメラが変わるたびに呼ぶ。平行投影では、全体表示やビューキューブでカメラが注視点に近づくことがあり、
    /// そのままだと手前にある物が切れるので、注視点を保ったまま遠ざける。
    /// </summary>
    private void OnCameraChangedForProjection()
    {
        bool ortho = Orthographic;
        if (_lastOrtho != ortho)
        {
            bool first = _lastOrtho == null; // 起動直後は知らせない
            _lastOrtho = ortho;
            GizmoLayer.Camera = View.Camera;
            if (!first) ProjectionChanged?.Invoke(ortho);
        }
        if (_adjustingCamera || View.Camera is not OrthographicCamera cam) return;
        var look = cam.LookDirection;
        double len = look.Length;
        if (len < 1e-9 || len >= OrthoDistance * 0.5) return;
        _adjustingCamera = true;
        try
        {
            var target = cam.Position + look;
            look *= OrthoDistance / len;
            cam.Position = target - look;
            cam.LookDirection = look;
        }
        finally
        {
            _adjustingCamera = false;
        }
    }

    /// <summary>今のカメラ（作業ファイルに保存する）。</summary>
    public CameraState GetCamera()
    {
        var cam = (ProjectionCamera)View.Camera!;
        static double[] A(Vector3D v) => [v.X, v.Y, v.Z];
        return new CameraState([cam.Position.X, cam.Position.Y, cam.Position.Z], A(cam.LookDirection), A(cam.UpDirection),
            cam is OrthographicCamera, (cam as OrthographicCamera)?.Width ?? 0, (cam as PerspectiveCamera)?.FieldOfView ?? 45);
    }

    /// <summary>保存しておいたカメラに戻す。</summary>
    public void SetCamera(CameraState state)
    {
        if (state.Position is not { Length: 3 } p || state.Look is not { Length: 3 } l || state.Up is not { Length: 3 } u) return;
        Orthographic = state.Orthographic;
        var cam = (ProjectionCamera)View.Camera!;
        cam.Position = new Point3D(p[0], p[1], p[2]);
        cam.LookDirection = new Vector3D(l[0], l[1], l[2]);
        cam.UpDirection = new Vector3D(u[0], u[1], u[2]);
        if (cam is OrthographicCamera oc && state.Width > 0) oc.Width = state.Width;
        if (cam is PerspectiveCamera pc && state.FieldOfView > 0) pc.FieldOfView = state.FieldOfView;
        UpdateGizmo();
    }

    public enum Preset { Front, Back, Right, Left, Top, Perspective }

    public void SetView(Preset preset)
    {
        // WPF 座標（Z 反転後）でのカメラ向き。BVE の +Z（進行方向）は WPF の -Z。
        (Vector3D look, Vector3D up) = preset switch
        {
            Preset.Front => (new Vector3D(0, 0, -1), new Vector3D(0, 1, 0)),
            Preset.Back => (new Vector3D(0, 0, 1), new Vector3D(0, 1, 0)),
            Preset.Right => (new Vector3D(-1, 0, 0), new Vector3D(0, 1, 0)),
            Preset.Left => (new Vector3D(1, 0, 0), new Vector3D(0, 1, 0)),
            Preset.Top => (new Vector3D(0, -1, 0), new Vector3D(0, 0, -1)),
            _ => (new Vector3D(-0.6, -0.45, -1), new Vector3D(0, 1, 0)),
        };
        View.SetView(new Point3D(0, 0, 0) - look * 20, look * 20, up, 0);
        FitAll();
    }

    // ───────── 選択 ─────────

    /// <summary>
    /// ビューキューブや座標軸の表示など、Helix が別の Viewport3D で描いている部品の上か。
    /// そこでのクリックは Helix の操作（視点の切り替え）に任せる。
    /// </summary>
    private bool IsOverHelixWidget(MouseEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null && d != View; d = VisualTreeHelper.GetParent(d))
            if (d is Viewport3D vp && vp != View.Viewport) return true;
        return false;
    }

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (_doc == null || IsOverHelixWidget(e)) return;
        _down = e.GetPosition(View.Viewport);
        ClearMeasurement();
        if (HandlePointPick(_down, e.ClickCount))
        {
            e.Handled = true;
            return;
        }
        if (TryBeginGizmoDrag(_down))
        {
            View.Viewport.CaptureMouse();
            e.Handled = true;
            return;
        }
        _leftDown = true;
        _dragging = false;
        View.Viewport.CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(View.Viewport);
        if (IsGizmoDragging)
        {
            UpdateGizmoDrag(p);
            return;
        }
        if (_pointPick != null)
        {
            _pickMouse = p;
            UpdatePickOverlay();
            return;
        }
        if (!_leftDown)
        {
            UpdateGizmoHover(p);
            return;
        }
        if (!_dragging && (p - _down).Length > 4) _dragging = true;
        if (_dragging)
        {
            var r = new Rect(_down, p);
            Canvas.SetLeft(RubberBand, r.X);
            Canvas.SetTop(RubberBand, r.Y);
            RubberBand.Width = r.Width;
            RubberBand.Height = r.Height;
            RubberBand.Visibility = Visibility.Visible;
        }
    }

    private void OnLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (IsGizmoDragging)
        {
            View.Viewport.ReleaseMouseCapture();
            EndGizmoDrag();
            e.Handled = true;
            return;
        }
        if (!_leftDown || _doc == null) return;
        _leftDown = false;
        View.Viewport.ReleaseMouseCapture();
        RubberBand.Visibility = Visibility.Collapsed;
        var p = e.GetPosition(View.Viewport);
        var hits = _dragging ? RectPick(new Rect(_down, p)) : ClickPick(p);
        Apply(hits);
        e.Handled = true;
    }

    private void Apply(List<ElementRef> hits)
    {
        var sel = _doc!.Selection;
        // 選択の変更通知より前に作業中メッシュを切り替える（パネルが新しいメッシュで描き直されるように）
        if (hits.Count > 0) _doc.ActiveMesh = hits[^1].Mesh;
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control))
        {
            foreach (var h in hits) sel.Toggle(h);
        }
        else if (mods.HasFlag(ModifierKeys.Shift)) sel.Add(hits);
        else sel.Set(hits);
    }

    private List<ElementRef> ClickPick(Point p)
    {
        var sel = _doc!.Selection;
        if (sel.Mode == SelectMode.Vertex)
        {
            // 12px 以内で一番近い点。重なっている点はカメラに近いほうを取る
            var near = new List<(double Dist, double Depth, ElementRef Ref)>();
            ForEachProjectedVertex((r, sp, depth) =>
            {
                var d = (sp - p).Length;
                if (d <= 12) near.Add((d, depth, r));
            });
            if (near.Count == 0) return [];
            var minDist = near.Min(n => n.Dist);
            return [near.Where(n => n.Dist <= minDist + 3).MinBy(n => n.Depth).Ref];
        }

        var face = RayPickFace(p);
        if (face is not { } f) return [];
        return sel.Mode == SelectMode.Face ? [f] : [new ElementRef(f.Mesh, -1)];
    }

    private ElementRef? RayPickFace(Point p)
    {
        ElementRef? result = null;
        double nearest = double.MaxValue;
        VisualTreeHelper.HitTest(View.Viewport, null, hr =>
        {
            if (hr is RayMeshGeometry3DHitTestResult rh && rh.ModelHit is GeometryModel3D gm &&
                _renderer!.Picks.TryGetValue(gm, out var info) && rh.DistanceToRayOrigin < nearest)
            {
                int tri = rh.VertexIndex1 / 3;
                if (tri < info.TriangleToFace.Length)
                {
                    nearest = rh.DistanceToRayOrigin;
                    result = new ElementRef(info.Mesh, info.TriangleToFace[tri]);
                }
            }
            return HitTestResultBehavior.Continue;
        }, new PointHitTestParameters(p));
        return result;
    }

    private List<ElementRef> RectPick(Rect rect)
    {
        var scene = _doc!.Scene;
        var mode = _doc.Selection.Mode;
        var result = new List<ElementRef>();
        if (mode == SelectMode.Vertex)
        {
            ForEachProjectedVertex((r, sp, _) => { if (rect.Contains(sp)) result.Add(r); });
            return result;
        }

        var m = Viewport3DHelper.GetTotalTransform(View.Viewport);
        for (int mi = 0; mi < scene.Meshes.Count; mi++)
        {
            if (_options!.HiddenMeshes.Contains(mi)) continue;
            var mesh = scene.Meshes[mi];
            if (mode == SelectMode.Object)
            {
                // 隠しているマテリアルの面だけが範囲に入っても選ばない
                var visible = _options.VisibleVertices(mi, mesh);
                if (Enumerable.Range(0, mesh.Positions.Count).Any(v => (visible == null || visible.Contains(v)) &&
                        Project(m, SceneRenderer.ToWpf(mesh.Positions[v]), out var sp, out _) && rect.Contains(sp)))
                    result.Add(new ElementRef(mi, -1));
                continue;
            }
            foreach (var fi in _options.VisibleFaces(mi, mesh))
                if (Project(m, SceneRenderer.ToWpf(mesh.FaceCenter(mesh.Faces[fi])), out var sp, out _) && rect.Contains(sp))
                    result.Add(new ElementRef(mi, fi));
        }
        return result;
    }

    private void ForEachProjectedVertex(Action<ElementRef, Point, double> action)
    {
        var scene = _doc!.Scene;
        var m = Viewport3DHelper.GetTotalTransform(View.Viewport);
        for (int mi = 0; mi < scene.Meshes.Count; mi++)
        {
            if (_options!.HiddenMeshes.Contains(mi)) continue;
            var visible = _options.VisibleVertices(mi, scene.Meshes[mi]);
            var pos = scene.Meshes[mi].Positions;
            for (int vi = 0; vi < pos.Count; vi++)
                if ((visible == null || visible.Contains(vi)) && Project(m, SceneRenderer.ToWpf(pos[vi]), out var sp, out var depth))
                    action(new ElementRef(mi, vi), sp, depth);
        }
    }

    private static bool Project(Matrix3D m, Point3D p, out Point screen, out double depth)
    {
        var p4 = m.Transform(new Point4D(p.X, p.Y, p.Z, 1));
        if (p4.W <= 1e-9)
        {
            screen = default;
            depth = 0;
            return false;
        }
        screen = new Point(p4.X / p4.W, p4.Y / p4.W);
        depth = p4.Z / p4.W;
        return true;
    }

    /// <summary>カメラの右・上方向（BVE 座標）。矢印キーでの移動を画面基準にしたいとき用。</summary>
    public (System.Numerics.Vector3 Right, System.Numerics.Vector3 Up, System.Numerics.Vector3 Forward) CameraAxes()
    {
        var cam = (ProjectionCamera)View.Camera!;
        var look = cam.LookDirection;
        look.Normalize();
        var up = cam.UpDirection;
        var right = Vector3D.CrossProduct(look, up);
        right.Normalize();
        up = Vector3D.CrossProduct(right, look);
        static System.Numerics.Vector3 ToBve(Vector3D v) => new((float)v.X, (float)v.Y, (float)-v.Z);
        return (ToBve(right), ToBve(up), ToBve(look));
    }
}
