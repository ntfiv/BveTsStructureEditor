namespace BveTsStructureEditor.Core.Geo;

/// <summary>取得するタイルの範囲と、つないだ画像から切り出す位置。ピクセルは (tileX0, tileY0) の左上が 0。</summary>
public sealed record TilePlan(int Zoom, int TileX0, int TileY0, int TileX1, int TileY1,
    int CropLeft, int CropTop, int CropWidth, int CropHeight, double MetersPerPixel)
{
    public int TileCount => (TileX1 - TileX0 + 1) * (TileY1 - TileY0 + 1);
    public double WidthMeters => CropWidth * MetersPerPixel;
    public double HeightMeters => CropHeight * MetersPerPixel;
}

/// <summary>
/// 地理院タイル（XYZ タイル、Web メルカトル、256px）の計算。
/// 数 km 程度の範囲なら縮尺の緯度による変化は無視できるので、中心の緯度の縮尺で実寸にする。
/// </summary>
public static class WebMercator
{
    public const int TileSize = 256;
    private const double EarthRadius = 6378137.0;

    /// <summary>ズーム <paramref name="zoom"/> での全体ピクセル座標（左上が 0、右と下が正）。</summary>
    public static (double X, double Y) ToPixel(double lat, double lon, int zoom)
    {
        double scale = TileSize * Math.Pow(2, zoom);
        double x = (lon + 180.0) / 360.0 * scale;
        double r = lat * Math.PI / 180.0;
        double y = (1 - Math.Log(Math.Tan(r) + 1 / Math.Cos(r)) / Math.PI) / 2 * scale;
        return (x, y);
    }

    /// <summary>地面での 1 ピクセルの長さ (m)。</summary>
    public static double MetersPerPixel(double lat, int zoom) =>
        Math.Cos(lat * Math.PI / 180.0) * 2 * Math.PI * EarthRadius / (TileSize * Math.Pow(2, zoom));

    /// <summary>範囲の長い辺が <paramref name="maxPixels"/> 以下に収まる、いちばん細かいズーム。</summary>
    public static int ChooseZoom(double lat, double sizeMeters, int maxZoom = 18, int minZoom = 2, int maxPixels = 4096)
    {
        for (int z = maxZoom; z > minZoom; z--)
            if (sizeMeters / MetersPerPixel(lat, z) <= maxPixels) return z;
        return minZoom;
    }

    /// <summary>中心 (lat, lon) を真ん中にした、一辺 <paramref name="sizeMeters"/> の正方形を取るための計画。</summary>
    public static TilePlan Plan(double lat, double lon, double sizeMeters, int zoom)
    {
        double mpp = MetersPerPixel(lat, zoom);
        var (cx, cy) = ToPixel(lat, lon, zoom);
        int side = Math.Max(1, (int)Math.Round(sizeMeters / mpp));
        double left = cx - side / 2.0, top = cy - side / 2.0;
        int tx0 = (int)Math.Floor(left / TileSize), ty0 = (int)Math.Floor(top / TileSize);
        int tx1 = (int)Math.Floor((left + side - 1) / TileSize), ty1 = (int)Math.Floor((top + side - 1) / TileSize);
        return new TilePlan(zoom, tx0, ty0, tx1, ty1,
            (int)Math.Round(left - tx0 * TileSize), (int)Math.Round(top - ty0 * TileSize), side, side, mpp);
    }
}
