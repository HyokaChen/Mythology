namespace Desktop.Controls;

/// <summary>
/// Fenwick tree height index for virtualized lists whose row height is unknown
/// until measured. Heights are estimates initially; measured heights update in
/// O(log n). Offset -> index uses binary search over prefix sums.
/// </summary>
public sealed class HeightIndex<T>
    where T : class
{
    private readonly List<T> _items = [];
    private readonly Dictionary<T, int> _index = [];
    private double[] _heights = [];
    private double[] _tree = [];

    public int Count => _items.Count;

    public double Total => Prefix(_items.Count);

    /// <summary>Rebuild from a complete list; O(n).</summary>
    public void Rebuild(IReadOnlyList<T> items, Func<T, double> measure)
    {
        _items.Clear();
        _index.Clear();
        _heights = new double[items.Count];
        _tree = new double[items.Count + 1];

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            _items.Add(item);
            _index[item] = i;
            var height = Math.Max(1, measure(item));
            _heights[i] = height;
            Add(i, height);
        }
    }

    /// <summary>Index of known item, or -1. O(1), mirrors the item order.</summary>
    public int IndexOf(T item) => _index.TryGetValue(item, out var index) ? index : -1;

    /// <summary>Update one known item; returns false when it is not in the index.</summary>
    public bool Update(T item, double height)
    {
        if (!_index.TryGetValue(item, out var index))
            return false;

        height = Math.Max(1, height);
        var delta = height - _heights[index];
        if (Math.Abs(delta) < 0.001)
            return true;

        _heights[index] = height;
        Add(index, delta);
        return true;
    }

    /// <summary>Sum of heights before <paramref name="index"/>.</summary>
    public double OffsetOf(int index) => Prefix(Math.Clamp(index, 0, _items.Count));

    /// <summary>Height sum in [start, end).</summary>
    public double Range(int start, int end)
    {
        start = Math.Clamp(start, 0, _items.Count);
        end = Math.Clamp(end, start, _items.Count);
        return Prefix(end) - Prefix(start);
    }

    /// <summary>First index whose end offset is >= <paramref name="offset"/>.</summary>
    public int IndexAtOffset(double offset)
    {
        var count = _items.Count;
        if (count == 0)
            return 0;
        if (offset <= 0)
            return 0;
        if (offset >= Total)
            return count - 1;

        var low = 0;
        var high = count - 1;
        var answer = count - 1;
        while (low <= high)
        {
            var mid = (low + high) >> 1;
            if (Prefix(mid + 1) >= offset)
            {
                answer = mid;
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }
        return answer;
    }

    private void Add(int index, double delta)
    {
        for (var i = index + 1; i < _tree.Length; i += i & -i)
            _tree[i] += delta;
    }

    private double Prefix(int count)
    {
        var sum = 0.0;
        for (var i = Math.Clamp(count, 0, _items.Count); i > 0; i -= i & -i)
            sum += _tree[i];
        return sum;
    }
}
