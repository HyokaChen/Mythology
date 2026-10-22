using Core.Data;

namespace Core.Evolution;

/// <summary>
/// 本体关系边（本地实体图的有向边）。EdgeType 词汇表与云端 云端图谱关系 对齐；
/// 出场记录边（appears-in 记录）与状态变化边（evolves）由 <see cref="EntityTimelineView"/> 提供，
/// 不进本索引（避免双份真源）。
/// </summary>
public sealed record EntityRelation(
    EntityType SourceType,
    string SourceId,
    EntityType TargetType,
    string TargetId,
    string EdgeType,
    string? Detail
);

/// <summary>
/// 本地本体关系索引（内存构建、不落盘，数百至数千条、毫秒级）：
/// 把 13 类实体的外键/列表字段投影为统一关系边，供 TUI（折叠内容的关系段）
/// 与 Desktop（关系窗 + 卡面 Chip 行）共用，并与云端图谱边词汇表一致。
/// 派生视图不落盘（NKW 原则，与 <see cref="EntityTimelineView"/> 同）。
/// </summary>
public sealed class EntityRelationIndex
{
    private readonly List<EntityRelation> _relations;
    private readonly Dictionary<(EntityType Type, string Id), List<int>> _byNode;
    private readonly Dictionary<(EntityType Type, string Id), string> _names;

    private EntityRelationIndex(
        List<EntityRelation> relations,
        Dictionary<(EntityType, string), List<int>> byNode,
        Dictionary<(EntityType, string), string> names
    )
    {
        _relations = relations;
        _byNode = byNode;
        _names = names;
    }

    /// <summary>全部关系边（诊断/计数用）。</summary>
    public IReadOnlyList<EntityRelation> All => _relations;

    /// <summary>该实体作为 Source 或 Target 出现的全部关系（无向视图，按 <see cref="OrderOf"/> 排序；无关系返回空列表）。</summary>
    public IReadOnlyList<EntityRelation> GetRelations(EntityType type, string id) =>
        _byNode.TryGetValue((type, id), out var indexes)
            ? [.. indexes.Select(i => _relations[i]).OrderBy(r => OrderOf(r.EdgeType))]
            : [];

    /// <summary>实体显示名（构建时缓存全量实体名）；未知 id 原样返回 id。</summary>
    public string NameOf(EntityType type, string id) =>
        _names.TryGetValue((type, id), out var name) && name.Length > 0 ? name : id;

    /// <summary>
    /// 站在 viewing 一侧的关系标签（对端类型参与判定：情节线看章节推进 =「被推进章节」、看卷推进 =「被推进卷」）。
    /// </summary>
    public static string LabelFor(EntityRelation relation, EntityType viewingType, string viewingId)
    {
        var isSource = relation.SourceType == viewingType && relation.SourceId == viewingId;
        var counterpart = isSource ? relation.TargetType : relation.SourceType;
        return EdgeLabel(relation.EdgeType, relation.SourceType, isSource, counterpart);
    }

    private static string EdgeLabel(
        string edgeType,
        EntityType sourceType,
        bool isSource,
        EntityType counterpart
    ) =>
        (edgeType, isSource, counterpart) switch
        {
            ("in-volume", true, _) => "所属分卷",
            ("in-volume", false, _) => "包含章节",
            ("membership", true, _) => "所属组织",
            ("membership", false, _) => "成员",
            ("leadership", true, _) => "首领",
            ("leadership", false, _) => "领导组织",
            ("headquarters", true, _) => "总部",
            ("headquarters", false, _) => "作为总部",
            ("contains", true, _) => "下级地点", // 地点层级：父地点 → 子地点（与 setup 亲缘 parent-of 区分）
            ("contains", false, _) => "上级地点",
            ("adjacent-to", _, _) => "相邻",
            ("located-at", true, _) => "场所",
            ("located-at", false, _) => "所属场景",
            ("in-chapter", true, _) => "所属章节",
            ("in-chapter", false, _) => "关联场景",
            ("belongs-to", true, _) => "推进情节线",
            ("belongs-to", false, _) => "关联场景",
            ("appears-in", true, EntityType.Item) => "出场场景",
            ("appears-in", true, _) => "参与场景",
            ("appears-in", false, EntityType.Item) => "出场道具",
            ("appears-in", false, _) => "参与角色",
            ("ownership", true, _) => "持有者",
            ("ownership", false, _) => "持有道具",
            ("relationship", _, _) => "人物关系",
            ("faction-relationship", _, _) => "阵营关系",
            ("advances", true, _) => "推进情节线",
            ("advances", false, EntityType.Volume) => "被推进卷",
            ("advances", false, _) => "被推进章节",
            ("features", true, _) when sourceType == EntityType.Chapter => "计划出场",
            ("features", true, _) => "关键角色",
            ("features", false, EntityType.Volume) => "出场卷",
            ("features", false, _) => "出场章节",
            ("reveals-at", true, _) => "计划揭示",
            ("reveals-at", false, _) => "待揭示情节",
            ("resolves-at", true, _) => "已解决于",
            ("resolves-at", false, _) => "已解决情节",
            ("occurs-in", true, _) => "发生章节",
            ("occurs-in", false, _) => "时间线事件",
            ("constrains", true, _) => "约束",
            ("constrains", false, _) => "受约束",
            ("affects", true, _) => "影响",
            ("affects", false, _) => "被规则影响",
            _ => edgeType,
        };

    /// <summary>关系展示排序权重（双端共用稳定顺序；in-volume 最前置，让章节卡首 Chip 是分卷）。</summary>
    public static int OrderOf(string edgeType) =>
        edgeType switch
        {
            "in-volume" => 0,
            "membership" => 1,
            "leadership" => 2,
            "headquarters" => 3,
            "contains" => 4,
            "adjacent-to" => 5,
            "located-at" => 6,
            "in-chapter" => 7,
            "belongs-to" => 8,
            "appears-in" => 9,
            "ownership" => 10,
            "relationship" => 11,
            "faction-relationship" => 12,
            "advances" => 13,
            "features" => 14,
            "reveals-at" => 15,
            "resolves-at" => 16,
            "occurs-in" => 17,
            "constrains" => 18,
            "affects" => 19,
            _ => 99,
        };

    /// <summary>
    /// 从 store 全量构建（一次快照）。悬空引用（对端实体不存在）跳过——
    /// 「宁漏不误报」先例；构建失败由调用方降级为空索引。
    /// </summary>
    public static async Task<EntityRelationIndex> BuildAsync(
        INovelStore store,
        string novelId,
        CancellationToken ct = default
    )
    {
        var characters = await store.GetCharacters(novelId, ct);
        var factions = await store.GetFactions(novelId, ct);
        var locations = await store.GetLocations(novelId, ct);
        var items = await store.GetItems(novelId, ct);
        var plotlines = await store.GetPlotlines(novelId, ct);
        var scenes = await store.GetScenesByNovel(novelId, ct);
        var relationships = await store.GetRelationshipsByNovel(novelId, ct);
        var factionRelationships = await store.GetFactionRelationships(novelId, ct);
        var timelineEvents = await store.GetTimelineEvents(novelId, ct);
        var worldRules = await store.GetWorldRules(novelId, ct);
        var volumes = await store.GetVolumes(novelId, ct);
        var chapters = await store.GetChapters(novelId, ct);
        var novel = await store.GetNovel(novelId, ct);

        var orderOf = chapters
            .GroupBy(c => c.Id)
            .ToDictionary(g => g.Key, g => g.First().Order, StringComparer.Ordinal);

        // 名称表：同时充当「实体存在」判定（悬空引用跳过的依据）
        var names = new Dictionary<(EntityType Type, string Id), string>();
        void Register(EntityType type, string id, string? name)
        {
            if (!string.IsNullOrEmpty(id))
                names[(type, id)] = name ?? "";
        }

        foreach (var c in characters)
            Register(EntityType.Character, c.Id, c.Name);
        foreach (var f in factions)
            Register(EntityType.Faction, f.Id, f.Name);
        foreach (var l in locations)
            Register(EntityType.Location, l.Id, l.Name);
        foreach (var i in items)
            Register(EntityType.Item, i.Id, i.Name);
        foreach (var p in plotlines)
            Register(EntityType.Plotline, p.Id, p.Name);
        foreach (var s in scenes)
            Register(EntityType.Scene, s.Id, s.Title);
        foreach (var e in timelineEvents)
            Register(EntityType.TimelineEvent, e.Id, e.Title);
        foreach (var v in volumes)
            Register(EntityType.Volume, v.Id, $"第{v.Order}卷 {v.Name}");
        foreach (var c in chapters)
            Register(
                EntityType.Chapter,
                c.Id,
                c.Title.Length > 0 ? $"第{c.Order}章 {c.Title}" : $"第{c.Order}章"
            );
        foreach (var r in worldRules.Where(r => r.EntryKind == WorldRuleEntryKind.Rule))
            Register(
                EntityType.WorldRule,
                r.Id,
                string.IsNullOrWhiteSpace(r.Description) ? r.Type : r.Description
            );
        Register(EntityType.Novel, novelId, novel?.Title);

        var relations = new List<EntityRelation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        bool Exists(EntityType type, string id) => names.ContainsKey((type, id));

        void Add(
            EntityType sourceType,
            string sourceId,
            EntityType targetType,
            string targetId,
            string edgeType,
            string? detail = null
        )
        {
            if (string.IsNullOrEmpty(sourceId) || string.IsNullOrEmpty(targetId))
                return;
            if (!Exists(sourceType, sourceId) || !Exists(targetType, targetId))
                return; // 悬空引用：宁漏不误报
            if (!seen.Add($"{edgeType}|{sourceType}:{sourceId}|{targetType}:{targetId}"))
                return;
            relations.Add(
                new EntityRelation(sourceType, sourceId, targetType, targetId, edgeType, detail)
            );
        }

        // ── 大纲三层 ──
        foreach (var chapter in chapters)
            Add(
                EntityType.Chapter,
                chapter.Id,
                EntityType.Volume,
                chapter.VolumeId ?? "",
                "in-volume"
            );

        foreach (var chapter in chapters)
        foreach (var plotlineId in chapter.PlotlineIds)
            Add(EntityType.Chapter, chapter.Id, EntityType.Plotline, plotlineId, "advances");

        foreach (var chapter in chapters)
        foreach (var characterId in chapter.CharacterIds)
            Add(EntityType.Chapter, chapter.Id, EntityType.Character, characterId, "features");

        foreach (var volume in volumes)
        {
            foreach (var plotlineId in volume.PlotlineIds)
                Add(EntityType.Volume, volume.Id, EntityType.Plotline, plotlineId, "advances");
            foreach (var characterId in volume.CharacterIds)
                Add(EntityType.Volume, volume.Id, EntityType.Character, characterId, "features");
        }

        // ── 角色 / 组织 / 地点 ──
        foreach (var character in characters)
        {
            Add(
                EntityType.Character,
                character.Id,
                EntityType.Faction,
                character.FactionId ?? "",
                "membership"
            );
        }

        foreach (var faction in factions)
        {
            Add(EntityType.Faction, faction.Id, EntityType.Character, faction.Leader, "leadership");
            Add(
                EntityType.Faction,
                faction.Id,
                EntityType.Location,
                faction.HeadquartersId ?? "",
                "headquarters"
            );
            foreach (var memberId in faction.Members)
                Add(EntityType.Character, memberId, EntityType.Faction, faction.Id, "membership");
        }

        foreach (var location in locations)
        {
            if (!string.IsNullOrWhiteSpace(location.ParentId))
                Add(
                    EntityType.Location,
                    location.ParentId,
                    EntityType.Location,
                    location.Id,
                    "contains"
                );

            foreach (var adjacent in location.AdjacentLocations ?? [])
                Add(
                    EntityType.Location,
                    location.Id,
                    EntityType.Location,
                    adjacent.TargetId,
                    "adjacent-to",
                    $"{adjacent.DistanceLi:0.##} 里"
                );
        }

        // ── 场景 ──
        foreach (var scene in scenes)
        {
            Add(
                EntityType.Scene,
                scene.Id,
                EntityType.Location,
                scene.LocationId ?? "",
                "located-at"
            );
            Add(
                EntityType.Scene,
                scene.Id,
                EntityType.Chapter,
                scene.ChapterId ?? "",
                "in-chapter"
            );

            foreach (var plotlineId in scene.PlotlineIds)
                Add(EntityType.Scene, scene.Id, EntityType.Plotline, plotlineId, "belongs-to");
            foreach (var participantId in scene.ParticipantIds)
                Add(EntityType.Character, participantId, EntityType.Scene, scene.Id, "appears-in");
            foreach (var itemId in scene.ItemIds)
                Add(EntityType.Item, itemId, EntityType.Scene, scene.Id, "appears-in");
        }

        // ── 道具持有（单值 + 时窗链，同持有者合并为一条边） ──
        foreach (var item in items)
        {
            var windowsByOwner = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var period in item.OwnershipPeriods)
            {
                if (string.IsNullOrEmpty(period.OwnerId))
                    continue;
                var window = FormatWindow(period.SinceChapterId, period.UntilChapterId, orderOf);
                if (window is null)
                    continue;
                if (!windowsByOwner.TryGetValue(period.OwnerId, out var list))
                    windowsByOwner[period.OwnerId] = list = [];
                list.Add(window);
            }

            if (!string.IsNullOrEmpty(item.OwnerId))
                Add(
                    EntityType.Item,
                    item.Id,
                    EntityType.Character,
                    item.OwnerId,
                    "ownership",
                    windowsByOwner.TryGetValue(item.OwnerId, out var windows)
                        ? string.Join("、", windows)
                        : null
                );

            foreach (var (ownerId, windows) in windowsByOwner)
                Add(
                    EntityType.Item,
                    item.Id,
                    EntityType.Character,
                    ownerId,
                    "ownership",
                    string.Join("、", windows)
                );
        }

        // ── 人物关系 / 阵营关系 ──
        foreach (var relationship in relationships)
        {
            Add(
                EntityType.Character,
                relationship.CharacterAId,
                EntityType.Character,
                relationship.CharacterBId,
                "relationship",
                FormatWindow(relationship.SinceChapterId, relationship.UntilChapterId, orderOf)
            );
        }

        foreach (var factionRelationship in factionRelationships)
        {
            Add(
                EntityType.Faction,
                factionRelationship.FactionAId,
                EntityType.Faction,
                factionRelationship.FactionBId,
                "faction-relationship"
            );
        }

        // ── 情节线锚点 / 时间线 / 规则 ──
        foreach (var plotline in plotlines)
        {
            Add(
                EntityType.Plotline,
                plotline.Id,
                EntityType.Chapter,
                plotline.PlannedRevealChapterId ?? "",
                "reveals-at"
            );
            Add(
                EntityType.Plotline,
                plotline.Id,
                EntityType.Chapter,
                plotline.ResolvedAtChapterId ?? "",
                "resolves-at"
            );
        }

        foreach (var timelineEvent in timelineEvents)
            Add(
                EntityType.TimelineEvent,
                timelineEvent.Id,
                EntityType.Chapter,
                timelineEvent.ChapterId ?? "",
                "occurs-in"
            );

        foreach (var rule in worldRules.Where(r => r.EntryKind == WorldRuleEntryKind.Rule))
        {
            var ruleDetail = string.IsNullOrWhiteSpace(rule.Type)
                ? rule.Description
                : $"{rule.Type}={rule.Value:0.##}{rule.Unit}";
            Add(EntityType.WorldRule, rule.Id, EntityType.Novel, novelId, "constrains", ruleDetail);

            // 规则主体：复用 constrains 语义（规则约束具体实体；SubjectId 为空 = 整类，不做单点边）
            if (rule.SubjectType is { } subjectType && rule.SubjectId is { Length: > 0 } subjectId)
                Add(
                    EntityType.WorldRule,
                    rule.Id,
                    subjectType,
                    subjectId,
                    "constrains",
                    rule.Description
                );

            // 后果目标：本地展示边 affects（云端图谱不投影）
            if (rule.Effect is { } effect)
            {
                var targetId = string.IsNullOrWhiteSpace(effect.TargetId)
                    ? rule.SubjectId
                    : effect.TargetId;
                if (targetId is { Length: > 0 })
                    Add(
                        EntityType.WorldRule,
                        rule.Id,
                        effect.TargetEntityType,
                        targetId,
                        "affects",
                        effect.Summary
                    );
            }
        }

        // ── 节点索引 ──
        var byNode = new Dictionary<(EntityType, string), List<int>>();
        for (var i = 0; i < relations.Count; i++)
        {
            var relation = relations[i];
            var sourceKey = (relation.SourceType, relation.SourceId);
            var targetKey = (relation.TargetType, relation.TargetId);
            if (!byNode.TryGetValue(sourceKey, out var sourceList))
                byNode[sourceKey] = sourceList = [];
            sourceList.Add(i);
            if (!byNode.TryGetValue(targetKey, out var targetList))
                byNode[targetKey] = targetList = [];
            targetList.Add(i);
        }

        return new EntityRelationIndex(relations, byNode, names);
    }

    /// <summary>章节时窗文案：`第{since}–{until}章` / `第{since}章起` / `第{until}章止`；两端口都无章节 → null。</summary>
    private static string? FormatWindow(
        string? sinceChapterId,
        string? untilChapterId,
        IReadOnlyDictionary<string, int> orderOf
    )
    {
        var since =
            sinceChapterId is not null && orderOf.TryGetValue(sinceChapterId, out var s)
                ? s
                : (int?)null;
        var until =
            untilChapterId is not null && orderOf.TryGetValue(untilChapterId, out var u)
                ? u
                : (int?)null;
        return (since, until) switch
        {
            (not null, not null) => $"第{since}–{until}章",
            (not null, null) => $"第{since}章起",
            (null, not null) => $"第{until}章止",
            _ => null,
        };
    }
}
