using Core.Cloud;

namespace CloudDiagnosis;

/// <summary>本地情节线事实（纯本地廉价启发式数据，不来自云端 payload）。</summary>
public sealed record PlotlineFact(
    string Name,
    int? PlannedRevealOrder,
    int? FirstAppearOrder = null
);

/// <summary>本地规则输入 — 由调用方从 INovelStore / WritingProfile 组装。</summary>
public sealed record LocalFacts
{
    public int MaxChapterOrder { get; init; }
    public IReadOnlyList<PlotlineFact> OpenPlotlines { get; init; } = [];
    public int ActiveInfoGaps { get; init; }
    public double? EntropyDelta { get; init; }
}

/// <summary>
/// 只保留依赖本地事实的规则；云端维度、规则码和阈值判定全部由服务端报告承担。
/// </summary>
public static class SignalRules
{
    private const int MinOpenPlotlines = 2;
    private const int MaxOpenPlotlines = 6;
    private const int OverdueFirstAppearGap = 30;

    public static IReadOnlyList<DiagnosisFinding> Evaluate(LocalFacts facts)
    {
        var findings = new List<DiagnosisFinding>();

        foreach (var plotline in facts.OpenPlotlines)
        {
            if (plotline.PlannedRevealOrder is { } planned && facts.MaxChapterOrder >= planned)
            {
                var gap = facts.MaxChapterOrder - planned;
                findings.Add(
                    Local(
                        "A-3",
                        "情节",
                        "medium",
                        $"伏笔「{plotline.Name}」计划于第 {planned} 章揭示，当前已至第 {facts.MaxChapterOrder} 章"
                            + (gap > 0 ? $"（已逾期 {gap} 章）" : "")
                            + "。",
                        $"立即安排「{plotline.Name}」回收，或插入渐近暗示段落维持存在感。"
                    )
                );
            }
            else if (
                plotline.FirstAppearOrder is { } first
                && facts.MaxChapterOrder - first > OverdueFirstAppearGap
            )
            {
                findings.Add(
                    Local(
                        "A-3",
                        "情节",
                        "medium",
                        $"伏笔「{plotline.Name}」自第 {first} 章埋设已逾 {facts.MaxChapterOrder - first} 章未回收。",
                        $"立即安排「{plotline.Name}」回收，或插入渐近暗示段落维持存在感。"
                    )
                );
            }
        }

        var openCount = facts.OpenPlotlines.Count;
        if (openCount < MinOpenPlotlines)
        {
            var suggestion = facts.EntropyDelta switch
            {
                < 0 => "叙事正在收束，宜优先收线或养护既有暗线，暂缓埋新线。",
                _ => "适合在本节点埋设新线，维持暗线存在感。",
            };
            findings.Add(Local("A-4", "情节", "low", $"活跃暗线仅 {openCount} 条。", suggestion));
        }
        else if (openCount > MaxOpenPlotlines)
        {
            findings.Add(
                Local(
                    "A-4",
                    "情节",
                    "medium",
                    $"活跃暗线达 {openCount} 条，密度偏高。",
                    "停止新埋，优先养护或回收既有暗线。"
                )
            );
        }

        if (facts.ActiveInfoGaps == 0 && openCount > 0)
        {
            findings.Add(
                Local(
                    "A-5",
                    "情节",
                    "medium",
                    "本章信息差计数为 0，但仍有未揭晓伏笔，疑似提前泄底。",
                    $"复核「{facts.OpenPlotlines[0].Name}」的信息投放节奏。"
                )
            );
        }

        return findings;
    }

    private static DiagnosisFinding Local(
        string trigger,
        string category,
        string severity,
        string problem,
        string suggestion
    ) =>
        new(
            "",
            category,
            severity,
            severity == "high" ? "严重"
                : severity == "medium" ? "中等"
                : "轻微",
            "suggest",
            "建议修改",
            "全章",
            problem,
            null,
            suggestion
        )
        {
            Trigger = trigger,
        };
}
