using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Core.Cloud;
using Core.Data;
using Core.Shared;
using Core.Tools;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 情节漏洞检测 — 跨章节检测未闭合伏笔、角色动机断裂、时间线冲突等情节逻辑问题
/// CHANGE_07：本地分析引擎已删除，全部通过 IFictionHarnessClient 调云端。
/// </summary>
public sealed class DetectPlotHolesTool(
    INovelStore store,
    IFictionHarnessClient? cloudHarness = null
) : CloudAnalysisTool<DetectPlotHolesParams, PlotHoleDetails>(store, cloudHarness)
{
    public override string Name => "detect_plot_holes";
    public override string Label => "情节漏洞检测";
    public override string Description =>
        "跨章节检测未闭合伏笔、角色动机断裂、时间线冲突等情节逻辑问题";
    public override ToolCategory Category => ToolCategory.EntityRead;
    protected override string Capability => StoryCheckCapability;
    protected override string Dimension => "情节漏洞";
    public override string[] Aliases =>
        ["未闭合伏笔", "情节漏洞", "剧情漏洞", "伏笔检查", "检查伏笔", "逻辑冲突检测"];
    public override string[] ExampleQueries => ["检查未回收的伏笔", "检测第1-5章的情节漏洞"];

    public override async Task<AgentToolResult<PlotHoleDetails>> ExecuteAsync(
        string toolCallId,
        DetectPlotHolesParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<PlotHoleDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var (chapters, novel) = await LoadChaptersAsync(args.NovelId, null, ct);
        ThrowIfCancelled(ct);
        if (chapters.Count == 0)
            return new AgentToolResult<PlotHoleDetails>
            {
                Content = [new TextContent("暂无章节内容可分析")],
                IsError = true,
            };
        var cloudResult = await ExecuteCloudAnalysisAsync(chapters, novel!, ct);
        if (cloudResult is null)
            return CloudUnavailableResult();
        var details = MapCloudResult(cloudResult.Value);
        var report = BuildReportText(details);
        return new AgentToolResult<PlotHoleDetails>
        {
            Content = [new TextContent(report)],
            Details = details,
        };
    }

    private static PlotHoleDetails MapCloudResult(JsonElement element)
    {
        var coherence = GetDimension(element, "coherence");
        var issues = new List<IssueEntry>();
        if (coherence.ValueKind == JsonValueKind.Array)
        {
            issues.AddRange(
                coherence
                    .EnumerateArray()
                    .Select(item => new IssueEntry
                    {
                        Type = GetDimensionString(item, "type"),
                        Severity = GetDimensionString(item, "severity"),
                        Description = GetDimensionString(item, "description"),
                        Evidence = GetDimensionString(item, "evidence"),
                        Suggestion = item.TryGetProperty("suggestion", out var sug)
                            ? sug.GetString()
                            : null,
                    })
            );
        }

        return new PlotHoleDetails { TotalIssues = issues.Count, Issues = issues };
    }

    private static string BuildReportText(PlotHoleDetails d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🔍 情节漏洞检测报告");
        sb.AppendLine();
        sb.AppendLine($"**综合评分**: **{d.TotalIssues}** / 100");
        sb.AppendLine();
        sb.AppendLine("---");
        return sb.ToString();
    }
}

public sealed record DetectPlotHolesParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description(
        "检测范围：伏笔（未闭合伏笔）/ 动机（角色动机断裂）/ 时间线（时间线冲突）/ all（全部）"
    )]
    public string? Scope { get; init; }

    [Description("可选：指定从哪个章节开始检测，支持章节ID或序号，留空则从第1章")]
    public string? FromChapter { get; init; }

    [Description("可选：指定检测到哪个章节，支持章节ID或序号，留空则到最后")]
    public string? ToChapter { get; init; }
}

public sealed record PlotHoleDetails
{
    public int TotalIssues { get; init; }
    public int ForeshadowingIssues { get; init; }
    public int MotivationIssues { get; init; }
    public int TimelineIssues { get; init; }
    public List<IssueEntry> Issues { get; init; } = [];
}

public sealed record IssueEntry
{
    public string Type { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Description { get; init; } = "";
    public string Evidence { get; init; } = "";
    public string? Suggestion { get; init; }
}
