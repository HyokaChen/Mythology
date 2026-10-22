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
/// 读者卷入度与写作境界 — 期待感+好奇心+写作境界检测：评估读者卷入度
/// CHANGE_07：本地分析引擎已删除，全部通过 IFictionHarnessClient 调云端。
/// </summary>
public sealed class EvaluateReaderEngagementTool(
    INovelStore store,
    IFictionHarnessClient? cloudHarness = null
) : CloudAnalysisTool<EvaluateReaderEngagementParams, ReaderEngagementDetails>(store, cloudHarness)
{
    public override string Name => "evaluate_reader_engagement";
    public override string Label => "读者卷入度与写作境界";
    public override string Description => "期待感+好奇心+写作境界检测：评估读者卷入度";
    public override ToolCategory Category => ToolCategory.EntityRead;
    protected override string Capability => StoryCheckCapability;
    protected override string Dimension => "读者参与";

    public override async Task<AgentToolResult<ReaderEngagementDetails>> ExecuteAsync(
        string toolCallId,
        EvaluateReaderEngagementParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<ReaderEngagementDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var (chapters, novel) = await LoadChaptersAsync(args.NovelId, args.ChapterIds, ct);
        ThrowIfCancelled(ct);
        if (chapters.Count == 0)
            return new AgentToolResult<ReaderEngagementDetails>
            {
                Content = [new TextContent("暂无章节内容可分析")],
                IsError = true,
            };
        var cloudResult = await ExecuteCloudAnalysisAsync(chapters, novel!, ct);
        if (cloudResult is null)
            return CloudUnavailableResult();
        var details = MapCloudResult(cloudResult.Value);
        var report = BuildReportText(details);
        return new AgentToolResult<ReaderEngagementDetails>
        {
            Content = [new TextContent(report)],
            Details = details,
        };
    }

    private static ReaderEngagementDetails MapCloudResult(JsonElement element)
    {
        var dim = GetDimension(element, "engagement");
        return new ReaderEngagementDetails
        {
            OverallEngagement = GetDimensionScore(dim, "overall-engagement"),
        };
    }

    private static string BuildReportText(ReaderEngagementDetails d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🔍 读者卷入度与写作境界报告");
        sb.AppendLine();
        sb.AppendLine($"**综合评分**: **{d.OverallEngagement:F1}** / 100");
        sb.AppendLine();
        sb.AppendLine("---");
        return sb.ToString();
    }
}

public sealed record EvaluateReaderEngagementParams
{
    [Description("小说项目ID")]
    public string NovelId { get; init; } = "";

    [Description("章节ID列表")]
    [JsonConverter(typeof(ChapterIdsConverter))]
    public List<string>? ChapterIds { get; init; }
}

public sealed record ReaderEngagementDetails
{
    public double OverallEngagement { get; init; }
    public double ImmersionScore { get; init; }
    public double AnticipationScore { get; init; }
    public double CuriosityScore { get; init; }
    public double ResonanceScore { get; init; }
    public double DeepImmersionScore { get; init; }
    public int DetectedWritingLevel { get; init; }
    public string DetectedWritingLabel { get; init; } = "";
    public List<EngagementIssueEntry> Issues { get; init; } = [];
    public List<string> Strengths { get; init; } = [];
}

public sealed record EngagementIssueEntry
{
    public string Dimension { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Suggestion { get; init; }
}
