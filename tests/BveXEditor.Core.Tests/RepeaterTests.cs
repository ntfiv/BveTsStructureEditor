using System.Numerics;
using BveXEditor.Core.Editing;
using BveXEditor.Core.Format;
using BveXEditor.Core.Model;

namespace BveXEditor.Core.Tests;

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
    public void Remainder_StaysInLastPiece()
    {
        var pieces = RepeaterSplit.Split(Wall(), new Selection(), new RepeaterSplitOptions { Length = 7 });
        Assert.Equal(4, pieces.Count);
        Assert.Equal([7f, 7f, 7f, 4f], pieces.Select(p => MathF.Round(p.Length, 3)));
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
        var dir = Path.Combine(Path.GetTempPath(), "BveXEditorRepeaterTest_" + Guid.NewGuid().ToString("N"));
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
        var places = RepeaterLayout.Compute(0, 5, 4, 2);
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
        // ブロックの境目（25 m ごと）から先は +X 側へ寄っていく
        var places = RepeaterLayout.Compute(200, 5, 12, 1);
        Assert.All(places.Take(6), p => Assert.Equal(0f, p.Position.X, 4));
        Assert.All(places.Skip(6), p => Assert.True(p.Position.X > 0, $"X {p.Position.X}"));
        Assert.True(places[^1].Position.X > places[6].Position.X);
    }

    [Fact]
    public void LeftCurve_IsTheMirrorOfRightCurve()
    {
        var right = RepeaterLayout.Compute(150, 5, 8, 1);
        var left = RepeaterLayout.Compute(-150, 5, 8, 1);
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
        var places = RepeaterLayout.Compute(100, 5, 5, 1);
        for (int i = 1; i < places.Count; i++)
        {
            // 弦の長さは弧より少し短い（半径 100 m・5 m 間隔ならほぼ同じ）
            float chord = Vector3.Distance(places[i].Position, places[i - 1].Position);
            Assert.InRange(chord, 4.99f, 5f);
        }
    }

    [Fact]
    public void Blocks_KeepTheSameYawWithinABlock_AndTurnAtTheBoundary()
    {
        // 25 m ブロック・5 m 間隔なら、5 本ごとに向きが変わる
        var places = RepeaterLayout.Compute(200, 5, 12, 1);
        Assert.Equal(0f, places[0].YawDegrees, 4);
        for (int i = 0; i < 5; i++) Assert.Equal(places[0].YawDegrees, places[i].YawDegrees, 4);
        float expected = 25f / 200f * 180f / MathF.PI;
        for (int i = 5; i < 10; i++) Assert.Equal(expected, places[i].YawDegrees, 4);
        Assert.Equal(2 * expected, places[10].YawDegrees, 4);
    }

    [Fact]
    public void Blocks_GoStraightInsideABlock()
    {
        var places = RepeaterLayout.Compute(200, 5, 8, 1);
        // 1 ブロック目（0〜25 m）はまっすぐ。境目の 25 m もまだ X=0
        for (int i = 0; i <= 5; i++)
        {
            Assert.Equal(0f, places[i].Position.X, 4);
            Assert.Equal(i * 5f, places[i].Position.Z, 4);
        }
        // 境目で向きが変わるので、その先から X が動く
        Assert.True(places[6].Position.X > 0);
    }

    [Fact]
    public void ContinuousMode_FollowsTheArc()
    {
        var places = RepeaterLayout.Compute(200, 5, 6, 1, blockLength: 0);
        Assert.All(places.Skip(1), p => Assert.True(p.Position.X > 0));
        Assert.Equal(25f / 200f * 180f / MathF.PI, places[5].YawDegrees, 4);
    }

    [Fact]
    public void RequiredOverlap_CoversTheOutsideOfTheCurve()
    {
        // 右カーブなら、外側は左（-X）。幅 2 m の張り出しで、ブロックの折れ角ぶんの伸ばしが要る
        var places = RepeaterLayout.Compute(200, 5, 12, 1);
        float overlap = RepeaterLayout.RequiredOverlap(places, 5, -2f, 0f);
        float expected = 2f * MathF.Sin(25f / 200f);
        Assert.Equal(expected, overlap, 3);
        // 直線なら伸ばす必要はない
        Assert.Equal(0f, RepeaterLayout.RequiredOverlap(RepeaterLayout.Compute(0, 5, 5, 1), 5, -2f, 2f), 4);
    }

    [Fact]
    public void RequiredOverlap_IsTheSameForLeftAndRightCurves()
    {
        float right = RepeaterLayout.RequiredOverlap(RepeaterLayout.Compute(200, 5, 12, 1), 5, -2f, 2f);
        float left = RepeaterLayout.RequiredOverlap(RepeaterLayout.Compute(-200, 5, 12, 1), 5, -2f, 2f);
        Assert.Equal(right, left, 4);
        Assert.True(right > 0);
    }

    [Fact]
    public void BadValues_GiveNothing()
    {
        Assert.Empty(RepeaterLayout.Compute(100, 0, 5, 1));
        Assert.Empty(RepeaterLayout.Compute(100, 5, 0, 1));
        Assert.Empty(RepeaterLayout.Compute(100, 5, 5, 0));
    }
}
