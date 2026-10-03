using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BveXEditor.App.Document;
using BveXEditor.Core.Editing;
using BveXEditor.Core.Format;
using BveXEditor.Core.Model;

namespace BveXEditor.App.Views;

public partial class EditPanel : UserControl
{
    public sealed record MaterialItem(string Label, Brush Swatch);

    private IEditorHost? _host;
    private bool _updating;

    public EditPanel()
    {
        InitializeComponent();
        UiState.Track(TransformSection, "Edit.Transform");
        UiState.Track(TransformMore, "Edit.TransformMore");
        UiState.Track(FaceSection, "Edit.Face");
        UiState.Track(ShapeSection, "Edit.Shape");
        UiState.Track(VertexSection, "Edit.Vertex");
        UiState.Track(ArraySection, "Edit.Array");
        UiState.Track(MeshSection, "Edit.Mesh");
        UiState.Track(MaterialSection, "Edit.Material");
        UiState.Track(MaterialMore, "Edit.MaterialMore");
    }

    public void Attach(IEditorHost host) => _host = host;

    private EditorDocument Doc => _host!.Document;
    private XMesh? ActiveMesh => Doc.ActiveMesh < Doc.Scene.Meshes.Count ? Doc.Scene.Meshes[Doc.ActiveMesh] : null;

    public float NudgeStepValue => F(NudgeStep.Text, 0.1f);

    public void Refresh(ChangeKind kind)
    {
        if (_host == null) return;
        _updating = true;
        try
        {
            var mesh = ActiveMesh;
            if (kind != ChangeKind.Geometry)
            {
                MeshName.Text = mesh?.Name ?? "";
                int keep = MaterialList.SelectedIndex;
                MaterialList.Items.Clear();
                if (mesh != null)
                    for (int i = 0; i < mesh.Materials.Count; i++)
                    {
                        var m = mesh.Materials[i];
                        var used = mesh.Faces.Count(f => f.Material == i);
                        var c = m.FaceColor;
                        var swatch = new SolidColorBrush(Color.FromScRgb(1, c.X, c.Y, c.Z));
                        swatch.Freeze();
                        var tex = string.IsNullOrEmpty(m.Texture) ? "（テクスチャなし）" : m.Texture;
                        MaterialList.Items.Add(new MaterialItem($"{i}: {tex}  [{used} 面]", swatch));
                    }
                if (kind.HasFlag(ChangeKind.Selection) && mesh != null)
                {
                    var faces = SelectionQuery.Faces(Doc.Scene, Doc.Selection, emptyMeansAll: false).GetValueOrDefault(Doc.ActiveMesh);
                    if (faces is { Count: > 0 }) keep = mesh.Faces[faces.First()].Material;
                }
                MaterialList.SelectedIndex = MaterialList.Items.Count == 0 ? -1 : Math.Clamp(keep, 0, MaterialList.Items.Count - 1);
                FillMaterialEditor();
            }
        }
        finally { _updating = false; }
    }

    // ───────── 共通 ─────────

    private static float F(string s, float fallback) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static string S(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private bool TryFloats(out float[] values, params TextBox[] boxes)
    {
        values = new float[boxes.Length];
        for (int i = 0; i < boxes.Length; i++)
        {
            if (!float.TryParse(boxes[i].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
            {
                _host!.SetStatus($"数値が読めません: \"{boxes[i].Text}\"");
                boxes[i].Focus();
                boxes[i].SelectAll();
                return false;
            }
        }
        return true;
    }

    private void Run(string label, Func<XScene, string> op, ChangeKind kind = ChangeKind.Geometry | ChangeKind.Structure) =>
        _host!.Run(label, op, kind);

    // ───────── 変形 ─────────

    private Vector3 Pivot()
    {
        return PivotBox.SelectedIndex switch
        {
            0 => MeshOps.Center(Doc.Scene, Doc.Selection),
            1 => BottomCenter(),
            _ => Vector3.Zero,
        };

        Vector3 BottomCenter()
        {
            var c = MeshOps.Center(Doc.Scene, Doc.Selection);
            float minY = float.MaxValue;
            foreach (var (m, verts) in SelectionQuery.Vertices(Doc.Scene, Doc.Selection))
                foreach (var v in verts) minY = MathF.Min(minY, Doc.Scene.Meshes[m].Positions[v].Y);
            return minY == float.MaxValue ? c : c with { Y = minY };
        }
    }

    private void OnApplyTransform(object sender, RoutedEventArgs e)
    {
        if (!TryFloats(out var v, TX, TY, TZ, RX, RY, RZ, SX, SY, SZ)) return;
        var matrix = MeshOps.BuildTransform(new(v[0], v[1], v[2]), new(v[3], v[4], v[5]), new(v[6], v[7], v[8]), Pivot());
        if (matrix.IsIdentity) { _host!.SetStatus("変形の値がすべて初期値です"); return; }
        Run("変形", s => Doc.TransformSelection(s, matrix), ChangeKind.Geometry);
    }

    private void OnResetTransform(object sender, RoutedEventArgs e)
    {
        foreach (var t in new[] { TX, TY, TZ, RX, RY, RZ }) t.Text = "0";
        foreach (var t in new[] { SX, SY, SZ }) t.Text = "1";
    }

    private void Mirror(Vector3 scale, string label)
    {
        var pivot = Pivot();
        var m = Matrix4x4.CreateScale(scale, pivot);
        Run(label, s => Doc.TransformSelection(s, m), ChangeKind.Geometry);
    }

    private void OnMirrorX(object sender, RoutedEventArgs e) => Mirror(new Vector3(-1, 1, 1), "X 反転");
    private void OnMirrorZ(object sender, RoutedEventArgs e) => Mirror(new Vector3(1, 1, -1), "Z 反転");

    private void OnAlignBottom(object sender, RoutedEventArgs e) =>
        Run("底面中心を原点へ", s => MeshOps.AlignToOrigin(s, Doc.Selection, MeshOps.OriginAlign.BottomCenter), ChangeKind.Geometry);

    private void OnAlignCenter(object sender, RoutedEventArgs e) =>
        Run("中心を原点へ", s => MeshOps.AlignToOrigin(s, Doc.Selection, MeshOps.OriginAlign.Center), ChangeKind.Geometry);

    // ───────── 面・頂点 ─────────

    private void OnFlip(object sender, RoutedEventArgs e) => Run("面を裏返す", s => MeshOps.FlipFaces(s, Doc.Selection));
    private void OnTriangulate(object sender, RoutedEventArgs e) => Run("三角形に分割", s => MeshOps.Triangulate(s, Doc.Selection));
    private void OnCreateFace(object sender, RoutedEventArgs e) => Run("面を張る", s => MeshOps.CreateFace(s, Doc.Selection));
    private void OnDetach(object sender, RoutedEventArgs e) => Run("切り離す", s => MeshOps.Detach(s, Doc.Selection));

    public void DeleteSelection()
    {
        var sel = Doc.Selection;
        if (sel.IsEmpty) { _host!.SetStatus("削除するものを選んでください"); return; }
        switch (sel.Mode)
        {
            case SelectMode.Vertex: Run("頂点を削除", s => MeshOps.DeleteVertices(s, sel)); break;
            case SelectMode.Face: Run("面を削除", s => MeshOps.DeleteFaces(s, sel)); break;
            default: DeleteMeshes(); break;
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e) => DeleteSelection();

    private void OnExtrude(object sender, RoutedEventArgs e)
    {
        if (!TryFloats(out var v, ExtrudeDist)) return;
        Run("押し出し", s => MeshOps.Extrude(s, Doc.Selection, v[0]));
    }

    private void OnChamfer(object sender, RoutedEventArgs e)
    {
        if (!TryFloats(out var v, ChamferDist, ChamferAngle)) return;
        Run("面取り", s => Chamfer.Apply(s, Doc.Selection, v[0], v[1]));
    }

    private void OnWeld(object sender, RoutedEventArgs e)
    {
        if (!TryFloats(out var v, WeldDist)) return;
        bool uv = WeldUV.IsChecked == true;
        Run("頂点を結合", s => MeshOps.Weld(s, Doc.Selection, v[0], uv));
    }

    private void OnNormals(object sender, RoutedEventArgs e)
    {
        if (!TryFloats(out var v, SmoothAngle)) return;
        Doc.WriteOptions.SmoothAngle = v[0];
        Run("法線を作り直す", s => MeshOps.RecomputeNormals(s, Doc.Selection, v[0]), ChangeKind.Geometry);
    }

    private void OnInset(object sender, RoutedEventArgs e)
    {
        if (!TryFloats(out var v, InsetDist)) return;
        Run("インセット", s => FaceOps.Inset(s, Doc.Selection, v[0]));
    }

    private void OnDoubleSide(object sender, RoutedEventArgs e) => Run("両面化", s => FaceOps.DoubleSide(s, Doc.Selection));

    private void OnSubdivide(object sender, RoutedEventArgs e)
    {
        if (!TryFloats(out var v, SubdivU, SubdivV)) return;
        Run("分割", s => FaceOps.Subdivide(s, Doc.Selection, (int)v[0], (int)v[1]));
    }

    private void OnLoopCut(object sender, RoutedEventArgs e)
    {
        if (!TryFloats(out var v, LoopCuts)) return;
        Run("ループカット", s => FaceOps.LoopCut(s, Doc.Selection, (int)v[0]));
    }

    private void OnArrayLinear(object sender, RoutedEventArgs e)
    {
        if (!TryFloats(out var v, ArrayCount, ArrayDX, ArrayDY, ArrayDZ)) return;
        Run("配列複製", s => FaceOps.ArrayLinear(s, Doc.Selection, (int)v[0], new Vector3(v[1], v[2], v[3])));
    }

    private void OnArrayRadial(object sender, RoutedEventArgs e)
    {
        if (!TryFloats(out var v, RadialCount, RadialAngle, RadialCX, RadialCZ)) return;
        Run("円周に配列複製", s => FaceOps.ArrayRadial(s, Doc.Selection, (int)v[0], v[1], new Vector3(v[2], 0, v[3])));
    }

    private void OnSymmetrize(object sender, RoutedEventArgs e)
    {
        bool keepPositive = SymmetrizeSide.SelectedIndex == 0;
        Run("対称化", s => Symmetry.Symmetrize(s, Doc.Selection, keepPositive));
    }

    // ───────── メッシュ ─────────

    private void OnRename(object sender, RoutedEventArgs e)
    {
        if (ActiveMesh == null) return;
        var name = MeshName.Text.Trim();
        int index = Doc.ActiveMesh;
        Run("名前の変更", s => { s.Meshes[index].Name = name; return $"名前を「{name}」にしました"; }, ChangeKind.Structure);
    }

    private void OnDuplicate(object sender, RoutedEventArgs e)
    {
        var meshes = SelectionQuery.Meshes(Doc.Scene, Doc.Selection, emptyMeansAll: false);
        if (meshes.Count == 0 && ActiveMesh != null) meshes = [Doc.ActiveMesh];
        Run("複製", s =>
        {
            foreach (var m in meshes.OrderDescending())
            {
                var copy = s.Meshes[m].Clone();
                copy.Name += "_copy";
                s.Meshes.Add(copy);
            }
            return $"{meshes.Count} 個のメッシュを複製しました（リストの末尾）";
        });
    }

    private void DeleteMeshes()
    {
        var meshes = SelectionQuery.Meshes(Doc.Scene, Doc.Selection, emptyMeansAll: false);
        if (meshes.Count == 0 && ActiveMesh != null) meshes = [Doc.ActiveMesh];
        if (meshes.Count == 0) return;
        Doc.Selection.Clear();
        Run("メッシュを削除", s =>
        {
            foreach (var m in meshes.OrderDescending()) s.Meshes.RemoveAt(m);
            return $"{meshes.Count} 個のメッシュを削除しました";
        });
    }

    private void OnDeleteMesh(object sender, RoutedEventArgs e) => DeleteMeshes();

    private void OnMerge(object sender, RoutedEventArgs e)
    {
        var meshes = SelectionQuery.Meshes(Doc.Scene, Doc.Selection, emptyMeansAll: false);
        if (meshes.Count < 2) { _host!.SetStatus("オブジェクト選択で 2 つ以上のメッシュを選んでください（Shift+クリック）"); return; }
        Doc.Selection.Clear();
        Run("メッシュを結合", s => MeshOps.MergeMeshes(s, meshes));
    }

    private void OnSeparate(object sender, RoutedEventArgs e) => Run("別メッシュに分離", s => MeshOps.SeparateFaces(s, Doc.Selection));

    private void OnSplitMaterial(object sender, RoutedEventArgs e)
    {
        if (ActiveMesh == null) return;
        int index = Doc.ActiveMesh;
        Doc.Selection.Clear();
        Run("マテリアルごとに分割", s => MeshOps.SplitByMaterial(s, index));
    }

    // ───────── マテリアル ─────────

    private XMaterial? SelectedMaterial =>
        ActiveMesh is { } m && MaterialList.SelectedIndex >= 0 && MaterialList.SelectedIndex < m.Materials.Count
            ? m.Materials[MaterialList.SelectedIndex] : null;

    private void OnMaterialSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating) FillMaterialEditor();
    }

    // ───────── 色・テクスチャの編集ウィンドウ ─────────

    private MaterialEditorWindow? _materialWindow;

    /// <summary>開いている編集ウィンドウの対象を、一覧で選んでいるマテリアルに合わせる。</summary>
    private void FillMaterialEditor() =>
        _materialWindow?.SetTarget(Doc.ActiveMesh, SelectedMaterial == null ? -1 : MaterialList.SelectedIndex);

    private void OpenMaterialEditor()
    {
        if (SelectedMaterial == null) { _host!.SetStatus("編集するマテリアルを一覧で選んでください"); return; }
        if (_materialWindow == null)
        {
            _materialWindow = new MaterialEditorWindow(_host!) { Owner = Window.GetWindow(this) };
            _materialWindow.Closed += (_, _) => _materialWindow = null;
            FillMaterialEditor();
            _materialWindow.Show();
        }
        else
        {
            FillMaterialEditor();
            _materialWindow.Activate();
        }
    }

    private void OnEditMaterial(object sender, RoutedEventArgs e) => OpenMaterialEditor();

    private void OnMaterialDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        e.Handled = true;
        OpenMaterialEditor();
    }
    private void OnAssignMaterial(object sender, RoutedEventArgs e)
    {
        if (SelectedMaterial == null) return;
        int mesh = Doc.ActiveMesh, index = MaterialList.SelectedIndex;
        Run("マテリアルの割り当て", s => MeshOps.AssignMaterial(s, Doc.Selection, mesh, index), ChangeKind.Materials);
    }

    private void OnAddMaterial(object sender, RoutedEventArgs e)
    {
        if (ActiveMesh == null) return;
        int mesh = Doc.ActiveMesh;
        var copy = SelectedMaterial?.Clone() ?? new XMaterial();
        Run("マテリアルの追加", s =>
        {
            s.Meshes[mesh].Materials.Add(copy);
            return $"マテリアル {s.Meshes[mesh].Materials.Count - 1} を追加しました（選んでいたものの複製）";
        }, ChangeKind.Materials);
        MaterialList.SelectedIndex = MaterialList.Items.Count - 1;
    }

    private void OnDeleteMaterial(object sender, RoutedEventArgs e)
    {
        if (SelectedMaterial == null || ActiveMesh!.Materials.Count <= 1) { _host!.SetStatus("最後の 1 つは削除できません"); return; }
        int mesh = Doc.ActiveMesh, index = MaterialList.SelectedIndex;
        Run("マテリアルの削除", s =>
        {
            MeshOps.DeleteMaterial(s.Meshes[mesh], index);
            return $"マテリアル {index} を削除しました（使っていた面は 0 番に）";
        }, ChangeKind.Materials);
    }

    private void OnMergeMaterials(object sender, RoutedEventArgs e)
    {
        if (ActiveMesh == null) return;
        int mesh = Doc.ActiveMesh;
        Run("マテリアルをまとめる", s => $"{MeshOps.MergeDuplicateMaterials(s.Meshes[mesh])} 個まとめました", ChangeKind.Materials);
    }

    private void OnRemoveUnusedMaterials(object sender, RoutedEventArgs e)
    {
        if (ActiveMesh == null) return;
        int mesh = Doc.ActiveMesh;
        Run("未使用マテリアルの削除", s => $"{MeshOps.RemoveUnusedMaterials(s.Meshes[mesh])} 個削除しました", ChangeKind.Materials);
    }
}
