using System.Numerics;

namespace BveXEditor.Core.Editing;

/// <summary>繰り返しの 1 回ぶん。<see cref="PieceIndex"/> はどのピースを置くか。</summary>
public sealed record RepeaterPlacement(int Index, int PieceIndex, Vector3 Position, float YawDegrees);

/// <summary>
/// BVE の Repeater と同じ置き方。ストラクチャ自体は曲がらないので、向きと位置だけを変える。
/// BVE は軌道を <see cref="DefaultBlockLength"/> ごとの直線でつないでいるので、同じブロックの中では
/// 向きが変わらず、ブロックの境目で折れる。緩和曲線・カント・勾配は扱わない。
/// </summary>
public static class RepeaterLayout
{
    /// <summary>これより半径が小さい指定は直線として扱う。</summary>
    public const float StraightRadius = 1e-3f;

    /// <summary>BVE が軌道を区切る長さ (m)。</summary>
    public const float DefaultBlockLength = 25f;

    /// <summary>進行方向の単位ベクトル（ヨー角 0 で +Z、正で右へ）。</summary>
    private static Vector3 Forward(float yaw) => new(MathF.Sin(yaw), 0, MathF.Cos(yaw));

    /// <param name="radius">曲線半径 (m)。正 = 右カーブ、負 = 左カーブ、0 = 直線。</param>
    /// <param name="span">1 回ぶんの間隔 (m)。ふつうはピースの区間の長さと同じにする。</param>
    /// <param name="count">繰り返す回数。</param>
    /// <param name="pieceCount">ピースの数（順ぐりに使う）。</param>
    /// <param name="blockLength">軌道を区切る長さ (m)。0 以下なら、折れずに曲線そのものに沿わせる。</param>
    public static List<RepeaterPlacement> Compute(float radius, float span, int count, int pieceCount,
        float blockLength = DefaultBlockLength)
    {
        var result = new List<RepeaterPlacement>();
        if (span <= 0 || count <= 0 || pieceCount <= 0) return result;
        bool straight = MathF.Abs(radius) < StraightRadius;

        // ブロックの始まりの位置と向き。ブロックを 1 つ進むごとに、その長さぶん曲がる
        var blockOrigin = Vector3.Zero;
        float blockYaw = 0;
        int currentBlock = 0;

        for (int i = 0; i < count; i++)
        {
            float s = i * span;
            Vector3 position;
            float yaw;
            if (straight)
            {
                position = new Vector3(0, 0, s);
                yaw = 0;
            }
            else if (blockLength <= 0)
            {
                // 折れずに円弧そのものに沿わせる（比べる用）
                float theta = s / radius;
                position = new Vector3(radius * (1 - MathF.Cos(theta)), 0, radius * MathF.Sin(theta));
                yaw = theta;
            }
            else
            {
                int block = (int)MathF.Floor(s / blockLength);
                while (currentBlock < block)
                {
                    blockOrigin += Forward(blockYaw) * blockLength;
                    blockYaw += blockLength / radius;
                    currentBlock++;
                }
                // 同じブロックの中は、ブロックの向きのまままっすぐ進む
                position = blockOrigin + Forward(blockYaw) * (s - block * blockLength);
                yaw = blockYaw;
            }
            result.Add(new RepeaterPlacement(i, i % pieceCount, position, yaw * 180f / MathF.PI));
        }
        return result;
    }

    /// <summary>
    /// 継ぎ目の外側に隙間が出ないために、区間の長さより伸ばしておきたい量 (m)。
    /// 次のピースの始まりの面が、今のピースの終わりより手前に来るぶんを、左右の端まで見て求める。
    /// </summary>
    public static float RequiredOverlap(IReadOnlyList<RepeaterPlacement> placements, float span, float xMin, float xMax)
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
                needed = MathF.Max(needed, ahead - x * MathF.Sin(delta) - span);
        }
        return MathF.Max(0, needed);
    }

    /// <summary>置いた範囲の説明（画面のステータス用）。</summary>
    public static string Describe(float radius, float span, int count, float blockLength = DefaultBlockLength)
    {
        if (MathF.Abs(radius) < StraightRadius) return $"直線に {span:0.###} m 間隔で {count} 本（全長 {span * count:0.#} m）";
        var how = blockLength > 0 ? $"{blockLength:0.#} m ブロックごとに折れる" : "曲線に沿わせる";
        return $"半径 {MathF.Abs(radius):0.#} m の{(radius > 0 ? "右" : "左")}カーブに {span:0.###} m 間隔で {count} 本" +
               $"（全長 {span * count:0.#} m・{span * count / MathF.Abs(radius) * 180f / MathF.PI:0.#}°・{how}）";
    }
}
