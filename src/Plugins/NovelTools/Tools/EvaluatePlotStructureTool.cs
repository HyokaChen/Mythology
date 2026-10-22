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
/// 情节结构评估 — 情节结构检测：三幕/英雄之旅/起承转合/多线并行，评估结构契合度与完整度
/// CHANGE_07：本地分析引擎已删除，全部通过 IFictionHarnessClient 调云端。
/// </summary>
public sealed class EvaluatePlotStructureTool(
    INovelStore store,
    IFictionHarnessClient? cloudHarness = null
) : CloudAnalysisTool<EvaluatePlotStructureParams, PlotStructureDetails>(store, cloudHarness)
{
    public override string Name => "evaluate_plot_structure";
    public override string Label => "情节结构评估";
    public override string Description =>
        "情节结构检测：三幕/英雄之旅/起承转合/多线并行，评估结构契合度与完整度";
    public override ToolCategory Category => ToolCategory.EntityRead;
    protected override string Capability => StoryCheckCapability;
    protected override string Dimension => "结构";

    public override async Task<AgentToolResult<PlotStructureDetails>> ExecuteAsync(
        string toolCallId,
        EvaluatePlotStructureParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<PlotStructureDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var (chapters, novel) = await LoadChaptersAsync(args.NovelId, args.ChapterIds, ct);
        ThrowIfCancelled(ct);
        if (chapters.Count == 0)
            return new AgentToolResult<PlotStructureDetails>
            {
                Content = [new TextContent("暂无章节内容可分析")],
                IsError = true,
            };
        var cloudResult = await ExecuteCloudAnalysisAsync(chapters, novel!, ct);
        if (cloudResult is null)
            return CloudUnavailableResult();
        var details = MapCloudResult(cloudResult.Value);
        var report = BuildReportText(details);
        return new AgentToolResult<PlotStructureDetails>
        {
            Content = [new TextContent(report)],
            Details = details,
        };
    }

    private static PlotStructureDetails MapCloudResult(JsonElement element)
    {
        var dim = GetDimension(element, "narrative");
        return new PlotStructureDetails { OverallScore = GetDimensionScore(dim) };
    }

    private static string BuildReportText(PlotStructureDetails d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🔍 情节结构评估报告");
        sb.AppendLine();
        sb.AppendLine($"**综合评分**: **{d.OverallScore:F1}** / 100");
        sb.AppendLine();
        sb.AppendLine("---");
        return sb.ToString();
    }
}

public sealed record EvaluatePlotStructureParams
{
    [Description("小说项目ID")]
    public string NovelId { get; init; } = "";

    [Description("章节ID列表")]
    [JsonConverter(typeof(ChapterIdsConverter))]
    public List<string>? ChapterIds { get; init; }
}

public sealed record PlotStructureDetails
{
    public double OverallScore { get; init; }
    public string DetectedStructure { get; init; } = "";
    public string DetectedStructureLabel { get; init; } = "";
    public double StructureFitScore { get; init; }
    public double CompletenessScore { get; init; }
    public Dictionary<string, double> StructureScores { get; init; } = [];
    public Dictionary<string, string> StructureScoreLabels { get; init; } = [];
    public List<PlotStructureIssueEntry> Issues { get; init; } = [];
    public List<string> Strengths { get; init; } = [];
}

public sealed record PlotStructureIssueEntry
{
    public string Dimension { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Suggestion { get; init; }
}
