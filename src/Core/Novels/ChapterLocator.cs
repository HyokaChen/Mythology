using System.Text;
using Core.Data;

namespace Core.Novels;

/// <summary>
/// 章节定位与翻页（纯函数，可单测）：把 <c>/read 12</c>、<c>/read ch_abc</c>、<c>/read 雨夜</c>
/// 这类输入解析成具体章节，并给出上一章/下一章。
/// 长篇（上千章）里"跳过去"比"翻列表"重要，所以规则要稳：
/// 序号精确 → ID 精确 → 标题子串（唯一才算命中，多个候选列清单而不是猜）。
/// </summary>
public static class ChapterLocator
{
    /// <summary>定位结果。</summary>
    /// <param name="Chapter">命中的章节；null 表示未命中/歧义/未给参数。</param>
    /// <param name="Message">未命中原因（含用法或候选），命中时为 null。</param>
    public readonly record struct Result(Chapter? Chapter, string? Message);

    /// <summary>未给参数时的用法提示。</summary>
    public const string UsageHint = "用法: <章节序号|章节ID|标题片段>";

    /// <summary>
    /// 解析查询串。<paramref name="query"/> 为 null/空白 → 返回用法提示。
    /// 标题匹配忽略大小写、按子串；多个候选按序号升序列出前 5 个。
    /// </summary>
    public static Result Resolve(IReadOnlyList<Chapter> chapters, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new Result(null, UsageHint);
        if (chapters.Count == 0)
            return new Result(null, "当前小说还没有章节");

        var trimmed = query.Trim();

        // 1) 纯数字 → 章节序号（1 基，与面板显示一致）
        if (int.TryParse(trimmed, out var order))
        {
            var byOrder = chapters.FirstOrDefault(c => c.Order == order);
            return byOrder is not null
                ? new Result(byOrder, null)
                : new Result(null, $"没有第 {order} 章（当前共 {chapters.Count} 章）");
        }

        // 2) 章节 ID 精确匹配
        var byId = chapters.FirstOrDefault(c =>
            string.Equals(c.Id, trimmed, StringComparison.Ordinal)
        );
        if (byId is not null)
            return new Result(byId, null);

        // 3) 标题子串（忽略大小写）
        var matches = chapters
            .Where(c => c.Title.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Order)
            .ToList();
        return matches.Count switch
        {
            1 => new Result(matches[0], null),
            0 => new Result(null, $"没有标题包含「{trimmed}」的章节"),
            _ => new Result(
                null,
                $"「{trimmed}」匹配到 {matches.Count} 章："
                    + string.Join("、", matches.Take(5).Select(c => $"第{c.Order}章 {c.Title}"))
                    + (matches.Count > 5 ? " …" : "")
                    + "（请用序号或更精确的标题）"
            ),
        };
    }

    /// <summary>
    /// 相对当前位置移动 <paramref name="delta"/> 章（阅读器 ←/→ 翻页）。
    /// 列表为空 / 找不到当前章节 / 越界 → 返回 null（调用方提示"已经是最后一章"）。
    /// </summary>
    public static Chapter? MoveBy(IReadOnlyList<Chapter> chapters, string? currentId, int delta)
    {
        if (chapters.Count == 0 || delta == 0)
            return null;

        var index = -1;
        for (var i = 0; i < chapters.Count; i++)
        {
            if (string.Equals(chapters[i].Id, currentId, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }
        if (index < 0)
            return null;

        var target = index + delta;
        return target >= 0 && target < chapters.Count ? chapters[target] : null;
    }

    /// <summary>位置描述（"第 3 / 120 章"），供阅读器表头显示。</summary>
    public static string DescribePosition(IReadOnlyList<Chapter> chapters, string? currentId)
    {
        for (var i = 0; i < chapters.Count; i++)
        {
            if (string.Equals(chapters[i].Id, currentId, StringComparison.Ordinal))
                return $"第 {i + 1} / {chapters.Count} 章";
        }
        return chapters.Count == 0 ? "0 章" : $"共 {chapters.Count} 章";
    }

    /// <summary>
    /// 汇总错义的章节候选清单（歧义/未命中时给用户的可行动消息）。
    /// </summary>
    public static string BuildCandidateList(IEnumerable<Chapter> matches, int max = 5)
    {
        var list = matches.Take(max).ToList();
        var sb = new StringBuilder();
        foreach (var c in list)
            sb.Append("第").Append(c.Order).Append("章 ").Append(c.Title).Append("、");
        if (sb.Length > 0)
            sb.Length--; // 去掉尾随顿号
        return sb.ToString();
    }
}
