using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Core.Agent;

/// <summary>
/// compact 摘要里的任务进度。进度来自工具调用事实（成功编辑/读取的章节），
/// 不靠章节字数或模型记忆推断；下次 compact 可从上一份摘要的 Progress 段恢复。
/// </summary>
internal sealed class CompactionTaskProgress
{
    public const string Heading = "## Progress / 进度";
    public const string Unrecorded = "- 未记录";

    private static readonly Regex NumberPattern = new(@"\d+", RegexOptions.Compiled);

    private CompactionTaskProgress(
        IEnumerable<int> editedOrders,
        IEnumerable<int> inspectedOrders,
        int? currentOrder,
        int? nextOrder,
        IEnumerable<int> outOfScopeOrders
    )
    {
        EditedOrders = Normalize(editedOrders);
        InspectedOrders = Normalize(inspectedOrders);
        CurrentOrder = currentOrder;
        NextOrder = nextOrder;
        OutOfScopeOrders = Normalize(outOfScopeOrders);
    }

    public IReadOnlyList<int> EditedOrders { get; }
    public IReadOnlyList<int> InspectedOrders { get; }
    public int? CurrentOrder { get; }
    public int? NextOrder { get; }
    public IReadOnlyList<int> OutOfScopeOrders { get; }

    public bool IsEmpty =>
        EditedOrders.Count == 0
        && InspectedOrders.Count == 0
        && CurrentOrder is null
        && NextOrder is null
        && OutOfScopeOrders.Count == 0;

    public static CompactionTaskProgress Create(
        IEnumerable<int> editedOrders,
        IEnumerable<int> inspectedOrders,
        int? currentOrder,
        int? nextOrder,
        IEnumerable<int> outOfScopeOrders
    ) => new(editedOrders, inspectedOrders, currentOrder, nextOrder, outOfScopeOrders);

    public string FormatSection() => $"{Heading}\n{FormatBody()}";

    public string FormatBody()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"- 已实际编辑：{FormatOrders(EditedOrders)}");
        sb.AppendLine($"- 已读/诊断：{FormatOrders(InspectedOrders)}");
        sb.AppendLine($"- 当前：{FormatOrder(CurrentOrder)}");
        sb.AppendLine($"- 下一章：{FormatOrder(NextOrder)}");
        sb.AppendLine($"- 范围外活动：{FormatOrders(OutOfScopeOrders)}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>从上一份摘要的固定 Progress 段恢复；旧摘要没有该段时返回 null。</summary>
    public static CompactionTaskProgress? TryParse(string? markdown)
    {
        var section = ExtractSection(markdown);
        if (string.IsNullOrWhiteSpace(section))
            return null;

        var edited = new List<int>();
        var inspected = new List<int>();
        var outOfScope = new List<int>();
        int? current = null;
        int? next = null;

        foreach (var rawLine in section.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("- ", StringComparison.Ordinal))
                continue;
            var colon = line.IndexOfAny(['：', ':']);
            if (colon < 0)
                continue;

            var label = line[2..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            switch (label)
            {
                case "已实际编辑":
                    edited.AddRange(ParseOrders(value));
                    break;
                case "已读/诊断":
                    inspected.AddRange(ParseOrders(value));
                    break;
                case "当前":
                    current = ParseOrder(value);
                    break;
                case "下一章":
                    next = ParseOrder(value);
                    break;
                case "范围外活动":
                    outOfScope.AddRange(ParseOrders(value));
                    break;
            }
        }

        var progress = new CompactionTaskProgress(edited, inspected, current, next, outOfScope);
        return progress.IsEmpty ? null : progress;
    }

    private static string? ExtractSection(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return null;
        var idx = markdown.IndexOf(Heading, StringComparison.Ordinal);
        if (idx < 0)
            return null;
        var contentStart = idx + Heading.Length;
        var nextIdx = markdown.IndexOf("\n## ", contentStart, StringComparison.Ordinal);
        return (
            nextIdx > contentStart ? markdown[contentStart..nextIdx] : markdown[contentStart..]
        ).Trim();
    }

    private static IReadOnlyList<int> Normalize(IEnumerable<int> orders) =>
        orders.Where(order => order > 0).Distinct().OrderBy(order => order).ToArray();

    private static string FormatOrders(IReadOnlyList<int> orders) =>
        orders.Count == 0 ? "无" : string.Join("、", orders);

    private static string FormatOrder(int? order) => order is int value ? $"第{value}章" : "未记录";

    private static List<int> ParseOrders(string value)
    {
        if (value is "无" or "未记录" or "尚未记录")
            return [];
        return NumberPattern
            .Matches(value)
            .Select(match =>
                int.TryParse(
                    match.Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var order
                )
                    ? order
                    : 0
            )
            .Where(order => order > 0)
            .Distinct()
            .OrderBy(order => order)
            .ToList();
    }

    private static int? ParseOrder(string value)
    {
        if (value is "无" or "未记录" or "尚未记录")
            return null;
        var order = ParseOrders(value).FirstOrDefault();
        return order > 0 ? order : null;
    }
}
