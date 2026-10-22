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
/// 节奏质量分析 — 过山车原理+句子长短调控：检测节奏波动性、张力曲线、连续高潮/平淡警告
/// CHANGE_07：本地分析引擎已删除，全部通过 IFictionHarnessClient 调云端。
/// </summary>
public sealed class AnalyzePacingTool(INovelStore store, IFictionHarnessClient? cloudHarness = null)
    : CloudAnalysisTool<AnalyzePacingParams, PacingDetails>(store, cloudHarness)
{
    public override string Name => "analyze_pacing_quality";
    public override string Label => "节奏质量分析";
    public override string Description =>
        "过山车原理+句子长短调控：检测节奏波动性、张力曲线、连续高潮/平淡警告";
    public override ToolCategory Category => ToolCategory.EntityRead;
    protected override string Capability => StoryCheckCapability;
    protected override string Dimension => "节奏";

    public override async Task<AgentToolResult<PacingDetails>> ExecuteAsync(
        string toolCallId,
        AnalyzePacingParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<PacingDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var (chapters, novel) = await LoadChaptersAsync(args.NovelId, args.ChapterIds, ct);
        ThrowIfCancelled(ct);
        if (chapters.Count == 0)
            return new AgentToolResult<PacingDetails>
            {
                Content = [new TextContent("暂无章节内容可分析")],
                IsError = true,
            };
        var cloudResult = await ExecuteCloudAnalysisAsync(chapters, novel!, ct);
        if (cloudResult is null)
            return CloudUnavailableResult();
        var details = MapCloudResult(cloudResult.Value);
        var report = BuildReportText(details);
        return new AgentToolResult<PacingDetails>
        {
            Content = [new TextContent(report)],
            Details = details,
        };
    }

    private static PacingDetails MapCloudResult(JsonElement element)
    {
        var dim = GetDimension(element, "pace");
        return new PacingDetails { OverallScore = GetDimensionScore(dim) };
    }

    private static string BuildReportText(PacingDetails d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🔍 节奏质量分析报告");
        sb.AppendLine();
        sb.AppendLine($"**综合评分**: **{d.OverallScore:F1}** / 100");
        sb.AppendLine();
        sb.AppendLine("---");
        return sb.ToString();
    }
}

public sealed record AnalyzePacingParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("要分析的章节ID列表，留空则分析全部章节")]
    [JsonConverter(typeof(ChapterIdsConverter))]
    public List<string>? ChapterIds { get; init; }
}

public sealed record PacingDetails
{
    public double OverallScore { get; init; }
    public double RhythmVarianceScore { get; init; }
    public double SentencePacingScore { get; init; }
    public double ParagraphPacingScore { get; init; }
    public double TensionCurveScore { get; init; }
    public List<PacingSegmentEntry> Segments { get; init; } = [];
    public List<PacingIssueEntry> Issues { get; init; } = [];
    public List<string> Strengths { get; init; } = [];
}

public sealed record PacingSegmentEntry
{
    public int ChapterOrder { get; init; }
    public string ChapterTitle { get; init; } = "";
    public string Level { get; init; } = "";
    public double Intensity { get; init; }
    public double AvgSentenceLen { get; init; }
    public double AvgParagraphLen { get; init; }
}

public sealed record PacingIssueEntry
{
    public string Dimension { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Suggestion { get; init; }
}
