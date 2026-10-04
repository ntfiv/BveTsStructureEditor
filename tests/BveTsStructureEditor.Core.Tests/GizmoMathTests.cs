using System.Numerics;
using BveTsStructureEditor.Core.Editing;
using BveTsStructureEditor.Core.Model;

namespace BveTsStructureEditor.Core.Tests;

public class GizmoMathTests
{
    [Fact]
    public void ClosestOnAxis_Finds_Crossing_Point()
    {
        // X 軸と、x=3 の位置で真上から下へ向かうレイ
        var t = GizmoMath.ClosestOnAxis(Vector3.Zero, Vector3.UnitX, new Vector3(3, 10, 0), -Vector3.UnitY);
        Assert.NotNull(t);
        Assert.Equal(3f, t!.Value, 4);
    }

    [Fact]
    public void ClosestOnAxis_Returns_Null_When_Parallel()
    {
        Assert.Null(GizmoMath.ClosestOnAxis(Vector3.Zero, Vector3.UnitZ, new Vector3(0, 1, -10), Vector3.UnitZ));
    }

    [Fact]
    public void RayPlane_Hits_Ground()
    {
        var hit = GizmoMath.RayPlane(Vector3.Zero, Vector3.UnitY, new Vector3(1, 5, 2), Vector3.Normalize(new Vector3(0, -1, 1)));
        Assert.NotNull(hit);
        Assert.True(Vector3.Distance(new Vector3(1, 0, 7), hit!.Value) < 1e-4f);
        Assert.Null(GizmoMath.RayPlane(Vector3.Zero, Vector3.UnitY, new Vector3(0, 5, 0), Vector3.UnitY)); // 後ろ向き
    }

    [Fact]
    public void Snap_And_Wrap()
    {
        Assert.Equal(0.3f, GizmoMath.Snap(0.26f, 0.1f), 4);
        Assert.Equal(new Vector3(1, -0.5f, 0), GizmoMath.Snap(new Vector3(1.1f, -0.6f, 0.2f), 0.5f));
        Assert.Equal(-MathF.PI / 2, GizmoMath.WrapAngle(MathF.PI * 1.5f), 4);
    }

    [Fact]
    public void ScaleFactor_Is_Distance_Ratio_With_Snap_And_Lower_Limit()
    {
        Assert.Equal(1.5f, GizmoMath.ScaleFactor(3, 2), 4);
        Assert.Equal(1.2f, GizmoMath.ScaleFactor(2.45f, 2, snap: 0.1f), 4);
        Assert.Equal(GizmoMath.MinScale, GizmoMath.ScaleFactor(-1, 2));   // 中心を越えても裏返らない
        Assert.Equal(GizmoMath.MinScale, GizmoMath.ScaleFactor(0.04f, 2, snap: 0.1f));
        Assert.Equal(1f, GizmoMath.ScaleFactor(5, 0));                   // 開始が中心なら変えない
    }

    [Fact]
    public void Scaling_Keeps_Pivot_And_Stretches_One_Axis()
    {
        var pivot = new Vector3(1, 0, 2);
        var m = GizmoMath.Scaling(new Vector3(2, 1, 1), pivot);
        Assert.Equal(pivot, Vector3.Transform(pivot, m));
        Assert.Equal(new Vector3(3, 5, 2), Vector3.Transform(new Vector3(2, 5, 2), m));
    }

    [Fact]
    public void Rotation_Around_Pivot_Keeps_Pivot()
    {
        var pivot = new Vector3(2, 0, 3);
        var m = GizmoMath.Rotation(Vector3.UnitY, MathF.PI / 2, pivot);
        Assert.True(Vector3.Distance(pivot, Vector3.Transform(pivot, m)) < 1e-5f);
        var p = Vector3.Transform(pivot + Vector3.UnitX, m);
        Assert.True(MathF.Abs(Vector3.Distance(p, pivot) - 1) < 1e-5f);
    }

    [Fact]
    public void Restore_Then_Retransform_Does_Not_Accumulate()
    {
        var mesh = Primitives.Box(1, 1, 1);
        MeshOps.RecomputeNormals(Scene(mesh), new Selection(), 30);
        var start = mesh.Clone();
        var scene = Scene(mesh);
        for (int i = 1; i <= 5; i++)
        {
            MeshCopy.RestoreGeometry(mesh, start);
            MeshOps.Transform(scene, new Selection(), Matrix4x4.CreateTranslation(i, 0, 0));
        }
        Assert.Equal(start.Positions[0] + new Vector3(5, 0, 0), mesh.Positions[0]);
        Assert.Equal(start.Normals, mesh.Normals);
    }

    private static XScene Scene(XMesh m)
    {
        var s = new XScene();
        s.Meshes.Add(m);
        return s;
    }
}
