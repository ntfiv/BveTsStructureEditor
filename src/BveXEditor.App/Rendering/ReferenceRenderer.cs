using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using BveXEditor.App.Document;
using BveXEditor.Core.Editing;

namespace BveXEditor.App.Rendering;

/// <summary>
/// 下絵を 3D ビューに描く。モデルの後に描く（半透明を正しく重ねるため）。
/// ピック用の対応表には入れないので、クリックや範囲選択には引っかからない。
/// </summary>
public sealed class ReferenceRenderer(TextureCache textures)
{
    /// <summary>地面の下絵をグリッドやモデルの底面とちらつかせないため、少しだけ下げる。</summary>
    private const float GroundOffset = -0.02f;

    public ModelVisual3D Visual { get; } = new();

    public List<string> Errors { get; } = [];

    public void Rebuild(IEnumerable<ReferenceImage> images)
    {
        Errors.Clear();
        var group = new Model3DGroup();
        foreach (var r in images)
        {
            if (!r.Visible) continue;
            var bmp = textures.Get(r.Path, out var err);
            if (bmp == null)
            {
                if (err != null) Errors.Add(err);
                continue;
            }
            float height = ReferencePlacement.HeightOf(r.Width, bmp.PixelWidth, bmp.PixelHeight);
            var center = r.Center;
            if (r.Plane == ReferencePlane.Ground) center.Y += GroundOffset;
            var corners = ReferencePlacement.Corners(r.Plane, center, r.Width, height, r.Rotation);

            var geo = new MeshGeometry3D();
            foreach (var c in corners) geo.Positions.Add(SceneRenderer.ToWpf(c));
            geo.TextureCoordinates.Add(new Point(0, 0));
            geo.TextureCoordinates.Add(new Point(1, 0));
            geo.TextureCoordinates.Add(new Point(1, 1));
            geo.TextureCoordinates.Add(new Point(0, 1));
            // 表裏どちらからも見えるよう両面を張る（BackMaterial も同じ）
            foreach (var i in (int[])[0, 1, 2, 0, 2, 3]) geo.TriangleIndices.Add(i);
            geo.Freeze();

            var brush = new ImageBrush(bmp) { Opacity = Math.Clamp(r.Opacity, 0.05, 1) };
            brush.Freeze();
            var mat = new DiffuseMaterial(brush);
            mat.Freeze();
            group.Children.Add(new GeometryModel3D(geo, mat) { BackMaterial = mat });
        }
        group.Freeze();
        Visual.Content = group;
    }
}
