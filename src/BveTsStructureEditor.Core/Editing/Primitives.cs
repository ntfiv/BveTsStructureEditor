using System.Numerics;
using BveTsStructureEditor.Core.Model;

namespace BveTsStructureEditor.Core.Editing;

/// <summary>
/// 新規作成用の基本形状。どれも底面が Y=0、X/Z は原点中心（BVE のストラクチャで置きやすい形）。
/// </summary>
public static class Primitives
{
    /// <summary>表向き <paramref name="facing"/> の四角形を足す。UV は 0〜1。</summary>
    public static void AddQuad(XMesh mesh, Vector3 center, Vector3 facing, float width, float height, int material = 0)
    {
        var (right, up) = UvOps.ViewBasis(facing);
        var r = right * (width / 2);
        var u = up * (height / 2);
        int a = mesh.AddVertex(center - r - u, new Vector2(0, 1));
        int b = mesh.AddVertex(center - r + u, new Vector2(0, 0));
        int c = mesh.AddVertex(center + r + u, new Vector2(1, 0));
        int d = mesh.AddVertex(center + r - u, new Vector2(1, 1));
        mesh.Faces.Add(new XFace([a, b, c, d], material));
    }

    public static XMesh Box(float w, float h, float d)
    {
        var m = new XMesh { Name = "Box" };
        m.Materials.Add(new XMaterial());
        float hy = h / 2;
        AddQuad(m, new(0, hy, -d / 2), -Vector3.UnitZ, w, h); // 手前
        AddQuad(m, new(0, hy, d / 2), Vector3.UnitZ, w, h);   // 奥
        AddQuad(m, new(-w / 2, hy, 0), -Vector3.UnitX, d, h); // 左
        AddQuad(m, new(w / 2, hy, 0), Vector3.UnitX, d, h);   // 右
        AddQuad(m, new(0, h, 0), Vector3.UnitY, w, d);        // 上
        AddQuad(m, new(0, 0, 0), -Vector3.UnitY, w, d);       // 下
        return m;
    }

    /// <summary>板。<paramref name="vertical"/> なら手前 (-Z) 向きの立て看板、そうでなければ上向きの地面。</summary>
    public static XMesh Plane(float w, float h, bool vertical)
    {
        var m = new XMesh { Name = "Plane" };
        m.Materials.Add(new XMaterial());
        if (vertical) AddQuad(m, new(0, h / 2, 0), -Vector3.UnitZ, w, h);
        else AddQuad(m, Vector3.Zero, Vector3.UnitY, w, h);
        return m;
    }

    public enum ImageSizeMode
    {
        /// <summary>高さを指定し、幅は画像の縦横比で決める。</summary>
        Height,
        /// <summary>幅を指定し、高さは画像の縦横比で決める。</summary>
        Width,
        /// <summary>1 ピクセルあたりの長さ (m) を指定する。</summary>
        PerPixel,
    }

    /// <summary>
    /// 画像を貼った板。縦横比は画像に合わせ、テクスチャ付きの白いマテリアルを 1 つ持つ。
    /// 置き方は <see cref="Plane"/> と同じ（立てるなら底辺中心が原点、寝かせるなら中心が原点）。
    /// </summary>
    public static XMesh ImagePlane(string name, int pixelWidth, int pixelHeight, ImageSizeMode mode, float size, bool vertical, string? texture)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0) throw new ArgumentException("画像の大きさが不正です");
        if (size <= 0) throw new ArgumentException("大きさは正の数にしてください");
        float aspect = (float)pixelWidth / pixelHeight;
        var (w, h) = mode switch
        {
            ImageSizeMode.Height => (size * aspect, size),
            ImageSizeMode.Width => (size, size / aspect),
            _ => (pixelWidth * size, pixelHeight * size),
        };
        var m = Plane(w, h, vertical);
        m.Name = name;
        m.Materials[0].Texture = texture;
        return m;
    }

    public static XMesh Cylinder(float radius, float height, int segments, bool caps = true)
    {
        segments = Math.Clamp(segments, 3, 128);
        var m = new XMesh { Name = "Cylinder" };
        m.Materials.Add(new XMaterial());

        // 側面: 継ぎ目の UV のため seg+1 列
        var bottom = new int[segments + 1];
        var top = new int[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            float t = (float)i / segments;
            float a = t * MathF.Tau;
            var p = new Vector3(radius * MathF.Cos(a), 0, radius * MathF.Sin(a));
            bottom[i] = m.AddVertex(p, new Vector2(t, 1));
            top[i] = m.AddVertex(p + new Vector3(0, height, 0), new Vector2(t, 0));
        }
        for (int i = 0; i < segments; i++)
            m.Faces.Add(new XFace([bottom[i], top[i], top[i + 1], bottom[i + 1]]));

        if (caps)
        {
            var bc = new int[segments];
            var tc = new int[segments];
            for (int i = 0; i < segments; i++)
            {
                float a = (float)i / segments * MathF.Tau;
                var uv = new Vector2(0.5f + 0.5f * MathF.Cos(a), 0.5f + 0.5f * MathF.Sin(a));
                var p = new Vector3(radius * MathF.Cos(a), 0, radius * MathF.Sin(a));
                bc[i] = m.AddVertex(p, uv);
                tc[i] = m.AddVertex(p + new Vector3(0, height, 0), uv);
            }
            m.Faces.Add(new XFace(bc));                 // 角度の増える順で下向き
            m.Faces.Add(new XFace(tc.Reverse().ToArray())); // 逆順で上向き
        }
        return m;
    }

    /// <summary>
    /// 中空の円柱（パイプ）。外側の半径 <paramref name="radius"/> から <paramref name="thickness"/> だけ内側に穴があく。
    /// 外側・内側の側面と、上下のドーナツ状の縁で閉じた形。底面 Y=0、軸は原点を通る縦線。
    /// </summary>
    public static XMesh Pipe(float radius, float thickness, float height, int segments)
    {
        if (radius <= 0 || height <= 0) throw new ArgumentException("半径と高さは正の数にしてください");
        if (thickness <= 0 || thickness >= radius) throw new ArgumentException("厚さは 0 より大きく、半径より小さくしてください");
        segments = Math.Clamp(segments, 3, 128);
        float inner = radius - thickness;
        var m = new XMesh { Name = "Pipe" };
        m.Materials.Add(new XMaterial());
        var up = new Vector3(0, height, 0);

        static Vector3 Ring(float r, float t) => new(r * MathF.Cos(t * MathF.Tau), 0, r * MathF.Sin(t * MathF.Tau));

        // 外側と内側の側面（UV の継ぎ目のため seg+1 列）。外側は軸から離れる向き、内側は軸へ向く
        foreach (var (r, sign) in new[] { (radius, 1f), (inner, -1f) })
        {
            var bottom = new int[segments + 1];
            var top = new int[segments + 1];
            for (int i = 0; i <= segments; i++)
            {
                float t = (float)i / segments;
                bottom[i] = m.AddVertex(Ring(r, t), new Vector2(t, 1));
                top[i] = m.AddVertex(Ring(r, t) + up, new Vector2(t, 0));
            }
            for (int i = 0; i < segments; i++)
            {
                float mid = (i + 0.5f) / segments;
                AddFacing(m, [bottom[i], top[i], top[i + 1], bottom[i + 1]], Vector3.Normalize(Ring(1, mid)) * sign);
            }
        }

        // 上下の縁。UV は上から見た位置（外径が 0〜1 に収まる）
        foreach (var (y, facing) in new[] { (0f, -Vector3.UnitY), (height, Vector3.UnitY) })
        {
            var outerIdx = new int[segments];
            var innerIdx = new int[segments];
            for (int i = 0; i < segments; i++)
            {
                float t = (float)i / segments;
                var po = Ring(radius, t);
                var pi = Ring(inner, t);
                outerIdx[i] = m.AddVertex(po with { Y = y }, new Vector2(0.5f + 0.5f * po.X / radius, 0.5f + 0.5f * po.Z / radius));
                innerIdx[i] = m.AddVertex(pi with { Y = y }, new Vector2(0.5f + 0.5f * pi.X / radius, 0.5f + 0.5f * pi.Z / radius));
            }
            for (int i = 0; i < segments; i++)
            {
                int j = (i + 1) % segments;
                AddFacing(m, [outerIdx[i], outerIdx[j], innerIdx[j], innerIdx[i]], facing);
            }
        }
        return m;
    }

    public enum ElbowDirection { PlusX, PlusZ, MinusX, MinusZ }

    /// <summary>
    /// L 字に曲がった円柱・パイプ。原点から縦 (Y) に <paramref name="height"/> 上がり、<paramref name="direction"/> へ
    /// <paramref name="length"/> 伸びる（どちらも中心線の角までの長さ）。<paramref name="bendRadius"/> が 0 なら角ばった継ぎ目、
    /// 正なら中心線をその半径の円弧で曲げる。<paramref name="thickness"/> が 0 なら中の詰まった棒、正ならパイプ。
    /// </summary>
    public static XMesh Elbow(float radius, float thickness, float height, float length, float bendRadius, int segments,
        ElbowDirection direction = ElbowDirection.PlusZ)
    {
        if (radius <= 0 || height <= 0 || length <= 0) throw new ArgumentException("半径・縦と横の長さは正の数にしてください");
        if (bendRadius < 0 || bendRadius > MathF.Min(height, length))
            throw new ArgumentException("曲げ半径は 0 以上で、縦と横の長さ以下にしてください");
        if (bendRadius > 0 && bendRadius < radius)
            throw new ArgumentException("曲げ半径が管の半径より小さいと、曲がりの内側がつぶれます（0 にすると角ばった継ぎ目）");

        // 中心線（X-Y 平面で +X へ曲がる形を作り、向きに合わせて Y 軸まわりに回す）
        var path = new List<Vector3> { Vector3.Zero };
        if (bendRadius == 0)
        {
            path.Add(new Vector3(0, height, 0));
        }
        else
        {
            var center = new Vector3(bendRadius, height - bendRadius, 0);
            int bend = Math.Clamp(segments / 2, 4, 32);
            // 縦の管の上端 (0, h-R) から横の管の始まり (R, h) までの 1/4 円
            for (int i = 0; i <= bend; i++)
            {
                float t = (float)i / bend * MathF.PI / 2;
                path.Add(center + new Vector3(-bendRadius * MathF.Cos(t), bendRadius * MathF.Sin(t), 0));
            }
        }
        path.Add(new Vector3(length, height, 0));

        float angle = direction switch
        {
            ElbowDirection.PlusZ => -MathF.PI / 2, // +X を +Z へ
            ElbowDirection.MinusX => MathF.PI,
            ElbowDirection.MinusZ => MathF.PI / 2,
            _ => 0,
        };
        if (angle != 0)
        {
            var rot = Matrix4x4.CreateRotationY(angle);
            for (int i = 0; i < path.Count; i++) path[i] = Vector3.Transform(path[i], rot);
        }
        var m = Tube(path, radius, thickness, segments);
        m.Name = thickness > 0 ? "ElbowPipe" : "Elbow";
        return m;
    }

    /// <summary>
    /// 折れ線 <paramref name="path"/> に沿って円の断面を押し出した管。折れ目は二等分面で継ぐので太さが変わらない。
    /// <paramref name="thickness"/> が 0 なら中の詰まった棒（両端は円の蓋）、正ならパイプ（両端はドーナツ状の縁）。
    /// </summary>
    public static XMesh Tube(IReadOnlyList<Vector3> path, float radius, float thickness, int segments)
    {
        var pts = new List<Vector3>();
        foreach (var p in path)
            if (pts.Count == 0 || Vector3.Distance(pts[^1], p) > 1e-5f) pts.Add(p);
        if (pts.Count < 2) throw new ArgumentException("経路が短すぎます");
        if (thickness < 0 || thickness >= radius) throw new ArgumentException("厚さは 0 以上で、半径より小さくしてください");
        segments = Math.Clamp(segments, 3, 128);

        // 区間ごとの向きと、ねじれないように回して運ぶ断面の基底
        int n = pts.Count;
        var dirs = new Vector3[n - 1];
        var us = new Vector3[n - 1];
        var vs = new Vector3[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            dirs[i] = Vector3.Normalize(pts[i + 1] - pts[i]);
            if (i == 0)
            {
                (us[0], vs[0]) = GizmoMath.Perpendiculars(dirs[0]);
                continue;
            }
            var a = dirs[i - 1];
            var b = dirs[i];
            float dot = Math.Clamp(Vector3.Dot(a, b), -1f, 1f);
            if (dot < -0.999f) throw new ArgumentException("経路が折り返しています");
            var axis = Vector3.Cross(a, b);
            if (axis.LengthSquared() < 1e-12f)
            {
                us[i] = us[i - 1];
                vs[i] = vs[i - 1];
            }
            else
            {
                var q = Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(dot));
                us[i] = Vector3.Transform(us[i - 1], q);
                vs[i] = Vector3.Transform(vs[i - 1], q);
            }
        }

        // 各点の断面（折れ目は、手前の区間の円を二等分面へ沿わせて写す）
        Vector3 RingPoint(int k, float r, float t)
        {
            float phi = t * MathF.Tau;
            int from = k == 0 ? 0 : k - 1; // この点へ入ってくる区間（始点は最初の区間）
            var offset = (us[from] * MathF.Cos(phi) + vs[from] * MathF.Sin(phi)) * r;
            if (k == 0 || k == n - 1) return pts[k] + offset;
            var miter = Vector3.Normalize(dirs[k - 1] + dirs[k]);
            float s = -Vector3.Dot(offset, miter) / Vector3.Dot(dirs[k - 1], miter);
            return pts[k] + offset + dirs[k - 1] * s;
        }

        var lengths = new float[n];
        for (int i = 1; i < n; i++) lengths[i] = lengths[i - 1] + Vector3.Distance(pts[i], pts[i - 1]);
        float total = MathF.Max(lengths[^1], 1e-6f);

        var mesh = new XMesh { Name = "Tube" };
        mesh.Materials.Add(new XMaterial());
        float inner = radius - thickness;
        var layers = thickness > 0 ? new[] { (radius, 1f), (inner, -1f) } : new[] { (radius, 1f) };
        foreach (var (r, sign) in layers)
        {
            var grid = new int[n, segments + 1];
            for (int k = 0; k < n; k++)
                for (int j = 0; j <= segments; j++)
                {
                    float t = (float)j / segments;
                    grid[k, j] = mesh.AddVertex(RingPoint(k, r, t), new Vector2(t, 1 - lengths[k] / total));
                }
            for (int k = 0; k < n - 1; k++)
                for (int j = 0; j < segments; j++)
                {
                    float phi = (j + 0.5f) / segments * MathF.Tau;
                    var radial = us[k] * MathF.Cos(phi) + vs[k] * MathF.Sin(phi);
                    AddFacing(mesh, [grid[k, j], grid[k + 1, j], grid[k + 1, j + 1], grid[k, j + 1]], radial * sign);
                }
        }

        // 両端
        foreach (var (k, facing) in new[] { (0, -dirs[0]), (n - 1, dirs[^1]) })
        {
            var outer = new int[segments];
            var hole = new int[segments];
            for (int j = 0; j < segments; j++)
            {
                float t = (float)j / segments;
                var uv = new Vector2(0.5f + 0.5f * MathF.Cos(t * MathF.Tau), 0.5f + 0.5f * MathF.Sin(t * MathF.Tau));
                outer[j] = mesh.AddVertex(RingPoint(k, radius, t), uv);
                if (thickness > 0) hole[j] = mesh.AddVertex(RingPoint(k, inner, t), uv * (inner / radius) + new Vector2(0.5f, 0.5f) * (1 - inner / radius));
            }
            if (thickness > 0)
                for (int j = 0; j < segments; j++)
                {
                    int jn = (j + 1) % segments;
                    AddFacing(mesh, [outer[j], outer[jn], hole[jn], hole[j]], facing);
                }
            else
                AddFacing(mesh, outer, facing);
        }
        return mesh;
    }

    /// <summary>面の表が <paramref name="facing"/> 側を向くように並びをそろえて足す。</summary>
    private static void AddFacing(XMesh mesh, int[] idx, Vector3 facing)
    {
        var face = new XFace(idx);
        if (Vector3.Dot(mesh.FaceNormal(face), facing) < 0) Array.Reverse(face.Indices);
        mesh.Faces.Add(face);
    }
}
