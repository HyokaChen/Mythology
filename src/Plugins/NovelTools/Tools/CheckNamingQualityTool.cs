using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Core.Cloud;
using Core.Data;
using Core.Shared;
using Core.Tools;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 命名质量检测 — 角色命名+标题命名+风格一致性检测
/// CHANGE_07：本地分析引擎已删除，全部通过 IFictionHarnessClient 调云端。
/// </summary>
public sealed class CheckNamingQualityTool(
    INovelStore store,
    IFictionHarnessClient? cloudHarness = null
) : CloudAnalysisTool<CheckNamingQualityParams, NamingQualityDetails>(store, cloudHarness)
{
    public override string Name => "check_naming_quality";
    public override string Label => "命名质量检测";
    public override string Description => "角色命名+标题命名+风格一致性检测";
    public override ToolCategory Category => ToolCategory.EntityRead;
    protected override string Capability => StoryCheckCapability;
    protected override string Dimension => "命名";

    public override async Task<AgentToolResult<NamingQualityDetails>> ExecuteAsync(
        string toolCallId,
        CheckNamingQualityParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<NamingQualityDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var (chapters, novel) = await LoadChaptersAsync(args.NovelId, null, ct);
        ThrowIfCancelled(ct);
        if (chapters.Count == 0)
            return new AgentToolResult<NamingQualityDetails>
            {
                Content = [new TextContent("暂无章节内容可分析")],
                IsError = true,
            };
        var cloudResult = await ExecuteCloudAnalysisAsync(chapters, novel!, ct);
        if (cloudResult is null)
            return CloudUnavailableResult();
        var details = MapCloudResult(cloudResult.Value);
        var report = BuildReportText(details);
        return new AgentToolResult<NamingQualityDetails>
        {
            Content = [new TextContent(report)],
            Details = details,
        };
    }

    private static NamingQualityDetails MapCloudResult(JsonElement element)
    {
        var dim = GetDimension(element, "naming");
        return new NamingQualityDetails { OverallScore = GetDimensionScore(dim) };
    }

    private static string BuildReportText(NamingQualityDetails d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🔍 命名质量检测报告");
        sb.AppendLine();
        sb.AppendLine($"**综合评分**: **{d.OverallScore:F1}** / 100");
        sb.AppendLine();
        sb.AppendLine("---");
        return sb.ToString();
    }
}

public sealed record CheckNamingQualityParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";
}

public sealed record NamingQualityDetails
{
    public double OverallScore { get; init; }
    public double TitleScore { get; init; }
    public double CharacterNameScore { get; init; }
    public double StyleConsistencyScore { get; init; }
    public List<NamingIssueEntry> Issues { get; init; } = [];
    public List<string> Strengths { get; init; } = [];
}

public sealed record NamingIssueEntry
{
    public string Dimension { get; init; } = "";
    public string IssueType { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Description { get; init; } = "";
    public string Evidence { get; init; } = "";
    public string? Suggestion { get; init; }
}
