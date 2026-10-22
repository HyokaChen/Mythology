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
/// 常见错误检测 — 8种毒性模式检测：时代错位/技术穿越/地理错误/法律错误/医学错误/军事错误/文化错误/逻辑矛盾
/// CHANGE_07：本地分析引擎已删除，全部通过 IFictionHarnessClient 调云端。
/// </summary>
public sealed class DetectAvoidableMistakesTool(
    INovelStore store,
    IFictionHarnessClient? cloudHarness = null
) : CloudAnalysisTool<DetectAvoidableMistakesParams, AvoidableMistakesDetails>(store, cloudHarness)
{
    public override string Name => "detect_avoidable_mistakes";
    public override string Label => "常见错误检测";
    public override string Description =>
        "8种毒性模式检测：时代错位/技术穿越/地理错误/法律错误/医学错误/军事错误/文化错误/逻辑矛盾";
    public override ToolCategory Category => ToolCategory.EntityRead;
    protected override string Capability => StoryCheckCapability;
    protected override string Dimension => "常识错误";

    public override async Task<AgentToolResult<AvoidableMistakesDetails>> ExecuteAsync(
        string toolCallId,
        DetectAvoidableMistakesParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<AvoidableMistakesDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var (chapters, novel) = await LoadChaptersAsync(args.NovelId, args.ChapterIds, ct);
        ThrowIfCancelled(ct);
        if (chapters.Count == 0)
            return new AgentToolResult<AvoidableMistakesDetails>
            {
                Content = [new TextContent("暂无章节内容可分析")],
                IsError = true,
            };
        var cloudResult = await ExecuteCloudAnalysisAsync(chapters, novel!, ct);
        if (cloudResult is null)
            return CloudUnavailableResult();
        var details = MapCloudResult(cloudResult.Value);
        var report = BuildReportText(details);
        return new AgentToolResult<AvoidableMistakesDetails>
        {
            Content = [new TextContent(report)],
            Details = details,
        };
    }

    private static AvoidableMistakesDetails MapCloudResult(JsonElement element)
    {
        var dim = GetDimension(element, "plausibility");
        return new AvoidableMistakesDetails { OverallScore = GetDimensionScore(dim) };
    }

    private static string BuildReportText(AvoidableMistakesDetails d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🔍 常见错误检测报告");
        sb.AppendLine();
        sb.AppendLine($"**综合评分**: **{d.OverallScore:F1}** / 100");
        sb.AppendLine();
        sb.AppendLine("---");
        return sb.ToString();
    }
}

public sealed record DetectAvoidableMistakesParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("要分析的章节ID列表，留空则分析全部章节")]
    [JsonConverter(typeof(ChapterIdsConverter))]
    public List<string>? ChapterIds { get; init; }
}

public sealed record AvoidableMistakesDetails
{
    public double OverallScore { get; init; }
    public double ToxicScore { get; init; }
    public double CommonMistakeScore { get; init; }
    public int TotalToxicHits { get; init; }
    public int TotalMistakeHits { get; init; }
    public List<ToxicHitEntry> ToxicHits { get; init; } = [];
    public List<MistakeHitEntry> MistakeHits { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public sealed record ToxicHitEntry
{
    public string Type { get; init; } = "";
    public string Label { get; init; } = "";
    public string Keyword { get; init; } = "";
    public string Harm { get; init; } = "";
    public string Context { get; init; } = "";
}

public sealed record MistakeHitEntry
{
    public string Category { get; init; } = "";
    public string Label { get; init; } = "";
    public string Keyword { get; init; } = "";
    public string Severity { get; init; } = "";
}
