using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using BveTsStructureEditor.App.Document;
using BveTsStructureEditor.App.Rendering;
using BveTsStructureEditor.Core.Editing;
using HelixToolkit.Wpf;

namespace BveTsStructureEditor.App.Views;

/// <summary>
/// 3D ビュー上の移動・回転ギズモ。
///
/// ドラッグ中は毎回「開始時の形に戻してから、開始点からの合計の変形を掛け直す」。
/// 少しずつ足していくと誤差や法線の変形が積み重なるため。確定は 1 回の「元に戻す」単位。
/// </summary>
public partial class ViewportView
{
    private GizmoMode _gizmoMode = GizmoMode.Move;
    private GizmoHandle _hover = GizmoHandle.None;

    // ドラッグ中の状態
    private GizmoHandle _active = GizmoHandle.None;
    private Vector3 _pivot0;
    private float _size0;
    private Point _gizmoDown;
    private float _axisT0;
    private Vector3 _planeHit0;
    private Vector3 _planeNormal;
    private float _angle;
    private double _prevScreenAngle;
    private float _rotSign = 1;
    private bool _rotByHorizontal;
    private Vector3 _currentOffset;
    private Vector3 _currentStretch = Vector3.One;
    private float _scaleStart;
    private bool _gizmoMoved;
    /// <summary>左右対称モードでドラッグ中に一緒に動かす頂点。開始時の形から 1 回だけ作る。</summary>
    private MirrorPlan? _mirrorPlan;
    /// <summary>連動する頂点の目印用（形か選択が変わったら捨てる）。</summary>
    private MirrorPlan? _markerPlan;
    /// <summary>頂点スナップ: ドラッグ中の頂点（吸着先の候補から外す）と、吸着させる基準の頂点（開始時の位置）。</summary>
    private HashSet<ElementRef>? _dragVertices;
    private Vector3? _snapBase;
    private Vector3 _grabPoint;

    /// <summary>ステータスバーに出したい文言。</summary>
    public event Action<string>? StatusRequested;

    /// <summary>Ctrl を押しながら移動したときの刻み (m)。</summary>
    public Func<float> MoveSnapStep { get; set; } = () => 0.1f;

    public const float RotateSnapDegrees = 15f;

    public bool IsGizmoDragging => _active != GizmoHandle.None;

    public GizmoMode GizmoMode
    {
        get => _gizmoMode;
        set
        {
            if (IsGizmoDragging) CancelGizmoDrag();
            _gizmoMode = value;
            _hover = GizmoHandle.None;
            UpdateGizmo();
        }
    }

    private void InitGizmo()
    {
        GizmoLayer.Camera = View.Camera;
        View.CameraChanged += (_, _) =>
        {
            // 平行投影との切り替えでカメラが差し替わることがある
            if (!ReferenceEquals(GizmoLayer.Camera, View.Camera)) GizmoLayer.Camera = View.Camera;
            OnCameraChangedForProjection();
            UpdateGizmo();
            UpdateShapePreview();
        };
        SizeChanged += (_, _) =>
        {
            UpdateGizmo();
            UpdateShapePreview();
        };
        // Alt+Tab などでマウスを奪われたら、そこまでの操作で確定する
        View.Viewport.LostMouseCapture += (_, _) => { if (IsGizmoDragging) EndGizmoDrag(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && IsGizmoDragging)
            {
                CancelGizmoDrag();
                e.Handled = true;
            }
        };
    }

    private bool GizmoVisible => _doc != null && _gizmoMode != GizmoMode.None && !_doc.Selection.IsEmpty;

    /// <summary>今のギズモの位置と大きさ。ドラッグ中は開始時の位置（移動ならずらした位置）。</summary>
    private (Vector3 Pivot, float Size) GizmoPlacement()
    {
        if (IsGizmoDragging) return (_pivot0 + _currentOffset, _size0);
        var pivot = MeshOps.Center(_doc!.Scene, _doc.Selection);
        return (pivot, SizeAt(pivot));
    }

    /// <summary>画面上で <see cref="TransformGizmo.ScreenSize"/> px になる長さ (m)。</summary>
    private float SizeAt(Vector3 pivot)
    {
        var (right, _, _) = CameraAxes();
        var m = Viewport3DHelper.GetTotalTransform(View.Viewport);
        if (ProjectBve(m, pivot) is { } a && ProjectBve(m, pivot + right) is { } b)
        {
            var px = (b - a).Length;
            if (px > 1e-6) return (float)(TransformGizmo.ScreenSize / px);
        }
        return 1f;
    }

    private static Point? ProjectBve(Matrix3D m, Vector3 p) =>
        Project(m, SceneRenderer.ToWpf(p), out var s, out _) ? s : null;

    public void UpdateGizmo()
    {
        UpdateOriginMarker();
        if (_pointPick != null || _measurement != null) UpdatePickOverlay(); // 視点が変わったら描いている線も追従
        UpdateSelectedVertexMarkers();
        UpdateDimensions();
        if (!GizmoVisible)
        {
            GizmoVisual.Content = null;
            return;
        }
        var (pivot, size) = GizmoPlacement();
        var hot = IsGizmoDragging ? _active : _hover;
        GizmoVisual.Content = TransformGizmo.Build(_gizmoMode, pivot, size, hot,
            IsGizmoDragging && _gizmoMode == GizmoMode.Scale ? _currentStretch : null);
    }

    // ───────── 原点 ─────────

    /// <summary>原点 (0,0,0) を、画面上で一定の大きさの小さな点として描く。グリッドを消すと一緒に消える。</summary>
    private LinesVisual3D? _mirrorLine;

    private void UpdateOriginMarker()
    {
        // 左右対称モードの中心線 (X=0)。モデルに隠れてよいので普通の層に描く
        bool showLine = _doc?.MirrorX == true;
        if (showLine && _mirrorLine == null)
        {
            _mirrorLine = new LinesVisual3D { Color = System.Windows.Media.Color.FromRgb(200, 120, 255), Thickness = 1.5 };
            _mirrorLine.Points.Add(new Point3D(0, 0.002, -50));
            _mirrorLine.Points.Add(new Point3D(0, 0.002, 50));
        }
        if (_mirrorLine != null)
        {
            bool shown = View.Children.Contains(_mirrorLine);
            if (showLine && !shown) View.Children.Add(_mirrorLine);
            if (!showLine && shown) View.Children.Remove(_mirrorLine);
        }

        if (!ShowGrid)
        {
            OriginVisual.Content = null;
            return;
        }
        var (right, up, forward) = CameraAxes();
        var p = Vector3.Zero;
        float px = SizeAt(p) / (float)TransformGizmo.ScreenSize;
        // 選択中の頂点の目印 (20px〜) より奥にして、重なったら頂点の方を見せる
        var toCamera = TowardCamera(p, forward);
        var outline = new MeshGeometry3D();
        var fill = new MeshGeometry3D();
        AddSquare(outline, p + toCamera * px * 8f, right, up, px * 3.5f);
        AddSquare(fill, p + toCamera * px * 10f, right, up, px * 2.5f);
        var group = new Model3DGroup();
        group.Children.Add(Flat(outline, System.Windows.Media.Color.FromRgb(20, 20, 20)));
        group.Children.Add(Flat(fill, System.Windows.Media.Color.FromRgb(255, 255, 255)));
        group.Freeze();
        OriginVisual.Content = group;
    }

    // ───────── 選択中の頂点 ─────────

    /// <summary>
    /// 選択中の頂点を、画面上で一定の大きさの四角として最前面の層に描く。
    /// 通常の点表示は奥行きで判定されるので、面の奥の頂点や、ギズモの中心の箱と重なった頂点が見えなくなる。
    /// </summary>
    private void UpdateSelectedVertexMarkers()
    {
        var selected = new List<Vector3>();
        if (_doc is { Selection.Mode: SelectMode.Vertex } doc && !doc.Selection.IsEmpty && _options != null)
        {
            foreach (var r in doc.Selection.Items)
            {
                if (r.Mesh >= doc.Scene.Meshes.Count || _options.HiddenMeshes.Contains(r.Mesh)) continue;
                var positions = doc.Scene.Meshes[r.Mesh].Positions;
                if (r.Index >= 0 && r.Index < positions.Count) selected.Add(positions[r.Index]);
            }
        }
        var picked = PickedPoints; // 下絵の縮尺合わせで指定済みの点
        var mirrored = new List<Vector3>(); // 左右対称モードで一緒に動く頂点
        if (_doc is { MirrorX: true } md && !md.Selection.IsEmpty && _options != null)
        {
            var plan = _mirrorPlan ?? (_markerPlan ??= Symmetry.Plan(md.Scene, md.Selection));
            foreach (var (m, verts) in plan.Targets)
            {
                if (m >= md.Scene.Meshes.Count || _options.HiddenMeshes.Contains(m)) continue;
                var positions = md.Scene.Meshes[m].Positions;
                foreach (var v in verts)
                    if (v < positions.Count) mirrored.Add(positions[v]);
            }
        }
        if (selected.Count == 0 && picked.Count == 0 && mirrored.Count == 0)
        {
            SelectedVertexVisual.Content = null;
            return;
        }

        var (right, up, forward) = CameraAxes();
        var outline = new MeshGeometry3D();
        var fill = new MeshGeometry3D();
        var pickFill = new MeshGeometry3D();
        var mirrorFill = new MeshGeometry3D();
        void Marker(Vector3 p, MeshGeometry3D target, float scale = 1f)
        {
            // 1px あたりの長さ (m)
            float px = SizeAt(p) / (float)TransformGizmo.ScreenSize;
            // ギズモ中心の箱（半径 約 4.5px）より手前に出す。視線に沿って動かすので画面上の位置は変わらない
            var toCamera = TowardCamera(p, forward);
            AddSquare(outline, p + toCamera * px * 20f, right, up, px * 6f * scale);
            AddSquare(target, p + toCamera * px * 22f, right, up, px * 4.5f * scale);
        }
        foreach (var p in mirrored) Marker(p, mirrorFill, 0.75f);
        foreach (var p in selected) Marker(p, fill);
        foreach (var p in picked) Marker(p, pickFill);

        var group = new Model3DGroup();
        group.Children.Add(Flat(outline, System.Windows.Media.Color.FromRgb(20, 20, 20)));
        group.Children.Add(Flat(fill, System.Windows.Media.Color.FromRgb(255, 150, 30)));
        group.Children.Add(Flat(pickFill, System.Windows.Media.Color.FromRgb(60, 220, 255)));
        group.Children.Add(Flat(mirrorFill, System.Windows.Media.Color.FromRgb(200, 120, 255)));
        group.Freeze();
        SelectedVertexVisual.Content = group;
    }

    /// <summary>点からカメラへ向かう単位ベクトル（BVE 座標）。平行投影なら視線の逆向き。</summary>
    private Vector3 TowardCamera(Vector3 p, Vector3 forward)
    {
        if (View.Camera is PerspectiveCamera pc)
        {
            var c = new Vector3((float)pc.Position.X, (float)pc.Position.Y, (float)-pc.Position.Z);
            var d = c - p;
            if (d.LengthSquared() > 1e-12f) return Vector3.Normalize(d);
        }
        return -forward;
    }

    private static void AddSquare(MeshGeometry3D g, Vector3 c, Vector3 right, Vector3 up, float half)
    {
        var r = right * half;
        var u = up * half;
        TransformGizmo.AddTri(g, c - r - u, c + r - u, c + r + u);
        TransformGizmo.AddTri(g, c - r - u, c + r + u, c - r + u);
    }

    private static GeometryModel3D Flat(MeshGeometry3D geo, System.Windows.Media.Color color)
    {
        geo.Freeze();
        var mat = new System.Windows.Media.Media3D.MaterialGroup();
        mat.Children.Add(new DiffuseMaterial(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Black)));
        mat.Children.Add(new EmissiveMaterial(new System.Windows.Media.SolidColorBrush(color)));
        mat.Freeze();
        return new GeometryModel3D(geo, mat) { BackMaterial = mat };
    }

    // ───────── 「形を追加」のプレビュー ─────────

    private Core.Model.XMesh? _shapePreview;

    /// <summary>追加予定の形をゴースト表示する。null で消す。</summary>
    public void SetShapePreview(Core.Model.XMesh? mesh)
    {
        _shapePreview = mesh;
        UpdateShapePreview();
    }

    private void UpdateShapePreview()
    {
        if (_shapePreview is not { Positions.Count: > 0 } mesh)
        {
            PreviewVisual.Content = null;
            return;
        }
        var min = mesh.Positions.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
        var max = mesh.Positions.Aggregate(new Vector3(float.MinValue), Vector3.Max);
        // 辺は画面上で 1.5px 程度の太さになるように
        float radius = SizeAt((min + max) / 2) / (float)TransformGizmo.ScreenSize * 1.5f;
        PreviewVisual.Content = ShapePreview.Build(mesh, radius);
    }

    private GizmoHandle PickGizmo(Point p)
    {
        if (!GizmoVisible) return GizmoHandle.None;
        var (pivot, size) = GizmoPlacement();
        var m = Viewport3DHelper.GetTotalTransform(View.Viewport);
        return TransformGizmo.Pick(_gizmoMode, pivot, size, p,
            v => Project(m, SceneRenderer.ToWpf(v), out var s, out var depth) ? (s, depth) : null);
    }

    private void UpdateGizmoHover(Point p)
    {
        var h = PickGizmo(p);
        if (h == _hover) return;
        _hover = h;
        UpdateGizmo();
        View.Viewport.Cursor = h == GizmoHandle.None ? null : Cursors.SizeAll;
    }

    // ───────── レイ ─────────

    /// <summary>画面上の点から奥へ向かうレイ（BVE 座標）。</summary>
    private bool TryRay(Point p, out Vector3 origin, out Vector3 dir)
    {
        origin = dir = default;
        double w = View.Viewport.ActualWidth, h = View.Viewport.ActualHeight;
        if (w <= 0 || h <= 0 || View.Camera is not ProjectionCamera cam) return false;

        var look = cam.LookDirection;
        look.Normalize();
        var right = Vector3D.CrossProduct(look, cam.UpDirection);
        right.Normalize();
        var up = Vector3D.CrossProduct(right, look);
        // WPF のカメラは横方向の画角・幅を基準にする
        double sx = (p.X - w / 2) / (w / 2);
        double sy = -(p.Y - h / 2) / (w / 2);

        Point3D o;
        Vector3D d;
        switch (cam)
        {
            case PerspectiveCamera pc:
                double t = Math.Tan(pc.FieldOfView * Math.PI / 360);
                o = pc.Position;
                d = look + right * (sx * t) + up * (sy * t);
                break;
            case OrthographicCamera oc:
                double half = oc.Width / 2;
                d = look;
                o = oc.Position + right * (sx * half) + up * (sy * half) - look * 10000;
                break;
            default:
                return false;
        }
        d.Normalize();
        origin = new Vector3((float)o.X, (float)o.Y, (float)-o.Z);
        dir = new Vector3((float)d.X, (float)d.Y, (float)-d.Z);
        return true;
    }

    /// <summary>画面上の点（このビュー基準）の真下にある地面 (Y=0) の点。地面が見えない向きなら null。</summary>
    public Vector3? GroundPointAt(Point viewPoint)
    {
        if (!TryRay(viewPoint, out var origin, out var dir)) return null;
        return GizmoMath.RayPlane(Vector3.Zero, Vector3.UnitY, origin, dir);
    }

    // ───────── ドラッグ ─────────

    private bool TryBeginGizmoDrag(Point p)
    {
        var handle = PickGizmo(p);
        if (handle == GizmoHandle.None || !TryRay(p, out var ro, out var rd)) return false;

        var (pivot, size) = GizmoPlacement();
        _pivot0 = pivot;
        _size0 = size;
        _gizmoDown = p;
        _currentOffset = Vector3.Zero;
        _angle = 0;
        _gizmoMoved = false;
        _currentStretch = Vector3.One;
        _mirrorPlan = _doc!.MirrorX ? Symmetry.Plan(_doc.Scene, _doc.Selection) : null;
        _dragVertices = null;
        _snapBase = null;

        if (_gizmoMode == GizmoMode.Scale)
        {
            if (!BeginScale(ref handle, ro, rd)) return false;
            _active = handle;
            _doc!.BeginEdit("ギズモで拡大縮小");
            UpdateGizmo();
            return true;
        }

        switch (handle)
        {
            case GizmoHandle.AxisX or GizmoHandle.AxisY or GizmoHandle.AxisZ:
                if (GizmoMath.ClosestOnAxis(pivot, TransformGizmo.AxisOf(handle), ro, rd) is not { } t) return false;
                _axisT0 = t;
                _grabPoint = pivot + TransformGizmo.AxisOf(handle) * t;
                break;

            case GizmoHandle.PlaneYZ or GizmoHandle.PlaneXZ or GizmoHandle.PlaneXY or GizmoHandle.Screen:
                _planeNormal = handle == GizmoHandle.Screen ? CameraAxes().Forward : TransformGizmo.AxisOf(handle);
                var hit = GizmoMath.RayPlane(pivot, _planeNormal, ro, rd);
                if (hit == null && handle != GizmoHandle.Screen)
                {
                    // 平面を真横から見ているときは画面と平行な移動にする
                    handle = GizmoHandle.Screen;
                    _planeNormal = CameraAxes().Forward;
                    hit = GizmoMath.RayPlane(pivot, _planeNormal, ro, rd);
                }
                if (hit is not { } h) return false;
                _planeHit0 = h;
                _grabPoint = handle == GizmoHandle.Screen ? pivot : h;
                break;

            default: // リング
                BeginRotation(handle, p);
                break;
        }

        _active = handle;
        _doc!.BeginEdit(_gizmoMode == GizmoMode.Move ? "ギズモで移動" : "ギズモで回転");
        UpdateGizmo();
        return true;
    }

    /// <summary>
    /// 拡大の基準になる「開始時の中心からの距離」を決める。
    /// 軸: 軸上の位置、平面: 2 軸の対角方向への距離、中心の箱: 左右の動きで全体を拡大するので不要。
    /// </summary>
    private bool BeginScale(ref GizmoHandle handle, Vector3 ro, Vector3 rd)
    {
        switch (handle)
        {
            case GizmoHandle.AxisX or GizmoHandle.AxisY or GizmoHandle.AxisZ:
                if (GizmoMath.ClosestOnAxis(_pivot0, TransformGizmo.AxisOf(handle), ro, rd) is not { } t) return false;
                // ハンドルは中心から離れた所にしかないが、念のため中心付近なら先端を掴んだことにする
                _axisT0 = MathF.Abs(t) < _size0 * 0.1f ? _size0 : t;
                return true;

            case GizmoHandle.PlaneYZ or GizmoHandle.PlaneXZ or GizmoHandle.PlaneXY:
                _planeNormal = TransformGizmo.AxisOf(handle);
                if (GizmoMath.RayPlane(_pivot0, _planeNormal, ro, rd) is { } hit)
                {
                    float s = Vector3.Dot(hit - _pivot0, PlaneDiagonal(handle));
                    _scaleStart = MathF.Abs(s) < _size0 * 0.05f ? _size0 * 0.3f : s;
                    return true;
                }
                // 平面を真横から見ているときは全体の拡大にする
                handle = GizmoHandle.Screen;
                return true;

            default:
                handle = GizmoHandle.Screen;
                return true;
        }
    }

    /// <summary>平面ハンドルの 2 軸の対角方向（例: YZ なら (0,1,1) の正規化）。</summary>
    private static Vector3 PlaneDiagonal(GizmoHandle h) =>
        Vector3.Normalize(Vector3.One - TransformGizmo.AxisOf(h));

    private void BeginRotation(GizmoHandle handle, Point p)
    {
        var m = Viewport3DHelper.GetTotalTransform(View.Viewport);
        var center = ProjectBve(m, _pivot0) ?? p;
        _prevScreenAngle = Math.Atan2(p.Y - center.Y, p.X - center.X);

        // 軸の向きとカメラの関係で、画面上の回り方と実際の回転の符号が変わる。
        // 基準点を少し回して、画面でどちらに回ったかを見て決める（座標系の約束に頼らない）。
        var axis = TransformGizmo.AxisOf(handle);
        var (u, _) = GizmoMath.Perpendiculars(axis);
        var r0 = _pivot0 + u * _size0;
        var r1 = Vector3.Transform(r0, GizmoMath.Rotation(axis, 0.2f, _pivot0));
        _rotByHorizontal = true;
        if (ProjectBve(m, r0) is { } s0 && ProjectBve(m, r1) is { } s1)
        {
            double a0 = Math.Atan2(s0.Y - center.Y, s0.X - center.X);
            double a1 = Math.Atan2(s1.Y - center.Y, s1.X - center.X);
            var da = GizmoMath.WrapAngle((float)(a1 - a0));
            // リングを真横から見ている（画面上で線になる）ときは、左右の動きで回す
            if (MathF.Abs(da) > 0.03f)
            {
                _rotSign = MathF.Sign(da);
                _rotByHorizontal = false;
            }
        }
    }

    private void UpdateGizmoDrag(Point p)
    {
        if (_doc?.EditStartScene is not { } start) return;
        if (!_gizmoMoved && (p - _gizmoDown).Length < 2) return;
        _gizmoMoved = true;
        bool snap = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        Matrix4x4 matrix;
        string status;
        if (_gizmoMode == GizmoMode.Scale)
        {
            const float step = 0.1f;
            float snapStep = snap ? step : 0;
            Vector3 scale;
            string what;
            if (_active is GizmoHandle.AxisX or GizmoHandle.AxisY or GizmoHandle.AxisZ)
            {
                if (!TryRay(p, out var ro, out var rd)) return;
                var axis = TransformGizmo.AxisOf(_active);
                if (GizmoMath.ClosestOnAxis(_pivot0, axis, ro, rd) is not { } t) return;
                float f = GizmoMath.ScaleFactor(t, _axisT0, snapStep);
                scale = Vector3.One + axis * (f - 1);
                what = $"{TransformGizmo.Describe(_active).Replace(" 軸", "")} 方向に {f:0.###} 倍";
            }
            else if (_active is GizmoHandle.PlaneYZ or GizmoHandle.PlaneXZ or GizmoHandle.PlaneXY)
            {
                if (!TryRay(p, out var ro, out var rd)) return;
                if (GizmoMath.RayPlane(_pivot0, _planeNormal, ro, rd) is not { } hit) return;
                float f = GizmoMath.ScaleFactor(Vector3.Dot(hit - _pivot0, PlaneDiagonal(_active)), _scaleStart, snapStep);
                scale = Vector3.One + (Vector3.One - TransformGizmo.AxisOf(_active)) * (f - 1);
                what = $"{TransformGizmo.Describe(_active)}を {f:0.###} 倍";
            }
            else
            {
                // 中心の箱: 右へドラッグで拡大、左で縮小（150px で約 2.7 倍）
                float f = MathF.Exp((float)(p.X - _gizmoDown.X) / 150f);
                if (snap) f = GizmoMath.Snap(f, step);
                f = MathF.Max(f, GizmoMath.MinScale);
                scale = new Vector3(f);
                what = $"全体を {f:0.###} 倍（左右にドラッグ）";
            }
            _currentStretch = scale;
            matrix = GizmoMath.Scaling(scale, _pivot0);
            status = what + (snap ? $"  [スナップ {step} 倍刻み]" : "  Ctrl で 0.1 倍刻み");
        }
        else if (_gizmoMode == GizmoMode.Move)
        {
            if (!TryRay(p, out var ro, out var rd)) return;
            Vector3 offset;
            if (_active is GizmoHandle.AxisX or GizmoHandle.AxisY or GizmoHandle.AxisZ)
            {
                var axis = TransformGizmo.AxisOf(_active);
                if (GizmoMath.ClosestOnAxis(_pivot0, axis, ro, rd) is not { } t) return;
                var d = t - _axisT0;
                if (snap) d = GizmoMath.Snap(d, MoveSnapStep());
                offset = axis * d;
            }
            else
            {
                if (GizmoMath.RayPlane(_pivot0, _planeNormal, ro, rd) is not { } hit) return;
                offset = hit - _planeHit0;
                if (snap) offset = GizmoMath.Snap(offset, MoveSnapStep());
            }
            string snapInfo = snap ? $"  [スナップ {MoveSnapStep():0.###} m]" : "  Ctrl でスナップ / Shift で頂点に吸着";
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && VertexSnapOffset(p, start) is { } snapped)
            {
                offset = snapped;
                snapInfo = "  [頂点に吸着]";
            }
            else HideSnapMarker();
            _currentOffset = offset;
            matrix = Matrix4x4.CreateTranslation(offset);
            status = $"{TransformGizmo.Describe(_active)}に移動 ({offset.X:0.###}, {offset.Y:0.###}, {offset.Z:0.###}) m" + snapInfo;
        }
        else
        {
            if (_rotByHorizontal)
            {
                _angle = (float)((p.X - _gizmoDown.X) * 0.01);
            }
            else
            {
                var m = Viewport3DHelper.GetTotalTransform(View.Viewport);
                var center = ProjectBve(m, _pivot0) ?? _gizmoDown;
                double a = Math.Atan2(p.Y - center.Y, p.X - center.X);
                _angle += GizmoMath.WrapAngle((float)(a - _prevScreenAngle)) * _rotSign;
                _prevScreenAngle = a;
            }
            float deg = _angle * 180f / MathF.PI;
            float applied = snap ? GizmoMath.Snap(deg, RotateSnapDegrees) : deg;
            matrix = GizmoMath.Rotation(TransformGizmo.AxisOf(_active), applied * MathF.PI / 180f, _pivot0);
            status = $"{TransformGizmo.Describe(_active)}に {applied:0.#}° 回転" +
                     (snap ? $"  [スナップ {RotateSnapDegrees:0}°]" : "  Ctrl で 15° 刻み");
        }

        var scene = _doc.Scene;
        var restore = SelectionQuery.Meshes(scene, _doc.Selection, emptyMeansAll: false);
        if (_mirrorPlan != null) restore.UnionWith(_mirrorPlan.Meshes);
        foreach (var mi in restore)
            if (mi < start.Meshes.Count) MeshCopy.RestoreGeometry(scene.Meshes[mi], start.Meshes[mi]);
        if (_mirrorPlan != null)
        {
            Symmetry.Transform(scene, _doc.Selection, matrix, _mirrorPlan);
            status += _mirrorPlan.TargetCount > 0 ? $"  [左右対称: 反対側 {_mirrorPlan.TargetCount} 頂点]" : "  [左右対称: 反対側の頂点なし]";
        }
        else MeshOps.Transform(scene, _doc.Selection, matrix);
        _doc.PreviewEdit(ChangeKind.Geometry | ChangeKind.Live);
        StatusRequested?.Invoke(status);
    }

    /// <summary>
    /// Shift で移動しているとき、マウスの近くの（動かしていない）頂点に、選択の中で掴んだ点に一番近い頂点を重ねる移動量。
    /// 軸ハンドルならその軸の成分だけ、平面ハンドルなら平面内の成分だけ合わせる。近くに頂点がなければ null。
    /// </summary>
    private Vector3? VertexSnapOffset(Point mouse, Core.Model.XScene start)
    {
        if (_doc == null) return null;
        if (_dragVertices == null)
        {
            _dragVertices = [];
            float best = float.MaxValue;
            foreach (var (m, verts) in SelectionQuery.Vertices(start, _doc.Selection))
                foreach (var v in verts)
                {
                    _dragVertices.Add(new ElementRef(m, v));
                    float d = Vector3.DistanceSquared(start.Meshes[m].Positions[v], _grabPoint);
                    if (d < best) { best = d; _snapBase = start.Meshes[m].Positions[v]; }
                }
            // 左右対称で一緒に動く頂点も吸着先にしない
            if (_mirrorPlan != null)
                foreach (var (m, verts) in _mirrorPlan.Targets)
                    foreach (var v in verts) _dragVertices.Add(new ElementRef(m, v));
        }
        if (_snapBase is not { } basePoint) return null;
        var drag = _dragVertices;
        if (FindSnapVertex(mouse, r => drag.Contains(r)) is not { } target) return null;

        var offset = target - basePoint;
        if (_active is GizmoHandle.AxisX or GizmoHandle.AxisY or GizmoHandle.AxisZ)
        {
            var axis = TransformGizmo.AxisOf(_active);
            offset = axis * Vector3.Dot(offset, axis);
        }
        else if (_active is GizmoHandle.PlaneYZ or GizmoHandle.PlaneXZ or GizmoHandle.PlaneXY)
        {
            var n = TransformGizmo.AxisOf(_active);
            offset -= n * Vector3.Dot(offset, n);
        }
        ShowSnapMarker(target);
        return offset;
    }

    private void ShowSnapMarker(Vector3 p)
    {
        EnsureOverlayShapes();
        if (Screen(p) is not { } s) return;
        System.Windows.Controls.Canvas.SetLeft(_snapMarker!, s.X - 6);
        System.Windows.Controls.Canvas.SetTop(_snapMarker!, s.Y - 6);
        _snapMarker!.Visibility = Visibility.Visible;
    }

    private void HideSnapMarker()
    {
        if (_snapMarker != null) _snapMarker.Visibility = Visibility.Collapsed;
    }

    private void EndGizmoDrag()
    {
        HideSnapMarker();
        var doc = _doc!;
        _active = GizmoHandle.None;
        _currentOffset = Vector3.Zero;
        _currentStretch = Vector3.One;
        _mirrorPlan = null;
        if (_gizmoMoved) doc.EndEdit(ChangeKind.Geometry);
        else doc.DropEdit();
        UpdateGizmo();
    }

    public void CancelGizmoDrag()
    {
        HideSnapMarker();
        _active = GizmoHandle.None;
        _currentOffset = Vector3.Zero;
        _currentStretch = Vector3.One;
        _mirrorPlan = null;
        View.Viewport.ReleaseMouseCapture();
        if (_gizmoMoved) _doc!.CancelEdit();
        else _doc!.DropEdit();
        UpdateGizmo();
        StatusRequested?.Invoke("ギズモの操作を取り消しました");
    }
}
