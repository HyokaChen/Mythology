using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Core.Cloud;
using Core.Data;
using Core.Shared;
using Core.Tools;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 人物弧光追踪 — 追踪角色弧光五要素（起点→驱动力→障碍→转折→终点），检测能耐-弱点平衡，评估弧光完整度
/// CHANGE_07：本地分析引擎已删除，全部通过 IFictionHarnessClient 调云端。
/// </summary>
public sealed class TrackCharacterArcTool(
    INovelStore store,
    IFictionHarnessClient? cloudHarness = null
) : CloudAnalysisTool<TrackCharacterArcParams, CharacterArcDetails>(store, cloudHarness)
{
    public override string Name => "track_character_arc";
    public override string[] Aliases => ["角色弧光", "人物成长", "人物弧线"];
    public override string[] ExampleQueries => ["跟踪主角的成长弧光"];
    public override string Label => "人物弧光追踪";
    public override string Description =>
        "追踪角色弧光五要素（起点→驱动力→障碍→转折→终点），检测能耐-弱点平衡，评估弧光完整度";
    public override ToolCategory Category => ToolCategory.EntityRead;
    protected override string Capability => StoryCheckCapability;
    protected override string Dimension => "角色弧";

    public override async Task<AgentToolResult<CharacterArcDetails>> ExecuteAsync(
        string toolCallId,
        TrackCharacterArcParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<CharacterArcDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var (chapters, novel) = await LoadChaptersAsync(args.NovelId, null, ct);
        ThrowIfCancelled(ct);
        if (chapters.Count == 0)
            return new AgentToolResult<CharacterArcDetails>
            {
                Content = [new TextContent("暂无章节内容可分析")],
                IsError = true,
            };
        var cloudResult = await ExecuteCloudAnalysisAsync(chapters, novel!, ct);
        if (cloudResult is null)
            return CloudUnavailableResult();
        var details = MapCloudResult(cloudResult.Value, novel, args);
        var report = BuildReportText(details);
        return new AgentToolResult<CharacterArcDetails>
        {
            Content = [new TextContent(report)],
            Details = details,
        };
    }

    private static CharacterArcDetails MapCloudResult(
        JsonElement element,
        Novel? novel,
        TrackCharacterArcParams args
    )
    {
        var group = GetDimension(element, "characters");
        var entries = new List<CharacterArcEntry>();
        if (group.ValueKind == JsonValueKind.Array)
        {
            var requestedNames = ResolveRequestedNames(novel, args);
            foreach (var item in group.EnumerateArray())
            {
                var name = GetDimensionString(item, "name");
                if (requestedNames.Count > 0 && !requestedNames.Contains(name))
                    continue;

                var arc = item.TryGetProperty("arc", out var arcJson) ? arcJson : default;
                var keyEvents = new List<ArcEventSummary>();
                if (
                    arc.ValueKind == JsonValueKind.Object
                    && arc.TryGetProperty("arc-events", out var events)
                    && events.ValueKind == JsonValueKind.Array
                )
                {
                    keyEvents = events
                        .EnumerateArray()
                        .GroupBy(e => GetDimensionString(e, "type"))
                        .Select(g => new ArcEventSummary
                        {
                            Type = g.Key,
                            Count = g.Count(),
                            Chapters = g.Select(e => GetDimensionString(e, "chapter-title"))
                                .Where(t => !string.IsNullOrWhiteSpace(t))
                                .Distinct()
                                .ToList(),
                        })
                        .ToList();
                }

                var arcIssues = new List<ArcIssueEntry>();
                if (
                    arc.ValueKind == JsonValueKind.Object
                    && arc.TryGetProperty("issues", out var issues)
                    && issues.ValueKind == JsonValueKind.Array
                )
                {
                    arcIssues.AddRange(
                        issues
                            .EnumerateArray()
                            .Select(issue => new ArcIssueEntry
                            {
                                Element = GetDimensionString(issue, "element"),
                                Severity = GetDimensionString(issue, "severity"),
                                Description = GetDimensionString(issue, "description"),
                                Suggestion = issue.TryGetProperty("suggestion", out var sug)
                                    ? sug.GetString()
                                    : null,
                            })
                    );
                }

                entries.Add(
                    new CharacterArcEntry
                    {
                        CharacterId = GetDimensionString(arc, "character-id"),
                        CharacterName = name,
                        ArcCompleteness = GetDimensionScore(arc, "arc-completeness"),
                        HasStartingState = GetDimensionBool(arc, "has-starting-state"),
                        HasDrivingForce = GetDimensionBool(arc, "has-driving-force"),
                        HasObstacles = GetDimensionBool(arc, "has-obstacles"),
                        HasTurningPoint = GetDimensionBool(arc, "has-turning-point"),
                        HasEndingState = GetDimensionBool(arc, "has-ending-state"),
                        HasPowerWeaknessBalance = GetDimensionBool(
                            arc,
                            "has-power-weakness-balance"
                        ),
                        KeyEvents = keyEvents,
                        Issues = arcIssues,
                        Strengths = ParseStringList(arc, "strengths"),
                    }
                );
            }
        }

        return new CharacterArcDetails
        {
            CharacterCount = entries.Count,
            AvgCompleteness = entries.Count > 0 ? entries.Average(e => e.ArcCompleteness) : 0,
            Characters = entries,
        };
    }

    private static HashSet<string> ResolveRequestedNames(Novel? novel, TrackCharacterArcParams args)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (novel is null)
            return names;

        if (args.CharacterIds is { Count: > 0 })
        {
            foreach (
                var name in novel
                    .Characters.Where(c => args.CharacterIds.Contains(c.Id))
                    .Select(c => c.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
            )
            {
                names.Add(name);
            }
        }

        if (!string.IsNullOrWhiteSpace(args.FocusRole))
        {
            if (Enum.TryParse<CharacterRole>(args.FocusRole, true, out var role))
            {
                foreach (
                    var name in novel
                        .Characters.Where(c => c.Role == role)
                        .Select(c => c.Name)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                )
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    private static string BuildReportText(CharacterArcDetails d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 🔍 人物弧光追踪报告");
        sb.AppendLine();
        sb.AppendLine($"**综合评分**: **{d.AvgCompleteness:F1}** / 100");
        sb.AppendLine();
        sb.AppendLine("---");
        return sb.ToString();
    }
}

public sealed record TrackCharacterArcParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("要追踪的角色ID列表，留空则自动分析主角和反派")]
    public List<string>? CharacterIds { get; init; }

    [Description("按角色定位筛选：protagonist/主角、antagonist/反派、supporting/配角、minor/龙套")]
    public string? FocusRole { get; init; }
}

public sealed record CharacterArcDetails
{
    public int CharacterCount { get; init; }
    public double AvgCompleteness { get; init; }
    public List<CharacterArcEntry> Characters { get; init; } = [];
    public List<string> GlobalWarnings { get; init; } = [];
}

public sealed record CharacterArcEntry
{
    public string CharacterId { get; init; } = "";
    public string CharacterName { get; init; } = "";
    public double ArcCompleteness { get; init; }
    public bool HasStartingState { get; init; }
    public bool HasDrivingForce { get; init; }
    public bool HasObstacles { get; init; }
    public bool HasTurningPoint { get; init; }
    public bool HasEndingState { get; init; }
    public bool HasPowerWeaknessBalance { get; init; }
    public List<ArcEventSummary> KeyEvents { get; init; } = [];
    public List<ArcIssueEntry> Issues { get; init; } = [];
    public List<string> Strengths { get; init; } = [];
}

public sealed record ArcEventSummary
{
    public string Type { get; init; } = "";
    public int Count { get; init; }
    public List<string> Chapters { get; init; } = [];
}

public sealed record ArcIssueEntry
{
    public string Element { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Suggestion { get; init; }
}
