using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Core.Cloud;
using Core.Data;
using Core.Shared;
using Core.Tools;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 网络结构分析 — 解析 fiction.story.check 的 network 维度：
/// 人物 PageRank（N1/N2）、章节枢纽（N4）、伏笔网络（N5）、
/// 跨章主角边缘化（N3）、TextRank 金句密度与章末钩子（CHANGE_04 §31）。
/// </summary>
public sealed class AnalyzeNetworkTool(
    INovelStore store,
    IFictionHarnessClient? cloudHarness = null
) : CloudAnalysisTool<AnalyzeNetworkParams, NetworkAnalysisDetails>(store, cloudHarness)
{
    public override string Name => "analyze_network_structure";
    public override string[] Aliases =>
        ["网络结构", "角色网络", "PageRank", "枢纽章", "伏笔网络", "金句密度"];
    public override string[] ExampleQueries =>
        ["分析角色网络结构重要性", "检查是否有主角被边缘化", "找出章节枢纽与金句密度"];
    public override string Label => "网络结构分析";
    public override string Description =>
        "人物 PageRank 结构重要性、章节枢纽图、伏笔网络、主角PR边缘化、TextRank 金句密度与章末钩子";
    public override ToolCategory Category => ToolCategory.EntityRead;
    protected override string Capability => StoryCheckCapability;
    protected override string Dimension => "网络结构";

    public override async Task<AgentToolResult<NetworkAnalysisDetails>> ExecuteAsync(
        string toolCallId,
        AnalyzeNetworkParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<NetworkAnalysisDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var (chapters, novel) = await LoadChaptersAsync(args.NovelId, null, ct);
        ThrowIfCancelled(ct);
        if (chapters.Count == 0)
            return new AgentToolResult<NetworkAnalysisDetails>
            {
                Content = [new TextContent("暂无章节内容可分析")],
                IsError = true,
            };
        var cloudResult = await ExecuteCloudAnalysisAsync(chapters, novel!, ct);
        if (cloudResult is null)
            return CloudUnavailableResult();
        var details = MapNetworkResult(cloudResult.Value);
        var report = BuildReportText(details);
        return new AgentToolResult<NetworkAnalysisDetails>
        {
            Content = [new TextContent(report)],
            Details = details,
        };
    }

    private static NetworkAnalysisDetails MapNetworkResult(JsonElement element)
    {
        var network = GetDimension(element, "network");
        if (network.ValueKind != JsonValueKind.Object)
            return new NetworkAnalysisDetails();

        var characterPageRank = default(JsonElement);
        if (network.TryGetProperty("character-page-rank", out var pr))
            characterPageRank = pr;

        var chapterHubs = default(JsonElement);
        if (network.TryGetProperty("chapter-hubs", out var hubs))
            chapterHubs = hubs;

        var foreshadowing = default(JsonElement);
        if (network.TryGetProperty("foreshadowing-network", out var fs))
            foreshadowing = fs;

        var protagonistArc = default(JsonElement);
        if (network.TryGetProperty("protagonist-arc", out var arc))
            protagonistArc = arc;

        var golden = default(JsonElement);
        if (network.TryGetProperty("golden-sentences", out var gs))
            golden = gs;

        var issues = new List<NetworkIssueEntry>();
        if (
            characterPageRank.ValueKind == JsonValueKind.Object
            && GetDimensionBool(characterPageRank, "n1-protagonist-eclipsed")
        )
            issues.Add(
                new NetworkIssueEntry
                {
                    Code = "N1",
                    Severity = Severity.High,
                    Description =
                        $"PageRank 榜首为「{GetDimensionString(characterPageRank, "top-character")}」（主角排名第 {GetDimensionInt(characterPageRank, "protagonist-rank")}），配角喧宾夺主",
                }
            );
        if (
            characterPageRank.ValueKind == JsonValueKind.Object
            && GetDimensionBool(characterPageRank, "n2-network-concentrated")
        )
            issues.Add(
                new NetworkIssueEntry
                {
                    Code = "N2",
                    Severity = Severity.Medium,
                    Description =
                        $"PageRank Gini {GetDimensionScore(characterPageRank, "rank-gini"):F2}，网络过度集中于单一角色",
                }
            );
        if (
            chapterHubs.ValueKind == JsonValueKind.Object
            && GetDimensionBool(chapterHubs, "n4-hub-poverty")
        )
            issues.Add(
                new NetworkIssueEntry
                {
                    Code = "N4",
                    Severity = Severity.Medium,
                    Description = "章节枢纽枯竭：缺少结构性锚点章，存在流水账风险",
                }
            );
        if (
            foreshadowing.ValueKind == JsonValueKind.Object
            && GetDimensionBool(foreshadowing, "n5-plotline-drift")
        )
            issues.Add(
                new NetworkIssueEntry
                {
                    Code = "N5",
                    Severity = Severity.Low,
                    Description = "支线漂移：支线伏笔 PR 超过主线，暗线悄悄变主线",
                }
            );
        if (
            protagonistArc.ValueKind == JsonValueKind.Object
            && GetDimensionBool(protagonistArc, "n3-protagonist-marginalized")
        )
            issues.Add(
                new NetworkIssueEntry
                {
                    Code = "N3",
                    Severity = Severity.High,
                    Description =
                        $"主角 PR 排名连续 {GetDimensionInt(protagonistArc, "sliding-decline-chapters")} 章下滑，存在边缘化风险",
                }
            );

        return new NetworkAnalysisDetails
        {
            CharacterPageRank =
                characterPageRank.ValueKind == JsonValueKind.Object
                    ? new CharacterPageRankDetails
                    {
                        TopCharacter = GetDimensionString(characterPageRank, "top-character"),
                        ProtagonistRank = GetDimensionInt(characterPageRank, "protagonist-rank"),
                        RankGini = GetDimensionScore(characterPageRank, "rank-gini"),
                        HubRatio = GetDimensionScore(characterPageRank, "hub-ratio"),
                        N1ProtagonistEclipsed = GetDimensionBool(
                            characterPageRank,
                            "n1-protagonist-eclipsed"
                        ),
                        N2NetworkConcentrated = GetDimensionBool(
                            characterPageRank,
                            "n2-network-concentrated"
                        ),
                    }
                    : null,
            ChapterHubs =
                chapterHubs.ValueKind == JsonValueKind.Object
                    ? new ChapterHubsDetails
                    {
                        HubChapterCount = GetDimensionInt(chapterHubs, "hub-chapter-count"),
                        HubTitles = ParseStringList(chapterHubs, "hub-titles"),
                        RankGini = GetDimensionScore(chapterHubs, "rank-gini"),
                        N4HubPoverty = GetDimensionBool(chapterHubs, "n4-hub-poverty"),
                    }
                    : null,
            ForeshadowingNetwork =
                foreshadowing.ValueKind == JsonValueKind.Object
                    ? new ForeshadowingNetworkDetails
                    {
                        PlotlineRanks = ParsePlotlineRanks(foreshadowing),
                        N5PlotlineDrift = GetDimensionBool(foreshadowing, "n5-plotline-drift"),
                    }
                    : null,
            ProtagonistArc = new ProtagonistArcDetails
            {
                SlidingDeclineChapters =
                    protagonistArc.ValueKind == JsonValueKind.Object
                        ? GetDimensionInt(protagonistArc, "sliding-decline-chapters")
                        : 0,
                N3ProtagonistMarginalized =
                    protagonistArc.ValueKind == JsonValueKind.Object
                    && GetDimensionBool(protagonistArc, "n3-protagonist-marginalized"),
            },
            GoldenSentences = new GoldenSentencesDetails
            {
                GoldenSentenceDensityPerThousand =
                    golden.ValueKind == JsonValueKind.Object
                        ? GetDimensionScore(golden, "golden-sentence-density-per-thousand")
                        : 0,
                LastSentenceHookScore =
                    golden.ValueKind == JsonValueKind.Object
                        ? GetDimensionScore(golden, "last-sentence-hook-score")
                        : 0,
            },
            Issues = issues,
        };
    }

    private static List<PlotlineRankEntry> ParsePlotlineRanks(JsonElement foreshadowing)
    {
        var result = new List<PlotlineRankEntry>();
        if (
            !foreshadowing.TryGetProperty("plotline-ranks", out var ranks)
            || ranks.ValueKind != JsonValueKind.Object
        )
            return result;

        foreach (var prop in ranks.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Number)
                result.Add(
                    new PlotlineRankEntry { Name = prop.Name, Rank = prop.Value.GetDouble() }
                );
        }

        return result.OrderByDescending(r => r.Rank).ToList();
    }

    private static string BuildReportText(NetworkAnalysisDetails d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🕸️ 网络结构分析报告");
        sb.AppendLine();

        if (d.CharacterPageRank is { } pr)
        {
            sb.AppendLine("### 人物 PageRank");
            sb.AppendLine();
            sb.AppendLine(
                $"**结构重要性榜首**: **{pr.TopCharacter}** | 主角排名: **{pr.ProtagonistRank}**"
            );
            sb.AppendLine($"**Rank Gini**: {pr.RankGini:F3} | **Hub Ratio**: {pr.HubRatio:F2}");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("> ⚠️ 人物不足 2 个或缺少段落数据，无法计算角色 PageRank。");
            sb.AppendLine();
        }

        if (d.ChapterHubs is { } hubs)
        {
            sb.AppendLine("### 章节枢纽");
            sb.AppendLine();
            sb.AppendLine(
                $"**枢纽章数**: **{hubs.HubChapterCount}** | Rank Gini: {hubs.RankGini:F3}"
            );
            if (hubs.HubTitles.Count > 0)
                sb.AppendLine($"枢纽章: {string.Join("、", hubs.HubTitles)}");
            sb.AppendLine();
        }

        if (d.ForeshadowingNetwork is { } fs)
        {
            sb.AppendLine("### 伏笔网络（PR）");
            sb.AppendLine();
            foreach (var rank in fs.PlotlineRanks)
                sb.AppendLine($"- **{rank.Name}**: {rank.Rank:F3}");
            sb.AppendLine();
        }

        sb.AppendLine("### 主角 PR 时序");
        sb.AppendLine(
            $"连续下滑: **{d.ProtagonistArc.SlidingDeclineChapters}** 章 | 边缘化: {(d.ProtagonistArc.N3ProtagonistMarginalized ? "⚠️ 是" : "否")}"
        );
        sb.AppendLine();

        sb.AppendLine("### 金句 / 章末钩子（TextRank）");
        sb.AppendLine(
            $"金句密度: **{d.GoldenSentences.GoldenSentenceDensityPerThousand:F2}** 句/千字 | 章末钩子势能: **{d.GoldenSentences.LastSentenceHookScore:F2}**"
        );
        sb.AppendLine();

        if (d.Issues.Count > 0)
        {
            sb.AppendLine("### N 系列风险");
            sb.AppendLine();
            foreach (var issue in d.Issues)
                sb.AppendLine(
                    $"{IssueEmoji(issue.Severity)} **[{issue.Code}]** {issue.Description}"
                );
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine("> 网络层（PageRank / TextRank）数值阈值仍标记为待校准，结果用于诊断参考。");
        return sb.ToString();
    }
}

public sealed record AnalyzeNetworkParams
{
    [Description("小说项目ID")]
    public string NovelId { get; init; } = "";
}

public sealed record NetworkAnalysisDetails
{
    public CharacterPageRankDetails? CharacterPageRank { get; init; }
    public ChapterHubsDetails? ChapterHubs { get; init; }
    public ForeshadowingNetworkDetails? ForeshadowingNetwork { get; init; }
    public ProtagonistArcDetails ProtagonistArc { get; init; } = new();
    public GoldenSentencesDetails GoldenSentences { get; init; } = new();
    public List<NetworkIssueEntry> Issues { get; init; } = [];
}

public sealed record CharacterPageRankDetails
{
    public string TopCharacter { get; init; } = "";
    public int ProtagonistRank { get; init; }
    public double RankGini { get; init; }
    public double HubRatio { get; init; }
    public bool N1ProtagonistEclipsed { get; init; }
    public bool N2NetworkConcentrated { get; init; }
}

public sealed record ChapterHubsDetails
{
    public int HubChapterCount { get; init; }
    public List<string> HubTitles { get; init; } = [];
    public double RankGini { get; init; }
    public bool N4HubPoverty { get; init; }
}

public sealed record ForeshadowingNetworkDetails
{
    public List<PlotlineRankEntry> PlotlineRanks { get; init; } = [];
    public bool N5PlotlineDrift { get; init; }
}

public sealed record PlotlineRankEntry
{
    public string Name { get; init; } = "";
    public double Rank { get; init; }
}

public sealed record ProtagonistArcDetails
{
    public int SlidingDeclineChapters { get; init; }
    public bool N3ProtagonistMarginalized { get; init; }
}

public sealed record GoldenSentencesDetails
{
    public double GoldenSentenceDensityPerThousand { get; init; }
    public double LastSentenceHookScore { get; init; }
}

public sealed record NetworkIssueEntry
{
    public string Code { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Description { get; init; } = "";
}
