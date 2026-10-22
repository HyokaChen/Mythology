using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Core.Cloud;
using Core.Data;
using Core.Shared;
using Core.Tools;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 开篇强度评估 — 检测开篇四维度：主角登场+悬念+脱衣舞原则+技术手法
/// CHANGE_07：本地分析引擎已删除，全部通过 IFictionHarnessClient 调云端。
/// </summary>
public sealed class EvaluateOpeningTool(
    INovelStore store,
    IFictionHarnessClient? cloudHarness = null
) : CloudAnalysisTool<EvaluateOpeningParams, OpeningDetails>(store, cloudHarness)
{
    public override string Name => "evaluate_opening_strength";
    public override string Label => "开篇强度评估";
    public override string Description => "检测开篇四维度：主角登场+悬念+脱衣舞原则+技术手法";
    public override ToolCategory Category => ToolCategory.EntityRead;
    protected override string Capability => StoryCheckCapability;
    protected override string Dimension => "开篇";

    public override async Task<AgentToolResult<OpeningDetails>> ExecuteAsync(
        string toolCallId,
        EvaluateOpeningParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<OpeningDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var (chapters, novel) = await LoadChaptersAsync(args.NovelId, null, ct);
        ThrowIfCancelled(ct);
        if (chapters.Count == 0)
            return new AgentToolResult<OpeningDetails>
            {
                Content = [new TextContent("暂无章节内容可分析")],
                IsError = true,
            };
        var cloudResult = await ExecuteCloudAnalysisAsync(chapters, novel!, ct);
        if (cloudResult is null)
            return CloudUnavailableResult();
        var details = MapCloudResult(cloudResult.Value);
        var report = BuildReportText(details);
        return new AgentToolResult<OpeningDetails>
        {
            Content = [new TextContent(report)],
            Details = details,
        };
    }

    private static OpeningDetails MapCloudResult(JsonElement element)
    {
        var dim = GetDimension(element, "opening");
        return new OpeningDetails { OverallScore = GetDimensionScore(dim) };
    }

    private static string BuildReportText(OpeningDetails d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🔍 开篇强度评估报告");
        sb.AppendLine();
        sb.AppendLine($"**综合评分**: **{d.OverallScore:F1}** / 100");
        sb.AppendLine();
        sb.AppendLine("---");
        return sb.ToString();
    }
}

public sealed record EvaluateOpeningParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";
}

public sealed record OpeningDetails
{
    public double OverallScore { get; init; }
    public double ProtagonistEntryScore { get; init; }
    public double SuspenseScore { get; init; }
    public double TechniqueScore { get; init; }
    public double StripTeaseScore { get; init; }
    public int ProtagonistEntryChapter { get; init; }
    public bool HasFlashback { get; init; }
    public bool HasOmen { get; init; }
    public bool HasPrologue { get; init; }
    public bool HasTimeBomb { get; init; }
    public List<OpeningIssueEntry> Issues { get; init; } = [];
    public List<string> Strengths { get; init; } = [];
}

public sealed record OpeningIssueEntry
{
    public string Dimension { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Suggestion { get; init; }
}
