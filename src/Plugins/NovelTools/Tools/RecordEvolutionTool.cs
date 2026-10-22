using System.ComponentModel;
using Core.Data;
using Core.Evolution;
using Core.Shared;
using Core.Tools;
using EntityTypeEnum = Core.Data.EntityType;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 登记变化事件（ENTITY-EVOLUTION §4.3 L2 + 状态变更生命周期）——
/// AI 写作时显式登记实体在某章发生的性格/行为/状态/关系等变化，Source=Ai。
/// 草稿章写入 Pending（待发布）；已发布章写入 pending 后立即补记（ApplyAmendment）。
/// </summary>
public sealed class RecordEvolutionTool(INovelStore store)
    : AgentTool<RecordEvolutionParams, RecordEvolutionDetails>
{
    /// <summary>工具唯一标识符</summary>
    public override string Name => "record_evolution";
    public override string[] Aliases => ["登记变化", "记录演化", "标记转变"];
    public override string[] ExampleQueries => ["记录主角第十二章死亡"];

    /// <summary>工具显示名称</summary>
    public override string Label => "登记变化";
    public override string Description =>
        "登记实体在某章发生的变化（性格/动机/关系/状态/持有等），可带结构化 effects；"
        + "草稿章待发布，已发布章立即补记。";
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突
    public override ToolCategory Category => ToolCategory.EntityCrud;

    public override async Task<AgentToolResult<RecordEvolutionDetails>> ExecuteAsync(
        string toolCallId,
        RecordEvolutionParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<RecordEvolutionDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        if (string.IsNullOrWhiteSpace(args.TargetId))
            return Error("必须提供目标实体 ID（target_id）");
        if (string.IsNullOrWhiteSpace(args.ChapterId))
            return Error("必须提供章节 ID（chapter_id）");

        var chapter = await store.GetChapter(args.NovelId, args.ChapterId, ct);
        ThrowIfCancelled(ct);
        if (chapter is null)
            return Error($"章节 {args.ChapterId} 不存在");

        var novel = await store.GetNovel(args.NovelId, ct);
        var chapterChanges = (await store.GetEntityStateChanges(args.NovelId, ct))
            .Where(c => string.Equals(c.ChapterId, chapter.Id, StringComparison.Ordinal))
            .ToList();

        var existing = string.IsNullOrWhiteSpace(args.ChangeId)
            ? null
            : chapterChanges.FirstOrDefault(c =>
                string.Equals(c.Id, args.ChangeId, StringComparison.Ordinal)
            );
        if (existing is { Lifecycle: ChangeLifecycle.Committed })
        {
            return Error(
                "已生效变化不可原地编辑；请撤回章节，或新增一条修正变化（新 change_id）。"
            );
        }

        // 1. 结构化效果：显式 effects，或 aspect=status + to 的快捷入口
        var effects = new List<StateEffect>();
        if (args.Effects is { Count: > 0 })
        {
            foreach (var raw in args.Effects)
            {
                effects.Add(
                    new StateEffect
                    {
                        TargetType = raw.TargetType,
                        TargetId = string.IsNullOrWhiteSpace(raw.TargetId)
                            ? args.TargetId
                            : raw.TargetId,
                        Field = raw.Field,
                        Operation = raw.Operation,
                        Value = raw.Value,
                    }
                );
            }
        }
        else if (
            args.Aspect == EvolutionAspect.Status
            && !string.IsNullOrWhiteSpace(args.ToValue ?? args.To)
        )
        {
            effects.Add(
                new StateEffect
                {
                    TargetType = args.TargetType,
                    TargetId = args.TargetId,
                    Field = "status",
                    Operation = StateEffectOperation.Set,
                    Value = args.ToValue ?? args.To,
                }
            );
        }

        foreach (var effect in effects)
        {
            var error = StateEffectValidator.Validate(effect, novel);
            if (error is not null)
                return Error($"状态效果非法：{error}");
        }

        // 2. 章内顺序与上下文
        var sequence =
            existing?.Sequence
            ?? (chapterChanges.Count == 0 ? 1 : chapterChanges.Max(c => c.Sequence) + 1);

        var scenes = await store.GetScenesByChapter(chapter.Id, ct);
        var targetScene =
            (
                args.SceneId is null
                    ? null
                    : scenes.FirstOrDefault(s =>
                        string.Equals(s.Id, args.SceneId, StringComparison.Ordinal)
                    )
            )
            ?? scenes.FirstOrDefault(s =>
                s.ParticipantIds.Contains(args.TargetId, StringComparer.Ordinal)
            )
            ?? scenes.FirstOrDefault();

        var locationId = args.LocationId ?? targetScene?.LocationId;
        var sceneId = args.SceneId ?? targetScene?.Id;
        var timeDescription = args.TimeDescription ?? targetScene?.TimeDescription;
        var plotlineIds =
            args.PlotlineIds is { Count: > 0 } ? args.PlotlineIds
            : targetScene is { PlotlineIds.Count: > 0 } ? targetScene.PlotlineIds
            : chapter.PlotlineIds;

        var visibility = ParseVisibility(args.Visibility);

        var toValue = effects.FirstOrDefault()?.Value ?? args.ToValue ?? args.To;
        var summary = string.IsNullOrWhiteSpace(args.Summary)
            ? effects.Count == 0
                ? $"{args.TargetType.ToKebabCaseString()} {args.TargetId} 变化"
                : $"{effects[0].Field}: {args.From ?? "?"} → {toValue ?? "?"}"
            : args.Summary;

        var now = DateTime.UtcNow;
        var change = new EntityStateChange
        {
            Id = string.IsNullOrWhiteSpace(args.ChangeId)
                ? $"ev_{chapter.Id}_{args.TargetId}_{Guid.NewGuid().ToString("N")[..6]}"
                : args.ChangeId,
            NovelId = args.NovelId,
            TargetEntityType = args.TargetType,
            TargetId = args.TargetId,
            ChapterId = chapter.Id,
            Aspect = args.Aspect,
            FromSummary = args.From,
            ToSummary = args.To ?? toValue,
            EvidenceQuote = args.Evidence,
            Summary = summary,
            Source = AuditSource.Ai,
            Effects = effects,
            Sequence = sequence,
            Cause = args.Cause,
            Visibility = visibility,
            WitnessIds = args.WitnessIds ?? [],
            LocationId = locationId,
            SceneId = sceneId,
            TimeDescription = timeDescription,
            PlotlineIds = plotlineIds,
            Lifecycle = ChangeLifecycle.Pending,
            SessionId = OntologyWriteContext.Current.SessionId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var merged = chapterChanges.Where(c => c.Id != change.Id).ToList();
        merged.Add(change);

        using (OntologyWriteContext.Attach(args.Evidence, args.ProposalId))
        using (
            OntologyWriteContext.AttachContext(
                chapter.Id,
                locationId,
                sceneId,
                plotlineIds,
                sequence
            )
        )
        {
            await store.SaveEntityStateChanges(args.NovelId, chapter.Id, merged, ct);
        }
        ThrowIfCancelled(ct);

        if (chapter.Status == ChapterStatus.Published)
        {
            var service = new ChapterLifecycleService(store);
            var result = await service.ApplyAmendmentAsync(args.NovelId, chapter.Id, change.Id, ct);
            if (!result.Success)
            {
                return new AgentToolResult<RecordEvolutionDetails>
                {
                    Content =
                    [
                        new TextContent($"变化已写入但补记失败（保留待发布）：{result.Error}"),
                    ],
                    IsError = true,
                };
            }
            return new AgentToolResult<RecordEvolutionDetails>
            {
                Content =
                [
                    new TextContent(
                        $"已生效：{args.TargetType} {args.TargetId} @ {chapter.Title} — {summary}"
                            + $"（联动 {result.AppliedSuggestionCount} 条）"
                    ),
                ],
                Details = new RecordEvolutionDetails
                {
                    ChangeId = change.Id,
                    ChapterId = chapter.Id,
                    TargetId = change.TargetId,
                    Aspect = change.Aspect,
                    Summary = change.Summary,
                    Lifecycle = ChangeLifecycle.Committed,
                },
            };
        }

        return new AgentToolResult<RecordEvolutionDetails>
        {
            Content =
            [
                new TextContent(
                    $"待发布：{args.TargetType} {args.TargetId} @ {chapter.Title} — {summary}"
                        + "（章节发布时统一应用）"
                ),
            ],
            Details = new RecordEvolutionDetails
            {
                ChangeId = change.Id,
                ChapterId = chapter.Id,
                TargetId = change.TargetId,
                Aspect = change.Aspect,
                Summary = change.Summary,
                Lifecycle = ChangeLifecycle.Pending,
            },
        };
    }

    private static ChangeVisibility ParseVisibility(string? raw) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            "secret" or "秘密" => ChangeVisibility.Secret,
            "private" or "私密" => ChangeVisibility.Private,
            _ => ChangeVisibility.Public,
        };

    private static AgentToolResult<RecordEvolutionDetails> Error(string message) =>
        new() { Content = [new TextContent(message)], IsError = true };
}

public sealed record RecordEvolutionParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("目标实体 ID（GUID）")]
    public string TargetId { get; init; } = "";

    [Description("目标实体类型：character/location/faction/item/plotline/scene/relationship")]
    public EntityType TargetType { get; init; } = EntityType.Character;

    [Description("章节 ID（GUID 或章节号）")]
    public string ChapterId { get; init; } = "";

    [Description(
        "变化面：status/personality/motivation/relationship/ability/possession/location/knowledge/behavior/other"
    )]
    public EvolutionAspect Aspect { get; init; } = EvolutionAspect.Other;

    [Description("变化前状态一句话（如「敌视」，可选）")]
    public string? From { get; init; }

    [Description("变化后状态一句话（如「信任」，可选）")]
    public string? To { get; init; }

    [Description("正文证据引文（可选）")]
    public string? Evidence { get; init; }

    [Description("一句话变化描述（可选；留空时按 effects 自动生成）")]
    public string Summary { get; init; } = "";

    [Description("记录 ID（可选，覆盖更新待发布记录时提供）")]
    public string? ChangeId { get; init; }

    [Description("提案 ID（可选，来自 scan_ontology）")]
    public string? ProposalId { get; init; }

    [Description("结构化效果列表；留空时按 aspect=status + to 自动生成一条 status 效果")]
    public List<StateEffectParams>? Effects { get; init; }

    [Description("死亡/失踪等状态值快捷入口；与 effects 二选一")]
    public string? ToValue { get; init; }

    [Description("直接原因")]
    public string? Cause { get; init; }

    [Description("可见性：public/secret/private")]
    public string? Visibility { get; init; }

    [Description("在场者 ID 列表")]
    public List<string>? WitnessIds { get; init; }

    [Description("发生地点 ID")]
    public string? LocationId { get; init; }

    [Description("发生场景 ID")]
    public string? SceneId { get; init; }

    [Description("故事内时间描述")]
    public string? TimeDescription { get; init; }

    [Description("关联情节线 ID 列表")]
    public List<string>? PlotlineIds { get; init; }
}

/// <summary>record_evolution 的单条结构化效果参数。</summary>
public sealed record StateEffectParams
{
    [Description("目标实体类型")]
    public EntityType TargetType { get; init; } = EntityType.Character;

    [Description("目标实体 ID（留空用主 target_id）")]
    public string? TargetId { get; init; }

    [Description(
        "字段名（kebab，如 status/faction-id/current-location-id/owner-id；metrics 用 metrics:key）"
    )]
    public string Field { get; init; } = "";

    [Description("操作：set/add/remove/clear/increment")]
    public StateEffectOperation Operation { get; init; } = StateEffectOperation.Set;

    [Description("新值（枚举名/中文状态/ID/数值/列表项）")]
    public string? Value { get; init; }
}

public sealed record RecordEvolutionDetails
{
    public string ChangeId { get; init; } = "";
    public string ChapterId { get; init; } = "";
    public string TargetId { get; init; } = "";
    public EvolutionAspect Aspect { get; init; }
    public string Summary { get; init; } = "";
    public ChangeLifecycle Lifecycle { get; init; }
}

// ── delete_evolution ──

/// <summary>删除待发布变化（已生效必须先撤回章节）。</summary>
public sealed class DeleteEvolutionTool(INovelStore store)
    : AgentTool<DeleteEvolutionParams, DeleteEvolutionDetails>
{
    public override string Name => "delete_evolution";
    public override string Label => "删除待发布变化";
    public override string[] Aliases => ["删除变化", "撤销变化"];
    public override string[] ExampleQueries => ["删掉刚才登记的那条死亡变化"];
    public override string Description =>
        "删除一条待发布的状态变化；已生效变化返回错误（先撤回章节）。";
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential;
    public override ToolCategory Category => ToolCategory.EntityCrud;

    public override async Task<AgentToolResult<DeleteEvolutionDetails>> ExecuteAsync(
        string toolCallId,
        DeleteEvolutionParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<DeleteEvolutionDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        if (string.IsNullOrWhiteSpace(args.ChangeId))
            return Error("必须提供 change_id");

        var change = (await store.GetEntityStateChanges(args.NovelId, ct)).FirstOrDefault(c =>
            string.Equals(c.Id, args.ChangeId, StringComparison.Ordinal)
        );
        if (change is null)
            return Error($"变化 {args.ChangeId} 不存在");
        if (string.IsNullOrEmpty(change.ChapterId))
            return Error("设定变更不可删除；可用 undo_manual_state_change 撤销");
        if (change.Lifecycle == ChangeLifecycle.Committed)
        {
            // 例外：变更集回滚/进程崩溃可能留下「草稿章 + 已生效变化」的残留记录；
            // 章节仍为草稿时允许删除作为恢复出口，已发布章仍必须先撤回。
            var owner = await store.GetChapter(args.NovelId, change.ChapterId, ct);
            if (owner is null)
                return Error("变化所属章节不存在");
            if (owner.Status == ChapterStatus.Published)
                return Error("已生效变化不可删除；请先撤回章节");
        }

        var records = await store.GetEntityStateChangesByChapter(
            args.NovelId,
            change.ChapterId,
            ct
        );
        records.RemoveAll(r => string.Equals(r.Id, change.Id, StringComparison.Ordinal));
        await store.SaveEntityStateChanges(args.NovelId, change.ChapterId, records, ct);

        return new AgentToolResult<DeleteEvolutionDetails>
        {
            Content = [new TextContent($"已删除待发布变化：{change.Summary}")],
            Details = new DeleteEvolutionDetails { ChangeId = change.Id },
        };
    }

    private static AgentToolResult<DeleteEvolutionDetails> Error(string message) =>
        new() { Content = [new TextContent(message)], IsError = true };
}

public sealed record DeleteEvolutionParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("变化记录 ID")]
    public string ChangeId { get; init; } = "";
}

public sealed record DeleteEvolutionDetails
{
    public string ChangeId { get; init; } = "";
}

// ── undo_manual_state_change ──

/// <summary>撤销最近一次人工设定变更（按字段反向写回并生成一条反向设定变更）。</summary>
public sealed class UndoManualStateChangeTool(INovelStore store)
    : AgentTool<UndoManualStateChangeParams, UndoManualStateChangeDetails>
{
    public override string Name => "undo_manual_state_change";
    public override string Label => "撤销设定变更";
    public override string[] Aliases => ["撤销人工状态", "还原设定变更"];
    public override string[] ExampleQueries => ["把刚才手动改成死亡的状态改回去"];
    public override string Description =>
        "撤销某实体某字段最近一次人工设定变更（反向应用并生成一条新的反向记录）。";
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential;
    public override ToolCategory Category => ToolCategory.EntityCrud;

    public override async Task<AgentToolResult<UndoManualStateChangeDetails>> ExecuteAsync(
        string toolCallId,
        UndoManualStateChangeParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<UndoManualStateChangeDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        if (string.IsNullOrWhiteSpace(args.EntityId) || string.IsNullOrWhiteSpace(args.Field))
            return Error("必须提供 entity_type/entity_id/field");

        var manual = (await store.GetManualStateChanges(args.NovelId, ct))
            .Where(c =>
                c.TargetEntityType == args.EntityType
                && string.Equals(c.TargetId, args.EntityId, StringComparison.Ordinal)
                && c.Effects.Any(e =>
                    string.Equals(
                        StateEffectValidator.Normalize(e).Field,
                        args.Field.Trim().ToLowerInvariant(),
                        StringComparison.Ordinal
                    )
                )
            )
            .OrderByDescending(c => c.CommittedAt ?? c.CreatedAt)
            .ToList();
        if (manual.Count == 0)
            return Error("没有可撤销的设定变更记录");

        var latest = manual[0];
        var effect = latest.Effects.First(e =>
            string.Equals(
                StateEffectValidator.Normalize(e).Field,
                args.Field.Trim().ToLowerInvariant(),
                StringComparison.Ordinal
            )
        );
        if (string.IsNullOrEmpty(effect.From))
            return Error("该设定变更没有记录前值，无法自动撤销");

        var entity = await LoadEntityAsync(args.NovelId, args.EntityType, args.EntityId, ct);
        if (entity is null)
            return Error("实体不存在");

        var inverse = new StateEffect
        {
            TargetType = args.EntityType,
            TargetId = args.EntityId,
            Field = effect.Field,
            Operation = StateEffectOperation.Set,
            Value = effect.From,
            From = effect.Value,
        };

        using (OntologyWriteContext.BeginUser(OntologyWriteContext.Current.SessionId))
        {
            var result = await StateEffectApplier.ApplyAllAsync(
                store,
                (await store.GetNovel(args.NovelId, ct))!,
                new Chapter
                {
                    Id = "",
                    NovelId = args.NovelId,
                    Title = "设定撤销",
                },
                [inverse],
                ct
            );
            if (result.Warnings.Count > 0)
                return Error(string.Join("；", result.Warnings));
        }

        return new AgentToolResult<UndoManualStateChangeDetails>
        {
            Content =
            [
                new TextContent(
                    $"已撤销：{args.EntityType.ToKebabCaseString()} {args.EntityId} {effect.Field} {effect.Value} → {effect.From}"
                ),
            ],
            Details = new UndoManualStateChangeDetails
            {
                EntityId = args.EntityId,
                Field = effect.Field,
                RestoredValue = effect.From,
            },
        };
    }

    private async Task<Entity?> LoadEntityAsync(
        string novelId,
        EntityType type,
        string id,
        CancellationToken ct
    ) =>
        type switch
        {
            EntityTypeEnum.Character => await store.GetCharacter(novelId, id, ct),
            EntityTypeEnum.Faction => await store.GetFaction(novelId, id, ct),
            EntityTypeEnum.Item => await store.GetItem(novelId, id, ct),
            EntityTypeEnum.Location => await store.GetLocation(novelId, id, ct),
            EntityTypeEnum.Plotline => await store.GetPlotline(novelId, id, ct),
            EntityTypeEnum.Relationship => await store.GetRelationship(novelId, id, ct),
            EntityTypeEnum.Scene => await store.GetScene(novelId, id, ct),
            _ => null,
        };

    private static AgentToolResult<UndoManualStateChangeDetails> Error(string message) =>
        new() { Content = [new TextContent(message)], IsError = true };
}

public sealed record UndoManualStateChangeParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("实体类型")]
    public EntityType EntityType { get; init; } = EntityType.Character;

    [Description("实体 ID")]
    public string EntityId { get; init; } = "";

    [Description("字段名（kebab，如 status/current-location-id/owner-id）")]
    public string Field { get; init; } = "";
}

public sealed record UndoManualStateChangeDetails
{
    public string EntityId { get; init; } = "";
    public string Field { get; init; } = "";
    public string? RestoredValue { get; init; }
}
