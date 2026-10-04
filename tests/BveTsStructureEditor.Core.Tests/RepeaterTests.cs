using System.Numerics;
using BveTsStructureEditor.Core.Editing;
using BveTsStructureEditor.Core.Format;
using BveTsStructureEditor.Core.Model;

namespace BveTsStructureEditor.Core.Tests;

public class RepeaterSplitTests
{
    /// <summary>Z = 0〜length に伸びる、UV 付きの壁（+X 向きの 1 枚板）。</summary>
    private static XScene Wall(float length = 25f, float height = 3f)
    {
        var mesh = new XMesh { Name = "Wall" };
        mesh.Materials.Add(new XMaterial { Texture = "tex.png" });
        mesh.AddVertex(new Vector3(0, 0, 0), new Vector2(0, 1));
        mesh.AddVertex(new Vector3(0, height, 0), new Vector2(0, 0));
        mesh.AddVertex(new Vector3(0, height, length), new Vector2(1, 0));
        mesh.AddVertex(new Vector3(0, 0, length), new Vector2(1, 1));
        mesh.Faces.Add(new XFace([0, 1, 2, 3], 0));
        var scene = new XScene();
        scene.Meshes.Add(mesh);
        return scene;
    }

    private static XScene BoxAt(float zStart, float zEnd)
    {
        var box = Primitives.Box(1, 1, zEnd - zStart);
        for (int i = 0; i < box.Positions.Count; i++)
            box.Positions[i] += new Vector3(0, 0, (zStart + zEnd) / 2);
        var scene = new XScene();
        scene.Meshes.Add(box);
        return scene;
    }

    [Fact]
    public void EvenDivision_GivesEqualPieces()
    {
        var pieces = RepeaterSplit.Split(Wall(), new Selection(), new RepeaterSplitOptions { Length = 5 });
        Assert.Equal(5, pieces.Count);
        for (int i = 0; i < pieces.Count; i++)
        {
            Assert.Equal(i, pieces[i].Index);
            Assert.Equal(5f, pieces[i].Length, 3);
            // Z=0 起点に揃うので、どのピースもちょうど 0〜5 m（切り口の誤差も残らない）
            Assert.Equal(0f, pieces[i].Mesh.Positions.Min(p => p.Z));
            Assert.Equal(5f, pieces[i].Mesh.Positions.Max(p => p.Z));
            Assert.Single(pieces[i].Mesh.Faces);
        }
    }

    [Fact]
    public void Remainder_StaysInLastPiece_WhenNotSkipped()
    {
        var pieces = RepeaterSplit.Split(Wall(), new Selection(),
            new RepeaterSplitOptions { Length = 7, SkipShortRemainder = false });
        Assert.Equal(4, pieces.Count);
        Assert.Equal([7f, 7f, 7f, 4f], pieces.Select(p => MathF.Round(p.Length, 3)));
    }

    [Fact]
    public void ShortRemainder_IsSkippedByDefault()
    {
        // 25.2 m を 5 m ずつ → 5 ピース。6 番目の 0.2 m は作らない
        var pieces = RepeaterSplit.Split(Wall(25.2f), new Selection(), new RepeaterSplitOptions { Length = 5 });
        Assert.Equal([0, 1, 2, 3, 4], pieces.Select(p => p.Index));
        Assert.All(pieces, p => Assert.Equal(5f, p.Length, 3));
        // 最後のピースも 25 m で切られ、端数の形は入らない
        Assert.Equal(5f, pieces[^1].Mesh.Positions.Max(p => p.Z), 3);
    }

    [Fact]
    public void ShortRemainder_WrapsFromTheStartAtTheLastFullPiece()
    {
        // 端数を飛ばすと周期は 25 m。最後のピースの重ね代には 25〜25.2 m ではなく先頭 0〜0.2 m が続く
        var pieces = RepeaterSplit.Split(Wall(25.2f), new Selection(), new RepeaterSplitOptions { Length = 5, Overlap = 0.2f });
        var last = pieces[^1];
        Assert.Equal(5.2f, last.Mesh.Positions.Max(p => p.Z), 3);
        var us = last.Mesh.TexCoords.Select(t => MathF.Round(t.X, 3)).Distinct().Order().ToList();
        Assert.Contains(0f, us); // 先頭の u=0 が回り込んで入っている
        Assert.DoesNotContain(us, u => u > 25f / 25.2f + 1e-3f);
    }

    [Fact]
    public void ShorterThanOnePiece_GivesNothing()
    {
        Assert.Empty(RepeaterSplit.Split(Wall(4f), new Selection(), new RepeaterSplitOptions { Length = 5 }));
        Assert.Equal(5, RepeaterSplit.FullPieceCount(25.0005f, 5));
        Assert.Equal(4, RepeaterSplit.FullPieceCount(24.99f, 5));
    }

    [Fact]
    public void Lift_MakesNeighboursDiffer_IncludingLastAndFirst()
    {
        foreach (int n in new[] { 2, 3, 5, 6 })
        {
            var lifts = Enumerable.Range(0, n).Select(k => RepeaterSplit.LiftFor(k, n, 0.003f)).ToList();
            for (int k = 0; k < n; k++)
                Assert.NotEqual(lifts[k], lifts[(k + 1) % n]);
            Assert.True(lifts.Max() <= 0.006f + 1e-6f);
        }
        Assert.Equal(0f, RepeaterSplit.LiftFor(0, 1, 0.003f));
    }

    [Fact]
    public void Lift_IsAppliedOnlyWithOverlap()
    {
        var lifted = RepeaterSplit.Split(Wall(), new Selection(), new RepeaterSplitOptions { Length = 5, Overlap = 0.2f, Lift = 0.003f });
        Assert.Equal([0f, 0.003f, 0f, 0.003f, 0.006f], lifted.Select(p => MathF.Round(p.Lift, 4)));
        Assert.Equal(0.003f, lifted[1].Mesh.Positions.Min(p => p.Y), 4);
        Assert.Equal(0f, lifted[0].Mesh.Positions.Min(p => p.Y), 4);

        var flat = RepeaterSplit.Split(Wall(), new Selection(), new RepeaterSplitOptions { Length = 5, Lift = 0.003f });
        Assert.All(flat, p => Assert.Equal(0f, p.Lift));
    }

    [Fact]
    public void CutEdges_InterpolateUv()
    {
        var pieces = RepeaterSplit.Split(Wall(), new Selection(), new RepeaterSplitOptions { Length = 5, AlignToZero = false });
        // 元の UV は Z=0 で u=0、Z=25 で u=1。2 番目のピース (Z 5〜10) の切り口は 0.2 と 0.4
        var mesh = pieces[1].Mesh;
        var us = mesh.TexCoords.Select(t => MathF.Round(t.X, 4)).Distinct().Order().ToList();
        Assert.Equal([0.2f, 0.4f], us);
    }

    [Fact]
    public void AlignToZero_CanBeTurnedOff()
    {
        var pieces = RepeaterSplit.Split(Wall(), new Selection(), new RepeaterSplitOptions { Length = 5, AlignToZero = false });
        Assert.Equal(10f, pieces[2].Mesh.Positions.Min(p => p.Z), 3);
        Assert.Equal(15f, pieces[2].Mesh.Positions.Max(p => p.Z), 3);
    }

    [Fact]
    public void EmptySections_AreDroppedButKeepTheirIndex()
    {
        // 0〜5 m と 15〜20 m にだけ箱がある（間は空）
        var scene = BoxAt(0, 5);
        foreach (var m in BoxAt(15, 20).Meshes) scene.Meshes.Add(m);
        var pieces = RepeaterSplit.Split(scene, new Selection(), new RepeaterSplitOptions { Length = 5 });
        Assert.Equal([0, 3], pieces.Select(p => p.Index));

        var all = RepeaterSplit.Split(scene, new Selection(), new RepeaterSplitOptions { Length = 5, DropEmpty = false });
        Assert.Equal(4, all.Count);
        Assert.Empty(all[1].Mesh.Faces);
    }

    [Fact]
    public void SplitBox_KeepsClosedMeshPerPiece()
    {
        var pieces = RepeaterSplit.Split(BoxAt(0, 20), new Selection(), new RepeaterSplitOptions { Length = 5 });
        Assert.Equal(4, pieces.Count);
        foreach (var piece in pieces)
            Assert.All(piece.Mesh.Faces, f => Assert.True(f.Indices.All(i => i >= 0 && i < piece.Mesh.Positions.Count)));
        // 1×1×20 m の箱の表面積は 4×20 + 2×1。切り口に蓋は作らないので、合計は変わらない
        Assert.Equal(4 * 20f + 2f, pieces.Sum(p => MeshAssert.Area(p.Mesh)), 2);
        // 端以外のピースは側面 4 枚（4 × 1 × 5 m）だけ
        Assert.Equal(20f, MeshAssert.Area(pieces[1].Mesh), 2);
    }

    [Fact]
    public void Overlap_MakesEachPieceLonger_AndKeepsUvContinuous()
    {
        var pieces = RepeaterSplit.Split(Wall(), new Selection(), new RepeaterSplitOptions { Length = 5, Overlap = 0.2f });
        Assert.Equal(5, pieces.Count);
        foreach (var piece in pieces)
        {
            Assert.Equal(5f, piece.Length, 3);        // 繰り返しの間隔は 5 m のまま
            Assert.Equal(5.2f, piece.CutLength, 3);   // 切り出した長さは 5.2 m
            Assert.Equal(5.2f, piece.Mesh.Positions.Max(p => p.Z), 3);
        }
        // 2 番目のピース (5〜10.2 m) の UV は 0.2〜0.408（元は Z=25 で u=1）
        var us = pieces[1].Mesh.TexCoords.Select(t => MathF.Round(t.X, 4)).Distinct().Order().ToList();
        Assert.Equal([0.2f, 0.408f], us);
    }

    [Fact]
    public void Overlap_OnTheLastPiece_WrapsFromTheStart()
    {
        var pieces = RepeaterSplit.Split(Wall(), new Selection(), new RepeaterSplitOptions { Length = 5, Overlap = 0.2f });
        var last = pieces[^1];
        // 20〜25 m のあとに、先頭 0〜0.2 m の形と UV が付く（繰り返しの続きになる）
        Assert.Equal(5.2f, last.Mesh.Positions.Max(p => p.Z));   // 誤差なくちょうど
        Assert.Single(last.Mesh.Materials);                       // 回り込みでマテリアルが増えない
        var us = last.Mesh.TexCoords.Select(t => MathF.Round(t.X, 4)).Distinct().Order().ToList();
        Assert.Equal([0f, 0.008f, 0.8f, 1f], us);
    }

    [Fact]
    public void Overlap_WithoutWrap_LeavesTheLastPieceShort()
    {
        var pieces = RepeaterSplit.Split(Wall(), new Selection(),
            new RepeaterSplitOptions { Length = 5, Overlap = 0.2f, WrapAround = false });
        Assert.Equal(5.2f, pieces[0].Mesh.Positions.Max(p => p.Z), 3);
        Assert.Equal(5f, pieces[^1].Mesh.Positions.Max(p => p.Z), 3);
    }

    [Fact]
    public void Overlap_OnABox_DoesNotLeaveFacesInsideTheJoint()
    {
        // 箱の前後の蓋が継ぎ目の中に残らない（回り込みで持ってきた先頭の蓋は落とす）
        var pieces = RepeaterSplit.Split(BoxAt(0, 20), new Selection(), new RepeaterSplitOptions { Length = 5, Overlap = 0.2f });
        var last = pieces[^1];
        int capsAtSeam = last.Mesh.Faces.Count(f => f.Indices.All(i => MathF.Abs(last.Mesh.Positions[i].Z - 5f) < 1e-3f));
        Assert.Equal(1, capsAtSeam); // 元の端の蓋 1 枚だけ
    }

    [Fact]
    public void SelectionLimitsTheMeshes()
    {
        var scene = Wall();
        foreach (var m in BoxAt(0, 25).Meshes) scene.Meshes.Add(m);
        var sel = new Selection();
        sel.SetMode(SelectMode.Object);
        sel.Set([new ElementRef(0, -1)]);
        var pieces = RepeaterSplit.Split(scene, sel, new RepeaterSplitOptions { Length = 5 });
        Assert.All(pieces, p => Assert.Single(p.Mesh.Faces)); // 壁だけ（箱は対象外）
    }

    [Fact]
    public void Split_DoesNotChangeTheSourceScene()
    {
        var scene = Wall();
        var before = XTextWriter.Write(scene);
        RepeaterSplit.Split(scene, new Selection(), new RepeaterSplitOptions { Length = 5 });
        Assert.Equal(before, XTextWriter.Write(scene));
    }

    [Fact]
    public void Save_WritesOnePerPieceAndCanBeReadBack()
    {
        var dir = Path.Combine(Path.GetTempPath(), "BveTsStructureEditorRepeaterTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            var pieces = RepeaterSplit.Split(Wall(), new Selection(), new RepeaterSplitOptions { Length = 5 });
            var results = RepeaterSplit.Save(pieces, null, dir, "wall", new XWriteOptions());
            Assert.All(results, r => Assert.True(r.Ok, r.Message));
            Assert.Equal(5, Directory.GetFiles(dir, "wall_*.x").Length);
            var read = XFile.Load(Path.Combine(dir, "wall_02.x")).Scene;
            Assert.Single(read.Meshes);
            Assert.Equal(5f, read.Meshes[0].Positions.Max(p => p.Z), 3);
            Assert.Equal("tex.png", read.Meshes[0].Materials[0].Texture);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}

public class RepeaterLayoutTests
{
    [Fact]
    public void Straight_PlacesAlongZ()
    {
        var places = RepeaterLayout.Compute(0, 5, 5, 4, 2);
        Assert.Equal(4, places.Count);
        for (int i = 0; i < places.Count; i++)
        {
            Assert.Equal(new Vector3(0, 0, i * 5), places[i].Position);
            Assert.Equal(0f, places[i].YawDegrees);
            Assert.Equal(i % 2, places[i].PieceIndex);
        }
    }

    [Fact]
    public void RightCurve_BendsTowardPlusX()
    {
        var places = RepeaterLayout.Compute(200, 5, 5, 12, 1);
        Assert.Equal(0f, places[0].Position.X, 4);
        Assert.All(places.Skip(1), p => Assert.True(p.Position.X > 0, $"X {p.Position.X}"));
        for (int i = 1; i < places.Count; i++) Assert.True(places[i].Position.X > places[i - 1].Position.X);
    }

    [Fact]
    public void LeftCurve_IsTheMirrorOfRightCurve()
    {
        var right = RepeaterLayout.Compute(150, 5, 5, 8, 1);
        var left = RepeaterLayout.Compute(-150, 5, 5, 8, 1);
        for (int i = 0; i < right.Count; i++)
        {
            Assert.Equal(right[i].Position.X, -left[i].Position.X, 4);
            Assert.Equal(right[i].Position.Z, left[i].Position.Z, 4);
            Assert.Equal(right[i].YawDegrees, -left[i].YawDegrees, 4);
        }
    }

    [Fact]
    public void ArcLength_MatchesSpan()
    {
        var places = RepeaterLayout.Compute(100, 5, 5, 5, 1);
        for (int i = 1; i < places.Count; i++)
        {
            // 弦の長さは弧より少し短い（半径 100 m・5 m 間隔ならほぼ同じ）
            float chord = Vector3.Distance(places[i].Position, places[i - 1].Position);
            Assert.InRange(chord, 4.99f, 5f);
        }
    }

    [Fact]
    public void Yaw_FollowsTheChordOfLengthSpan()
    {
        // BVE と同じく、置いた点から span 先の点へ向かう弦の向きに +Z を合わせる
        foreach (float span in new[] { 5f, 10f, 0f })
        {
            var places = RepeaterLayout.Compute(200, 5, span, 6, 1);
            for (int i = 0; i < places.Count; i++)
                Assert.Equal((i * 5f / 200f + span / 400f) * 180f / MathF.PI, places[i].YawDegrees, 4);
        }
        // 置く位置は軌道に沿って interval ごと
        var p = RepeaterLayout.Compute(200, 5, 5, 6, 1);
        Assert.Equal(200f * MathF.Sin(25f / 200f), p[5].Position.Z, 3);
        // span = interval なら、+Z はちょうど次の置き場所を向く
        for (int i = 0; i + 1 < p.Count; i++)
        {
            var dir = Vector3.Normalize(p[i + 1].Position - p[i].Position);
            float yaw = p[i].YawDegrees * MathF.PI / 180f;
            Assert.Equal(MathF.Sin(yaw), dir.X, 4);
            Assert.Equal(MathF.Cos(yaw), dir.Z, 4);
        }
    }

    [Fact]
    public void IntervalAndSpan_AreIndependent()
    {
        // 置く間隔は interval だけで決まり、span は向きだけを変える
        var a = RepeaterLayout.Compute(150, 5, 5, 4, 1);
        var b = RepeaterLayout.Compute(150, 5, 20, 4, 1);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Position.X, b[i].Position.X, 4);
            Assert.Equal(a[i].Position.Z, b[i].Position.Z, 4);
            Assert.True(b[i].YawDegrees > a[i].YawDegrees);
        }
    }

    [Fact]
    public void RequiredOverlap_CoversTheOutsideOfTheCurve()
    {
        // 右カーブなら、外側は左（-X）。次のピースは弦の長さ c = 2R sin(L/2R) 先から、L/R だけ向きを変えて始まる
        var places = RepeaterLayout.Compute(200, 5, 5, 12, 1);
        float overlap = RepeaterLayout.RequiredOverlap(places, 5, -2f, 0f);
        float expected = 2f * 200f * MathF.Sin(5f / 400f) + 2f * MathF.Sin(5f / 200f) - 5f;
        Assert.Equal(expected, overlap, 3);
        // 直線なら伸ばす必要はない
        Assert.Equal(0f, RepeaterLayout.RequiredOverlap(RepeaterLayout.Compute(0, 5, 5, 5, 1), 5, -2f, 2f), 4);
    }

    [Fact]
    public void RequiredOverlap_IsTheSameForLeftAndRightCurves()
    {
        float right = RepeaterLayout.RequiredOverlap(RepeaterLayout.Compute(200, 5, 5, 12, 1), 5, -2f, 2f);
        float left = RepeaterLayout.RequiredOverlap(RepeaterLayout.Compute(-200, 5, 5, 12, 1), 5, -2f, 2f);
        Assert.Equal(right, left, 4);
        Assert.True(right > 0);
    }

    [Fact]
    public void AutoOverlap_CoversBothDirections_AndRoundsUpToMillimetres()
    {
        // 右に 3 m・左に 1 m 張り出すモデル。左カーブでは右側（3 m）が外になるので、そちらで決まる
        float overlap = RepeaterLayout.AutoOverlap(80, 5, 5, 5, -1f, 3f);
        float expected = MathF.Ceiling((2f * 80f * MathF.Sin(5f / 160f) + 3f * MathF.Sin(5f / 80f) - 5f) * 1000f) / 1000f;
        Assert.Equal(expected, overlap, 4);
        Assert.Equal(overlap, RepeaterLayout.AutoOverlap(-80, 5, 5, 5, -1f, 3f), 4);
        Assert.Equal(0f, RepeaterLayout.AutoOverlap(0, 5, 5, 5, -1f, 3f));
    }

    [Fact]
    public void BadValues_GiveNothing()
    {
        Assert.Empty(RepeaterLayout.Compute(100, 0, 5, 5, 1));
        Assert.Empty(RepeaterLayout.Compute(100, 5, -1, 5, 1));
        Assert.Empty(RepeaterLayout.Compute(100, 5, 5, 0, 1));
        Assert.Empty(RepeaterLayout.Compute(100, 5, 5, 5, 0));
    }
}
