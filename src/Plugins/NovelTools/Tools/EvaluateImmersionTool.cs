using System.ComponentModel;
using System.Text;
using System.Text.Json.Serialization;
using Core.Cloud;
using Core.Data;
using Core.Shared;
using Core.Tools;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 代入感评估工具 — 基于铁律1的四维度检测：模糊细节+贴合读者+小缺陷+视角一致性。
/// CHANGE_07：本地分析引擎已删除，全部通过 IFictionHarnessClient 调云端。
/// 云端不可用时返回可读错误，不伪造分数，不阻塞本地创作。
/// </summary>
public sealed class EvaluateImmersionTool(
    INovelStore store,
    IFictionHarnessClient? cloudHarness = null
) : CloudAnalysisTool<EvaluateImmersionParams, ImmersionDetails>(store, cloudHarness)
{
    public override string Name => "evaluate_immersion_strength";
    public override string Label => "代入感评估";
    public override string Description =>
        "检测主角代入感四维度：模糊细节（避免精确数字）、贴合读者（年轻男性为主）、小缺陷（路痴/胆小等）、视角一致性";
    public override ToolCategory Category => ToolCategory.EntityRead;

    protected override string Capability => StoryCheckCapability;
    protected override string Dimension => DimensionKeys.Immersion;

    public override async Task<AgentToolResult<ImmersionDetails>> ExecuteAsync(
        string toolCallId,
        EvaluateImmersionParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<ImmersionDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);

        var (chapters, novel) = await LoadChaptersAsync(args.NovelId, args.ChapterIds, ct);
        ThrowIfCancelled(ct);

        if (chapters.Count == 0)
            return new AgentToolResult<ImmersionDetails>
            {
                Content = [new TextContent("暂无章节内容可分析")],
                IsError = true,
            };

        var cloudResult = await ExecuteCloudAnalysisAsync(chapters, novel!, ct);
        if (cloudResult is null)
            return CloudUnavailableResult();

        var dim = GetDimension(cloudResult.Value, "immersion");
        var details = new ImmersionDetails
        {
            OverallScore = GetDimensionScore(dim),
            FuzzyDetailScore = GetDimensionScore(dim, "fuzzy-detail-score"),
            ReaderFitScore = GetDimensionScore(dim, "reader-fit-score"),
            FlawScore = GetDimensionScore(dim, "flaw-score"),
            ViewpointScore = GetDimensionScore(dim, "viewpoint-score"),
            HasProtagonist =
                novel?.Characters.Any(c => c.Role == CharacterRole.Protagonist) ?? false,
            Issues = ParseDimensionIssues(dim)
                .Select(i => new ImmersionIssueEntry
                {
                    Dimension = i.Dimension,
                    Severity = i.Severity,
                    Description = i.Description,
                    Evidence = i.Evidence,
                    Suggestion = i.Suggestion,
                })
                .ToList(),
            Strengths = ParseStringList(dim, "strengths"),
        };

        var report = BuildReportText(details);
        return new AgentToolResult<ImmersionDetails>
        {
            Content = [new TextContent(report)],
            Details = details,
        };
    }

    private static string BuildReportText(ImmersionDetails d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🎭 代入感评估报告");
        sb.AppendLine();
        sb.AppendLine($"**综合代入感**: **{d.OverallScore:F1}** / 100");
        sb.AppendLine();

        sb.AppendLine("### 四维度评分");
        sb.AppendLine();
        sb.AppendLine("| 维度 | 分数 | 状态 |");
        sb.AppendLine("|------|------|------|");
        sb.AppendLine($"| 模糊细节 | {d.FuzzyDetailScore:F1} | {ScoreEmoji(d.FuzzyDetailScore)} |");
        sb.AppendLine($"| 贴合读者 | {d.ReaderFitScore:F1} | {ScoreEmoji(d.ReaderFitScore)} |");
        sb.AppendLine($"| 小缺陷 | {d.FlawScore:F1} | {ScoreEmoji(d.FlawScore)} |");
        sb.AppendLine($"| 视角一致 | {d.ViewpointScore:F1} | {ScoreEmoji(d.ViewpointScore)} |");
        sb.AppendLine();

        if (!d.HasProtagonist)
        {
            sb.AppendLine("> ⚠️ 未识别到主角(Protagonist)，贴合读者维度评估受限");
            sb.AppendLine();
        }

        if (d.Strengths.Count > 0)
        {
            sb.AppendLine("### ✅ 亮点");
            foreach (var s in d.Strengths)
                sb.AppendLine($"- {s}");
            sb.AppendLine();
        }

        if (d.Issues.Count > 0)
        {
            sb.AppendLine("### ⚠️ 问题");
            foreach (var issue in d.Issues)
            {
                var emoji = IssueEmoji(issue.Severity);
                sb.AppendLine($"{emoji} **[{issue.Dimension}]** {issue.Description}");
                if (!string.IsNullOrEmpty(issue.Evidence))
                    sb.AppendLine($"  证据: {issue.Evidence}");
                if (!string.IsNullOrEmpty(issue.Suggestion))
                    sb.AppendLine($"  建议: {issue.Suggestion}");
                sb.AppendLine();
            }
        }

        sb.AppendLine("---");
        sb.AppendLine(
            "> 铁律1：代入感 = 模糊细节 + 贴合读者 + 加小缺陷。身高写「一人高」不写175cm，主角=十几岁男性学生，加路痴/胆小等小缺陷。"
        );
        return sb.ToString();
    }
}

public sealed record EvaluateImmersionParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("要分析的章节ID列表，留空则分析全部章节")]
    [JsonConverter(typeof(ChapterIdsConverter))]
    public List<string>? ChapterIds { get; init; }
}

public sealed record ImmersionDetails
{
    public double OverallScore { get; init; }
    public double FuzzyDetailScore { get; init; }
    public double ReaderFitScore { get; init; }
    public double FlawScore { get; init; }
    public double ViewpointScore { get; init; }
    public bool HasProtagonist { get; init; }
    public List<ImmersionIssueEntry> Issues { get; init; } = [];
    public List<string> Strengths { get; init; } = [];
}

public sealed record ImmersionIssueEntry
{
    public string Dimension { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Description { get; init; } = "";
    public string Evidence { get; init; } = "";
    public string? Suggestion { get; init; }
}
