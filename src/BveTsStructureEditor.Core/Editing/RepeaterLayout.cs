using System.Numerics;

namespace BveTsStructureEditor.Core.Editing;

/// <summary>繰り返しの 1 回ぶん。<see cref="PieceIndex"/> はどのピースを置くか。</summary>
public sealed record RepeaterPlacement(int Index, int PieceIndex, Vector3 Position, float YawDegrees);

/// <summary>
/// BVE の Repeater と同じ置き方。軌道に沿って <c>interval</c> ごとの距離程 dist にストラクチャの原点を置き、
/// その +Z 軸を「dist の点から dist + <c>span</c> の点へ向かう弦」の向きに合わせる（BVE の span は弦の長さ）。
/// ストラクチャ自体は曲がらない。緩和曲線・カント・勾配は扱わない。
/// </summary>
public static class RepeaterLayout
{
    /// <summary>これより半径が小さい指定は直線として扱う。</summary>
    public const float StraightRadius = 1e-3f;

    /// <summary>進行方向の単位ベクトル（ヨー角 0 で +Z、正で右へ）。</summary>
    private static Vector3 Forward(float yaw) => new(MathF.Sin(yaw), 0, MathF.Cos(yaw));

    /// <param name="radius">曲線半径 (m)。正 = 右カーブ、負 = 左カーブ、0 = 直線。</param>
    /// <param name="interval">置く間隔 (m)。軌道に沿った距離。ふつうはピースの区間の長さと同じにする。</param>
    /// <param name="span">向きを決める弦の長さ (m)。ふつうは interval と同じ。0 なら置く位置の接線の向き。</param>
    /// <param name="count">繰り返す回数。</param>
    /// <param name="pieceCount">ピースの数（順ぐりに使う。BVE の (dist / interval) mod N 番目）。</param>
    public static List<RepeaterPlacement> Compute(float radius, float interval, float span, int count, int pieceCount)
    {
        var result = new List<RepeaterPlacement>();
        if (interval <= 0 || span < 0 || count <= 0 || pieceCount <= 0) return result;
        bool straight = MathF.Abs(radius) < StraightRadius;

        for (int i = 0; i < count; i++)
        {
            float s = i * interval;
            Vector3 position;
            float yaw;
            if (straight)
            {
                position = new Vector3(0, 0, s);
                yaw = 0;
            }
            else
            {
                float theta = s / radius;
                position = new Vector3(radius * (1 - MathF.Cos(theta)), 0, radius * MathF.Sin(theta));
                // 円弧上の 2 点を結ぶ弦の向きは、2 点の接線の向きのちょうど中間
                yaw = theta + span / (2 * radius);
            }
            result.Add(new RepeaterPlacement(i, i % pieceCount, position, yaw * 180f / MathF.PI));
        }
        return result;
    }

    /// <summary>
    /// 継ぎ目の外側に隙間が出ないために、区間の長さ <paramref name="pieceLength"/> より伸ばしておきたい量 (m)。
    /// 次のピースの始まりの面が、今のピースの終わりより先に来るぶんを、左右の端まで見て求める。
    /// </summary>
    public static float RequiredOverlap(IReadOnlyList<RepeaterPlacement> placements, float pieceLength, float xMin, float xMax)
    {
        float needed = 0;
        for (int i = 0; i + 1 < placements.Count; i++)
        {
            var a = placements[i];
            var b = placements[i + 1];
            float ya = a.YawDegrees * MathF.PI / 180f;
            float delta = (b.YawDegrees - a.YawDegrees) * MathF.PI / 180f;
            // 今のピースの進行方向で測った、次のピースの始まりの面の位置
            float ahead = Vector3.Dot(b.Position - a.Position, Forward(ya));
            foreach (float x in (float[])[xMin, xMax])
                needed = MathF.Max(needed, ahead - x * MathF.Sin(delta) - pieceLength);
        }
        return MathF.Max(0, needed);
    }

    /// <summary>重ね代の自動計算で、半径を指定しなかったときの基準（急な曲線でも隙間が出ないよう小さめ）。</summary>
    public const float DefaultOverlapRadius = 80f;

    /// <summary>
    /// 長さ <paramref name="pieceLength"/> のピースを、半径 <paramref name="radius"/> の曲線に
    /// <paramref name="interval"/>・<paramref name="span"/> で並べても継ぎ目に隙間が出ない重ね代 (m)。
    /// 右カーブ・左カーブのどちらに置かれても足りるよう両方で求めて大きい方を取り、1 mm 単位で切り上げる。
    /// </summary>
    public static float AutoOverlap(float radius, float interval, float span, float pieceLength, float xMin, float xMax)
    {
        if (interval <= 0 || pieceLength <= 0 || MathF.Abs(radius) < StraightRadius) return 0;
        // 円弧に沿って同じ間隔で置くので、どの継ぎ目も同じ。2 本並べれば足りる
        float r = MathF.Abs(radius);
        float needed = MathF.Max(
            RequiredOverlap(Compute(r, interval, span, 2, 1), pieceLength, xMin, xMax),
            RequiredOverlap(Compute(-r, interval, span, 2, 1), pieceLength, xMin, xMax));
        // 1e-4 未満の誤差で 1 mm 切り上がらないようにしてから切り上げる
        return MathF.Ceiling(MathF.Round(needed, 4) * 1000f) / 1000f;
    }

    /// <summary>置いた範囲の説明（画面のステータス用）。</summary>
    public static string Describe(float radius, float interval, float span, int count)
    {
        var how = $"interval {interval:0.###} m・span {span:0.###} m で {count} 本";
        if (MathF.Abs(radius) < StraightRadius) return $"直線に {how}（全長 {interval * count:0.#} m）";
        return $"半径 {MathF.Abs(radius):0.#} m の{(radius > 0 ? "右" : "左")}カーブに {how}" +
               $"（全長 {interval * count:0.#} m・{interval * count / MathF.Abs(radius) * 180f / MathF.PI:0.#}°）";
    }
}
