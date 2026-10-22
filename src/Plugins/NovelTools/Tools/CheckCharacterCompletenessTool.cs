using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Core.Cloud;
using Core.Data;
using Core.Shared;
using Core.Tools;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 角色完整性检测 — 角色设定20问完整性检测+悲剧人物健康度+复活禁忌检测
/// CHANGE_07：本地分析引擎已删除，全部通过 IFictionHarnessClient 调云端。
/// </summary>
public sealed class CheckCharacterCompletenessTool(
    INovelStore store,
    IFictionHarnessClient? cloudHarness = null
)
    : CloudAnalysisTool<CheckCharacterCompletenessParams, CharacterCompletenessDetails>(
        store,
        cloudHarness
    )
{
    public override string Name => "check_character_completeness";
    public override string[] Aliases => ["角色完整性", "人物设定检查", "二十问"];
    public override string[] ExampleQueries => ["检查主角林晚的设定是否完整"];
    public override string Label => "角色完整性检测";
    public override string Description => "角色设定20问完整性检测+悲剧人物健康度+复活禁忌检测";
    public override ToolCategory Category => ToolCategory.EntityRead;
    protected override string Capability => StoryCheckCapability;
    protected override string Dimension => "角色完整性";

    public override async Task<AgentToolResult<CharacterCompletenessDetails>> ExecuteAsync(
        string toolCallId,
        CheckCharacterCompletenessParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<CharacterCompletenessDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var (chapters, novel) = await LoadChaptersAsync(args.NovelId, null, ct);
        ThrowIfCancelled(ct);
        if (chapters.Count == 0)
            return new AgentToolResult<CharacterCompletenessDetails>
            {
                Content = [new TextContent("暂无章节内容可分析")],
                IsError = true,
            };
        var cloudResult = await ExecuteCloudAnalysisAsync(chapters, novel!, ct);
        if (cloudResult is null)
            return CloudUnavailableResult();
        var details = MapCloudResult(cloudResult.Value, novel, args);
        var report = BuildReportText(details);
        return new AgentToolResult<CharacterCompletenessDetails>
        {
            Content = [new TextContent(report)],
            Details = details,
        };
    }

    private static CharacterCompletenessDetails MapCloudResult(
        JsonElement element,
        Novel? novel,
        CheckCharacterCompletenessParams args
    )
    {
        var charactersGroup = GetDimension(element, "characters");
        var character = SelectCharacterDimension(
            charactersGroup,
            novel?.Characters,
            args.CharacterId
        );
        var completeness = character.TryGetProperty("completeness", out var comp) ? comp : default;

        var fieldChecks = new List<FieldCheckEntry>();
        if (
            completeness.ValueKind == JsonValueKind.Object
            && completeness.TryGetProperty("field-checks", out var checks)
            && checks.ValueKind == JsonValueKind.Array
        )
        {
            fieldChecks.AddRange(
                checks
                    .EnumerateArray()
                    .Select(check => new FieldCheckEntry
                    {
                        Field = GetDimensionString(check, "field"),
                        Label = GetDimensionString(check, "label"),
                        Importance = GetDimensionString(check, "importance"),
                        IsFilled = GetDimensionBool(check, "is-filled"),
                    })
            );
        }

        var tragic =
            completeness.ValueKind == JsonValueKind.Object
            && completeness.TryGetProperty("tragic-analysis", out var tragicJson)
                ? tragicJson
                : default;

        return new CharacterCompletenessDetails
        {
            OverallScore = GetDimensionScore(completeness),
            FieldCompletenessScore = GetDimensionScore(completeness, "field-completeness-score"),
            TragicHealthScore = GetDimensionScore(completeness, "tragic-health-score"),
            ArcBalanceScore = GetDimensionScore(completeness, "arc-balance-score"),
            FieldChecks = fieldChecks,
            Issues = ParseDimensionIssues(completeness)
                .Select(i => new CompletenessIssueEntry
                {
                    Dimension = i.Dimension,
                    Severity = i.Severity,
                    Description = i.Description,
                    Suggestion = i.Suggestion,
                })
                .ToList(),
            Strengths = ParseStringList(completeness, "strengths"),
            HasTragicElements =
                tragic.ValueKind == JsonValueKind.Object
                && GetDimensionBool(tragic, "has-tragic-elements"),
            HasResurrectionTaboo =
                tragic.ValueKind == JsonValueKind.Object
                && GetDimensionBool(tragic, "has-resurrection-taboo"),
        };
    }

    private static string BuildReportText(CharacterCompletenessDetails d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🔍 角色完整性检测报告");
        sb.AppendLine();
        sb.AppendLine($"**综合评分**: **{d.OverallScore:F1}** / 100");
        sb.AppendLine();
        sb.AppendLine("---");
        return sb.ToString();
    }
}

public sealed record CheckCharacterCompletenessParams
{
    [Description("小说项目ID")]
    public string NovelId { get; init; } = "";

    [Description("角色ID")]
    public string CharacterId { get; init; } = "";
}

public sealed record CharacterCompletenessDetails
{
    public double OverallScore { get; init; }
    public double FieldCompletenessScore { get; init; }
    public double TragicHealthScore { get; init; }
    public double ArcBalanceScore { get; init; }
    public List<FieldCheckEntry> FieldChecks { get; init; } = [];
    public List<CompletenessIssueEntry> Issues { get; init; } = [];
    public List<string> Strengths { get; init; } = [];
    public bool HasTragicElements { get; init; }
    public bool HasResurrectionTaboo { get; init; }
}

public sealed record FieldCheckEntry
{
    public string Field { get; init; } = "";
    public string Label { get; init; } = "";
    public string Importance { get; init; } = "";
    public bool IsFilled { get; init; }
}

public sealed record CompletenessIssueEntry
{
    public string Dimension { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Suggestion { get; init; }
}
