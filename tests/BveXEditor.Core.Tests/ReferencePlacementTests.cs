using System.Numerics;
using BveXEditor.Core.Editing;

namespace BveXEditor.Core.Tests;

public class ReferencePlacementTests
{
    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"{expected} != {actual}");

    [Fact]
    public void Ground_Image_Top_Points_Forward_And_Right_Is_Plus_X()
    {
        var c = ReferencePlacement.Corners(ReferencePlane.Ground, Vector3.Zero, 4, 2, 0);
        Near(new Vector3(-2, 0, 1), c[0]); // 左上 = 奥の左
        Near(new Vector3(2, 0, 1), c[1]);
        Near(new Vector3(2, 0, -1), c[2]);
        Near(new Vector3(-2, 0, -1), c[3]);
    }

    [Fact]
    public void Front_And_Side_Stand_Up()
    {
        var f = ReferencePlacement.Corners(ReferencePlane.Front, new Vector3(0, 1, 0), 4, 2, 0);
        Near(new Vector3(-2, 2, 0), f[0]);
        Near(new Vector3(2, 0, 0), f[2]);

        // 側面図は右から見るので、画像の右が進行方向 (+Z)
        var s = ReferencePlacement.Corners(ReferencePlane.Side, new Vector3(0, 1, 0), 4, 2, 0);
        Near(new Vector3(0, 2, -2), s[0]);
        Near(new Vector3(0, 0, 2), s[2]);
    }

    [Fact]
    public void Rotation_Turns_Image_Within_Its_Plane()
    {
        var c = ReferencePlacement.Corners(ReferencePlane.Ground, Vector3.Zero, 4, 2, 90);
        foreach (var p in c) Assert.Equal(0f, p.Y, 4);
        // 90° 回すと画像の右が +Z に向く
        var (right, _) = ReferencePlacement.Axes(ReferencePlane.Ground, 90);
        Near(Vector3.UnitZ, right);
    }

    [Fact]
    public void Two_Point_Scaling_Keeps_First_Point_And_Matches_Distance()
    {
        var center = new Vector3(10, 0, 5);
        var p1 = new Vector3(8, 0, 5);
        var p2 = new Vector3(9, 0, 5);  // 図面上では 1 m
        var (newCenter, newWidth) = ReferencePlacement.ScaleByTwoPoints(center, 20, p1, p2, 25); // 実際は 25 m
        Assert.Equal(500f, newWidth, 3);
        Near(new Vector3(8 + 2 * 25, 0, 5), newCenter);
        // 1 点目は動かない: 1 点目から見た中心の向きが同じで、距離が 25 倍
        Near(Vector3.Normalize(center - p1), Vector3.Normalize(newCenter - p1));
        Assert.Throws<ArgumentException>(() => ReferencePlacement.ScaleByTwoPoints(center, 20, p1, p1, 25));
    }
}
