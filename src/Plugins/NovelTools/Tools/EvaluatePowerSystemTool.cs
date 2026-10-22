using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Core.Cloud;
using Core.Data;
using Core.Shared;
using Core.Tools;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 力量体系评估 — 境界体系+修炼方法+战斗系统+资源体系评估，检测境界通胀和力量膨胀
/// CHANGE_07：本地分析引擎已删除，全部通过 IFictionHarnessClient 调云端。
/// </summary>
public sealed class EvaluatePowerSystemTool(
    INovelStore store,
    IFictionHarnessClient? cloudHarness = null
) : CloudAnalysisTool<EvaluatePowerSystemParams, PowerSystemDetails>(store, cloudHarness)
{
    public override string Name => "evaluate_power_system";
    public override string Label => "力量体系评估";
    public override string Description =>
        "境界体系+修炼方法+战斗系统+资源体系评估，检测境界通胀和力量膨胀";
    public override ToolCategory Category => ToolCategory.EntityRead;
    protected override string Capability => StoryCheckCapability;
    protected override string Dimension => "力量体系";

    public override async Task<AgentToolResult<PowerSystemDetails>> ExecuteAsync(
        string toolCallId,
        EvaluatePowerSystemParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<PowerSystemDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var (chapters, novel) = await LoadChaptersAsync(args.NovelId, null, ct);
        ThrowIfCancelled(ct);
        if (chapters.Count == 0)
            return new AgentToolResult<PowerSystemDetails>
            {
                Content = [new TextContent("暂无章节内容可分析")],
                IsError = true,
            };
        var cloudResult = await ExecuteCloudAnalysisAsync(chapters, novel!, ct);
        if (cloudResult is null)
            return CloudUnavailableResult();
        var details = MapCloudResult(cloudResult.Value);
        var report = BuildReportText(details);
        return new AgentToolResult<PowerSystemDetails>
        {
            Content = [new TextContent(report)],
            Details = details,
        };
    }

    private static PowerSystemDetails MapCloudResult(JsonElement element)
    {
        var dim = GetDimension(element, "power-system");
        return new PowerSystemDetails { OverallScore = GetDimensionScore(dim) };
    }

    private static string BuildReportText(PowerSystemDetails d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🔍 力量体系评估报告");
        sb.AppendLine();
        sb.AppendLine($"**综合评分**: **{d.OverallScore:F1}** / 100");
        sb.AppendLine();
        sb.AppendLine("---");
        return sb.ToString();
    }
}

public sealed record EvaluatePowerSystemParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";
}

public sealed record PowerSystemDetails
{
    public double OverallScore { get; init; }
    public double RealmSystemScore { get; init; }
    public double CultivationMethodScore { get; init; }
    public double CombatSystemScore { get; init; }
    public double ResourceSystemScore { get; init; }
    public bool HasRealmSystem { get; init; }
    public bool HasCultivationMethod { get; init; }
    public bool HasCombatSystem { get; init; }
    public bool HasResourceSystem { get; init; }
    public bool HasRealmInflation { get; init; }
    public bool HasPowerCreep { get; init; }
    public List<PowerSystemIssueEntry> Issues { get; init; } = [];
    public List<string> Strengths { get; init; } = [];
}

public sealed record PowerSystemIssueEntry
{
    public string Dimension { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Suggestion { get; init; }
}
