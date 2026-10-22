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
/// 描写质量检测 — 五感描写+四忌检测：视觉/听觉/触觉/内感/味嗅觉覆盖度
/// CHANGE_07：本地分析引擎已删除，全部通过 IFictionHarnessClient 调云端。
/// </summary>
public sealed class DetectDescriptionQualityTool(
    INovelStore store,
    IFictionHarnessClient? cloudHarness = null
)
    : CloudAnalysisTool<DetectDescriptionQualityParams, DescriptionQualityDetails>(
        store,
        cloudHarness
    )
{
    public override string Name => "detect_description_quality";
    public override string Label => "描写质量检测";
    public override string Description => "五感描写+四忌检测：视觉/听觉/触觉/内感/味嗅觉覆盖度";
    public override ToolCategory Category => ToolCategory.EntityRead;
    protected override string Capability => StoryCheckCapability;
    protected override string Dimension => "描写";

    public override async Task<AgentToolResult<DescriptionQualityDetails>> ExecuteAsync(
        string toolCallId,
        DetectDescriptionQualityParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<DescriptionQualityDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var (chapters, novel) = await LoadChaptersAsync(args.NovelId, args.ChapterIds, ct);
        ThrowIfCancelled(ct);
        if (chapters.Count == 0)
            return new AgentToolResult<DescriptionQualityDetails>
            {
                Content = [new TextContent("暂无章节内容可分析")],
                IsError = true,
            };
        var cloudResult = await ExecuteCloudAnalysisAsync(chapters, novel!, ct);
        if (cloudResult is null)
            return CloudUnavailableResult();
        var details = MapCloudResult(cloudResult.Value);
        var report = BuildReportText(details);
        return new AgentToolResult<DescriptionQualityDetails>
        {
            Content = [new TextContent(report)],
            Details = details,
        };
    }

    private static DescriptionQualityDetails MapCloudResult(JsonElement element)
    {
        var dim = GetDimension(element, "description");
        return new DescriptionQualityDetails { OverallScore = GetDimensionScore(dim) };
    }

    private static string BuildReportText(DescriptionQualityDetails d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🔍 描写质量检测报告");
        sb.AppendLine();
        sb.AppendLine($"**综合评分**: **{d.OverallScore:F1}** / 100");
        sb.AppendLine();
        sb.AppendLine("---");
        return sb.ToString();
    }
}

public sealed record DetectDescriptionQualityParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("要分析的章节ID列表，留空则分析全部章节")]
    [JsonConverter(typeof(ChapterIdsConverter))]
    public List<string>? ChapterIds { get; init; }
}

public sealed record DescriptionQualityDetails
{
    public double OverallScore { get; init; }
    public double TabooScore { get; init; }
    public double FiveSenseScore { get; init; }
    public bool HasVisual { get; init; }
    public bool HasAuditory { get; init; }
    public bool HasTactile { get; init; }
    public bool HasInnerSense { get; init; }
    public bool HasTasteSmell { get; init; }
    public int SensesEngaged { get; init; }
    public List<DescriptionIssueEntry> Issues { get; init; } = [];
    public List<string> Strengths { get; init; } = [];
}

public sealed record DescriptionIssueEntry
{
    public string Dimension { get; init; } = "";
    public string TabooType { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Description { get; init; } = "";
    public string Evidence { get; init; } = "";
    public string? Suggestion { get; init; }
}
