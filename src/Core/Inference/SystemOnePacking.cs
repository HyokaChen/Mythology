namespace Core.Inference;

/// <summary>
/// System One 请求批打包（提炼自 jegrep：按字符预算把多张卡塞进一次请求，键 p00/p01…）。
/// 贪心累加每项的渲染长度，超出预算前封包；单项超预算时仍单独成包
/// （不丢弃——超长文本由调用方在渲染时截断）。
/// </summary>
public static class SystemOnePacking
{
    public static List<List<T>> Pack<T>(
        IReadOnlyList<T> items,
        int budgetChars,
        Func<T, string> render
    )
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(render);

        var batches = new List<List<T>>();
        if (items.Count == 0)
            return batches;

        var budget = Math.Max(budgetChars, 1);
        var current = new List<T>();
        var currentLength = 0;
        foreach (var item in items)
        {
            var length = render(item)?.Length ?? 0;
            if (current.Count > 0 && currentLength + length > budget)
            {
                batches.Add(current);
                current = [];
                currentLength = 0;
            }
            current.Add(item);
            currentLength += length;
        }
        if (current.Count > 0)
            batches.Add(current);
        return batches;
    }
}
