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
/// 伏笔建议 — 伏笔布局+线索规划+回顾呼应：检测埋设/回收/巧合
/// CHANGE_07：本地分析引擎已删除，全部通过 IFictionHarnessClient 调云端。
/// </summary>
public sealed class SuggestForeshadowingTool(
    INovelStore store,
    IFictionHarnessClient? cloudHarness = null
) : CloudAnalysisTool<SuggestForeshadowingParams, ForeshadowingDetails>(store, cloudHarness)
{
    public override string Name => "suggest_foreshadowing";
    public override string Label => "伏笔建议";
    public override string Description => "伏笔布局+线索规划+回顾呼应：检测埋设/回收/巧合";
    public override ToolCategory Category => ToolCategory.EntityRead;
    protected override string Capability => StoryCheckCapability;
    protected override string Dimension => "伏笔";
    public override string[] Aliases =>
        ["伏笔建议", "埋设伏笔", "伏笔布局", "伏笔回收", "线索规划", "回顾呼应"];
    public override string[] ExampleQueries =>
        ["为第3章埋一个伏笔", "建议回收第1章的伏笔", "规划伏笔回收时间点"];

    public override async Task<AgentToolResult<ForeshadowingDetails>> ExecuteAsync(
        string toolCallId,
        SuggestForeshadowingParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<ForeshadowingDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var (chapters, novel) = await LoadChaptersAsync(args.NovelId, args.ChapterIds, ct);
        ThrowIfCancelled(ct);
        if (chapters.Count == 0)
            return new AgentToolResult<ForeshadowingDetails>
            {
                Content = [new TextContent("暂无章节内容可分析")],
                IsError = true,
            };
        var cloudResult = await ExecuteCloudAnalysisAsync(chapters, novel!, ct);
        if (cloudResult is null)
            return CloudUnavailableResult();
        var details = MapCloudResult(cloudResult.Value);
        var report = BuildReportText(details);
        return new AgentToolResult<ForeshadowingDetails>
        {
            Content = [new TextContent(report)],
            Details = details,
        };
    }

    private static ForeshadowingDetails MapCloudResult(JsonElement element)
    {
        var dim = GetDimension(element, "threads");
        return new ForeshadowingDetails { OverallScore = GetDimensionScore(dim) };
    }

    private static string BuildReportText(ForeshadowingDetails d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🔍 伏笔建议报告");
        sb.AppendLine();
        sb.AppendLine($"**综合评分**: **{d.OverallScore:F1}** / 100");
        sb.AppendLine();
        sb.AppendLine("---");
        return sb.ToString();
    }
}

public sealed record SuggestForeshadowingParams
{
    [Description("小说项目ID")]
    public string NovelId { get; init; } = "";

    [Description("章节ID列表")]
    [JsonConverter(typeof(ChapterIdsConverter))]
    public List<string>? ChapterIds { get; init; }
}

public sealed record ForeshadowingDetails
{
    public double OverallScore { get; init; }
    public double PlantScore { get; init; }
    public double PayoffScore { get; init; }
    public double CoincidenceScore { get; init; }
    public int PlantCount { get; init; }
    public int PayoffCount { get; init; }
    public int CoincidenceCount { get; init; }
    public double PlantPayoffRatio { get; init; }
    public List<ForeshadowingIssueEntry> Issues { get; init; } = [];
    public List<string> Strengths { get; init; } = [];
}

public sealed record ForeshadowingIssueEntry
{
    public string Dimension { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Suggestion { get; init; }
}
