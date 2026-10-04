using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using BveTsStructureEditor.Core.Editing;
using BveTsStructureEditor.Core.Model;

namespace BveTsStructureEditor.App.Rendering;

/// <summary>
/// Repeater で並べたところを 3D ビューに重ねて描く。ピースの形は 1 回だけ作って（Freeze して）、
/// 繰り返しぶんは同じ形を変換違いで置く。ピック用の対応表に入れないので選択には引っかからない。
/// </summary>
public sealed class RepeaterPreviewRenderer(TextureCache textures)
{
    public ModelVisual3D Visual { get; } = new();

    private List<Model3DGroup> _pieces = [];

    /// <summary>並べるピースの形を作り直す（分割をやり直したとき）。</summary>
    public void SetPieces(IEnumerable<XMesh> pieces, string? directory)
    {
        _pieces = pieces.Select(m => Build(m, directory)).ToList();
        Clear();
    }

    /// <summary>今のピースを、計算した位置と向きに並べる。</summary>
    public void Place(IEnumerable<RepeaterPlacement> placements)
    {
        Visual.Children.Clear();
        if (_pieces.Count == 0) return;
        foreach (var p in placements)
        {
            if (p.PieceIndex < 0 || p.PieceIndex >= _pieces.Count) continue;
            Visual.Children.Add(new ModelVisual3D { Content = _pieces[p.PieceIndex], Transform = ToWpfTransform(p) });
        }
    }

    public void Clear() => Visual.Children.Clear();

    /// <summary>BVE 空間の「Y 軸で回してから動かす」を、Z を反転した WPF 空間の行列にする（行ベクトル）。</summary>
    private static Transform3D ToWpfTransform(RepeaterPlacement p)
    {
        var m = MeshOps.BuildTransform(p.Position, new System.Numerics.Vector3(0, p.YawDegrees, 0),
            System.Numerics.Vector3.One, System.Numerics.Vector3.Zero);
        var flip = System.Numerics.Matrix4x4.CreateScale(1, 1, -1);
        var w = flip * m * flip;
        var transform = new MatrixTransform3D(new Matrix3D(
            w.M11, w.M12, w.M13, w.M14,
            w.M21, w.M22, w.M23, w.M24,
            w.M31, w.M32, w.M33, w.M34,
            w.M41, w.M42, w.M43, w.M44));
        transform.Freeze();
        return transform;
    }

    /// <summary>マテリアルごとに三角形をまとめる（レンダラと同じく Z を反転し、頂点の並びも逆にする）。</summary>
    private Model3DGroup Build(XMesh mesh, string? directory)
    {
        var group = new Model3DGroup();
        var byMaterial = new Dictionary<int, MeshGeometry3D>();
        foreach (var f in mesh.Faces)
        {
            if (f.Indices.Length < 3) continue;
            if (!byMaterial.TryGetValue(f.Material, out var geo)) byMaterial[f.Material] = geo = new MeshGeometry3D();
            foreach (var (a, b, c) in MeshMath.Fan(f.Indices))
                foreach (var v in (int[])[a, c, b])
                {
                    if (v < 0 || v >= mesh.Positions.Count) continue;
                    geo.TriangleIndices.Add(geo.Positions.Count);
                    geo.Positions.Add(SceneRenderer.ToWpf(mesh.Positions[v]));
                    if (mesh.HasUV) geo.TextureCoordinates.Add(new System.Windows.Point(mesh.TexCoords[v].X, mesh.TexCoords[v].Y));
                }
        }
        foreach (var (mi, geo) in byMaterial)
        {
            geo.Freeze();
            var mat = mi >= 0 && mi < mesh.Materials.Count ? mesh.Materials[mi] : new XMaterial();
            var material = MakeMaterial(mat, mesh.HasUV, directory);
            group.Children.Add(new GeometryModel3D(geo, material) { BackMaterial = material });
        }
        group.Freeze();
        return group;
    }

    private Material MakeMaterial(XMaterial mat, bool hasUv, string? directory)
    {
        var c = mat.FaceColor;
        BitmapSource? tex = null;
        if (hasUv && !string.IsNullOrWhiteSpace(mat.Texture))
        {
            var rel = mat.Texture.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            var full = Path.IsPathRooted(rel) ? rel : directory != null ? Path.GetFullPath(Path.Combine(directory, rel)) : null;
            if (full != null) tex = textures.Get(full, out _);
        }
        Brush brush = tex != null
            ? new ImageBrush(tex)
            {
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new System.Windows.Rect(0, 0, 1, 1),
                TileMode = TileMode.Tile,
            }
            : new SolidColorBrush(Color.FromArgb(
                (byte)Math.Clamp(c.W * 255, 0, 255),
                (byte)Math.Clamp(c.X * 255, 0, 255), (byte)Math.Clamp(c.Y * 255, 0, 255), (byte)Math.Clamp(c.Z * 255, 0, 255)));
        brush.Freeze();
        var material = new DiffuseMaterial(brush);
        material.Freeze();
        return material;
    }
}
