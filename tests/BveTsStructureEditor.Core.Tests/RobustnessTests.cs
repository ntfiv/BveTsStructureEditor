using System.Text;
using BveTsStructureEditor.Core.Editing;
using BveTsStructureEditor.Core.Format;
using BveTsStructureEditor.Core.Format.Pmx;
using BveTsStructureEditor.Core.Model;

namespace BveTsStructureEditor.Core.Tests;

/// <summary>
/// 壊れたファイルを開いたとき、読み込みが「読めません」(XFormatException) 以外の例外で落ちないこと。
/// 途中で切れたファイルと、ランダムに書き換えたファイルを固定の乱数で作って試す。
/// </summary>
public class RobustnessTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static IEnumerable<byte[]> Corruptions(byte[] data, int seed)
    {
        // 途中で切る
        foreach (var fraction in new[] { 0.0, 0.01, 0.05, 0.1, 0.25, 0.5, 0.75, 0.9, 0.99 })
            yield return data[..(int)(data.Length * fraction)];
        // ランダムに数バイト書き換える
        var rng = new Random(seed);
        for (int i = 0; i < 150; i++)
        {
            var copy = (byte[])data.Clone();
            int flips = rng.Next(1, 6);
            for (int f = 0; f < flips; f++) copy[rng.Next(copy.Length)] = (byte)rng.Next(256);
            yield return copy;
        }
    }

    private static List<string> Unexpected(IEnumerable<byte[]> inputs, Action<byte[]> load)
    {
        var problems = new List<string>();
        int n = 0;
        foreach (var input in inputs)
        {
            try
            {
                load(input);
            }
            catch (XFormatException)
            {
                // 期待どおり
            }
            catch (Exception ex)
            {
                var at = ex.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("BveTsStructureEditor.Core"))?.Trim();
                problems.Add($"#{n} ({input.Length} bytes): {ex.GetType().Name}: {ex.Message} {at}");
            }
            n++;
        }
        return problems;
    }

    [Theory]
    [InlineData("real_toyonokuni.x")]
    [InlineData("real_sample_building.x")]
    [InlineData("grid_tzip.x")]
    public void BrokenX_OnlyThrowsFormatException(string name)
    {
        if (Fixtures.Missing(name)) return; // 配布物のテストデータはリポジトリに入れていない
        var problems = Unexpected(Corruptions(File.ReadAllBytes(Fixture(name)), 1), d => XFile.Load(d));
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(10)));
    }

    [Fact]
    public void BrokenRewrittenX_OnlyThrowsFormatException()
    {
        // このアプリが書き出したテキスト .x（読み直した形）を壊す
        if (Fixtures.Missing("real_toyonokuni.x")) return;
        var scene = XFile.Load(File.ReadAllBytes(Fixture("real_toyonokuni.x"))).Scene;
        var written = Encoding.ASCII.GetBytes(XTextWriter.Write(scene));
        var problems = Unexpected(Corruptions(written, 2), d => XFile.Load(d));
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(10)));
    }

    [Theory]
    [InlineData("kodan.pmx")]
    [InlineData("WallUnder_QN_25m_hukusen.pmx")]
    public void BrokenPmx_OnlyThrowsFormatException(string name)
    {
        if (Fixtures.Missing(name)) return;
        var problems = Unexpected(Corruptions(File.ReadAllBytes(Fixture(name)), 3), d => PmxReader.Load(d));
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(10)));
    }

    [Fact]
    public void BrokenBvex_OnlyThrowsFormatException()
    {
        var scene = new XScene();
        scene.Meshes.Add(Primitives.Box(1, 2, 3));
        using var ms = new MemoryStream();
        BvexFile.Write(ms, scene, null);
        var problems = Unexpected(Corruptions(ms.ToArray(), 4), d => BvexFile.Read(new MemoryStream(d)));
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(10)));
    }

    [Fact]
    public void BrokenTextEdit_OnlyThrowsFormatException()
    {
        // テキストタブで打ち間違えたときの読み込み（LoadText）
        if (Fixtures.Missing("real_sample_building.x")) return;
        var text = Encoding.UTF8.GetString(File.ReadAllBytes(Fixture("real_sample_building.x")));
        var rng = new Random(5);
        var inputs = new List<byte[]>();
        for (int i = 0; i < 150; i++)
        {
            var sb = new StringBuilder(text);
            for (int k = 0; k < 3; k++)
            {
                int at = rng.Next(sb.Length);
                sb.Remove(at, Math.Min(rng.Next(1, 20), sb.Length - at));
                sb.Insert(at, "};,;{-1e99 abc"[rng.Next(14)..]);
            }
            inputs.Add(Encoding.UTF8.GetBytes(sb.ToString()));
        }
        var problems = Unexpected(inputs, d => XFile.LoadText(Encoding.UTF8.GetString(d)));
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(10)));
    }
}
