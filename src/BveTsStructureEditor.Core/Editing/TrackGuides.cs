using System.Numerics;

namespace BveTsStructureEditor.Core.Editing;

public enum GuideGauge { None, Narrow1067, Standard1435, Shinkansen }

public enum GuideKind { Rail, VehicleGauge, StructureGauge, Platform }

/// <summary>折れ線 1 本（BVE 座標）。</summary>
public sealed record GuideLine(GuideKind Kind, Vector3[] Points, bool Closed);

/// <summary>
/// 線路と限界の目安線。原点を軌道中心・レール面 (Y=0) とする。
/// 限界の輪郭は、在来線・新幹線の代表的な寸法を単純化した「目安」で、規程の正確な形ではない。
/// </summary>
public static class TrackGuides
{
    /// <summary>レールやホームの線を引く前後の長さ (m)。</summary>
    public const float Length = 50f;

    private sealed record Spec(float Gauge, (float X, float Y)[] Vehicle, (float X, float Y)[] Structure, float PlatformHeight, float PlatformEdge);

    // 右半分の輪郭（下から上へ）。左右対称に広げて使う
    private static readonly Spec Narrow = new(1.067f,
        Vehicle: [(1.20f, 0.10f), (1.50f, 0.40f), (1.50f, 3.20f), (1.35f, 3.90f), (0.90f, 4.10f), (0f, 4.10f)],
        Structure: [(1.45f, 0f), (1.90f, 0.40f), (1.90f, 4.00f), (1.50f, 4.60f), (1.00f, 5.70f), (0f, 5.70f)],
        PlatformHeight: 1.10f, PlatformEdge: 1.60f);

    private static readonly Spec Standard = Narrow with { Gauge = 1.435f };

    private static readonly Spec Shin = new(1.435f,
        Vehicle: [(1.30f, 0.10f), (1.70f, 0.50f), (1.70f, 3.60f), (1.40f, 4.30f), (0.80f, 4.50f), (0f, 4.50f)],
        Structure: [(1.60f, 0f), (2.20f, 0.50f), (2.20f, 5.00f), (1.80f, 5.80f), (1.20f, 6.45f), (0f, 6.45f)],
        PlatformHeight: 1.25f, PlatformEdge: 1.76f);

    public static IReadOnlyList<GuideLine> Build(GuideGauge gauge, bool platform)
    {
        var spec = gauge switch
        {
            GuideGauge.Narrow1067 => Narrow,
            GuideGauge.Standard1435 => Standard,
            GuideGauge.Shinkansen => Shin,
            _ => null,
        };
        if (spec == null) return [];

        var lines = new List<GuideLine>();
        float g = spec.Gauge / 2;
        foreach (var x in new[] { -g, g })
            lines.Add(new GuideLine(GuideKind.Rail, [new(x, 0, -Length), new(x, 0, Length)], false));

        // 限界は Z=0 の断面に濃く、前後にも同じ形を置いて奥行きがわかるようにする
        foreach (var z in new[] { 0f, -10f, 10f })
        {
            lines.Add(new GuideLine(GuideKind.VehicleGauge, Outline(spec.Vehicle, z), true));
            lines.Add(new GuideLine(GuideKind.StructureGauge, Outline(spec.Structure, z), true));
        }

        if (platform)
            foreach (var side in new[] { -1f, 1f })
                lines.Add(new GuideLine(GuideKind.Platform,
                    [new(side * spec.PlatformEdge, spec.PlatformHeight, -Length), new(side * spec.PlatformEdge, spec.PlatformHeight, Length)], false));
        return lines;
    }

    /// <summary>右半分の輪郭を左右に広げた閉じた折れ線。</summary>
    private static Vector3[] Outline((float X, float Y)[] half, float z)
    {
        var pts = new List<Vector3>();
        foreach (var (x, y) in half) pts.Add(new(x, y, z));
        for (int i = half.Length - 1; i >= 0; i--)
            if (half[i].X > 0) pts.Add(new(-half[i].X, half[i].Y, z));
        return pts.ToArray();
    }

    public static string Describe(GuideGauge gauge) => gauge switch
    {
        GuideGauge.Narrow1067 => "在来線 1067 mm",
        GuideGauge.Standard1435 => "在来線 1435 mm",
        GuideGauge.Shinkansen => "新幹線",
        _ => "なし",
    };
}
