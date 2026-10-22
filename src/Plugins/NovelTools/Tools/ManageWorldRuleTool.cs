using System.ComponentModel;
using Core.Data;
using Core.Evolution;
using Core.Shared;
using Core.Tools;
using EntityTypeEnum = Core.Data.EntityType;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 世界规则/设定管理（AI 写入面）：create/update/delete/list。
/// 规则是本体「规则」要素；写入经 INovelStore 自动落入本体动作日志。
/// </summary>
public sealed class ManageWorldRuleTool(INovelStore store)
    : AgentTool<ManageWorldRuleParams, ManageWorldRuleDetails>
{
    public override string Name => "manage_world_rule";
    public override string Label => "世界规则管理";
    public override string[] Aliases => ["世界规则", "规则管理", "设定规则", "world rule"];
    public override string[] ExampleQueries =>
        ["给宗门加一条硬约束：御剑需要金丹", "新增一条世界设定：外门弟子不得御剑"];
    public override string Description =>
        "世界规则/设定 CRUD：create/update/delete/list。规则支持数值/前置/禁止/触发四类，"
        + "可设 severity（info/warning/constraint）、章节生效范围与关系前置（member-of 等）。";
    public override ToolCategory Category => ToolCategory.EntityCrud;
    public override string? EntityType => "world-rule";
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential;

    public override async Task<AgentToolResult<ManageWorldRuleDetails>> ExecuteAsync(
        string toolCallId,
        ManageWorldRuleParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<ManageWorldRuleDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        using var _ = OntologyWriteContext.Attach(args.Evidence, args.ProposalId);
        var novelId = args.NovelId?.Trim() ?? "";
        if (novelId.Length == 0)
            return Error("novel-id 必填");

        try
        {
            return (args.Action ?? "list").Trim().ToLowerInvariant() switch
            {
                "list" => await ListAsync(novelId, ct),
                "create" => await CreateAsync(novelId, args, ct),
                "update" => await UpdateAsync(novelId, args, ct),
                "delete" => await DeleteAsync(novelId, args, ct),
                _ => Error("未知操作，支持 create/update/delete/list"),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Error($"世界规则操作失败：{ex.Message}");
        }
    }

    private async Task<AgentToolResult<ManageWorldRuleDetails>> ListAsync(
        string novelId,
        CancellationToken ct
    )
    {
        var rules = await store.GetWorldRules(novelId, ct);
        if (rules.Count == 0)
            return Ok("暂无世界规则/设定", new ManageWorldRuleDetails());

        var lines = rules.Select(rule =>
        {
            var kind = rule.EntryKind == WorldRuleEntryKind.Setting ? "设定" : "规则";
            var title =
                !string.IsNullOrWhiteSpace(rule.Description) ? rule.Description
                : !string.IsNullOrWhiteSpace(rule.Type) ? rule.Type
                : Truncate(rule.Content, 40);
            var subject = rule.SubjectType is { } type
                ? $"{type.ToKebabCaseString()}:{rule.SubjectId ?? "*"}"
                : "global";
            var mechanism =
                rule.EntryKind == WorldRuleEntryKind.Setting
                    ? rule.Category.ToString().ToLowerInvariant()
                    : rule.RuleKind.ToString().ToLowerInvariant();
            return $"{kind} | {rule.Id} | {mechanism} | {rule.Severity.ToString().ToLowerInvariant()} | {subject} | {title}";
        });
        return Ok(string.Join("\n", lines), new ManageWorldRuleDetails { Count = rules.Count });
    }

    private async Task<AgentToolResult<ManageWorldRuleDetails>> CreateAsync(
        string novelId,
        ManageWorldRuleParams args,
        CancellationToken ct
    )
    {
        var rule = new WorldRule { Id = NewId(), NovelId = novelId };
        Apply(rule, args);
        var error = await ValidateAsync(novelId, rule, ct);
        if (error is not null)
            return Error(error);

        await store.SaveWorldRule(novelId, rule, ct);
        return Ok($"规则已创建: {rule.Id}", new ManageWorldRuleDetails { Id = rule.Id, Count = 1 });
    }

    private async Task<AgentToolResult<ManageWorldRuleDetails>> UpdateAsync(
        string novelId,
        ManageWorldRuleParams args,
        CancellationToken ct
    )
    {
        var ruleId = args.RuleId?.Trim() ?? "";
        if (ruleId.Length == 0)
            return Error("update 需要 rule-id");
        var rule = (await store.GetWorldRules(novelId, ct)).FirstOrDefault(r =>
            string.Equals(r.Id, ruleId, StringComparison.Ordinal)
        );
        if (rule is null)
            return Error($"规则不存在: {ruleId}");

        Apply(rule, args);
        var error = await ValidateAsync(novelId, rule, ct);
        if (error is not null)
            return Error(error);

        await store.SaveWorldRule(novelId, rule, ct);
        return Ok($"规则已更新: {rule.Id}", new ManageWorldRuleDetails { Id = rule.Id, Count = 1 });
    }

    private async Task<AgentToolResult<ManageWorldRuleDetails>> DeleteAsync(
        string novelId,
        ManageWorldRuleParams args,
        CancellationToken ct
    )
    {
        var ruleId = args.RuleId?.Trim() ?? "";
        if (ruleId.Length == 0)
            return Error("delete 需要 rule-id");
        var exists = (await store.GetWorldRules(novelId, ct)).Any(r =>
            string.Equals(r.Id, ruleId, StringComparison.Ordinal)
        );
        if (!exists)
            return Error($"规则不存在: {ruleId}");

        await store.DeleteWorldRule(novelId, ruleId, ct);
        return Ok($"规则已删除: {ruleId}", new ManageWorldRuleDetails { Id = ruleId, Count = 0 });
    }

    private static void Apply(WorldRule rule, ManageWorldRuleParams args)
    {
        if (args.EntryKind is { Length: > 0 } entryKind)
            rule.EntryKind = ParseEnum(entryKind, WorldRuleEntryKind.Rule);
        if (args.Category is { Length: > 0 } category)
            rule.Category = ParseEnum(category, WorldRuleCategory.Other);
        if (args.Description is not null)
            rule.Description = args.Description.Trim();
        if (args.Content is not null)
            rule.Content = args.Content.Trim();
        if (args.RuleKind is { Length: > 0 } ruleKind)
            rule.RuleKind = ParseEnum(ruleKind, RuleKind.Numeric);
        if (args.Type is not null)
            rule.Type = args.Type.Trim();
        if (args.Value is { } value)
            rule.Value = value;
        if (args.Unit is not null)
            rule.Unit = args.Unit.Trim();
        if (args.SubjectType is { Length: > 0 } subjectType)
            rule.SubjectType =
                EntityTypeExtensions.ParseEntityType(subjectType)
                ?? ParseEnum(subjectType, EntityTypeEnum.Character);
        if (args.SubjectId is not null)
            rule.SubjectId = NullIfEmpty(args.SubjectId);
        if (args.Metric is not null)
            rule.Metric = NullIfEmpty(args.Metric);
        if (args.Operator is not null)
            rule.Operator = NullIfEmpty(args.Operator);
        if (args.Threshold is { } threshold)
            rule.Threshold = threshold;
        if (args.RuleAction is not null)
            rule.Action = NullIfEmpty(args.RuleAction);
        if (args.RequiredState is not null)
            rule.RequiredState = NullIfEmpty(args.RequiredState);
        if (args.ForbiddenAction is not null)
            rule.ForbiddenAction = NullIfEmpty(args.ForbiddenAction);
        if (args.RequiredRelationType is not null)
            rule.RequiredRelationType = NullIfEmpty(args.RequiredRelationType);
        if (args.RequiredRelationTargetId is not null)
            rule.RequiredRelationTargetId = NullIfEmpty(args.RequiredRelationTargetId);
        if (args.Severity is { Length: > 0 } severity)
            rule.Severity = ParseEnum(severity, RuleSeverity.Warning);
        if (args.EffectiveFromChapterId is not null)
            rule.EffectiveFromChapterId = NullIfEmpty(args.EffectiveFromChapterId);
        if (args.EffectiveUntilChapterId is not null)
            rule.EffectiveUntilChapterId = NullIfEmpty(args.EffectiveUntilChapterId);
        if (args.Effect is { } effect)
        {
            rule.Effect = new PlannedDelta
            {
                Kind = effect.Kind is { Length: > 0 } kind
                    ? ParseEnum(kind, PlannedDeltaKind.StateChange)
                    : PlannedDeltaKind.StateChange,
                TargetEntityType = effect.TargetEntityType is { Length: > 0 } targetType
                    ? EntityTypeExtensions.ParseEntityType(targetType)
                        ?? ParseEnum(targetType, EntityTypeEnum.Character)
                    : EntityTypeEnum.Character,
                TargetId = effect.TargetId ?? "",
                Aspect = effect.Aspect is { Length: > 0 } aspect
                    ? ParseEnum(aspect, EvolutionAspect.Other)
                    : null,
                FromSummary = NullIfEmpty(effect.From),
                ToSummary = NullIfEmpty(effect.To),
                Summary = effect.Summary ?? "",
            };
        }
    }

    private async Task<string?> ValidateAsync(string novelId, WorldRule rule, CancellationToken ct)
    {
        if (rule.EntryKind == WorldRuleEntryKind.Setting)
        {
            rule.Content = rule.Content ?? "";
        }
        var error = WorldRulePresentation.Validate(rule);
        if (error is not null)
            return error;

        if (
            !string.IsNullOrWhiteSpace(rule.EffectiveFromChapterId)
            || !string.IsNullOrWhiteSpace(rule.EffectiveUntilChapterId)
        )
        {
            var chapters = await store.GetChapters(novelId, ct);
            var ids = chapters.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            if (
                !string.IsNullOrWhiteSpace(rule.EffectiveFromChapterId)
                && !ids.Contains(rule.EffectiveFromChapterId)
            )
                return $"章节不存在: {rule.EffectiveFromChapterId}";
            if (
                !string.IsNullOrWhiteSpace(rule.EffectiveUntilChapterId)
                && !ids.Contains(rule.EffectiveUntilChapterId)
            )
                return $"章节不存在: {rule.EffectiveUntilChapterId}";
        }
        return null;
    }

    private static T ParseEnum<T>(string value, T fallback)
        where T : struct, Enum =>
        Enum.TryParse<T>(value.Replace("-", "").Replace("_", ""), true, out var parsed)
            ? parsed
            : fallback;

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    private static string Truncate(string? text, int max) =>
        string.IsNullOrEmpty(text) ? ""
        : text.Length <= max ? text
        : text[..max];

    private static AgentToolResult<ManageWorldRuleDetails> Ok(
        string message,
        ManageWorldRuleDetails details
    ) => new() { Content = [new TextContent(message)], Details = details };

    private static AgentToolResult<ManageWorldRuleDetails> Error(string message) =>
        new() { Content = [new TextContent(message)], IsError = true };
}

public sealed record ManageWorldRuleParams
{
    [Description("操作: create / update / delete / list")]
    public string Action { get; init; } = "list";

    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("规则ID（update/delete 时必填；list 可查看全部规则 ID）")]
    public string? RuleId { get; init; }

    [Description("条目类型：rule（可执行规则）/ setting（描述性设定）")]
    public string? EntryKind { get; init; }

    [Description("领域：natural/power/social/narrative/cognitive/other")]
    public string? Category { get; init; }

    [Description("规则/设定标题（必填）")]
    public string? Description { get; init; }

    [Description("设定正文（entry-kind=setting 时必填）")]
    public string? Content { get; init; }

    [Description("规则机制：numeric/requires/forbids/triggers")]
    public string? RuleKind { get; init; }

    [Description("规则类型规范键（numeric 预设用，如 travel-speed/location-distance）")]
    public string? Type { get; init; }

    [Description("规则数值（numeric 预设用）")]
    public double? Value { get; init; }

    [Description("单位（numeric 预设用，如 里/日）")]
    public string? Unit { get; init; }

    [Description("作用主体类型：character/faction/location/item")]
    public string? SubjectType { get; init; }

    [Description("作用主体 ID（留空 = 该类型全部实体）")]
    public string? SubjectId { get; init; }

    [Description("数值指标名（numeric 用，如 speed）")]
    public string? Metric { get; init; }

    [Description("比较符：> >= < <= ==")]
    public string? Operator { get; init; }

    [Description("阈值（numeric 用）")]
    public double? Threshold { get; init; }

    [Description("行动/条件 token（requires/forbids/triggers 用，如 御剑）")]
    public string? RuleAction { get; init; }

    [Description("所需状态 token（requires 用，如 金丹）")]
    public string? RequiredState { get; init; }

    [Description("被禁止的行动 token（forbids 用）")]
    public string? ForbiddenAction { get; init; }

    [Description(
        "关系前置 token：member-of/has-member/relationship/owns/owned-by/parent/adjacent/headquarters"
    )]
    public string? RequiredRelationType { get; init; }

    [Description("关系前置目标 ID（留空 = 任意目标）")]
    public string? RequiredRelationTargetId { get; init; }

    [Description("严重度：info/warning/constraint（默认 warning）")]
    public string? Severity { get; init; }

    [Description("规则生效起始章节 ID（可选）")]
    public string? EffectiveFromChapterId { get; init; }

    [Description("规则生效结束章节 ID（可选）")]
    public string? EffectiveUntilChapterId { get; init; }

    [Description("条件满足后的后果（可选）")]
    public RuleEffectParams? Effect { get; init; }

    [Description("正文证据引文（可选，应用提案时传）")]
    public string? Evidence { get; init; }

    [Description("提案 ID（可选，来自 scan_ontology）")]
    public string? ProposalId { get; init; }
}

public sealed record RuleEffectParams
{
    [Description("后果类型：state-change/relation-change/rule-trigger/action/plotline-reveal")]
    public string? Kind { get; init; }

    [Description("后果目标实体类型")]
    public string? TargetEntityType { get; init; }

    [Description("后果目标实体 ID")]
    public string? TargetId { get; init; }

    [Description(
        "变化面：status/personality/motivation/relationship/ability/possession/location/knowledge/behavior/other"
    )]
    public string? Aspect { get; init; }

    [Description("变化前一句话")]
    public string? From { get; init; }

    [Description("变化后一句话")]
    public string? To { get; init; }

    [Description("后果摘要（必填）")]
    public string? Summary { get; init; }
}

public sealed record ManageWorldRuleDetails
{
    public string Id { get; init; } = "";
    public int Count { get; init; }
}
