using System.Numerics;
using BveTsStructureEditor.Core.Editing;
using BveTsStructureEditor.Core.Model;

namespace BveTsStructureEditor.Core.Tests;

public class ProfileOpsTests
{
    private static readonly Vector2[] LShape = [new(0, 0), new(2, 0), new(2, 1), new(1, 1), new(1, 2), new(0, 2)];

    /// <summary>外向きなら正になる符号付き体積。</summary>
    private static float Volume(XMesh mesh)
    {
        float v = 0;
        foreach (var f in mesh.Faces)
            foreach (var (a, b, c) in MeshMath.Fan(f.Indices))
                v += Vector3.Dot(mesh.Positions[a], Vector3.Cross(mesh.Positions[b] - mesh.Positions[a], mesh.Positions[c] - mesh.Positions[a])) / 6;
        return v;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Triangulate_ConcaveL_CoversArea(bool clockwise)
    {
        var poly = clockwise ? LShape.Reverse().ToArray() : LShape;
        var tris = ProfileOps.Triangulate(poly);
        Assert.NotNull(tris);
        Assert.Equal(4, tris!.Count);
        float area = tris.Sum(t => MathF.Abs(ProfileOps.SignedArea([poly[t.A], poly[t.B], poly[t.C]])));
        Assert.Equal(3f, area, 4);
        // 回り方は入力と同じ
        foreach (var t in tris)
            Assert.Equal(MathF.Sign(ProfileOps.SignedArea(poly)), MathF.Sign(ProfileOps.SignedArea([poly[t.A], poly[t.B], poly[t.C]])));
    }

    [Fact]
    public void Triangulate_SelfIntersecting_ReturnsNullOrPartial()
    {
        Vector2[] bowtie = [new(0, 0), new(1, 1), new(1, 0), new(0, 1)];
        var tris = ProfileOps.Triangulate(bowtie);
        Assert.True(tris == null || tris.Count <= 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExtrudePolygon_LOnGround_ClosedOutward(bool clockwise)
    {
        var pts = (clockwise ? LShape.Reverse() : LShape).Select(p => new Vector3(p.X, 0, p.Y)).ToList();
        var mesh = ProfileOps.ExtrudePolygon(pts, new Vector3(0, 3, 0));
        MeshAssert.ClosedAndConsistent(mesh);
        Assert.Equal(9f, Volume(mesh), 3);
        Assert.Equal(3f, mesh.Positions.Max(p => p.Y), 4);
    }

    [Fact]
    public void ExtrudePolygon_FrontProfileAlongZ()
    {
        var pts = LShape.Select(p => new Vector3(p.X, p.Y, 0)).ToList();
        var mesh = ProfileOps.ExtrudePolygon(pts, new Vector3(0, 0, 10));
        MeshAssert.ClosedAndConsistent(mesh);
        Assert.Equal(30f, Volume(mesh), 2);
    }

    [Fact]
    public void Lathe_Cylinder_ClosedOutward()
    {
        Vector2[] profile = [new(0, 0), new(1, 0), new(1, 2), new(0, 2)];
        var mesh = ProfileOps.Lathe(profile, 16);
        MeshAssert.ClosedAndConsistent(mesh);
        float expected = 16 / 2f * MathF.Sin(2 * MathF.PI / 16) * 2; // 正 16 角柱
        Assert.Equal(expected, Volume(mesh), 2);

        var reversed = ProfileOps.Lathe(profile.Reverse().ToArray(), 16);
        Assert.Equal(expected, Volume(reversed), 2);
    }
}
