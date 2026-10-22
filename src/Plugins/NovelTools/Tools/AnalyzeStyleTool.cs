using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Core.Cloud;
using Core.Data;
using Core.Shared;
using Core.Tools;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 写作风格分析 — 句式/词汇/段落/对话/综合风格分析
/// CHANGE_07：本地分析引擎已删除，全部通过 IFictionHarnessClient 调云端。
/// </summary>
public sealed class AnalyzeStyleTool(INovelStore store, IFictionHarnessClient? cloudHarness = null)
    : CloudAnalysisTool<AnalyzeStyleParams, StyleAnalysisDetails>(store, cloudHarness)
{
    public override string Name => "analyze_style";
    public override string Label => "写作风格分析";
    public override string Description => "句式/词汇/段落/对话/综合风格分析";
    public override ToolCategory Category => ToolCategory.EntityRead;
    protected override string Capability => StoryCheckCapability;
    protected override string Dimension => "风格";

    public override async Task<AgentToolResult<StyleAnalysisDetails>> ExecuteAsync(
        string toolCallId,
        AnalyzeStyleParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<StyleAnalysisDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var (chapters, novel) = await LoadChaptersAsync(args.NovelId, args.ChapterIds, ct);
        ThrowIfCancelled(ct);
        if (chapters.Count == 0)
            return new AgentToolResult<StyleAnalysisDetails>
            {
                Content = [new TextContent("暂无章节内容可分析")],
                IsError = true,
            };
        var cloudResult = await ExecuteCloudAnalysisAsync(chapters, novel!, ct);
        if (cloudResult is null)
            return CloudUnavailableResult();
        var details = MapCloudResult(cloudResult.Value);
        var report = BuildReportText(details);
        return new AgentToolResult<StyleAnalysisDetails>
        {
            Content = [new TextContent(report)],
            Details = details,
        };
    }

    private static StyleAnalysisDetails MapCloudResult(JsonElement element)
    {
        var dim = GetDimension(element, "style");
        return new StyleAnalysisDetails
        {
            AvgSentenceLength = GetDimensionScore(dim, "avg-sentence-length"),
        };
    }

    private static string BuildReportText(StyleAnalysisDetails d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🔍 写作风格分析报告");
        sb.AppendLine();
        sb.AppendLine($"**综合评分**: **{d.AvgSentenceLength:F1}** / 100");
        sb.AppendLine();
        sb.AppendLine("---");
        return sb.ToString();
    }
}

public sealed record AnalyzeStyleParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("要分析的章节ID列表，留空则分析全部章节")]
    [JsonConverter(typeof(ChapterIdsConverter))]
    public List<string>? ChapterIds { get; init; }

    [Description(
        "分析维度：sentence（句式）/ vocabulary（词汇）/ paragraph（段落）/ dialogue（对话）/ comprehensive（综合）"
    )]
    public string? AnalysisType { get; init; }
}

public sealed record StyleAnalysisDetails
{
    public string AnalysisId { get; init; } = ""; // 分析ID
    public int ChapterCount { get; init; } // 分析章节数
    public int TotalWords { get; init; } // 总字数
    public double AvgSentenceLength { get; init; } // 平均句长
    public double DialogueRatio { get; init; } // 对话占比
    public double VocabRichness { get; init; } // 词汇丰富度
    public double PassiveVoiceRatio { get; init; } // 被动句比例
    public double ShortSentenceRatio { get; init; } // 短句比例
    public double LongSentenceRatio { get; init; } // 长句比例
    public double AvgParagraphLength { get; init; } // 平均段长
    public string StyleLabel { get; init; } = ""; // 风格标签
    public List<TopWordEntry> TopAdjectives { get; init; } = []; // 高频形容词
    public List<TopWordEntry> TopVerbs { get; init; } = []; // 高频动词
    public List<TopWordEntry> TopNouns { get; init; } = []; // 高频名词
}

public sealed record TopWordEntry
{
    public string Word { get; init; } = ""; // 词
    public int Count { get; init; } // 出现次数
    public double Frequency { get; init; } // 频率（每万字）
}
