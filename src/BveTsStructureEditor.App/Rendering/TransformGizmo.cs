using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using BveTsStructureEditor.Core.Editing;

namespace BveTsStructureEditor.App.Rendering;

public enum GizmoMode { None, Move, Rotate, Scale }

public enum GizmoHandle { None, AxisX, AxisY, AxisZ, PlaneYZ, PlaneXZ, PlaneXY, Screen, RingX, RingY, RingZ }

/// <summary>
/// 移動・回転ギズモの形と、画面上での当たり判定。
///
/// 形は BVE 空間で作って <see cref="SceneRenderer.ToWpf"/> で渡す。当たり判定は 3D のヒットテストでなく
/// 「投影した線からの画面上の距離」で行う。細い矢印やリングでも掴みやすく、カメラの種類にも依存しない。
/// </summary>
public static class TransformGizmo
{
    /// <summary>ギズモの軸の長さ（画面上のおおよその px）。</summary>
    public const double ScreenSize = 90;

    private const double PickTolerance = 9;
    private const int RingSamples = 72;

    private static readonly Color XColor = Color.FromRgb(235, 70, 70);
    private static readonly Color YColor = Color.FromRgb(100, 210, 80);
    private static readonly Color ZColor = Color.FromRgb(70, 130, 245);
    private static readonly Color HotColor = Color.FromRgb(255, 225, 60);

    /// <summary>軸ハンドルとリングはその軸、平面ハンドルはその法線。</summary>
    public static Vector3 AxisOf(GizmoHandle h) => h switch
    {
        GizmoHandle.AxisX or GizmoHandle.RingX or GizmoHandle.PlaneYZ => Vector3.UnitX,
        GizmoHandle.AxisY or GizmoHandle.RingY or GizmoHandle.PlaneXZ => Vector3.UnitY,
        GizmoHandle.AxisZ or GizmoHandle.RingZ or GizmoHandle.PlaneXY => Vector3.UnitZ,
        _ => Vector3.Zero,
    };

    public static string Describe(GizmoHandle h) => h switch
    {
        GizmoHandle.AxisX => "X 軸",
        GizmoHandle.AxisY => "Y 軸",
        GizmoHandle.AxisZ => "Z 軸",
        GizmoHandle.PlaneYZ => "YZ 平面",
        GizmoHandle.PlaneXZ => "XZ 平面（水平）",
        GizmoHandle.PlaneXY => "XY 平面",
        GizmoHandle.Screen => "画面と平行",
        GizmoHandle.RingX => "X 軸まわり",
        GizmoHandle.RingY => "Y 軸まわり",
        GizmoHandle.RingZ => "Z 軸まわり",
        _ => "",
    };

    private static (Vector3 A, Vector3 B) PlaneAxes(GizmoHandle h) => h switch
    {
        GizmoHandle.PlaneYZ => (Vector3.UnitY, Vector3.UnitZ),
        GizmoHandle.PlaneXZ => (Vector3.UnitX, Vector3.UnitZ),
        _ => (Vector3.UnitX, Vector3.UnitY),
    };

    private static readonly GizmoHandle[] Axes = [GizmoHandle.AxisX, GizmoHandle.AxisY, GizmoHandle.AxisZ];
    private static readonly GizmoHandle[] Planes = [GizmoHandle.PlaneYZ, GizmoHandle.PlaneXZ, GizmoHandle.PlaneXY];
    private static readonly GizmoHandle[] Rings = [GizmoHandle.RingX, GizmoHandle.RingY, GizmoHandle.RingZ];

    private static Color ColorOf(GizmoHandle h) => AxisOf(h) switch
    {
        { X: 1 } => XColor,
        { Y: 1 } => YColor,
        _ => ZColor,
    };

    // ───────── 形 ─────────

    /// <param name="stretch">拡大ギズモのドラッグ中、軸ごとの倍率に合わせてハンドルを伸ばす（null なら 1）。</param>
    public static Model3DGroup Build(GizmoMode mode, Vector3 pivot, float size, GizmoHandle hot, Vector3? stretch = null)
    {
        var group = new Model3DGroup();
        if (mode == GizmoMode.None) return group;

        if (mode == GizmoMode.Scale)
        {
            var s = stretch ?? Vector3.One;
            foreach (var h in Axes)
            {
                var a = AxisOf(h);
                float len = size * MathF.Max(Vector3.Dot(a, s), 0.05f);
                var geo = new MeshGeometry3D();
                AddTube(geo, pivot + a * size * 0.12f, pivot + a * (len - size * 0.06f), size * 0.018f, 10);
                AddBox(geo, pivot + a * len, size * 0.06f); // 先端は立方体（移動の矢印と見分けるため）
                group.Children.Add(Model(geo, h == hot ? HotColor : ColorOf(h), 255));
            }
            foreach (var h in Planes)
            {
                var (a, b) = PlaneAxes(h);
                var geo = new MeshGeometry3D();
                float lo = size * 0.22f, hi = size * 0.42f;
                // 2 軸を同じ倍率で変えるハンドル。2 軸の間の角を斜めに横切る帯（移動の四角と見分けるため）
                AddQuad(geo, pivot + a * lo, pivot + a * hi, pivot + b * hi, pivot + b * lo);
                group.Children.Add(Model(geo, h == hot ? HotColor : ColorOf(h), h == hot ? (byte)210 : (byte)120));
            }
            var uniform = new MeshGeometry3D();
            AddBox(uniform, pivot, size * 0.08f);
            group.Children.Add(Model(uniform, hot == GizmoHandle.Screen ? HotColor : Colors.White, 230));
            group.Freeze();
            return group;
        }

        if (mode == GizmoMode.Move)
        {
            foreach (var h in Axes)
            {
                var a = AxisOf(h);
                var geo = new MeshGeometry3D();
                AddTube(geo, pivot + a * size * 0.12f, pivot + a * size * 0.78f, size * 0.018f, 10);
                AddCone(geo, pivot + a * size * 0.78f, pivot + a * size, size * 0.06f, 16);
                group.Children.Add(Model(geo, h == hot ? HotColor : ColorOf(h), 255));
            }
            foreach (var h in Planes)
            {
                var (a, b) = PlaneAxes(h);
                var geo = new MeshGeometry3D();
                float lo = size * 0.22f, hi = size * 0.42f;
                AddQuad(geo, pivot + a * lo + b * lo, pivot + a * hi + b * lo, pivot + a * hi + b * hi, pivot + a * lo + b * hi);
                group.Children.Add(Model(geo, h == hot ? HotColor : ColorOf(h), h == hot ? (byte)200 : (byte)110));
            }
            var center = new MeshGeometry3D();
            AddBox(center, pivot, size * 0.05f);
            group.Children.Add(Model(center, hot == GizmoHandle.Screen ? HotColor : Colors.White, 230));
        }
        else
        {
            foreach (var h in Rings)
            {
                var geo = new MeshGeometry3D();
                AddTorus(geo, pivot, AxisOf(h), size, size * 0.016f, RingSamples, 8);
                group.Children.Add(Model(geo, h == hot ? HotColor : ColorOf(h), 255));
            }
            var center = new MeshGeometry3D();
            AddBox(center, pivot, size * 0.03f);
            group.Children.Add(Model(center, Colors.White, 200));
        }
        group.Freeze();
        return group;
    }

    private static GeometryModel3D Model(MeshGeometry3D geo, Color color, byte alpha)
    {
        geo.Freeze();
        var c = Color.FromArgb(alpha, color.R, color.G, color.B);
        var mat = new MaterialGroup();
        mat.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0))));
        mat.Children.Add(new EmissiveMaterial(new SolidColorBrush(c)));
        mat.Freeze();
        return new GeometryModel3D(geo, mat) { BackMaterial = mat };
    }

    internal static void AddTri(MeshGeometry3D g, Vector3 a, Vector3 b, Vector3 c)
    {
        int i = g.Positions.Count;
        g.Positions.Add(SceneRenderer.ToWpf(a));
        g.Positions.Add(SceneRenderer.ToWpf(b));
        g.Positions.Add(SceneRenderer.ToWpf(c));
        g.TriangleIndices.Add(i);
        g.TriangleIndices.Add(i + 1);
        g.TriangleIndices.Add(i + 2);
    }

    private static void AddQuad(MeshGeometry3D g, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        AddTri(g, a, b, c);
        AddTri(g, a, c, d);
    }

    internal static void AddTube(MeshGeometry3D g, Vector3 from, Vector3 to, float r, int seg)
    {
        var (u, v) = GizmoMath.Perpendiculars(to - from);
        for (int i = 0; i < seg; i++)
        {
            float a0 = MathF.Tau * i / seg, a1 = MathF.Tau * (i + 1) / seg;
            var o0 = (u * MathF.Cos(a0) + v * MathF.Sin(a0)) * r;
            var o1 = (u * MathF.Cos(a1) + v * MathF.Sin(a1)) * r;
            AddQuad(g, from + o0, to + o0, to + o1, from + o1);
        }
    }

    private static void AddCone(MeshGeometry3D g, Vector3 baseCenter, Vector3 tip, float r, int seg)
    {
        var (u, v) = GizmoMath.Perpendiculars(tip - baseCenter);
        for (int i = 0; i < seg; i++)
        {
            float a0 = MathF.Tau * i / seg, a1 = MathF.Tau * (i + 1) / seg;
            var p0 = baseCenter + (u * MathF.Cos(a0) + v * MathF.Sin(a0)) * r;
            var p1 = baseCenter + (u * MathF.Cos(a1) + v * MathF.Sin(a1)) * r;
            AddTri(g, p0, tip, p1);
            AddTri(g, p0, p1, baseCenter);
        }
    }

    private static void AddBox(MeshGeometry3D g, Vector3 c, float h)
    {
        Vector3 P(int x, int y, int z) => c + new Vector3(x * h, y * h, z * h);
        AddQuad(g, P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1), P(1, -1, -1));
        AddQuad(g, P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1));
        AddQuad(g, P(-1, -1, -1), P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1));
        AddQuad(g, P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), P(1, -1, 1));
        AddQuad(g, P(-1, 1, -1), P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1));
        AddQuad(g, P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), P(-1, -1, 1));
    }

    private static void AddTorus(MeshGeometry3D g, Vector3 c, Vector3 axis, float radius, float tube, int seg, int tubeSeg)
    {
        var (u, v) = GizmoMath.Perpendiculars(axis);
        Vector3 Point(int i, int j)
        {
            float a = MathF.Tau * i / seg, b = MathF.Tau * j / tubeSeg;
            var dir = u * MathF.Cos(a) + v * MathF.Sin(a);
            return c + dir * (radius + tube * MathF.Cos(b)) + axis * (tube * MathF.Sin(b));
        }
        for (int i = 0; i < seg; i++)
        for (int j = 0; j < tubeSeg; j++)
            AddQuad(g, Point(i, j), Point(i + 1, j), Point(i + 1, j + 1), Point(i, j + 1));
    }

    // ───────── 当たり判定 ─────────

    /// <summary>
    /// マウス位置にあるハンドル。<paramref name="projectWithDepth"/> は BVE 座標 → 画面座標と奥行き（カメラの後ろなら null）。
    /// </summary>
    public static GizmoHandle Pick(GizmoMode mode, Vector3 pivot, float size, Point mouse, Func<Vector3, (Point P, double Depth)?> projectWithDepth)
    {
        Point? project(Vector3 v) => projectWithDepth(v)?.P;
        var project2 = projectWithDepth;
        if (mode == GizmoMode.None || project(pivot) is not { } c) return GizmoHandle.None;

        if (mode is GizmoMode.Move or GizmoMode.Scale)
        {
            if ((mouse - c).Length <= PickTolerance + (mode == GizmoMode.Scale ? 5 : 2)) return GizmoHandle.Screen;

            var best = GizmoHandle.None;
            double bestD = PickTolerance;
            foreach (var h in Axes)
            {
                if (project(pivot + AxisOf(h) * size) is not { } end) continue;
                if ((end - c).Length < 12) continue; // 画面の奥を向いている軸は掴めない
                var d = DistanceToSegment(mouse, c, end);
                if (d < bestD) { bestD = d; best = h; }
            }
            if (best != GizmoHandle.None) return best;

            foreach (var h in Planes)
            {
                var (a, b) = PlaneAxes(h);
                float lo = size * 0.22f, hi = size * 0.42f;
                var shape = mode == GizmoMode.Scale
                    ? new[] { a * lo, a * hi, b * hi, b * lo }
                    : new[] { a * lo + b * lo, a * hi + b * lo, a * hi + b * hi, a * lo + b * hi };
                var corners = shape.Select(o => project(pivot + o)).ToArray();
                if (corners.Any(p => p == null)) continue;
                if (InPolygon(mouse, corners.Select(p => p!.Value).ToArray())) return h;
            }
            return GizmoHandle.None;
        }

        // リングは画面上で重なりやすい。近さがほぼ同じなら、手前（カメラに近い）側を通るリングを優先する
        var candidates = new List<(GizmoHandle H, double Dist, double Depth)>();
        foreach (var h in Rings)
        {
            var (u, v) = GizmoMath.Perpendiculars(AxisOf(h));
            (double Dist, double Depth) best = (double.MaxValue, 0);
            (Point P, double Depth)? prev = null;
            for (int i = 0; i <= RingSamples; i++)
            {
                float a = MathF.Tau * i / RingSamples;
                var cur = project2(pivot + (u * MathF.Cos(a) + v * MathF.Sin(a)) * size);
                if (cur is { } c1 && prev is { } c0)
                {
                    var d = DistanceToSegment(mouse, c0.P, c1.P);
                    if (d < best.Dist) best = (d, (c0.Depth + c1.Depth) / 2);
                }
                prev = cur;
            }
            if (best.Dist <= PickTolerance) candidates.Add((h, best.Dist, best.Depth));
        }
        if (candidates.Count == 0) return GizmoHandle.None;
        var minDist = candidates.Min(c => c.Dist);
        return candidates.Where(c => c.Dist <= minDist + 4).MinBy(c => c.Depth).H;
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        double len2 = ab.LengthSquared;
        if (len2 < 1e-9) return (p - a).Length;
        double t = Math.Clamp(((p - a) * ab) / len2, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    private static bool InPolygon(Point p, Point[] poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) &&
                p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        }
        return inside;
    }
}
