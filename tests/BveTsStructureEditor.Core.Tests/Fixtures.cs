namespace BveTsStructureEditor.Core.Tests;

/// <summary>
/// テスト用のファイルの場所。
/// 他の方が配布しているモデル（kodan.pmx・WallUnder_*.pmx・real_*.x など）は再配布になるのでリポジトリに入れていない。
/// 手元にあるときだけそれらのテストを走らせ、無いときは <see cref="Missing"/> で飛ばす。
/// </summary>
internal static class Fixtures
{
    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static bool Missing(string name) => !File.Exists(Path(name));
}
