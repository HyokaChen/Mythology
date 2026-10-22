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
/// 冲突强度评估 — 三维度冲突检测：内心冲突+人际对抗+环境冲突，评估场景结构与冲突密度
/// CHANGE_07：本地分析引擎已删除，全部通过 IFictionHarnessClient 调云端。
/// </summary>
public sealed class EvaluateConflictTool(
    INovelStore store,
    IFictionHarnessClient? cloudHarness = null
) : CloudAnalysisTool<EvaluateConflictParams, ConflictDetails>(store, cloudHarness)
{
    public override string Name => "evaluate_conflict_intensity";
    public override string Label => "冲突强度评估";
    public override string Description =>
        "三维度冲突检测：内心冲突+人际对抗+环境冲突，评估场景结构与冲突密度";
    public override ToolCategory Category => ToolCategory.EntityRead;
    protected override string Capability => StoryCheckCapability;
    protected override string Dimension => "冲突";

    public override async Task<AgentToolResult<ConflictDetails>> ExecuteAsync(
        string toolCallId,
        EvaluateConflictParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<ConflictDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var (chapters, novel) = await LoadChaptersAsync(args.NovelId, args.ChapterIds, ct);
        ThrowIfCancelled(ct);
        if (chapters.Count == 0)
            return new AgentToolResult<ConflictDetails>
            {
                Content = [new TextContent("暂无章节内容可分析")],
                IsError = true,
            };
        var cloudResult = await ExecuteCloudAnalysisAsync(chapters, novel!, ct);
        if (cloudResult is null)
            return CloudUnavailableResult();
        var details = MapCloudResult(cloudResult.Value);
        var report = BuildReportText(details);
        return new AgentToolResult<ConflictDetails>
        {
            Content = [new TextContent(report)],
            Details = details,
        };
    }

    private static ConflictDetails MapCloudResult(JsonElement element)
    {
        var dim = GetDimension(element, "tension");
        return new ConflictDetails { OverallScore = GetDimensionScore(dim) };
    }

    private static string BuildReportText(ConflictDetails d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🔍 冲突强度评估报告");
        sb.AppendLine();
        sb.AppendLine($"**综合评分**: **{d.OverallScore:F1}** / 100");
        sb.AppendLine();
        sb.AppendLine("---");
        return sb.ToString();
    }
}

public sealed record EvaluateConflictParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("要分析的章节ID列表，留空则分析全部章节")]
    [JsonConverter(typeof(ChapterIdsConverter))]
    public List<string>? ChapterIds { get; init; }
}

public sealed record ConflictDetails
{
    public double OverallScore { get; init; }
    public double InnerConflictScore { get; init; }
    public double InterpersonalConflictScore { get; init; }
    public double EnvironmentalConflictScore { get; init; }
    public double SceneStructureScore { get; init; }
    public List<ConflictHitEntry> TopHits { get; init; } = [];
    public List<ConflictIssueEntry> Issues { get; init; } = [];
    public List<string> Strengths { get; init; } = [];
}

public sealed record ConflictHitEntry
{
    public string Type { get; init; } = "";
    public double Weight { get; init; }
    public string Keyword { get; init; } = "";
    public string Context { get; init; } = "";
}

public sealed record ConflictIssueEntry
{
    public string Dimension { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Suggestion { get; init; }
}
