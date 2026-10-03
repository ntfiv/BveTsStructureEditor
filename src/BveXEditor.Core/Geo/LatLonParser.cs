using System.Globalization;
using System.Text.RegularExpressions;

namespace BveXEditor.Core.Geo;

/// <summary>
/// 貼り付けられた文字から緯度経度を読む。対応する書き方:
/// 「35.681236, 139.767125」「35.681236 139.767125」、地理院地図の URL（#16/35.68/139.76/）、
/// Google マップの URL（@35.68,139.76,17z）、度分秒（35°40'52.5"N 139°46'1.6"E）。
/// 緯度と経度が逆に書かれていても、値の範囲から判断して入れ替える。
/// </summary>
public static partial class LatLonParser
{
    [GeneratedRegex(@"#\d+(?:\.\d+)?/(-?\d+(?:\.\d+)?)/(-?\d+(?:\.\d+)?)")]
    private static partial Regex GsiUrl();

    [GeneratedRegex(@"@(-?\d+(?:\.\d+)?),(-?\d+(?:\.\d+)?)")]
    private static partial Regex GoogleUrl();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*[°度]\s*(?:(\d+(?:\.\d+)?)\s*['′分])?\s*(?:(\d+(?:\.\d+)?)\s*(?:""|″|''|秒))?\s*([NSEW北南東西])?", RegexOptions.IgnoreCase)]
    private static partial Regex Dms();

    [GeneratedRegex(@"-?\d+(?:\.\d+)?")]
    private static partial Regex Number();

    public static bool TryParse(string? text, out double lat, out double lon)
    {
        lat = lon = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();

        var m = GsiUrl().Match(text);
        if (m.Success) return Accept(D(m.Groups[1].Value), D(m.Groups[2].Value), out lat, out lon);
        m = GoogleUrl().Match(text);
        if (m.Success) return Accept(D(m.Groups[1].Value), D(m.Groups[2].Value), out lat, out lon);

        var dms = Dms().Matches(text);
        if (dms.Count >= 2 && (text.Contains('°') || text.Contains('度')))
        {
            double V(Match x)
            {
                double deg = D(x.Groups[1].Value);
                if (x.Groups[2].Success) deg += D(x.Groups[2].Value) / 60;
                if (x.Groups[3].Success) deg += D(x.Groups[3].Value) / 3600;
                var h = x.Groups[4].Value.ToUpperInvariant();
                return h is "S" or "W" or "南" or "西" ? -deg : deg;
            }
            double a = V(dms[0]), b = V(dms[1]);
            // 経度を示す文字 (E/W/東/西) が先に来ていたら入れ替える
            var first = dms[0].Groups[4].Value.ToUpperInvariant();
            return first is "E" or "W" or "東" or "西" ? Accept(b, a, out lat, out lon) : Accept(a, b, out lat, out lon);
        }

        var nums = Number().Matches(text);
        if (nums.Count >= 2) return Accept(D(nums[0].Value), D(nums[1].Value), out lat, out lon);
        return false;
    }

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static bool Accept(double a, double b, out double lat, out double lon)
    {
        // 緯度は ±90 まで。先の値が 90 を超えていれば「経度, 緯度」の順とみなす
        if (Math.Abs(a) > 90 && Math.Abs(b) <= 90) (a, b) = (b, a);
        lat = a;
        lon = b;
        return Math.Abs(lat) <= 85.0511 && Math.Abs(lon) <= 180;
    }
}
