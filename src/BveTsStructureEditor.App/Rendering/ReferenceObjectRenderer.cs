using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using BveTsStructureEditor.App.Document;
using BveTsStructureEditor.Core.Model;

namespace BveTsStructureEditor.App.Rendering;

/// <summary>
/// 参考オブジェクトを半透明で描く。ピック用の対応表に入れないので、クリックや範囲選択には引っかからない。
/// 読み込んだモデルと三角形はファイルごとに覚えておき、位置は変換だけ、不透明度はマテリアルだけ作り直す。
/// </summary>
public sealed class ReferenceObjectRenderer(TextureCache textures)
{
    private sealed record Part(MeshGeometry3D Geometry, XMaterial Material, string? TexturePath);
    private sealed record Loaded(DateTime Stamp, List<Part>? Parts, int Faces, string Format, string? Error);

    private readonly Dictionary<string, Loaded> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ModelVisual3D Visual { get; } = new();

    /// <summary>モデルを読む処理（形式の判定はドキュメント側に任せる）。</summary>
    public Func<string, (XScene Scene, string Format)>? Loader { get; set; }

    public void Rebuild(IEnumerable<ReferenceObject> items)
    {
        Visual.Children.Clear();
        foreach (var item in items)
        {
            if (!item.Visible || Get(item.Path) is not { Parts: { } parts }) continue;
            var group = new Model3DGroup();
            foreach (var part in parts)
            {
                var material = MakeMaterial(part, Math.Clamp(item.Opacity, 0.05f, 1f));
                group.Children.Add(new GeometryModel3D(part.Geometry, material) { BackMaterial = material });
            }
            group.Freeze();
            Visual.Children.Add(new ModelVisual3D { Content = group, Transform = ToWpfTransform(item) });
        }
    }

    /// <summary>
    /// BVE 空間での回転・移動（変形欄と同じ行列）を、Z を反転した WPF 空間の行列にする。
    /// 行ベクトルなので v_wpf' = v_wpf · S · M · S（S は Z 反転）。
    /// </summary>
    private static Transform3D ToWpfTransform(ReferenceObject item)
    {
        var m = Core.Editing.MeshOps.BuildTransform(item.Offset, item.Rotation, System.Numerics.Vector3.One, System.Numerics.Vector3.Zero);
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

    /// <summary>ファイルの状態（面数・形式・読めなかった理由）。</summary>
    public (int Faces, string Format, string? Error) Describe(string path) =>
        Get(path) is { } l ? (l.Faces, l.Format, l.Error) : (0, "", "見つかりません");

    /// <summary>ファイルを読み直す（外で書き換えたとき）。</summary>
    public void Forget(string path) => _cache.Remove(Path.GetFullPath(path));

    private Loaded? Get(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) return new Loaded(default, null, 0, "", "ファイルが見つかりません");
        var stamp = File.GetLastWriteTimeUtc(full);
        if (_cache.TryGetValue(full, out var hit) && hit.Stamp == stamp) return hit;

        Loaded loaded;
        try
        {
            var (scene, format) = Loader?.Invoke(full) ?? throw new InvalidOperationException("読み込みの準備ができていません");
            loaded = new Loaded(stamp, Build(scene, Path.GetDirectoryName(full)!), scene.TotalFaces, format, null);
        }
        catch (Exception ex) when (ex is Core.Format.XFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            loaded = new Loaded(stamp, null, 0, "", ex.Message);
        }
        _cache[full] = loaded;
        return loaded;
    }

    /// <summary>マテリアルごとに三角形をまとめる（レンダラと同じく Z を反転し、頂点の並びも逆にする）。</summary>
    private static List<Part> Build(XScene scene, string directory)
    {
        var parts = new List<Part>();
        foreach (var mesh in scene.Meshes)
        {
            var byMaterial = new Dictionary<int, MeshGeometry3D>();
            foreach (var f in mesh.Faces)
            {
                if (f.Indices.Length < 3) continue;
                if (!byMaterial.TryGetValue(f.Material, out var geo)) byMaterial[f.Material] = geo = new MeshGeometry3D();
                foreach (var (a, b, c) in MeshMath.Fan(f.Indices))
                {
                    foreach (var v in (int[])[a, c, b])
                    {
                        if (v < 0 || v >= mesh.Positions.Count) continue;
                        geo.TriangleIndices.Add(geo.Positions.Count);
                        geo.Positions.Add(SceneRenderer.ToWpf(mesh.Positions[v]));
                        if (mesh.HasUV) geo.TextureCoordinates.Add(new System.Windows.Point(mesh.TexCoords[v].X, mesh.TexCoords[v].Y));
                    }
                }
            }
            foreach (var (mi, geo) in byMaterial)
            {
                geo.Freeze();
                var mat = mi >= 0 && mi < mesh.Materials.Count ? mesh.Materials[mi] : new XMaterial();
                string? tex = null;
                if (mesh.HasUV && !string.IsNullOrWhiteSpace(mat.Texture))
                {
                    var rel = mat.Texture.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                    tex = Path.IsPathRooted(rel) ? rel : Path.GetFullPath(Path.Combine(directory, rel));
                }
                parts.Add(new Part(geo, mat, tex));
            }
        }
        return parts;
    }

    private Material MakeMaterial(Part part, float opacity)
    {
        var c = part.Material.FaceColor;
        BitmapSource? tex = part.TexturePath != null ? textures.Get(part.TexturePath, out _) : null;
        Brush brush = tex != null
            ? new ImageBrush(tex)
            {
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new System.Windows.Rect(0, 0, 1, 1),
                TileMode = TileMode.Tile,
                Opacity = opacity * c.W,
            }
            : new SolidColorBrush(Color.FromArgb(
                (byte)Math.Clamp(opacity * c.W * 255, 0, 255),
                (byte)Math.Clamp(c.X * 255, 0, 255), (byte)Math.Clamp(c.Y * 255, 0, 255), (byte)Math.Clamp(c.Z * 255, 0, 255)));
        brush.Freeze();
        var material = new DiffuseMaterial(brush);
        material.Freeze();
        return material;
    }
}
