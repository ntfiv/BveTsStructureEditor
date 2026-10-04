using System.Numerics;
using BveTsStructureEditor.Core.Model;

namespace BveTsStructureEditor.Core.Editing;

public enum UvProjectAxis
{
    /// <summary>面ごとに一番近い軸で投影し、軸の違う面どうしは頂点を切り離す（箱投影）。</summary>
    Auto,
    /// <summary>選択面の平均法線の方向から見て投影する。</summary>
    Average,
    Front,
    Side,
    Top,
}

public enum UvFit
{
    /// <summary>0〜1 に縦横別々に引き伸ばす。</summary>
    Stretch,
    /// <summary>縦横比を保って 0〜1 に収める。</summary>
    KeepAspect,
    /// <summary>実寸で貼る（<c>metersPerRepeat</c> m でテクスチャ 1 枚）。</summary>
    WorldScale,
}

public static class UvOps
{
    /// <summary>選んだ頂点（面選択ならその頂点）の UV に 2D 行列を掛ける。</summary>
    public static string Transform(XScene scene, Selection sel, Matrix3x2 matrix)
    {
        int count = 0;
        foreach (var (m, verts) in SelectionQuery.Vertices(scene, sel))
        {
            var mesh = scene.Meshes[m];
            mesh.EnsureUV();
            foreach (var v in verts) mesh.TexCoords[v] = Vector2.Transform(mesh.TexCoords[v], matrix);
            count += verts.Count;
        }
        return $"{count} 頂点の UV を変形しました";
    }

    public static Vector2 Center(XScene scene, Selection sel)
    {
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var (m, verts) in SelectionQuery.Vertices(scene, sel))
        {
            var mesh = scene.Meshes[m];
            if (!mesh.HasUV) continue;
            foreach (var v in verts)
            {
                min = Vector2.Min(min, mesh.TexCoords[v]);
                max = Vector2.Max(max, mesh.TexCoords[v]);
            }
        }
        return min.X > max.X ? new Vector2(0.5f) : (min + max) / 2;
    }

    /// <summary>
    /// 見る方向 <paramref name="facing"/>（面の表の法線）から、画面右・画面上のベクトルを返す。
    /// 左手系なので right = Cross(up, forward)。forward は -facing。
    /// </summary>
    public static (Vector3 Right, Vector3 Up) ViewBasis(Vector3 facing)
    {
        var forward = -Vector3.Normalize(facing);
        var up = MathF.Abs(forward.Y) > 0.9f ? Vector3.UnitZ : Vector3.UnitY;
        var right = Vector3.Normalize(Vector3.Cross(up, forward));
        up = Vector3.Cross(forward, right);
        // Cross(forward, right) の向きを確認: forward=+Z, right=+X なら (0,1,0) になる
        return (right, up);
    }

    private static Vector3 AxisNormal(Vector3 n)
    {
        var a = Vector3.Abs(n);
        if (a.X >= a.Y && a.X >= a.Z) return new Vector3(MathF.Sign(n.X) is 0 ? 1 : MathF.Sign(n.X), 0, 0);
        if (a.Y >= a.Z) return new Vector3(0, MathF.Sign(n.Y) is 0 ? 1 : MathF.Sign(n.Y), 0);
        return new Vector3(0, 0, MathF.Sign(n.Z) is 0 ? -1 : MathF.Sign(n.Z));
    }

    /// <summary>選んだ面に平面投影で UV を付ける。</summary>
    public static string Project(XScene scene, Selection sel, UvProjectAxis axis, UvFit fit, float metersPerRepeat = 1f)
    {
        int total = 0;
        foreach (var (m, faces) in SelectionQuery.Faces(scene, sel))
        {
            var mesh = scene.Meshes[m];
            if (faces.Count == 0) continue;
            mesh.EnsureUV();

            // 面 → 投影方向
            var facing = new Dictionary<int, Vector3>();
            if (axis == UvProjectAxis.Auto)
                foreach (var f in faces) facing[f] = AxisNormal(mesh.FaceNormal(mesh.Faces[f]));
            else
            {
                Vector3 dir = axis switch
                {
                    UvProjectAxis.Front => -Vector3.UnitZ,
                    UvProjectAxis.Side => Vector3.UnitX,
                    UvProjectAxis.Top => Vector3.UnitY,
                    _ => faces.Aggregate(Vector3.Zero, (s, f) => s + mesh.FaceNormal(mesh.Faces[f])),
                };
                if (dir.LengthSquared() < 1e-12f) dir = -Vector3.UnitZ;
                foreach (var f in faces) facing[f] = Vector3.Normalize(dir);
            }

            // 投影方向ごと、および非選択面との間で頂点を切り離す
            var dirs = facing.Values.Distinct().ToList();
            var group = new int[mesh.Faces.Count];
            for (int f = 0; f < group.Length; f++)
                group[f] = facing.TryGetValue(f, out var d) ? dirs.IndexOf(d) + 1 : 0;
            MeshOps.SplitVerticesByGroup(mesh, group);

            // グループごとに投影
            foreach (var d in dirs)
            {
                var (right, up) = ViewBasis(d);
                var verts = new HashSet<int>();
                foreach (var (f, fd) in facing)
                    if (fd == d) foreach (var v in mesh.Faces[f].Indices) verts.Add(v);

                var raw = verts.ToDictionary(v => v, v =>
                    new Vector2(Vector3.Dot(mesh.Positions[v], right), -Vector3.Dot(mesh.Positions[v], up)));
                var min = raw.Values.Aggregate(new Vector2(float.MaxValue), Vector2.Min);
                var max = raw.Values.Aggregate(new Vector2(float.MinValue), Vector2.Max);
                var size = Vector2.Max(max - min, new Vector2(1e-6f));
                foreach (var (v, p) in raw)
                {
                    mesh.TexCoords[v] = fit switch
                    {
                        UvFit.Stretch => (p - min) / size,
                        UvFit.KeepAspect => (p - min) / MathF.Max(size.X, size.Y),
                        _ => (p - new Vector2(min.X, min.Y)) / MathF.Max(metersPerRepeat, 1e-4f),
                    };
                }
            }
            total += faces.Count;
        }
        return $"{total} 面に UV を投影しました";
    }
}
