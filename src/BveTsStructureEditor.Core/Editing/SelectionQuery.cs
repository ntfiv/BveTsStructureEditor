using BveTsStructureEditor.Core.Model;

namespace BveTsStructureEditor.Core.Editing;

/// <summary>選択を「どの頂点・どの面に効くか」に読み替える。</summary>
public static class SelectionQuery
{
    /// <summary>選択が空なら全体を対象にする。</summary>
    public static Dictionary<int, HashSet<int>> Vertices(XScene scene, Selection sel, bool emptyMeansAll = true)
    {
        var result = new Dictionary<int, HashSet<int>>();
        if (sel.IsEmpty)
        {
            if (emptyMeansAll)
                for (int m = 0; m < scene.Meshes.Count; m++)
                    result[m] = Enumerable.Range(0, scene.Meshes[m].Positions.Count).ToHashSet();
            return result;
        }
        foreach (var r in sel.Items)
        {
            if (r.Mesh >= scene.Meshes.Count) continue;
            var mesh = scene.Meshes[r.Mesh];
            var set = result.TryGetValue(r.Mesh, out var s) ? s : result[r.Mesh] = [];
            switch (sel.Mode)
            {
                case SelectMode.Object:
                    for (int i = 0; i < mesh.Positions.Count; i++) set.Add(i);
                    break;
                case SelectMode.Face when r.Index < mesh.Faces.Count:
                    foreach (var i in mesh.Faces[r.Index].Indices) set.Add(i);
                    break;
                case SelectMode.Vertex when r.Index < mesh.Positions.Count:
                    set.Add(r.Index);
                    break;
            }
        }
        return result;
    }

    /// <summary>
    /// 対象の面。頂点選択では「すべての頂点が選ばれている面」。選択が空なら全体。
    /// </summary>
    public static Dictionary<int, HashSet<int>> Faces(XScene scene, Selection sel, bool emptyMeansAll = true)
    {
        var result = new Dictionary<int, HashSet<int>>();
        if (sel.IsEmpty)
        {
            if (emptyMeansAll)
                for (int m = 0; m < scene.Meshes.Count; m++)
                    result[m] = Enumerable.Range(0, scene.Meshes[m].Faces.Count).ToHashSet();
            return result;
        }
        if (sel.Mode == SelectMode.Vertex)
        {
            foreach (var (m, verts) in Vertices(scene, sel))
            {
                var mesh = scene.Meshes[m];
                var set = new HashSet<int>();
                for (int f = 0; f < mesh.Faces.Count; f++)
                    if (mesh.Faces[f].Indices.All(verts.Contains)) set.Add(f);
                if (set.Count > 0) result[m] = set;
            }
            return result;
        }
        foreach (var r in sel.Items)
        {
            if (r.Mesh >= scene.Meshes.Count) continue;
            var mesh = scene.Meshes[r.Mesh];
            var set = result.TryGetValue(r.Mesh, out var s) ? s : result[r.Mesh] = [];
            if (sel.Mode == SelectMode.Object)
                for (int i = 0; i < mesh.Faces.Count; i++) set.Add(i);
            else if (r.Index < mesh.Faces.Count)
                set.Add(r.Index);
        }
        return result;
    }

    public static HashSet<int> Meshes(XScene scene, Selection sel, bool emptyMeansAll = true)
    {
        if (sel.IsEmpty) return emptyMeansAll ? Enumerable.Range(0, scene.Meshes.Count).ToHashSet() : [];
        return sel.Items.Select(r => r.Mesh).Where(m => m < scene.Meshes.Count).ToHashSet();
    }
}
