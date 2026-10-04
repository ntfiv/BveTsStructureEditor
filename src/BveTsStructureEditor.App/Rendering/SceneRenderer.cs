using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using BveTsStructureEditor.App.Document;
using BveTsStructureEditor.Core.Editing;
using BveTsStructureEditor.Core.Model;
using HelixToolkit.Wpf;

namespace BveTsStructureEditor.App.Rendering;

public sealed class ViewOptions
{
    public bool ShowTextures { get; set; } = true;
    public bool ShowWireframe { get; set; }
    /// <summary>裏面を赤で塗る。BVE は裏面を描かないので、裏返った面を見つけるのに使う。</summary>
    public bool ShowBackFaces { get; set; } = true;
    public HashSet<int> HiddenMeshes { get; } = [];
    /// <summary>隠しているマテリアル（メッシュ番号, マテリアル番号）。</summary>
    public HashSet<(int Mesh, int Material)> HiddenMaterials { get; } = [];

    private readonly List<int> _materialCounts = [];

    public bool IsFaceVisible(int mesh, XFace face) =>
        !HiddenMeshes.Contains(mesh) && !HiddenMaterials.Contains((mesh, face.Material));

    public IEnumerable<int> VisibleFaces(int meshIndex, XMesh mesh)
    {
        if (HiddenMeshes.Contains(meshIndex)) yield break;
        for (int i = 0; i < mesh.Faces.Count; i++)
            if (!HiddenMaterials.Contains((meshIndex, mesh.Faces[i].Material))) yield return i;
    }

    /// <summary>見えている面が使う頂点。隠しているマテリアルが無いメッシュは null（全部見えている）。</summary>
    public HashSet<int>? VisibleVertices(int meshIndex, XMesh mesh)
    {
        if (HiddenMeshes.Contains(meshIndex)) return [];
        if (!HiddenMaterials.Any(h => h.Mesh == meshIndex)) return null;
        var set = new HashSet<int>();
        foreach (var fi in VisibleFaces(meshIndex, mesh))
            foreach (var v in mesh.Faces[fi].Indices) set.Add(v);
        return set;
    }

    /// <summary>
    /// マテリアルの数が変わったメッシュ（削除・統合・分割など）は番号がずれ、別のマテリアルを
    /// 隠してしまうので、そのメッシュの非表示を解除する。メッシュの数が変わったら全部解除。
    /// </summary>
    public void Prune(XScene scene)
    {
        if (_materialCounts.Count != scene.Meshes.Count)
        {
            if (_materialCounts.Count > 0) HiddenMaterials.Clear();
        }
        else
        {
            for (int m = 0; m < scene.Meshes.Count; m++)
                if (_materialCounts[m] != scene.Meshes[m].Materials.Count)
                    HiddenMaterials.RemoveWhere(h => h.Mesh == m);
        }
        _materialCounts.Clear();
        _materialCounts.AddRange(scene.Meshes.Select(m => m.Materials.Count));
    }
}

/// <summary>
/// シーンを WPF 3D の Visual にする。
///
/// BVE 空間は左手系なので、WPF（右手系）には Z を反転して渡す。カメラも一緒に反転した
/// 位置から見るので、画面上の巡回の向きは変わらない。DirectX は画面上で時計回りが表、
/// WPF は反時計回りが表なので、三角形の頂点順だけ逆にして渡す。
/// </summary>
public sealed class SceneRenderer
{
    public sealed record PickInfo(int Mesh, int[] TriangleToFace);

    private static readonly Color SelectColor = Color.FromRgb(255, 150, 30);

    private readonly ModelVisual3D _sceneVisual = new();
    private readonly ModelVisual3D _overlayVisual = new();
    private readonly LinesVisual3D _wire = new() { Color = Color.FromArgb(200, 20, 20, 20), Thickness = 1, DepthOffset = 1e-4 };
    private readonly LinesVisual3D _selEdges = new() { Color = SelectColor, Thickness = 2, DepthOffset = 2e-4 };
    // 頂点は面の上にあるので、奥行きを少し手前にずらさないと面に埋もれて見えなくなる
    private readonly PointsVisual3D _points = new() { Color = Color.FromRgb(40, 60, 120), Size = 5, DepthOffset = 1e-3 };
    private readonly TextureCache _textures;

    public ModelVisual3D Root { get; } = new();
    public Dictionary<GeometryModel3D, PickInfo> Picks { get; } = [];
    public List<string> TextureErrors { get; } = [];

    public SceneRenderer(TextureCache textures)
    {
        _textures = textures;
        Root.Children.Add(_sceneVisual);
        Root.Children.Add(_overlayVisual);
        Root.Children.Add(_wire);
        Root.Children.Add(_selEdges);
        Root.Children.Add(_points);
    }

    public static Point3D ToWpf(Vector3 v) => new(v.X, v.Y, -v.Z);
    public static Vector3D ToWpfDir(Vector3 v) => new(v.X, v.Y, -v.Z);

    public void RebuildScene(EditorDocument doc, ViewOptions opt)
    {
        Picks.Clear();
        TextureErrors.Clear();
        var group = new Model3DGroup();
        var back = opt.ShowBackFaces ? MakeBack() : null;

        for (int mi = 0; mi < doc.Scene.Meshes.Count; mi++)
        {
            if (opt.HiddenMeshes.Contains(mi)) continue;
            var mesh = doc.Scene.Meshes[mi];
            if (mesh.Faces.Count == 0) continue;

            List<Vector3> normals;
            IReadOnlyList<int[]> faceNormals;
            if (mesh.HasExplicitNormals)
            {
                normals = mesh.Normals;
                faceNormals = mesh.Faces.Select(f => f.NormalIndices!).ToList();
            }
            else
            {
                var (n, fn) = MeshMath.ComputeNormals(mesh, doc.WriteOptions.SmoothAngle);
                normals = n;
                faceNormals = fn;
            }

            var byMaterial = new Dictionary<int, (MeshGeometry3D Geo, List<int> TriToFace)>();
            for (int fi = 0; fi < mesh.Faces.Count; fi++)
            {
                var f = mesh.Faces[fi];
                if (!opt.IsFaceVisible(mi, f)) continue;
                if (!byMaterial.TryGetValue(f.Material, out var bucket))
                    byMaterial[f.Material] = bucket = (new MeshGeometry3D(), []);
                var fn = faceNormals[fi];
                for (int k = 1; k + 1 < f.Indices.Length; k++)
                {
                    foreach (var c in (ReadOnlySpan<int>)[0, k + 1, k])
                    {
                        int v = f.Indices[c];
                        bucket.Geo.Positions.Add(ToWpf(mesh.Positions[v]));
                        bucket.Geo.Normals.Add(ToWpfDir(normals[fn[c]]));
                        if (mesh.HasUV) bucket.Geo.TextureCoordinates.Add(new System.Windows.Point(mesh.TexCoords[v].X, mesh.TexCoords[v].Y));
                    }
                    bucket.TriToFace.Add(fi);
                }
            }

            foreach (var (matIndex, (geo, triToFace)) in byMaterial)
            {
                var xm = matIndex < mesh.Materials.Count ? mesh.Materials[matIndex] : new XMaterial();
                BitmapSource? tex = null;
                if (opt.ShowTextures && mesh.HasUV && !string.IsNullOrEmpty(xm.Texture))
                {
                    tex = _textures.Get(doc.ResolveTexture(xm.Texture), out var err);
                    if (err != null && !TextureErrors.Contains(err)) TextureErrors.Add(err);
                }
                geo.Freeze();
                var model = new GeometryModel3D(geo, MakeMaterial(xm, tex)) { BackMaterial = back };
                group.Children.Add(model);
                Picks[model] = new PickInfo(mi, triToFace.ToArray());
            }
        }
        _sceneVisual.Content = group;

        var wire = new Point3DCollection();
        if (opt.ShowWireframe)
            for (int mi = 0; mi < doc.Scene.Meshes.Count; mi++)
                AddEdges(wire, doc.Scene.Meshes[mi], opt.VisibleFaces(mi, doc.Scene.Meshes[mi]));
        _wire.Points = wire;
    }

    public void RebuildSelection(EditorDocument doc, ViewOptions opt)
    {
        var scene = doc.Scene;
        var sel = doc.Selection;
        var edges = new Point3DCollection();
        var overlay = new Model3DGroup();
        var points = new Point3DCollection();

        switch (sel.Mode)
        {
            case SelectMode.Object:
                foreach (var m in sel.Items.Select(r => r.Mesh).Distinct())
                    if (m < scene.Meshes.Count)
                        AddEdges(edges, scene.Meshes[m], opt.VisibleFaces(m, scene.Meshes[m]));
                break;

            case SelectMode.Face:
                foreach (var (m, faces) in SelectionQuery.Faces(scene, sel, emptyMeansAll: false))
                {
                    var mesh = scene.Meshes[m];
                    var shown = faces.Where(fi => opt.IsFaceVisible(m, mesh.Faces[fi])).ToList();
                    if (shown.Count == 0) continue;
                    AddEdges(edges, mesh, shown);
                    var geo = new MeshGeometry3D();
                    foreach (var fi in shown)
                    {
                        var f = mesh.Faces[fi];
                        var offset = mesh.FaceNormal(f) * 0.003f;
                        foreach (var (a, b, c) in MeshMath.Fan(f.Indices))
                        {
                            geo.Positions.Add(ToWpf(mesh.Positions[a] + offset));
                            geo.Positions.Add(ToWpf(mesh.Positions[b] + offset));
                            geo.Positions.Add(ToWpf(mesh.Positions[c] + offset));
                        }
                    }
                    geo.Freeze();
                    var mat = new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(110, SelectColor.R, SelectColor.G, SelectColor.B)));
                    overlay.Children.Add(new GeometryModel3D(geo, mat) { BackMaterial = mat });
                }
                break;

            case SelectMode.Vertex:
                for (int m = 0; m < scene.Meshes.Count; m++)
                {
                    var visible = opt.VisibleVertices(m, scene.Meshes[m]);
                    var pos = scene.Meshes[m].Positions;
                    for (int v = 0; v < pos.Count; v++)
                        if (visible == null || visible.Contains(v)) points.Add(ToWpf(pos[v]));
                }
                // 選択中の頂点はモデルやギズモに隠れないよう、ビュー側が最前面の層に描く（ViewportView.UpdateSelectedVertexMarkers）
                // 頂点モードでは形がわかるよう辺も薄く出す
                if (!opt.ShowWireframe)
                    for (int m = 0; m < scene.Meshes.Count; m++)
                        AddEdges(edges, scene.Meshes[m], opt.VisibleFaces(m, scene.Meshes[m]));
                break;
        }

        _selEdges.Color = sel.Mode == SelectMode.Vertex ? Color.FromArgb(160, 30, 30, 30) : SelectColor;
        _selEdges.Thickness = sel.Mode == SelectMode.Vertex ? 1 : 2;
        _selEdges.Points = edges;
        _overlayVisual.Content = overlay;
        _points.Points = points;
    }

    private static void AddEdges(Point3DCollection pts, XMesh mesh, IEnumerable<int> faces)
    {
        var seen = new HashSet<(int, int)>();
        foreach (var fi in faces)
        {
            var idx = mesh.Faces[fi].Indices;
            for (int j = 0; j < idx.Length; j++)
            {
                int a = idx[j], b = idx[(j + 1) % idx.Length];
                if (!seen.Add(a < b ? (a, b) : (b, a))) continue;
                pts.Add(ToWpf(mesh.Positions[a]));
                pts.Add(ToWpf(mesh.Positions[b]));
            }
        }
    }

    private static Color ToColor(Vector3 c, float a = 1) => Color.FromArgb(
        (byte)Math.Clamp(a * 255, 0, 255), (byte)Math.Clamp(c.X * 255, 0, 255),
        (byte)Math.Clamp(c.Y * 255, 0, 255), (byte)Math.Clamp(c.Z * 255, 0, 255));

    private static Material MakeMaterial(XMaterial m, BitmapSource? tex)
    {
        var group = new MaterialGroup();
        var diffuse = new Vector3(m.FaceColor.X, m.FaceColor.Y, m.FaceColor.Z);
        if (tex != null)
        {
            var brush = new ImageBrush(tex)
            {
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new System.Windows.Rect(0, 0, 1, 1),
                TileMode = TileMode.Tile,
                Opacity = Math.Clamp(m.FaceColor.W, 0, 1),
            };
            brush.Freeze();
            // DirectX の固定機能と同じく、拡散色をテクスチャに掛ける
            group.Children.Add(new DiffuseMaterial(brush) { Color = ToColor(diffuse) });
        }
        else
        {
            group.Children.Add(new DiffuseMaterial(new SolidColorBrush(ToColor(diffuse, m.FaceColor.W))));
        }
        if (m.Emissive != Vector3.Zero)
            group.Children.Add(new EmissiveMaterial(new SolidColorBrush(ToColor(m.Emissive))));
        if (m.Power > 0 && m.Specular != Vector3.Zero)
            group.Children.Add(new SpecularMaterial(new SolidColorBrush(ToColor(m.Specular)), m.Power));
        group.Freeze();
        return group;
    }

    private static Material MakeBack()
    {
        var m = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(170, 40, 50)));
        m.Freeze();
        return m;
    }
}
