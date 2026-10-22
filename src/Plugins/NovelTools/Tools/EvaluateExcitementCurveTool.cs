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
/// 兴奋点与结局评估 — 兴奋点三层次（生理/情绪/认知）+结局类型+定时炸弹检测
/// CHANGE_07：本地分析引擎已删除，全部通过 IFictionHarnessClient 调云端。
/// </summary>
public sealed class EvaluateExcitementCurveTool(
    INovelStore store,
    IFictionHarnessClient? cloudHarness = null
) : CloudAnalysisTool<EvaluateExcitementCurveParams, ExcitementCurveDetails>(store, cloudHarness)
{
    public override string Name => "evaluate_excitement_curve";
    public override string Label => "兴奋点与结局评估";
    public override string Description => "兴奋点三层次（生理/情绪/认知）+结局类型+定时炸弹检测";
    public override ToolCategory Category => ToolCategory.EntityRead;
    protected override string Capability => StoryCheckCapability;
    protected override string Dimension => "高潮";

    public override async Task<AgentToolResult<ExcitementCurveDetails>> ExecuteAsync(
        string toolCallId,
        EvaluateExcitementCurveParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<ExcitementCurveDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var (chapters, novel) = await LoadChaptersAsync(args.NovelId, args.ChapterIds, ct);
        ThrowIfCancelled(ct);
        if (chapters.Count == 0)
            return new AgentToolResult<ExcitementCurveDetails>
            {
                Content = [new TextContent("暂无章节内容可分析")],
                IsError = true,
            };
        var cloudResult = await ExecuteCloudAnalysisAsync(chapters, novel!, ct);
        if (cloudResult is null)
            return CloudUnavailableResult();
        var details = MapCloudResult(cloudResult.Value);
        var report = BuildReportText(details);
        return new AgentToolResult<ExcitementCurveDetails>
        {
            Content = [new TextContent(report)],
            Details = details,
        };
    }

    private static ExcitementCurveDetails MapCloudResult(JsonElement element)
    {
        var dim = GetDimension(element, "excitement");
        return new ExcitementCurveDetails
        {
            ExcitementScore = GetDimensionScore(dim, "excitement-score"),
        };
    }

    private static string BuildReportText(ExcitementCurveDetails d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🔍 兴奋点与结局评估报告");
        sb.AppendLine();
        sb.AppendLine($"**综合评分**: **{d.ExcitementScore:F1}** / 100");
        sb.AppendLine();
        sb.AppendLine("---");
        return sb.ToString();
    }
}

public sealed record EvaluateExcitementCurveParams
{
    [Description("小说项目ID")]
    public string NovelId { get; init; } = "";

    [Description("章节ID列表")]
    [JsonConverter(typeof(ChapterIdsConverter))]
    public List<string>? ChapterIds { get; init; }
}

public sealed record ExcitementCurveDetails
{
    public double ExcitementScore { get; init; }
    public double PhysiologicalScore { get; init; }
    public double EmotionalScore { get; init; }
    public double CognitiveScore { get; init; }
    public double TimeBombScore { get; init; }
    public string DetectedEndingType { get; init; } = "";
    public string DetectedEndingLabel { get; init; } = "";
    public double EndingClarityScore { get; init; }
    public List<ExcitementIssueEntry> Issues { get; init; } = [];
    public List<string> Strengths { get; init; } = [];
}

public sealed record ExcitementIssueEntry
{
    public string Dimension { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Suggestion { get; init; }
}
