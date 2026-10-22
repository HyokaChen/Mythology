using System.ComponentModel;
using Core.Data;
using Core.Evolution;
using Core.Shared;
using Core.Tools;
using EntityTypeEnum = Core.Data.EntityType;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 场景计划变化（SceneDefinition.PlannedDeltas）写入面：replace/append/clear。
/// 计划变化是本体「行动/状态/关系」的写作前声明，进入素材边界检测。
/// </summary>
public sealed class PlanSceneDeltasTool(INovelStore store)
    : AgentTool<PlanSceneDeltasParams, PlanSceneDeltasDetails>
{
    public override string Name => "plan_scene_deltas";
    public override string Label => "场景计划变化";
    public override string[] Aliases => ["计划变化", "场景 delta", "预演变化"];
    public override string[] ExampleQueries =>
        ["给这一场加一条计划：林长歌重伤", "清空本场景的计划变化"];
    public override string Description =>
        "写入/替换/清空场景的 planned-deltas（状态/关系/规则触发/行动/情节揭示），"
        + "写作前声明本场预期变化；写入经 INovelStore 并记录本体动作日志。";
    public override ToolCategory Category => ToolCategory.EntityCrud;
    public override string? EntityType => "scene";
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential;

    public override async Task<AgentToolResult<PlanSceneDeltasDetails>> ExecuteAsync(
        string toolCallId,
        PlanSceneDeltasParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<PlanSceneDeltasDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var novelId = args.NovelId?.Trim() ?? "";
        var sceneId = args.SceneId?.Trim() ?? "";
        if (novelId.Length == 0 || sceneId.Length == 0)
            return Error("novel-id 与 scene-id 必填");

        try
        {
            var scene = await store.GetScene(novelId, sceneId, ct);
            if (scene is null)
                return Error($"场景不存在: {sceneId}");

            var mode = (args.Mode ?? "replace").Trim().ToLowerInvariant();
            if (mode is not ("replace" or "append" or "clear"))
                return Error("mode 只能是 replace / append / clear");

            if (mode == "clear")
            {
                scene.PlannedDeltas.Clear();
            }
            else
            {
                var parsed = new List<PlannedDelta>();
                if (args.Deltas is { Count: > 0 })
                {
                    var rules = await store.GetWorldRules(novelId, ct);
                    var ruleIds = rules.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
                    foreach (var delta in args.Deltas)
                    {
                        if (delta is null)
                            return Error("deltas 不能包含空条目");
                        var built = Build(delta, ruleIds, out var error);
                        if (built is null)
                            return Error(error ?? "计划变化无效");
                        parsed.Add(built);
                    }
                }

                if (mode == "replace")
                {
                    scene.PlannedDeltas.Clear();
                }
                scene.PlannedDeltas.AddRange(parsed);
            }

            using (OntologyWriteContext.Attach(args.Evidence, args.ProposalId))
            {
                await store.SaveScene(scene, ct);
            }

            var text =
                mode == "clear"
                    ? $"场景「{scene.Title}」计划变化已清空"
                    : $"场景「{scene.Title}」计划变化已更新：{scene.PlannedDeltas.Count} 条";
            return Ok(
                text,
                new PlanSceneDeltasDetails { SceneId = scene.Id, Count = scene.PlannedDeltas.Count }
            );
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Error($"写入计划变化失败：{ex.Message}");
        }
    }

    private static PlannedDelta? Build(
        PlannedDeltaParams args,
        IReadOnlySet<string> ruleIds,
        out string? error
    )
    {
        error = null;
        if (string.IsNullOrWhiteSpace(args.Summary))
        {
            error = "每条计划变化必须填写 summary";
            return null;
        }
        if (string.IsNullOrWhiteSpace(args.TargetId))
        {
            error = "每条计划变化必须填写 target-id";
            return null;
        }

        var kind = ParseEnum(args.Kind, PlannedDeltaKind.StateChange);
        if (kind == PlannedDeltaKind.RuleTrigger)
        {
            var ruleId = args.RuleId?.Trim() ?? "";
            if (ruleId.Length == 0)
            {
                error = "kind=rule-trigger 时 rule-id 必填";
                return null;
            }
            if (!ruleIds.Contains(ruleId))
            {
                error = $"规则不存在: {ruleId}";
                return null;
            }
        }

        var targetType =
            EntityTypeExtensions.ParseEntityType(args.TargetEntityType)
            ?? ParseEnum(args.TargetEntityType, EntityTypeEnum.Character);
        EvolutionAspect? aspect = null;
        if (!string.IsNullOrWhiteSpace(args.Aspect))
        {
            aspect = ParseEnum(args.Aspect, EvolutionAspect.Other);
        }

        return new PlannedDelta
        {
            Kind = kind,
            TargetEntityType = targetType,
            TargetId = args.TargetId.Trim(),
            Aspect = aspect,
            FromSummary = NullIfEmpty(args.From),
            ToSummary = NullIfEmpty(args.To),
            Summary = args.Summary.Trim(),
            RuleId = NullIfEmpty(args.RuleId),
            ActionType = NullIfEmpty(args.ActionType),
        };
    }

    private static T ParseEnum<T>(string? value, T fallback)
        where T : struct, Enum =>
        !string.IsNullOrWhiteSpace(value)
        && Enum.TryParse<T>(value.Replace("-", "").Replace("_", ""), true, out var parsed)
            ? parsed
            : fallback;

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static AgentToolResult<PlanSceneDeltasDetails> Ok(
        string message,
        PlanSceneDeltasDetails details
    ) => new() { Content = [new TextContent(message)], Details = details };

    private static AgentToolResult<PlanSceneDeltasDetails> Error(string message) =>
        new() { Content = [new TextContent(message)], IsError = true };
}

public sealed record PlanSceneDeltasParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("场景 ID（必填）")]
    public string SceneId { get; init; } = "";

    [Description("replace（替换全部）/ append（追加）/ clear（清空）")]
    public string Mode { get; init; } = "replace";

    [Description("计划变化列表（clear 时可为空）")]
    public List<PlannedDeltaParams>? Deltas { get; init; }

    [Description("正文证据引文（可选，应用提案时传）")]
    public string? Evidence { get; init; }

    [Description("提案 ID（可选，来自 scan_ontology 时传）")]
    public string? ProposalId { get; init; }
}

public sealed record PlannedDeltaParams
{
    [Description("类型：state-change/relation-change/rule-trigger/action/plotline-reveal")]
    public string? Kind { get; init; }

    [Description("目标实体类型：character/faction/location/item/plotline/scene")]
    public string? TargetEntityType { get; init; }

    [Description("目标实体 ID")]
    public string? TargetId { get; init; }

    [Description(
        "变化面：status/personality/motivation/relationship/ability/possession/location/knowledge/behavior/other"
    )]
    public string? Aspect { get; init; }

    [Description("变化前一句话")]
    public string? From { get; init; }

    [Description("变化后一句话")]
    public string? To { get; init; }

    [Description("一句话摘要（必填）")]
    public string? Summary { get; init; }

    [Description("规则 ID（kind=rule-trigger 必填，必须是已有规则）")]
    public string? RuleId { get; init; }

    [Description("行动类型（kind=action 用，如 御剑/battle/ceremony）")]
    public string? ActionType { get; init; }
}

public sealed record PlanSceneDeltasDetails
{
    public string SceneId { get; init; } = "";
    public int Count { get; init; }
}
