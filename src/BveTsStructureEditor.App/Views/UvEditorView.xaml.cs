using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using BveTsStructureEditor.App.Document;
using BveTsStructureEditor.Core.Editing;

namespace BveTsStructureEditor.App.Views;

public partial class UvEditorView : UserControl
{
    private IEditorHost? _host;
    private bool _updating;
    private int _lastMesh = -1;

    public UvEditorView()
    {
        InitializeComponent();
    }

    public void Attach(IEditorHost host)
    {
        _host = host;
        Canvas.Attach(host);
    }

    /// <summary>ドキュメントが変わったときに呼ぶ。</summary>
    public void Refresh(ChangeKind kind)
    {
        if (_host == null) return;
        var doc = _host.Document;
        var mesh = doc.ActiveMesh < doc.Scene.Meshes.Count ? doc.Scene.Meshes[doc.ActiveMesh] : null;

        _updating = true;
        try
        {
            if (kind.HasFlag(ChangeKind.Materials) || kind.HasFlag(ChangeKind.Structure) || kind.HasFlag(ChangeKind.Selection) || _lastMesh != doc.ActiveMesh)
            {
                MaterialBox.Items.Clear();
                if (mesh != null)
                    for (int i = 0; i < mesh.Materials.Count; i++)
                        MaterialBox.Items.Add($"{i}: {mesh.Materials[i].Describe()}");

                // 選択面のマテリアルに自動で合わせる
                int want = Canvas.Material;
                if (kind.HasFlag(ChangeKind.Selection) && mesh != null)
                {
                    var faces = SelectionQuery.Faces(doc.Scene, doc.Selection, emptyMeansAll: false).GetValueOrDefault(doc.ActiveMesh);
                    if (faces is { Count: > 0 }) want = mesh.Faces[faces.First()].Material;
                }
                if (_lastMesh != doc.ActiveMesh && (kind & ChangeKind.Selection) == 0) want = 0;
                if (mesh != null && MaterialBox.Items.Count > 0)
                    MaterialBox.SelectedIndex = Math.Clamp(want, 0, MaterialBox.Items.Count - 1);
                Canvas.Material = Math.Max(0, MaterialBox.SelectedIndex);
                _lastMesh = doc.ActiveMesh;
            }
            UpdateTexture();
        }
        finally { _updating = false; }
        Canvas.InvalidateVisual();
    }

    private void UpdateTexture()
    {
        var doc = _host!.Document;
        var mesh = doc.ActiveMesh < doc.Scene.Meshes.Count ? doc.Scene.Meshes[doc.ActiveMesh] : null;
        var mat = mesh != null && Canvas.Material < mesh.Materials.Count ? mesh.Materials[Canvas.Material] : null;
        if (mat?.Texture is { Length: > 0 } tex)
        {
            var img = _host.Textures.Get(doc.ResolveTexture(tex), out var err);
            Canvas.Texture = img;
            TextureInfo.Text = img != null ? $"{tex}  ({img.PixelWidth}×{img.PixelHeight})" : err ?? "";
        }
        else
        {
            Canvas.Texture = null;
            TextureInfo.Text = mesh == null ? "" : "テクスチャなし";
        }
    }

    private void OnMaterialChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || MaterialBox.SelectedIndex < 0) return;
        Canvas.Material = MaterialBox.SelectedIndex;
        UpdateTexture();
        Canvas.InvalidateVisual();
    }

    private void OnFit(object sender, RoutedEventArgs e) => Canvas.FitView();

    private void RunUv(string label, Func<Vector2, Matrix3x2> build)
    {
        var doc = _host!.Document;
        if (doc.Selection.IsEmpty) { _host.SetStatus("UV を変形する面・頂点を選んでください"); return; }
        var center = UvOps.Center(doc.Scene, doc.Selection);
        _host.Run(label, s => UvOps.Transform(s, doc.Selection, build(center)), ChangeKind.Geometry);
    }

    private void OnFlipU(object sender, RoutedEventArgs e) =>
        RunUv("UV 左右反転", c => Matrix3x2.CreateScale(-1, 1, c));

    private void OnFlipV(object sender, RoutedEventArgs e) =>
        RunUv("UV 上下反転", c => Matrix3x2.CreateScale(1, -1, c));

    private void OnRotate(object sender, RoutedEventArgs e) =>
        RunUv("UV 90° 回転", c => Matrix3x2.CreateRotation(MathF.PI / 2, c));

    private void OnFitUnit(object sender, RoutedEventArgs e)
    {
        var doc = _host!.Document;
        var verts = SelectionQuery.Vertices(doc.Scene, doc.Selection, emptyMeansAll: false);
        var uvs = verts.SelectMany(kv => kv.Value.Where(_ => doc.Scene.Meshes[kv.Key].HasUV).Select(v => doc.Scene.Meshes[kv.Key].TexCoords[v])).ToList();
        if (uvs.Count == 0) { _host.SetStatus("UV を合わせる面・頂点を選んでください"); return; }
        var min = uvs.Aggregate(new Vector2(float.MaxValue), Vector2.Min);
        var max = uvs.Aggregate(new Vector2(float.MinValue), Vector2.Max);
        var size = Vector2.Max(max - min, new Vector2(1e-6f));
        RunUv("UV を 0〜1 に合わせる", _ => Matrix3x2.CreateTranslation(-min) * Matrix3x2.CreateScale(1 / size.X, 1 / size.Y));
    }

    private void OnDetach(object sender, RoutedEventArgs e)
    {
        var doc = _host!.Document;
        _host.Run("切り離す", s => MeshOps.Detach(s, doc.Selection));
    }

    private void OnNumeric(object sender, RoutedEventArgs e)
    {
        if (!TryF(MoveU.Text, out var u) || !TryF(MoveV.Text, out var v) || !TryF(ScaleUV.Text, out var sc))
        {
            _host!.SetStatus("数値が読めません");
            return;
        }
        RunUv("UV を数値で変形", c => Matrix3x2.CreateScale(sc, c) * Matrix3x2.CreateTranslation(u, v));
    }

    private void OnProject(object sender, RoutedEventArgs e)
    {
        var doc = _host!.Document;
        if (doc.Selection.IsEmpty) { _host.SetStatus("投影する面を選んでください（面選択・オブジェクト選択）"); return; }
        var axis = (UvProjectAxis)ProjectAxis.SelectedIndex;
        var fit = (UvFit)ProjectFit.SelectedIndex;
        if (!TryF(MetersPerRepeat.Text, out var mpr) || mpr <= 0) mpr = 1;
        _host.Run("UV 投影", s => UvOps.Project(s, doc.Selection, axis, fit, mpr));
    }

    private static bool TryF(string s, out float v) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}
