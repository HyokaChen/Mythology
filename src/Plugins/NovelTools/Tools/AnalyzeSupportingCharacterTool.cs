using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Core.Cloud;
using Core.Data;
using Core.Shared;
using Core.Tools;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 配角功能分析 — 配角功能分类与质量评估：检测工具人配角、类型化配角、功能缺失
/// CHANGE_07：本地分析引擎已删除，全部通过 IFictionHarnessClient 调云端。
/// </summary>
public sealed class AnalyzeSupportingCharacterTool(
    INovelStore store,
    IFictionHarnessClient? cloudHarness = null
)
    : CloudAnalysisTool<AnalyzeSupportingCharacterParams, SupportingCharacterDetails>(
        store,
        cloudHarness
    )
{
    public override string Name => "analyze_supporting_character";
    public override string Label => "配角功能分析";
    public override string Description =>
        "配角功能分类与质量评估：检测工具人配角、类型化配角、功能缺失";
    public override ToolCategory Category => ToolCategory.EntityRead;
    protected override string Capability => StoryCheckCapability;
    protected override string Dimension => "配角";

    public override async Task<AgentToolResult<SupportingCharacterDetails>> ExecuteAsync(
        string toolCallId,
        AnalyzeSupportingCharacterParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<SupportingCharacterDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var (chapters, novel) = await LoadChaptersAsync(args.NovelId, null, ct);
        ThrowIfCancelled(ct);
        if (chapters.Count == 0)
            return new AgentToolResult<SupportingCharacterDetails>
            {
                Content = [new TextContent("暂无章节内容可分析")],
                IsError = true,
            };
        var cloudResult = await ExecuteCloudAnalysisAsync(chapters, novel!, ct);
        if (cloudResult is null)
            return CloudUnavailableResult();
        var details = MapCloudResult(cloudResult.Value);
        var report = BuildReportText(details);
        return new AgentToolResult<SupportingCharacterDetails>
        {
            Content = [new TextContent(report)],
            Details = details,
        };
    }

    private static SupportingCharacterDetails MapCloudResult(JsonElement element)
    {
        var dim = GetDimension(element, "supporting-characters");
        var entries = new List<SupportingCharacterEntry>();
        if (
            dim.ValueKind == JsonValueKind.Object
            && dim.TryGetProperty("characters", out var chars)
            && chars.ValueKind == JsonValueKind.Array
        )
        {
            foreach (var item in chars.EnumerateArray())
            {
                var matchedTypes = new List<string>();
                if (
                    item.TryGetProperty("matched-types", out var types)
                    && types.ValueKind == JsonValueKind.Array
                )
                {
                    foreach (var t in types.EnumerateArray())
                    {
                        if (t.ValueKind == JsonValueKind.String)
                            matchedTypes.Add(t.GetString() ?? "");
                    }
                }

                entries.Add(
                    new SupportingCharacterEntry
                    {
                        CharacterId = GetDimensionString(item, "character-id"),
                        CharacterName = GetDimensionString(item, "character-name"),
                        PrimaryType = GetDimensionString(item, "primary-type"),
                        PrimaryLabel = GetDimensionString(item, "primary-label"),
                        Function = GetDimensionString(item, "function"),
                        TypeConfidence = GetDimensionScore(item, "type-confidence"),
                        MatchedTypes = matchedTypes,
                        IsToolCharacter = GetDimensionBool(item, "is-tool-character"),
                    }
                );
            }
        }

        return new SupportingCharacterDetails
        {
            OverallScore = GetDimensionScore(dim),
            TotalSupporting = GetDimensionInt(dim, "total-supporting"),
            TypedSupporting = GetDimensionInt(dim, "typed-supporting"),
            ToolCharacterCount = GetDimensionInt(dim, "tool-character-count"),
            Characters = entries,
            Issues = ParseDimensionIssues(dim)
                .Select(i => new SupportingIssueEntry
                {
                    Dimension = i.Dimension,
                    Severity = i.Severity,
                    Description = i.Description,
                    Suggestion = i.Suggestion,
                })
                .ToList(),
            Strengths = ParseStringList(dim, "strengths"),
        };
    }

    private static string BuildReportText(SupportingCharacterDetails d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🔍 配角功能分析报告");
        sb.AppendLine();
        sb.AppendLine($"**综合评分**: **{d.OverallScore:F1}** / 100");
        sb.AppendLine();
        sb.AppendLine("---");
        return sb.ToString();
    }
}

public sealed record AnalyzeSupportingCharacterParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";
}

public sealed record SupportingCharacterDetails
{
    public double OverallScore { get; init; }
    public int TotalSupporting { get; init; }
    public int TypedSupporting { get; init; }
    public int ToolCharacterCount { get; init; }
    public List<SupportingCharacterEntry> Characters { get; init; } = [];
    public List<SupportingIssueEntry> Issues { get; init; } = [];
    public List<string> Strengths { get; init; } = [];
}

public sealed record SupportingCharacterEntry
{
    public string CharacterId { get; init; } = "";
    public string CharacterName { get; init; } = "";
    public string PrimaryType { get; init; } = "";
    public string PrimaryLabel { get; init; } = "";
    public string Function { get; init; } = "";
    public double TypeConfidence { get; init; }
    public List<string> MatchedTypes { get; init; } = [];
    public bool IsToolCharacter { get; init; }
}

public sealed record SupportingIssueEntry
{
    public string Dimension { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Suggestion { get; init; }
}
