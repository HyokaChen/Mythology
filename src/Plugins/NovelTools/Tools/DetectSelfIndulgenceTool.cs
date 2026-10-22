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
/// 自我感动/场景转换/信息密度 — 自我感动+场景过渡+信息密度检测
/// CHANGE_07：本地分析引擎已删除，全部通过 IFictionHarnessClient 调云端。
/// </summary>
public sealed class DetectSelfIndulgenceTool(
    INovelStore store,
    IFictionHarnessClient? cloudHarness = null
) : CloudAnalysisTool<DetectSelfIndulgenceParams, SelfIndulgenceDetails>(store, cloudHarness)
{
    public override string Name => "detect_self_indulgence";
    public override string Label => "自我感动/场景转换/信息密度";
    public override string Description => "自我感动+场景过渡+信息密度检测";
    public override ToolCategory Category => ToolCategory.EntityRead;
    protected override string Capability => StoryCheckCapability;
    protected override string Dimension => "自我感动";

    public override async Task<AgentToolResult<SelfIndulgenceDetails>> ExecuteAsync(
        string toolCallId,
        DetectSelfIndulgenceParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<SelfIndulgenceDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var (chapters, novel) = await LoadChaptersAsync(args.NovelId, args.ChapterIds, ct);
        ThrowIfCancelled(ct);
        if (chapters.Count == 0)
            return new AgentToolResult<SelfIndulgenceDetails>
            {
                Content = [new TextContent("暂无章节内容可分析")],
                IsError = true,
            };
        var cloudResult = await ExecuteCloudAnalysisAsync(chapters, novel!, ct);
        if (cloudResult is null)
            return CloudUnavailableResult();
        var details = MapCloudResult(cloudResult.Value);
        var report = BuildReportText(details);
        return new AgentToolResult<SelfIndulgenceDetails>
        {
            Content = [new TextContent(report)],
            Details = details,
        };
    }

    private static SelfIndulgenceDetails MapCloudResult(JsonElement element)
    {
        var dim = GetDimension(element, "self-indulgence");
        return new SelfIndulgenceDetails
        {
            SelfIndulgenceScore = GetDimensionScore(dim, "self-indulgence-score"),
        };
    }

    private static string BuildReportText(SelfIndulgenceDetails d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🔍 自我感动/场景转换/信息密度报告");
        sb.AppendLine();
        sb.AppendLine($"**综合评分**: **{d.SelfIndulgenceScore:F1}** / 100");
        sb.AppendLine();
        sb.AppendLine("---");
        return sb.ToString();
    }
}

public sealed record DetectSelfIndulgenceParams
{
    [Description("小说项目ID")]
    public string NovelId { get; init; } = "";

    [Description("章节ID列表")]
    [JsonConverter(typeof(ChapterIdsConverter))]
    public List<string>? ChapterIds { get; init; }
}

public sealed record SelfIndulgenceDetails
{
    public double SelfIndulgenceScore { get; init; }
    public double SceneTransitionScore { get; init; }
    public double InfoDensityScore { get; init; }
    public string DominantTransitionType { get; init; } = "";
    public List<SelfIndulgenceIssueEntry> Issues { get; init; } = [];
    public List<string> Strengths { get; init; } = [];
}

public sealed record SelfIndulgenceIssueEntry
{
    public string Dimension { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Suggestion { get; init; }
}
