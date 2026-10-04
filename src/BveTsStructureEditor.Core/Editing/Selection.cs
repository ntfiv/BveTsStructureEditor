namespace BveTsStructureEditor.Core.Editing;

public enum SelectMode { Object, Face, Vertex }

/// <summary>メッシュ番号と、その中の要素番号（面か頂点。オブジェクト選択では -1）。</summary>
public readonly record struct ElementRef(int Mesh, int Index);

/// <summary>
/// 選択状態。頂点から面を張るときに順番が要るので、集合と並びの両方を持つ。
/// </summary>
public sealed class Selection
{
    private readonly List<ElementRef> _order = [];
    private readonly HashSet<ElementRef> _set = [];

    public SelectMode Mode { get; private set; } = SelectMode.Object;
    public IReadOnlyList<ElementRef> Items => _order;
    public int Count => _order.Count;
    public bool IsEmpty => _order.Count == 0;

    public event Action? Changed;

    public bool Contains(ElementRef r) => _set.Contains(r);

    public void SetMode(SelectMode mode)
    {
        if (Mode == mode) return;
        Mode = mode;
        ClearSilently();
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_order.Count == 0) return;
        ClearSilently();
        Changed?.Invoke();
    }

    private void ClearSilently()
    {
        _order.Clear();
        _set.Clear();
    }

    public void Set(IEnumerable<ElementRef> items)
    {
        ClearSilently();
        foreach (var i in items)
            if (_set.Add(i)) _order.Add(i);
        Changed?.Invoke();
    }

    public void Add(IEnumerable<ElementRef> items)
    {
        foreach (var i in items)
            if (_set.Add(i)) _order.Add(i);
        Changed?.Invoke();
    }

    public void Toggle(ElementRef item)
    {
        if (_set.Remove(item)) _order.Remove(item);
        else { _set.Add(item); _order.Add(item); }
        Changed?.Invoke();
    }

    /// <summary>メッシュを消したあとなど、範囲外を落として番号を詰める。</summary>
    public void Validate(Model.XScene scene)
    {
        var keep = _order.Where(r => r.Mesh >= 0 && r.Mesh < scene.Meshes.Count && (
            Mode == SelectMode.Object ||
            (Mode == SelectMode.Face && r.Index < scene.Meshes[r.Mesh].Faces.Count) ||
            (Mode == SelectMode.Vertex && r.Index < scene.Meshes[r.Mesh].Positions.Count))).ToList();
        if (keep.Count == _order.Count) return;
        ClearSilently();
        foreach (var k in keep) { _set.Add(k); _order.Add(k); }
        Changed?.Invoke();
    }
}
