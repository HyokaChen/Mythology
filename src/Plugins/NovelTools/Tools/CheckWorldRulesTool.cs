using System.ComponentModel;
using Core.Agent;
using Core.Data;
using Core.Evolution;
using Core.Shared;
using Core.Tools;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 本地世界规则检查：复用 <see cref="MaterialBoundaryDetector"/> 的规则触发结论，
/// 按 severity 汇总，不调用云端、不写库。
/// </summary>
public sealed class CheckWorldRulesTool(INovelStore store, Agent? agent = null)
    : AgentTool<CheckWorldRulesParams, CheckWorldRulesDetails>
{
    public override string Name => "check_world_rules";
    public override string Label => "世界规则检查";
    public override string[] Aliases => ["规则检查", "规则校验", "检查世界规则", "rule check"];
    public override string[] ExampleQueries =>
        ["检查这一章有没有违反宗门规则", "本章触发了哪些世界规则"];
    public override string Description =>
        "本地确定性检查当前章的规则触发/违反（不调用云端、不扣积分），按硬约束/警告/提示分组。";
    public override ToolCategory Category => ToolCategory.EntityRead;
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<CheckWorldRulesDetails>> ExecuteAsync(
        string toolCallId,
        CheckWorldRulesParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<CheckWorldRulesDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var novelId = args.NovelId?.Trim();
        if (string.IsNullOrWhiteSpace(novelId))
            novelId = agent?.CurrentNovelId;
        if (string.IsNullOrWhiteSpace(novelId))
            return Error("novel-id 必填（或先打开小说）");

        try
        {
            var novel = await store.GetNovel(novelId, ct);
            if (novel is null)
                return Error($"未找到小说（novel_id={novelId}）");

            var chapters = await store.GetChapters(novelId, ct);
            var chapter = !string.IsNullOrWhiteSpace(args.ChapterId)
                ? chapters.FirstOrDefault(c => c.Id == args.ChapterId)
                : chapters.OrderBy(c => c.Order).LastOrDefault();
            if (chapter is null)
                return Error("没有可检查的章节");

            var worldRules = await store.GetWorldRules(novelId, ct);
            var rules = worldRules.Where(r => r.EntryKind == WorldRuleEntryKind.Rule).ToList();
            if (rules.Count == 0)
            {
                return Ok(
                    "未定义世界规则（world-rules.yaml 中无 entry-kind=rule 条目）",
                    new CheckWorldRulesDetails()
                );
            }

            var scenes = await store.GetScenesByChapter(chapter.Id, ct);
            var view = await EntityTimelineView.BuildAsync(store, novelId, ct);
            var boundaries = MaterialBoundaryDetector.DetectForChapter(
                view,
                novel,
                chapter,
                scenes,
                worldRules
            );
            var findings = boundaries
                .SelectMany(b => b.Deltas)
                .Where(d =>
                    d.Kind == PlannedDeltaKind.RuleTrigger && !string.IsNullOrEmpty(d.RuleId)
                )
                .GroupBy(d => $"{d.RuleId}|{d.EntityType}|{d.EntityId}", StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();
            if (findings.Count == 0)
            {
                return Ok(
                    $"第 {chapter.Order} 章未触发任何世界规则（共 {rules.Count} 条规则）",
                    new CheckWorldRulesDetails { RuleCount = rules.Count }
                );
            }

            var max = Math.Clamp(args.MaxFindings <= 0 ? 20 : args.MaxFindings, 1, 50);
            var byId = rules.ToDictionary(r => r.Id, StringComparer.Ordinal);
            var ordered = findings
                .Select(d =>
                {
                    byId.TryGetValue(d.RuleId!, out var rule);
                    return (Delta: d, Rule: rule);
                })
                .Where(x => x.Rule is not null)
                .OrderBy(x =>
                    x.Rule!.Severity switch
                    {
                        RuleSeverity.Constraint => 0,
                        RuleSeverity.Warning => 1,
                        _ => 2,
                    }
                )
                .ThenBy(x => x.Delta.EntityName, StringComparer.Ordinal)
                .Take(max)
                .ToList();

            var lines = new List<string>
            {
                $"世界规则检查（第 {chapter.Order} 章，命中 {ordered.Count} 条）",
            };
            foreach (var (delta, rule) in ordered)
            {
                var label = rule!.Severity switch
                {
                    RuleSeverity.Constraint => "硬约束",
                    RuleSeverity.Warning => "警告",
                    _ => "提示",
                };
                lines.Add($"[{label}] 规则「{rule.Description}」");
                lines.Add(
                    $"  主体：{delta.EntityType.ToKebabCaseString()}「{delta.EntityName}」| 后果：{delta.Summary}"
                );
            }

            return Ok(
                string.Join("\n", lines),
                new CheckWorldRulesDetails { RuleCount = rules.Count, FindingCount = ordered.Count }
            );
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Error($"规则检查失败：{ex.Message}");
        }
    }

    private static AgentToolResult<CheckWorldRulesDetails> Ok(
        string message,
        CheckWorldRulesDetails details
    ) => new() { Content = [new TextContent(message)], Details = details };

    private static AgentToolResult<CheckWorldRulesDetails> Error(string message) =>
        new() { Content = [new TextContent(message)], IsError = true };
}

public sealed record CheckWorldRulesParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("章节 ID（留空 = 最新章）")]
    public string? ChapterId { get; init; }

    [Description("最多返回的命中条数（默认 20，范围 1-50）")]
    public int MaxFindings { get; init; }
}

public sealed record CheckWorldRulesDetails
{
    public int RuleCount { get; init; }
    public int FindingCount { get; init; }
}
