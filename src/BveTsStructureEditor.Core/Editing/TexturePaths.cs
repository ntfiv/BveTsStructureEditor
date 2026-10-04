using BveTsStructureEditor.Core.Model;

namespace BveTsStructureEditor.Core.Editing;

public static class TexturePaths
{
    /// <summary>
    /// 別のファイルから持ってきたメッシュのテクスチャを、置き先のファイル基準に書き換える。
    /// 相対パスは <paramref name="fromDirectory"/>（元のファイルの場所）基準なので、いったん絶対パスにし、
    /// <paramref name="toDirectory"/> があればそこからの相対パスにする（無ければ絶対パスのまま）。
    /// </summary>
    public static void Rebase(IEnumerable<XMesh> meshes, string fromDirectory, string? toDirectory)
    {
        foreach (var mat in meshes.SelectMany(m => m.Materials))
        {
            if (string.IsNullOrEmpty(mat.Texture)) continue;
            var normalized = mat.Texture.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            var full = Path.IsPathRooted(normalized) ? normalized : Path.GetFullPath(Path.Combine(fromDirectory, normalized));
            mat.Texture = toDirectory == null ? full : Path.GetRelativePath(toDirectory, full);
        }
    }

    /// <summary>
    /// 絶対パスのテクスチャのうち、<paramref name="directory"/> 以下にあるものを相対パスに直す。
    /// 未保存のうちに画像から板を作ると絶対パスになるので、保存先が決まったときに呼ぶ。
    /// フォルダーの外（別ドライブや上の階層）はそのまま残す。戻り値は直した数。
    /// </summary>
    public static int MakeRelative(XScene scene, string directory)
    {
        var root = Path.GetFullPath(directory);
        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;
        int changed = 0;
        foreach (var mat in scene.Meshes.SelectMany(m => m.Materials))
        {
            if (string.IsNullOrEmpty(mat.Texture) || !Path.IsPathRooted(mat.Texture)) continue;
            var full = Path.GetFullPath(mat.Texture);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            mat.Texture = Path.GetRelativePath(root, full);
            changed++;
        }
        return changed;
    }
}
