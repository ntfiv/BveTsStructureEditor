using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using BveTsStructureEditor.Core.Model;

namespace BveTsStructureEditor.App.Rendering;

/// <summary>
/// 「形を追加」で置く予定の形のゴースト表示。半透明の面と、画面上で一定の太さに見える辺。
/// ギズモと同じ前面の層に描くので、既存のモデルの中に埋もれていても見える。
/// </summary>
public static class ShapePreview
{
    private static readonly Color Ghost = Color.FromRgb(80, 220, 255);

    /// <param name="edgeRadius">辺の太さ (m)。画面上の太さを揃えるため呼ぶ側で距離から決める。</param>
    public static Model3DGroup Build(XMesh mesh, float edgeRadius)
    {
        var group = new Model3DGroup();

        var fill = new MeshGeometry3D();
        foreach (var f in mesh.Faces)
            foreach (var (a, b, c) in MeshMath.Fan(f.Indices))
                TransformGizmo.AddTri(fill, mesh.Positions[a], mesh.Positions[b], mesh.Positions[c]);
        group.Children.Add(Model(fill, Color.FromArgb(55, Ghost.R, Ghost.G, Ghost.B)));

        var edges = new MeshGeometry3D();
        var seen = new HashSet<(int, int)>();
        foreach (var f in mesh.Faces)
        {
            for (int j = 0; j < f.Indices.Length; j++)
            {
                int a = f.Indices[j], b = f.Indices[(j + 1) % f.Indices.Length];
                // 箱などは面ごとに頂点が別なので、位置で重複を判定する
                var pa = mesh.Positions[a];
                var pb = mesh.Positions[b];
                if (Vector3.DistanceSquared(pa, pb) < 1e-10f) continue;
                if (!seen.Add(Key(pa, pb))) continue;
                TransformGizmo.AddTube(edges, pa, pb, edgeRadius, 6);
            }
        }
        group.Children.Add(Model(edges, Color.FromArgb(230, Ghost.R, Ghost.G, Ghost.B)));
        group.Freeze();
        return group;
    }

    private static (int, int) Key(Vector3 a, Vector3 b)
    {
        int H(Vector3 v) => HashCode.Combine(MathF.Round(v.X, 4), MathF.Round(v.Y, 4), MathF.Round(v.Z, 4));
        int ha = H(a), hb = H(b);
        return ha < hb ? (ha, hb) : (hb, ha);
    }

    private static GeometryModel3D Model(MeshGeometry3D geo, Color color)
    {
        geo.Freeze();
        var mat = new MaterialGroup();
        mat.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(color.A, 0, 0, 0))));
        mat.Children.Add(new EmissiveMaterial(new SolidColorBrush(color)));
        mat.Freeze();
        return new GeometryModel3D(geo, mat) { BackMaterial = mat };
    }
}
