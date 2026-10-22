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
/// 对话质量分析 — 对话自然度+可信度+承载性检测：说教/信息倾泻/过长/无潜台词
/// CHANGE_07：本地分析引擎已删除，全部通过 IFictionHarnessClient 调云端。
/// </summary>
public sealed class AnalyzeDialogueQualityTool(
    INovelStore store,
    IFictionHarnessClient? cloudHarness = null
) : CloudAnalysisTool<AnalyzeDialogueQualityParams, DialogueQualityDetails>(store, cloudHarness)
{
    public override string Name => "analyze_dialogue_quality";
    public override string Label => "对话质量分析";
    public override string Description =>
        "对话自然度+可信度+承载性检测：说教/信息倾泻/过长/无潜台词";
    public override ToolCategory Category => ToolCategory.EntityRead;
    protected override string Capability => StoryCheckCapability;
    protected override string Dimension => "对话";

    public override async Task<AgentToolResult<DialogueQualityDetails>> ExecuteAsync(
        string toolCallId,
        AnalyzeDialogueQualityParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<DialogueQualityDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var (chapters, novel) = await LoadChaptersAsync(args.NovelId, args.ChapterIds, ct);
        ThrowIfCancelled(ct);
        if (chapters.Count == 0)
            return new AgentToolResult<DialogueQualityDetails>
            {
                Content = [new TextContent("暂无章节内容可分析")],
                IsError = true,
            };
        var cloudResult = await ExecuteCloudAnalysisAsync(chapters, novel!, ct);
        if (cloudResult is null)
            return CloudUnavailableResult();
        var details = MapCloudResult(cloudResult.Value);
        var report = BuildReportText(details);
        return new AgentToolResult<DialogueQualityDetails>
        {
            Content = [new TextContent(report)],
            Details = details,
        };
    }

    private static DialogueQualityDetails MapCloudResult(JsonElement element)
    {
        var dim = GetDimension(element, "dialogue");
        return new DialogueQualityDetails { OverallScore = GetDimensionScore(dim) };
    }

    private static string BuildReportText(DialogueQualityDetails d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🔍 对话质量分析报告");
        sb.AppendLine();
        sb.AppendLine($"**综合评分**: **{d.OverallScore:F1}** / 100");
        sb.AppendLine();
        sb.AppendLine("---");
        return sb.ToString();
    }
}

public sealed record AnalyzeDialogueQualityParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("G要分析的章节ID列表，留空则分析全部章节")]
    [JsonConverter(typeof(ChapterIdsConverter))]
    public List<string>? ChapterIds { get; init; }
}

public sealed record DialogueQualityDetails
{
    public double OverallScore { get; init; }
    public double NaturalnessScore { get; init; }
    public double CredibilityScore { get; init; }
    public double BearingScore { get; init; }
    public double DialogueRatio { get; init; }
    public int DialogueCount { get; init; }
    public int PreachyCount { get; init; }
    public int InfoDumpCount { get; init; }
    public int LongDialogueCount { get; init; }
    public int NoSubtextCount { get; init; }
    public List<DialogueIssueEntry> Issues { get; init; } = [];
    public List<string> Strengths { get; init; } = [];
}

public sealed record DialogueIssueEntry
{
    public string Dimension { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Suggestion { get; init; }
}
