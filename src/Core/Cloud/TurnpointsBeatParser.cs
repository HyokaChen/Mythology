using System.Text.RegularExpressions;

namespace Core.Cloud;

/// <summary>
/// Turnpoints 确定性解析器（CHANGE_10 §2.7 矩阵源③：推断数据）。
///
/// Plotline.Turnpoints 的编辑约定（EntityEditDialog 占位符）是
/// 「每行一个转折点，如：第5章：真相揭露」—— 半结构化文本，正则可解析，
/// <b>不需要 LLM</b>。LLM 解析层降级为自由文本格式的增强，不再是 Beats 的前置依赖。
///
/// 解析规则（宁可不猜，不误报——推断数据只喂 info/warning 级矩阵）：
///   - 条目分隔：换行 / 中文分号 / 英文分号
///   - 章节前缀：第N章 / 第N节 / chN / ch_N / N.（缺前缀的行整体跳过）
///   - 类型：中文关键词确定性分类（首个命中）；无命中 → progress
///   - 章节号不在 chapters 表内 → 跳过（调用方传入合法章号集）
/// </summary>
public static partial class TurnpointsBeatParser
{
    [GeneratedRegex(
        @"(?:^|[\n；;])\s*第\s*(\d+)\s*[章节][：:，,]?\s*([^；;\n]*)|(?:^|[\n；;])\s*(?:ch[_-]?)?(\d+)\s*[.：:]\s*([^；;\n]*)",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex EntryPattern();

    /// <summary>解析出的单个 beat。</summary>
    public sealed record ParsedBeat(
        int ChapterOrder,
        string Type,
        double Tension,
        string Description
    );

    /// <summary>
    /// 解析 Turnpoints 文本为 beat 列表。
    /// </summary>
    /// <param name="turnpoints">自由文本</param>
    /// <param name="validOrders">合法章号集（来自已加载 chapters）；空集 = 跳过章号校验</param>
    public static IReadOnlyList<ParsedBeat> Parse(string? turnpoints, IReadOnlySet<int> validOrders)
    {
        if (string.IsNullOrWhiteSpace(turnpoints))
            return [];

        var beats = new List<ParsedBeat>();
        foreach (var m in EntryPattern().Matches(turnpoints))
        {
            // 两个交替分支：中文「第N章：desc」或「chN./N.：desc」
            int order;
            string desc;
            if (m is Match { Groups.Count: > 0 } match)
            {
                if (match.Groups[1].Success)
                {
                    order = int.Parse(match.Groups[1].Value);
                    desc = match.Groups[2].Value;
                }
                else if (match.Groups[3].Success)
                {
                    order = int.Parse(match.Groups[3].Value);
                    desc = match.Groups[4].Value;
                }
                else
                {
                    continue;
                }
            }
            else
            {
                continue;
            }

            if (validOrders.Count > 0 && !validOrders.Contains(order))
                continue; // 章号不存在 → 跳过（宁可不猜）
            var trimmed = desc.Trim().TrimEnd('。', '.');
            if (trimmed.Length == 0)
                continue;

            var (type, tension) = Classify(trimmed);
            beats.Add(new ParsedBeat(order, type, tension, trimmed));
        }
        return beats;
    }

    /// <summary>
    /// 中文关键词确定性分类（首个命中优先，顺序即优先级）。
    /// tension 默认值与云端 OutlineMetrics 的 cell 语义对齐。
    /// </summary>
    private static (string Type, double Tension) Classify(string description)
    {
        foreach (var (keywords, type, tension) in s_rules)
        {
            if (keywords.Any(description.Contains))
                return (type, tension);
        }
        return ("progress", 0.5);
    }

    private static readonly (string[] Keywords, string Type, double Tension)[] s_rules =
    [
        (["回收", "兑现", "呼应", "揭晓答案", "填坑"], "payoff", 0.4),
        (["揭示", "揭露", "真相", "揭开", "曝光"], "reveal", 0.9),
        (["高潮", "决战", "大战", "生死", "巅峰"], "critical", 1.0),
        (["埋下", "伏笔", "铺垫", "暗线", "埋设"], "plant", 0.2),
        (["误导", "假象", "反转线索"], "misdirect", 0.7),
    ];
}
