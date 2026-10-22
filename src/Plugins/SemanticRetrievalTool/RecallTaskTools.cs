using System.ComponentModel;
using System.Text;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Core.Vector;

namespace Plugins.SemanticRetrievalTool;

// ════════════════════════════════════════════════════════════════
//  任务导向检索工具（VEC-LOCAL §3.6 目标工具族补齐）
//
//  设计原则：
//  - 工具面向用户任务，不让 Agent 猜 vec0 表结构（§3.6）
//  - metadata 过滤是候选集约束，先于 dense/FTS 生成（§2.7）
//  - 关联检索从 INovelStore 事实字段构建，不建关系表（§0.2）
//  - 列表参数用逗号/顿号分隔字符串，避免 LLM 数组序列化坑
// ════════════════════════════════════════════════════════════════

/// <summary>解析逗号/顿号/空格分隔的文本为清洗后的列表。</summary>
internal static class ParamList
{
    public static List<string> Split(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];
        return raw.Split(
                [',', '，', '、', ';', '；'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
            .Where(s => s.Length > 0)
            .ToList();
    }
}

// ────────────────────────────────────────────────────────────────
//  1. recall_scene — 场景计划检索（SceneDefinition + 结构化过滤 + hybrid）
// ────────────────────────────────────────────────────────────────

public sealed class RecallSceneParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("场景描述查询，用于语义检索（可为空，仅用过滤条件）")]
    public string Query { get; init; } = "";

    [Description("参与角色名称（多个用逗号或顿号分隔），按名称精确匹配后过滤场景")]
    public string CharacterNames { get; init; } = "";

    [Description("推进的情节线名称（多个用逗号或顿号分隔），按名称精确匹配后过滤场景")]
    public string PlotlineNames { get; init; } = "";

    [Description("地点名称，精确匹配后过滤场景")]
    public string LocationName { get; init; } = "";

    [Description("场景类型过滤（多个用逗号分隔）：Action/Dialogue/Reflection/Transition")]
    public string SceneTypes { get; init; } = "";

    [Description("章节序号下限（含），1 起")]
    public int? ChapterMin { get; init; }

    [Description("章节序号上限（含）")]
    public int? ChapterMax { get; init; }

    [Description("返回结果数量上限（默认 5）")]
    public int TopK { get; init; } = 5;
}

/// <summary>
/// 检索场景计划：按参与角色/情节线/地点/场景类型/章节范围做 metadata 前置过滤，
/// 结合查询文本 hybrid 召回。返回场景标题、类型、参与角色、情节线与备注。
/// </summary>
public sealed class RecallSceneTool(RetrievalKernel kernel, IPluginStore store)
    : AgentTool<RecallSceneParams, RecallSceneDetails>
{
    public override string Name => "recall_scene";
    public override string Label => "检索场景";
    public override string Description =>
        "检索场景计划（场景定义）。可按参与角色、情节线、地点、场景类型和章节范围过滤，"
        + "并用描述文本做语义匹配。适合写作时查某角色/某情节线/某地点的场景安排。";
    public override ToolCategory Category => ToolCategory.SearchTool;

    public override async Task<AgentToolResult<RecallSceneDetails>> ExecuteAsync(
        string toolCallId,
        RecallSceneParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<RecallSceneDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        if (string.IsNullOrWhiteSpace(args.NovelId))
            return RecallToolResult.Err<RecallSceneDetails>("请提供 novel_id（当前小说项目ID）。");

        // 名称 → ID 解析（LLM 传名称，不传 ID；确定性优先）
        var characterIds = await RecallToolResult.ResolveCharacterIdsAsync(
            store,
            args.NovelId,
            args.CharacterNames
        );
        var plotlineIds = await RecallToolResult.ResolvePlotlineIdsAsync(
            store,
            args.NovelId,
            args.PlotlineNames
        );
        var locationId = await RecallToolResult.ResolveLocationIdAsync(
            store,
            args.NovelId,
            args.LocationName
        );

        // 未解析到任何过滤目标：名称过滤请求落空 → 直接失败，不得扩大召回
        if (
            ParamList.Split(args.CharacterNames).Count > 0 && characterIds.Count == 0
            || ParamList.Split(args.PlotlineNames).Count > 0 && plotlineIds.Count == 0
            || !string.IsNullOrWhiteSpace(args.LocationName) && locationId is null
        )
        {
            return RecallToolResult.Err<RecallSceneDetails>(
                "未能在该小说中找到匹配的角色/情节线/地点名称，无法过滤场景。"
                    + "请确认名称与该小说的实体名称完全一致。"
            );
        }

        // 无文本查询：纯过滤路径——从事实源枚举满足条件的场景（§0.2 事实源为准）
        if (string.IsNullOrWhiteSpace(args.Query))
        {
            var filtered = await RecallToolResult.FilterScenesFromFactsAsync(
                store,
                args.NovelId,
                characterIds,
                plotlineIds,
                locationId,
                ParamList.Split(args.SceneTypes),
                args.ChapterMin,
                args.ChapterMax,
                args.TopK
            );
            ThrowIfCancelled(ct);

            if (filtered.Count == 0)
                return RecallToolResult.Ok<RecallSceneDetails>("未找到符合条件的场景。", count: 0);

            var factSb = new StringBuilder();
            factSb.AppendLine($"找到 {filtered.Count} 个场景：");
            foreach (var s in filtered)
            {
                factSb.AppendLine($"- 场景: {s.Title}（{s.SceneType}）");
                if (!string.IsNullOrWhiteSpace(s.Conflict))
                    factSb.AppendLine($"  冲突: {s.Conflict}");
            }
            return RecallToolResult.Ok<RecallSceneDetails>(
                factSb.ToString(),
                count: filtered.Count,
                relatedIds: filtered.ConvertAll(s => s.Id)
            );
        }

        var sceneTypes = ParamList.Split(args.SceneTypes);

        (int Min, int Max)? chapterRange =
            args.ChapterMin is null && args.ChapterMax is null
                ? null
                : (args.ChapterMin ?? 1, args.ChapterMax ?? int.MaxValue);

        var request = new RetrievalSearchRequest
        {
            Domain = RetrievalDomain.NovelKnowledge,
            OwnerId = "local",
            NovelId = args.NovelId,
            Query = args.Query,
            DocumentKinds = [DocumentKind.SceneDefinition],
            ParticipantCharacterIds = characterIds,
            PlotlineIds = plotlineIds,
            LocationId = locationId,
            SceneTypes = sceneTypes.Count > 0 ? sceneTypes : null,
            ChapterOrderRange = chapterRange,
            FinalTopK = args.TopK,
        };

        var results = await kernel.SearchAsync(request, ct);
        ThrowIfCancelled(ct);

        if (results.Count == 0)
            return RecallToolResult.Ok<RecallSceneDetails>("未找到符合条件的场景。", count: 0);

        var sb = new StringBuilder();
        sb.AppendLine($"找到 {results.Count} 个场景：");
        foreach (var r in results)
        {
            sb.AppendLine($"- {r.Text}");
            if (r.ChapterOrder is { } co)
                sb.AppendLine($"  （第 {co} 章）");
        }
        return RecallToolResult.Ok<RecallSceneDetails>(
            sb.ToString(),
            count: results.Count,
            relatedIds: results.ConvertAll(r => r.SourceEntityId ?? "")
        );
    }
}

// ────────────────────────────────────────────────────────────────
//  2. recall_timeline — 时间线事件检索（章节范围过滤 + lexical 优先）
// ────────────────────────────────────────────────────────────────

public sealed class RecallTimelineParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("事件描述查询（可为空，仅按章节范围返回事件）")]
    public string Query { get; init; } = "";

    [Description("角色名称：只返回该角色相关的事件（并入查询做词法匹配，可为空）")]
    public string CharacterName { get; init; } = "";

    [Description("章节序号下限（含），1 起")]
    public int? ChapterMin { get; init; }

    [Description("章节序号上限（含）")]
    public int? ChapterMax { get; init; }

    [Description("返回结果数量上限（默认 10）")]
    public int TopK { get; init; } = 10;
}

/// <summary>
/// 检索时间线事件：章节范围 metadata 前置过滤，事件文本词法优先（ConsistencyCheck profile）。
/// 事件一致性检查用：确认某角色/某章节区间发生过什么。
/// </summary>
public sealed class RecallTimelineTool(RetrievalKernel kernel, IPluginStore store)
    : AgentTool<RecallTimelineParams, RecallTimelineDetails>
{
    public override string Name => "recall_timeline";
    public override string Label => "检索时间线";
    public override string Description =>
        "检索小说时间线事件。可按章节范围过滤，事件文本用词法优先匹配（适合精确核对事件）。"
        + "传角色名可聚焦该角色相关的事件。用于事件一致性检查。";
    public override ToolCategory Category => ToolCategory.SearchTool;

    public override async Task<AgentToolResult<RecallTimelineDetails>> ExecuteAsync(
        string toolCallId,
        RecallTimelineParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<RecallTimelineDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        if (string.IsNullOrWhiteSpace(args.NovelId))
            return RecallToolResult.Err<RecallTimelineDetails>(
                "请提供 novel_id（当前小说项目ID）。"
            );

        // 无文本查询或角色名过滤：走事实源（角色名过滤是确定性词法匹配，不依赖 FTS 分词；§3.6）
        if (string.IsNullOrWhiteSpace(args.Query) || !string.IsNullOrWhiteSpace(args.CharacterName))
        {
            var events = await RecallToolResult.FilterTimelineFromFactsAsync(
                store,
                args.NovelId,
                args.ChapterMin,
                args.ChapterMax,
                args.TopK,
                args.CharacterName
            );
            ThrowIfCancelled(ct);

            if (events.Count == 0)
                return RecallToolResult.Ok<RecallTimelineDetails>(
                    "未找到符合条件的时间线事件。",
                    count: 0
                );

            var factSb = new StringBuilder();
            factSb.AppendLine($"找到 {events.Count} 个时间线事件：");
            foreach (var (evt, order) in events)
            {
                var chapter = order is { } o ? $"[第{o}章] " : "";
                factSb.AppendLine($"- {chapter}事件: {evt.Title}");
                if (!string.IsNullOrWhiteSpace(evt.Description))
                    factSb.AppendLine($"  描述: {evt.Description}");
            }
            return RecallToolResult.Ok<RecallTimelineDetails>(
                factSb.ToString(),
                count: events.Count,
                relatedIds: events.ConvertAll(e => e.Event.Id)
            );
        }

        // 纯文本查询路径（角色名过滤已在上方事实源分支处理）
        var query = args.Query;

        (int Min, int Max)? chapterRange =
            args.ChapterMin is null && args.ChapterMax is null
                ? null
                : (args.ChapterMin ?? 1, args.ChapterMax ?? int.MaxValue);

        var request = new RetrievalSearchRequest
        {
            Domain = RetrievalDomain.NovelKnowledge,
            OwnerId = "local",
            NovelId = args.NovelId,
            Query = query,
            DocumentKinds = [DocumentKind.TimelineEvent],
            ChapterOrderRange = chapterRange,
            RankingProfile = RankingProfile.ConsistencyCheck,
            FinalTopK = args.TopK,
        };

        var results = await kernel.SearchAsync(request, ct);
        ThrowIfCancelled(ct);

        if (results.Count == 0)
            return RecallToolResult.Ok<RecallTimelineDetails>(
                "未找到符合条件的时间线事件。",
                count: 0
            );

        var sb = new StringBuilder();
        sb.AppendLine($"找到 {results.Count} 个时间线事件：");
        foreach (var r in results.OrderBy(r => r.ChapterOrder ?? int.MaxValue))
        {
            var chapter = r.ChapterOrder is { } co ? $"[第{co}章] " : "";
            sb.AppendLine($"- {chapter}{r.Text}");
        }
        return RecallToolResult.Ok<RecallTimelineDetails>(
            sb.ToString(),
            count: results.Count,
            relatedIds: results.ConvertAll(r => r.SourceEntityId ?? "")
        );
    }
}

// ────────────────────────────────────────────────────────────────
//  3. recall_related — 给定实体的精确关联 + hybrid 补充
// ────────────────────────────────────────────────────────────────

public sealed class RecallRelatedParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("实体类型：character / plotline / location / faction / item / scene")]
    public string EntityType { get; init; } = "character";

    [Description("实体名称（与实体ID二选一，名称优先精确匹配）")]
    public string EntityName { get; init; } = "";

    [Description("实体ID（与实体名称二选一）")]
    public string EntityId { get; init; } = "";

    [Description("可选语义查询：补充检索与实体相关的正文/设定内容")]
    public string Query { get; init; } = "";

    [Description("语义补充检索的结果上限（默认 5）")]
    public int TopK { get; init; } = 5;
}

/// <summary>
/// 检索与给定实体直接相关的内容：从事实源字段构建精确关联（关系/参与场景/成员组织等），
/// 再用查询文本做语义补充检索。不建实体关系表（§0.2）。
/// </summary>
public sealed class RecallRelatedTool(RetrievalKernel kernel, IPluginStore store)
    : AgentTool<RecallRelatedParams, RecallRelatedDetails>
{
    public override string Name => "recall_related";
    public override string Label => "检索相关实体";
    public override string Description =>
        "检索与给定实体（角色/情节线/地点/组织/道具/场景）直接关联的内容："
        + "角色关系、参与的场景、推进的情节线、成员组织等，来自事实设定而非语义猜测。"
        + "可附加语义查询补充正文相关内容。";
    public override ToolCategory Category => ToolCategory.SearchTool;

    public override async Task<AgentToolResult<RecallRelatedDetails>> ExecuteAsync(
        string toolCallId,
        RecallRelatedParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<RecallRelatedDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        if (string.IsNullOrWhiteSpace(args.NovelId))
            return RecallToolResult.Err<RecallRelatedDetails>(
                "请提供 novel_id（当前小说项目ID）。"
            );
        if (string.IsNullOrWhiteSpace(args.EntityName) && string.IsNullOrWhiteSpace(args.EntityId))
            return RecallToolResult.Err<RecallRelatedDetails>("请提供实体名称或实体ID。");

        // 解析实体 → 事实源关联（并行读取）
        var rel = await BuildAssociationsAsync(
            args.NovelId,
            args.EntityType.Trim().ToLowerInvariant(),
            args.EntityName,
            args.EntityId
        );
        ThrowIfCancelled(ct);

        var sb = new StringBuilder();
        if (rel.NotFound)
        {
            sb.AppendLine(
                $"未找到实体：类型={args.EntityType}，名称/ID={FirstNonEmpty(args.EntityName, args.EntityId)}"
            );
            sb.AppendLine("请确认实体类型与名称/ID 正确。");
        }
        else
        {
            sb.AppendLine($"实体「{rel.DisplayName}」的直接关联：");
            if (rel.Lines.Count == 0)
                sb.AppendLine("- （事实源中无直接关联记录）");
            else
                foreach (var line in rel.Lines)
                    sb.AppendLine($"- {line}");
        }

        // hybrid 语义补充（查询文本或实体名）
        var query =
            !string.IsNullOrWhiteSpace(args.Query) ? args.Query
            : rel.NotFound ? ""
            : rel.DisplayName;
        if (!string.IsNullOrWhiteSpace(query) && !rel.NotFound)
        {
            var request = new RetrievalSearchRequest
            {
                Domain = RetrievalDomain.NovelKnowledge,
                OwnerId = "local",
                NovelId = args.NovelId,
                Query = query,
                FinalTopK = args.TopK,
            };
            var semantic = await kernel.SearchAsync(request, ct);
            ThrowIfCancelled(ct);
            if (semantic.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"语义相关内容（{semantic.Count} 条）：");
                foreach (var r in semantic)
                    sb.AppendLine($"- [{r.DocumentKind.ToChineseLabel()}] {r.Text}");
            }
        }

        return RecallToolResult.Ok<RecallRelatedDetails>(
            sb.ToString(),
            count: rel.Lines.Count,
            relatedIds: rel.RelatedEntityIds
        );
    }

    private sealed record Associations(
        bool NotFound,
        string DisplayName,
        List<string> Lines,
        List<string> RelatedEntityIds
    );

    private async Task<Associations> BuildAssociationsAsync(
        string novelId,
        string entityType,
        string entityName,
        string entityId
    )
    {
        var lines = new List<string>();
        var relatedIds = new List<string>();

        switch (entityType)
        {
            case "character":
            {
                var ch = await FindCharacterAsync(novelId, entityName, entityId);
                if (ch is null)
                    return new Associations(
                        true,
                        FirstNonEmpty(entityName, entityId),
                        lines,
                        relatedIds
                    );

                var characters = await store.GetCharacters(novelId);
                var nameById = characters.ToDictionary(c => c.Id, c => c.Name);
                var relationships = await store.GetRelationshipsByNovel(novelId);
                foreach (
                    var r in relationships.Where(r =>
                        r.CharacterAId == ch.Id || r.CharacterBId == ch.Id
                    )
                )
                {
                    var otherId = r.CharacterAId == ch.Id ? r.CharacterBId : r.CharacterAId;
                    var otherName = nameById.GetValueOrDefault(otherId, otherId);
                    lines.Add($"关系：{ch.Name} — {otherName}（{r.Type}，强度 {r.Strength}）");
                    relatedIds.Add(otherId);
                }
                var scenes = await store.GetScenesByNovel(novelId);
                foreach (var s in scenes.Where(s => s.ParticipantIds.Contains(ch.Id)))
                {
                    lines.Add($"参与场景：{s.Title}");
                    relatedIds.Add(s.Id);
                }
                var factions = await store.GetFactions(novelId);
                foreach (var f in factions.Where(f => f.Members.Contains(ch.Id)))
                {
                    lines.Add($"所属组织：{f.Name}");
                    relatedIds.Add(f.Id);
                }
                return new Associations(false, ch.Name, lines, relatedIds.Distinct().ToList());
            }

            case "plotline":
            {
                var pl = await FindPlotlineAsync(novelId, entityName, entityId);
                if (pl is null)
                    return new Associations(
                        true,
                        FirstNonEmpty(entityName, entityId),
                        lines,
                        relatedIds
                    );

                var scenes = await store.GetScenesByNovel(novelId);
                foreach (var s in scenes.Where(s => s.PlotlineIds.Contains(pl.Id)))
                {
                    lines.Add($"推进场景：{s.Title}");
                    relatedIds.Add(s.Id);
                }
                return new Associations(false, pl.Name, lines, relatedIds.Distinct().ToList());
            }

            case "location":
            {
                var loc = await FindLocationAsync(novelId, entityName, entityId);
                if (loc is null)
                    return new Associations(
                        true,
                        FirstNonEmpty(entityName, entityId),
                        lines,
                        relatedIds
                    );

                var scenes = await store.GetScenesByNovel(novelId);
                foreach (var s in scenes.Where(s => s.LocationId == loc.Id))
                {
                    lines.Add($"场景：{s.Title}（{loc.Name}）");
                    relatedIds.Add(s.Id);
                }
                return new Associations(false, loc.Name, lines, relatedIds.Distinct().ToList());
            }

            case "faction":
            {
                var f = await FindFactionAsync(novelId, entityName, entityId);
                if (f is null)
                    return new Associations(
                        true,
                        FirstNonEmpty(entityName, entityId),
                        lines,
                        relatedIds
                    );

                var characters = await store.GetCharacters(novelId);
                foreach (
                    var c in characters.Where(c => c.FactionId == f.Id || f.Members.Contains(c.Id))
                )
                {
                    lines.Add($"成员：{c.Name}");
                    relatedIds.Add(c.Id);
                }
                var rels = await store.GetFactionRelationships(novelId);
                var nameById = (await store.GetFactions(novelId)).ToDictionary(
                    x => x.Id,
                    x => x.Name
                );
                foreach (var r in rels.Where(r => r.FactionAId == f.Id || r.FactionBId == f.Id))
                {
                    var otherId = r.FactionAId == f.Id ? r.FactionBId : r.FactionAId;
                    var otherName = nameById.GetValueOrDefault(otherId, otherId);
                    lines.Add($"关系：{f.Name} — {otherName}（{r.Type}）");
                    relatedIds.Add(otherId);
                }
                return new Associations(false, f.Name, lines, relatedIds.Distinct().ToList());
            }

            case "item":
            {
                var item = await FindItemAsync(novelId, entityName, entityId);
                if (item is null)
                    return new Associations(
                        true,
                        FirstNonEmpty(entityName, entityId),
                        lines,
                        relatedIds
                    );

                if (!string.IsNullOrWhiteSpace(item.OwnerId))
                {
                    var owner = await store.GetCharacter(novelId, item.OwnerId);
                    lines.Add($"持有者：{owner?.Name ?? item.OwnerId}");
                    relatedIds.Add(item.OwnerId);
                }
                return new Associations(false, item.Name, lines, relatedIds.Distinct().ToList());
            }

            case "scene":
            {
                var scene = await FindSceneAsync(novelId, entityName, entityId);
                if (scene is null)
                    return new Associations(
                        true,
                        FirstNonEmpty(entityName, entityId),
                        lines,
                        relatedIds
                    );

                var characters = await store.GetCharacters(novelId);
                var nameById = characters.ToDictionary(c => c.Id, c => c.Name);
                foreach (var pid in scene.ParticipantIds)
                {
                    lines.Add($"参与角色：{nameById.GetValueOrDefault(pid, pid)}");
                    relatedIds.Add(pid);
                }
                var plotlines = await store.GetPlotlines(novelId);
                var plById = plotlines.ToDictionary(p => p.Id, p => p.Name);
                foreach (var plid in scene.PlotlineIds)
                {
                    lines.Add($"推进情节线：{plById.GetValueOrDefault(plid, plid)}");
                    relatedIds.Add(plid);
                }
                if (!string.IsNullOrWhiteSpace(scene.LocationId))
                {
                    var loc = await store.GetLocation(novelId, scene.LocationId);
                    lines.Add($"地点：{loc?.Name ?? scene.LocationId}");
                    relatedIds.Add(scene.LocationId);
                }
                return new Associations(false, scene.Title, lines, relatedIds.Distinct().ToList());
            }

            default:
                return new Associations(
                    true,
                    $"{entityType}:{FirstNonEmpty(entityName, entityId)}",
                    lines,
                    relatedIds
                );
        }
    }

    private static string FirstNonEmpty(string a, string b) =>
        !string.IsNullOrWhiteSpace(a) ? a : b;

    private async Task<Character?> FindCharacterAsync(string novelId, string name, string id)
    {
        if (!string.IsNullOrWhiteSpace(id))
            return await store.GetCharacter(novelId, id);
        var chars = await store.GetCharacters(novelId);
        return chars.FirstOrDefault(c =>
            c.Name == name
            || c.Name.Contains(name, StringComparison.Ordinal)
            || name.Contains(c.Name, StringComparison.Ordinal)
        );
    }

    private async Task<Plotline?> FindPlotlineAsync(string novelId, string name, string id)
    {
        var plots = await store.GetPlotlines(novelId);
        if (!string.IsNullOrWhiteSpace(id))
            return plots.FirstOrDefault(p => p.Id == id);
        return plots.FirstOrDefault(p =>
            p.Name == name || p.Name.Contains(name, StringComparison.Ordinal)
        );
    }

    private async Task<Location?> FindLocationAsync(string novelId, string name, string id)
    {
        if (!string.IsNullOrWhiteSpace(id))
            return await store.GetLocation(novelId, id);
        var locs = await store.GetLocations(novelId);
        return locs.FirstOrDefault(l =>
            l.Name == name || l.Name.Contains(name, StringComparison.Ordinal)
        );
    }

    private async Task<Faction?> FindFactionAsync(string novelId, string name, string id)
    {
        var factions = await store.GetFactions(novelId);
        if (!string.IsNullOrWhiteSpace(id))
            return factions.FirstOrDefault(f => f.Id == id);
        return factions.FirstOrDefault(f =>
            f.Name == name || f.Name.Contains(name, StringComparison.Ordinal)
        );
    }

    private async Task<Item?> FindItemAsync(string novelId, string name, string id)
    {
        if (!string.IsNullOrWhiteSpace(id))
            return await store.GetItem(novelId, id);
        var items = await store.GetItems(novelId);
        return items.FirstOrDefault(i =>
            i.Name == name || i.Name.Contains(name, StringComparison.Ordinal)
        );
    }

    private async Task<SceneDefinition?> FindSceneAsync(string novelId, string name, string id)
    {
        var scenes = await store.GetScenesByNovel(novelId);
        if (!string.IsNullOrWhiteSpace(id))
            return scenes.FirstOrDefault(s => s.Id == id);
        return scenes.FirstOrDefault(s =>
            s.Title == name || s.Title.Contains(name, StringComparison.Ordinal)
        );
    }
}

/// <summary>任务导向检索工具的共享结果辅助与名称解析。</summary>
internal static class RecallToolResult
{
    public static AgentToolResult<T> Err<T>(string msg) =>
        new() { Content = [new TextContent(msg)], IsError = true };

    public static AgentToolResult<T> Ok<T>(string text, int count, List<string>? relatedIds = null)
        where T : class, new()
    {
        var details = typeof(T) switch
        {
            var t when t == typeof(RecallSceneDetails) => (T)
                (object)new RecallSceneDetails { Count = count },
            var t when t == typeof(RecallTimelineDetails) => (T)
                (object)new RecallTimelineDetails { Count = count },
            var t when t == typeof(RecallRelatedDetails) => (T)
                (object)new RecallRelatedDetails { Count = count, RelatedEntityIds = relatedIds },
            _ => new T(),
        };

        return new AgentToolResult<T> { Content = [new TextContent(text)], Details = details };
    }

    public static async Task<List<string>> ResolveCharacterIdsAsync(
        IPluginStore store,
        string novelId,
        string names
    )
    {
        var raw = ParamList.Split(names);
        if (raw.Count == 0)
            return [];
        var chars = await store.GetCharacters(novelId);
        return chars
            .Where(c => raw.Contains(c.Name, StringComparer.Ordinal))
            .Select(c => c.Id)
            .ToList();
    }

    public static async Task<List<string>> ResolvePlotlineIdsAsync(
        IPluginStore store,
        string novelId,
        string names
    )
    {
        var raw = ParamList.Split(names);
        if (raw.Count == 0)
            return [];
        var plots = await store.GetPlotlines(novelId);
        return plots
            .Where(p => raw.Contains(p.Name, StringComparer.Ordinal))
            .Select(p => p.Id)
            .ToList();
    }

    public static async Task<string?> ResolveLocationIdAsync(
        IPluginStore store,
        string novelId,
        string name
    )
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var locs = await store.GetLocations(novelId);
        return locs.FirstOrDefault(l => l.Name == name)?.Id;
    }

    /// <summary>
    /// 从事实源枚举满足过滤条件的场景（无文本查询路径）。
    /// 场景事实以 INovelStore 为准（§0.2），按章节序号排序后取 topK。
    /// </summary>
    public static async Task<List<SceneDefinition>> FilterScenesFromFactsAsync(
        IPluginStore store,
        string novelId,
        List<string> characterIds,
        List<string> plotlineIds,
        string? locationId,
        List<string> sceneTypes,
        int? chapterMin,
        int? chapterMax,
        int topK
    )
    {
        var scenes = await store.GetScenesByNovel(novelId);

        // 章节 ID → 序号映射（场景按章节排序）
        var chapters = await store.GetChapters(novelId);
        var orderByChapter = chapters
            .Where(ch => !string.IsNullOrEmpty(ch.Id))
            .ToDictionary(ch => ch.Id!, ch => ch.Order);

        var min = chapterMin ?? 1;
        var max = chapterMax ?? int.MaxValue;

        return scenes
            .Where(s =>
                (characterIds.Count == 0 || s.ParticipantIds.Intersect(characterIds).Any())
                && (plotlineIds.Count == 0 || s.PlotlineIds.Intersect(plotlineIds).Any())
                && (locationId is null || s.LocationId == locationId)
                && (sceneTypes.Count == 0 || sceneTypes.Contains(s.SceneType.ToString()))
                && (
                    s.ChapterId is null
                    || !orderByChapter.TryGetValue(s.ChapterId, out var order)
                    || (order >= min && order <= max)
                )
            )
            .OrderBy(s =>
                s.ChapterId is { } cid && orderByChapter.TryGetValue(cid, out var o)
                    ? o
                    : int.MaxValue
            )
            .ThenBy(s => s.Order)
            .Take(topK > 0 ? topK : 5)
            .ToList();
    }

    /// <summary>
    /// 从事实源枚举满足章节范围的时间线事件（无文本查询路径）。
    /// 事件事实以 INovelStore 为准（§0.2），按章节序号排序后取 topK。
    /// </summary>
    public static async Task<
        List<(TimelineEvent Event, int? ChapterOrder)>
    > FilterTimelineFromFactsAsync(
        IPluginStore store,
        string novelId,
        int? chapterMin,
        int? chapterMax,
        int topK,
        string? characterName = null
    )
    {
        var events = await store.GetTimelineEvents(novelId);

        // 角色名确定性词法过滤：事件标题/描述含角色名（不依赖 FTS 分词）
        if (!string.IsNullOrWhiteSpace(characterName))
        {
            events = events
                .Where(e =>
                    e.Title.Contains(characterName, StringComparison.Ordinal)
                    || e.Description.Contains(characterName, StringComparison.Ordinal)
                )
                .ToList();
        }

        // 章节 ID → 序号映射
        var chapters = await store.GetChapters(novelId);
        var orderByChapter = chapters
            .Where(ch => !string.IsNullOrEmpty(ch.Id))
            .ToDictionary(ch => ch.Id, ch => ch.Order);

        var min = chapterMin ?? 1;
        var max = chapterMax ?? int.MaxValue;

        var withOrder = events
            .Select(e =>
                (
                    Event: e,
                    Order: e.ChapterId is { } cid && orderByChapter.TryGetValue(cid, out var o)
                        ? (int?)o
                        : null
                )
            )
            .Where(x =>
                (chapterMin is null && chapterMax is null)
                || (x.Order is { } o && o >= min && o <= max)
            )
            .OrderBy(x => x.Order ?? int.MaxValue)
            .ToList();

        return withOrder.Take(topK > 0 ? topK : 10).ToList();
    }
}

/// <summary>检索场景工具的返回详情。</summary>
public sealed record RecallSceneDetails
{
    public int Count { get; init; }
}

/// <summary>检索时间线工具的返回详情。</summary>
public sealed record RecallTimelineDetails
{
    public int Count { get; init; }
}

/// <summary>检索相关实体工具的返回详情。</summary>
public sealed record RecallRelatedDetails
{
    public int Count { get; init; }

    /// <summary>关联到的实体 ID 列表（供后续工具引用）。</summary>
    public List<string>? RelatedEntityIds { get; init; }
}
