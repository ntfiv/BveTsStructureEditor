using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BveTsStructureEditor.App.Document;
using BveTsStructureEditor.Core.Editing;
using BveTsStructureEditor.Core.Model;
using Point = System.Windows.Point;
using Vector = System.Windows.Vector;

namespace BveTsStructureEditor.App.Views;

/// <summary>
/// UV を 2D で表示・編集する面。対象は作業中のメッシュの、選んだマテリアルの面だけ。
/// 画面座標 = 原点位置 + UV × 拡大率（V は下向きで画面と同じ）。
/// </summary>
public sealed class UvCanvas : FrameworkElement
{
    private static readonly Color Accent = Color.FromRgb(255, 150, 30);

    private IEditorHost? _host;
    private double _scale = 300;
    private Vector _origin = new(40, 40);
    private bool _fitted;

    private enum DragMode { None, Pan, Band, Move }
    private DragMode _drag;
    private Point _down;
    private Point _last;
    private Dictionary<int, Vector2>? _moveStart;

    public int Material { get; set; }
    public BitmapSource? Texture { get; set; }

    public UvCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        SizeChanged += (_, _) => { if (!_fitted && ActualWidth > 0) FitView(); };
    }

    public void Attach(IEditorHost host) => _host = host;

    private EditorDocument? Doc => _host?.Document;

    private XMesh? Mesh => Doc is { } d && d.ActiveMesh < d.Scene.Meshes.Count ? d.Scene.Meshes[d.ActiveMesh] : null;

    public void FitView()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        _scale = Math.Max(20, Math.Min(ActualWidth, ActualHeight) * 0.85);
        _origin = new Vector((ActualWidth - _scale) / 2, (ActualHeight - _scale) / 2);
        _fitted = true;
        InvalidateVisual();
    }

    private Point ToScreen(Vector2 uv) => new(_origin.X + uv.X * _scale, _origin.Y + uv.Y * _scale);
    private Vector2 ToUv(Point p) => new((float)((p.X - _origin.X) / _scale), (float)((p.Y - _origin.Y) / _scale));

    private IEnumerable<int> VisibleFaces(XMesh mesh)
    {
        for (int i = 0; i < mesh.Faces.Count; i++)
            if (mesh.Faces[i].Material == Material) yield return i;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(36, 39, 45)), null, new Rect(0, 0, ActualWidth, ActualHeight));

        var unit = new Rect(ToScreen(Vector2.Zero), ToScreen(Vector2.One));
        if (Texture != null)
        {
            // 周囲 (繰り返し) は薄く、0〜1 は濃く
            dc.PushOpacity(0.25);
            for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++)
                if (x != 0 || y != 0)
                    dc.DrawImage(Texture, new Rect(unit.X + x * unit.Width, unit.Y + y * unit.Height, unit.Width, unit.Height));
            dc.Pop();
            dc.DrawImage(Texture, unit);
        }
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1);
        for (int k = -4; k <= 8; k++)
        {
            var a = ToScreen(new Vector2(k / 4f, -1));
            var b = ToScreen(new Vector2(k / 4f, 2));
            dc.DrawLine(gridPen, a, b);
            a = ToScreen(new Vector2(-1, k / 4f));
            b = ToScreen(new Vector2(2, k / 4f));
            dc.DrawLine(gridPen, a, b);
        }
        dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1.5), unit);

        var mesh = Mesh;
        var doc = Doc;
        if (mesh == null || doc == null) { DrawHint(dc, "メッシュがありません"); return; }
        if (!mesh.HasUV) { DrawHint(dc, "このメッシュには UV がありません。「投影」で UV を付けられます"); return; }

        var sel = doc.Selection;
        var selFaces = SelectionQuery.Faces(doc.Scene, sel, emptyMeansAll: false).GetValueOrDefault(doc.ActiveMesh) ?? [];
        var selVerts = SelectionQuery.Vertices(doc.Scene, sel, emptyMeansAll: false).GetValueOrDefault(doc.ActiveMesh) ?? [];

        var edgePen = new Pen(new SolidColorBrush(Color.FromArgb(220, 120, 200, 255)), 1);
        var selPen = new Pen(new SolidColorBrush(Accent), 1.6);
        var selFill = new SolidColorBrush(Color.FromArgb(60, Accent.R, Accent.G, Accent.B));
        edgePen.Freeze();
        selPen.Freeze();

        foreach (var fi in VisibleFaces(mesh))
        {
            var f = mesh.Faces[fi];
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(ToScreen(mesh.TexCoords[f.Indices[0]]), selFaces.Contains(fi) && sel.Mode != SelectMode.Vertex, true);
                for (int j = 1; j < f.Indices.Length; j++) ctx.LineTo(ToScreen(mesh.TexCoords[f.Indices[j]]), true, false);
            }
            geo.Freeze();
            bool selected = selFaces.Contains(fi) && sel.Mode != SelectMode.Vertex;
            dc.DrawGeometry(selected ? selFill : null, selected ? selPen : edgePen, geo);
        }

        if (sel.Mode == SelectMode.Vertex)
        {
            var dot = new SolidColorBrush(Color.FromRgb(120, 200, 255));
            var selDot = new SolidColorBrush(Accent);
            var drawn = new HashSet<int>();
            foreach (var fi in VisibleFaces(mesh))
                foreach (var v in mesh.Faces[fi].Indices)
                    if (drawn.Add(v))
                    {
                        bool s = selVerts.Contains(v);
                        dc.DrawEllipse(s ? selDot : dot, null, ToScreen(mesh.TexCoords[v]), s ? 4 : 2.5, s ? 4 : 2.5);
                    }
        }

        if (_drag == DragMode.Band)
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(34, Accent.R, Accent.G, Accent.B)),
                new Pen(new SolidColorBrush(Accent), 1) { DashStyle = DashStyles.Dash }, new Rect(_down, _last));
        }
    }

    private void DrawHint(DrawingContext dc, string text)
    {
        var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Yu Gothic UI"), 13, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(12, 12));
    }

    // ───────── 操作 ─────────

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        var uv = ToUv(p);
        _scale = Math.Clamp(_scale * (e.Delta > 0 ? 1.15 : 1 / 1.15), 10, 100000);
        _origin = new Vector(p.X - uv.X * _scale, p.Y - uv.Y * _scale);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        _down = _last = e.GetPosition(this);
        if (e.ChangedButton is MouseButton.Middle or MouseButton.Right)
        {
            _drag = DragMode.Pan;
            CaptureMouse();
            return;
        }
        if (e.ChangedButton != MouseButton.Left || Doc == null || Mesh is not { HasUV: true } mesh) return;

        // 選択済みのものを掴んだら移動、そうでなければ範囲選択（クリックなら単独選択）
        if (Keyboard.Modifiers == ModifierKeys.None && HitSelected(_down))
        {
            _drag = DragMode.Move;
            Doc.BeginEdit("UV を移動");
            var verts = SelectionQuery.Vertices(Doc.Scene, Doc.Selection, emptyMeansAll: false).GetValueOrDefault(Doc.ActiveMesh) ?? [];
            _moveStart = verts.ToDictionary(v => v, v => mesh.TexCoords[v]);
        }
        else _drag = DragMode.Band;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        switch (_drag)
        {
            case DragMode.Pan:
                _origin += p - _last;
                InvalidateVisual();
                break;
            case DragMode.Band:
                InvalidateVisual();
                break;
            case DragMode.Move when Mesh is { } mesh && _moveStart != null:
                var delta = ToUv(p) - ToUv(_down);
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    // Shift で縦か横だけ
                    if (MathF.Abs(delta.X) > MathF.Abs(delta.Y)) delta.Y = 0; else delta.X = 0;
                }
                foreach (var (v, start) in _moveStart) mesh.TexCoords[v] = start + delta;
                Doc!.PreviewEdit(ChangeKind.Geometry | ChangeKind.Live);
                break;
        }
        _last = p;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_drag == DragMode.None) return;
        ReleaseMouseCapture();
        var mode = _drag;
        _drag = DragMode.None;
        var p = e.GetPosition(this);

        if (mode == DragMode.Move)
        {
            _moveStart = null;
            if ((p - _down).Length < 1) Doc!.CancelEdit();
            else Doc!.EndEdit(ChangeKind.Geometry);
            return;
        }
        if (mode == DragMode.Band && Doc != null)
        {
            var hits = (p - _down).Length < 4 ? ClickHit(p) : BandHit(new Rect(_down, p));
            var sel = Doc.Selection;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) foreach (var h in hits) sel.Toggle(h);
            else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) sel.Add(hits);
            else sel.Set(hits);
        }
        InvalidateVisual();
    }

    private bool HitSelected(Point p)
    {
        var doc = Doc!;
        if (doc.Selection.IsEmpty) return false;
        if (doc.Selection.Mode == SelectMode.Object) return doc.Selection.Items.Any(r => r.Mesh == doc.ActiveMesh);
        return ClickHit(p).Any(doc.Selection.Contains);
    }

    private List<ElementRef> ClickHit(Point p)
    {
        var doc = Doc!;
        var mesh = Mesh!;
        int m = doc.ActiveMesh;
        switch (doc.Selection.Mode)
        {
            case SelectMode.Vertex:
            {
                int best = -1;
                double bestD = 8;
                foreach (var fi in VisibleFaces(mesh))
                    foreach (var v in mesh.Faces[fi].Indices)
                    {
                        var d = (ToScreen(mesh.TexCoords[v]) - p).Length;
                        if (d < bestD) { bestD = d; best = v; }
                    }
                return best >= 0 ? [new ElementRef(m, best)] : [];
            }
            case SelectMode.Face:
            {
                var uv = ToUv(p);
                int hit = -1;
                foreach (var fi in VisibleFaces(mesh))
                    if (Contains(mesh, mesh.Faces[fi], uv)) hit = fi;
                return hit >= 0 ? [new ElementRef(m, hit)] : [];
            }
            default:
                return VisibleFaces(mesh).Any(fi => Contains(mesh, mesh.Faces[fi], ToUv(p))) ? [new ElementRef(m, -1)] : [];
        }
    }

    private List<ElementRef> BandHit(Rect r)
    {
        var doc = Doc!;
        var mesh = Mesh;
        int m = doc.ActiveMesh;
        if (mesh is not { HasUV: true }) return [];
        var result = new HashSet<ElementRef>();
        foreach (var fi in VisibleFaces(mesh))
        {
            var f = mesh.Faces[fi];
            switch (doc.Selection.Mode)
            {
                case SelectMode.Vertex:
                    foreach (var v in f.Indices)
                        if (r.Contains(ToScreen(mesh.TexCoords[v]))) result.Add(new ElementRef(m, v));
                    break;
                case SelectMode.Face:
                    var c = f.Indices.Aggregate(Vector2.Zero, (s, v) => s + mesh.TexCoords[v]) / f.Indices.Length;
                    if (r.Contains(ToScreen(c))) result.Add(new ElementRef(m, fi));
                    break;
                default:
                    if (f.Indices.Any(v => r.Contains(ToScreen(mesh.TexCoords[v])))) result.Add(new ElementRef(m, -1));
                    break;
            }
        }
        return result.ToList();
    }

    private static bool Contains(XMesh mesh, XFace f, Vector2 p)
    {
        bool inside = false;
        for (int i = 0, j = f.Indices.Length - 1; i < f.Indices.Length; j = i++)
        {
            var a = mesh.TexCoords[f.Indices[i]];
            var b = mesh.TexCoords[f.Indices[j]];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }
}
