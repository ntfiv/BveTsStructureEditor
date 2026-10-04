using BveTsStructureEditor.Core.Geo;

namespace BveTsStructureEditor.Core.Tests;

public class GeoTests
{
    [Theory]
    [InlineData("35.681236, 139.767125")]
    [InlineData("35.681236 139.767125")]
    [InlineData("139.767125, 35.681236")]                                   // 経度・緯度の順
    [InlineData("https://maps.gsi.go.jp/#16/35.681236/139.767125/&base=std")]
    [InlineData("https://www.google.com/maps/@35.681236,139.767125,17z")]
    [InlineData("35°40'52.45\"N 139°46'1.65\"E")]
    public void Parses_Common_Formats(string text)
    {
        Assert.True(LatLonParser.TryParse(text, out var lat, out var lon));
        Assert.Equal(35.6812, lat, 3);
        Assert.Equal(139.7671, lon, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("東京駅")]
    [InlineData("95, 200")]
    public void Rejects_Invalid(string text) => Assert.False(LatLonParser.TryParse(text, out _, out _));

    [Fact]
    public void Known_Tile_For_Tokyo_Station()
    {
        // 東京駅付近はズーム 16 でタイル (58211, 25806)
        var (x, y) = WebMercator.ToPixel(35.681236, 139.767125, 16);
        Assert.Equal(58211, (int)(x / 256));
        Assert.Equal(25806, (int)(y / 256));
    }

    [Fact]
    public void Meters_Per_Pixel_Matches_Gsi_Resolution()
    {
        // 赤道・ズーム 0 で 約 156543 m/px、緯度 35.68° ではその cos 倍
        Assert.Equal(156543.03, WebMercator.MetersPerPixel(0, 0), 1);
        Assert.Equal(156543.03 / 65536 * Math.Cos(35.68 * Math.PI / 180), WebMercator.MetersPerPixel(35.68, 16), 3);
    }

    [Fact]
    public void Plan_Covers_Requested_Square_Centered_On_Point()
    {
        const double lat = 35.681236, lon = 139.767125;
        int z = WebMercator.ChooseZoom(lat, 1000);
        Assert.InRange(z, 15, 18);
        var plan = WebMercator.Plan(lat, lon, 1000, z);
        Assert.InRange(plan.WidthMeters, 999, 1001);
        Assert.True(plan.CropWidth <= 4096);
        // 切り出し範囲はタイルの中に収まる
        Assert.True(plan.CropLeft >= 0 && plan.CropTop >= 0);
        Assert.True(plan.CropLeft + plan.CropWidth <= (plan.TileX1 - plan.TileX0 + 1) * 256);
        Assert.True(plan.CropTop + plan.CropHeight <= (plan.TileY1 - plan.TileY0 + 1) * 256);
        // 中心点が切り出し範囲の真ん中
        var (cx, cy) = WebMercator.ToPixel(lat, lon, z);
        Assert.Equal(plan.CropLeft + plan.CropWidth / 2.0, cx - plan.TileX0 * 256, 0);
        Assert.Equal(plan.CropTop + plan.CropHeight / 2.0, cy - plan.TileY0 * 256, 0);
    }
}
