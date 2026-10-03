using System.Numerics;

namespace BveXEditor.Core.Editing;

/// <summary>下絵を置く面。</summary>
public enum ReferencePlane
{
    /// <summary>上面図: 地面 (XZ) に寝かせる。画像の右 = +X、上 = +Z（進行方向）。</summary>
    Ground,
    /// <summary>正面図: 手前 (-Z) を向けて立てる。画像の右 = +X、上 = +Y。</summary>
    Front,
    /// <summary>側面図: 右 (+X) を向けて立てる。画像の右 = +Z（進行方向）、上 = +Y。</summary>
    Side,
}

/// <summary>
/// 下絵（図面や写真）の置き方の計算。座標は BVE 空間。
/// 位置は「画像の中心」、大きさは幅 (m) で持ち、高さは画像の縦横比から決める。
/// </summary>
public static class ReferencePlacement
{
    public static Vector3 Normal(ReferencePlane plane) => plane switch
    {
        ReferencePlane.Ground => Vector3.UnitY,
        ReferencePlane.Front => -Vector3.UnitZ,
        _ => Vector3.UnitX,
    };

    /// <summary>画像の右・上方向（面の法線まわりに <paramref name="rotationDeg"/> 度回したもの）。</summary>
    public static (Vector3 Right, Vector3 Up) Axes(ReferencePlane plane, float rotationDeg)
    {
        var (right, up) = UvOps.ViewBasis(Normal(plane));
        float a = rotationDeg * MathF.PI / 180f;
        float c = MathF.Cos(a), s = MathF.Sin(a);
        return (right * c + up * s, up * c - right * s);
    }

    /// <summary>
    /// 四隅（左上・右上・右下・左下）。UV は順に (0,0) (1,0) (1,1) (0,1)。
    /// </summary>
    public static Vector3[] Corners(ReferencePlane plane, Vector3 center, float width, float height, float rotationDeg)
    {
        var (right, up) = Axes(plane, rotationDeg);
        var r = right * (width / 2);
        var u = up * (height / 2);
        return [center - r + u, center + r + u, center + r - u, center - r - u];
    }

    public static float HeightOf(float width, int pixelWidth, int pixelHeight) =>
        pixelWidth > 0 ? width * pixelHeight / pixelWidth : width;

    /// <summary>
    /// 図面上の 2 点（クリックした位置）の距離が <paramref name="realDistance"/> m になるように拡大する。
    /// 1 点目は動かさない（その点を中心に拡大）。戻り値は新しい中心と幅。
    /// </summary>
    public static (Vector3 Center, float Width) ScaleByTwoPoints(Vector3 center, float width, Vector3 p1, Vector3 p2, float realDistance)
    {
        float measured = Vector3.Distance(p1, p2);
        if (measured < 1e-6f || realDistance <= 0) throw new ArgumentException("2 点が近すぎるか、距離が正ではありません");
        float s = realDistance / measured;
        return (p1 + (center - p1) * s, width * s);
    }
}
