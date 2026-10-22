using Core.Data;

namespace Core.Evolution;

/// <summary>单个素材边界 delta（本体状态变更的素材投影）。</summary>
public sealed record MaterialDelta(
    string Id,
    PlannedDeltaKind Kind,
    DeltaSource Source,
    EntityType EntityType,
    string EntityId,
    string EntityName,
    EvolutionAspect? Aspect,
    string? From,
    string? To,
    string Summary,
    MaterialNeed Need,
    IReadOnlyList<string> Kinds,
    IReadOnlyList<string> QueryTerms,
    string CalculationId = "persisted-delta",
    string? RuleId = null
);

/// <summary>一个场景的素材边界结果。</summary>
public sealed record SceneMaterialBoundary(
    SceneDefinition Scene,
    IReadOnlyList<MaterialDelta> Deltas
);

/// <summary>
/// 本体状态变更 → 素材边界检测（纯函数，无 DB/LLM）。
///
/// 口径：
/// - checked at every SceneDefinition（无场景时退化为章级单场景）；
/// - persisted delta = StateAsOf(当前章开始) - StateAsOf(上次 Present 章结束)；
/// - 首次 Present 出场 = FirstAppearance；
/// - planned delta = SceneDefinition.PlannedDeltas；
/// - 规则触发 = RuleEvaluator 对结构化 facts 求值（无 facts 不猜）。
///
/// 老角色回归但状态没变 → delta 为空 → 不触发；缺席期间状态变化 → 触发。
/// </summary>
public static class MaterialBoundaryDetector
{
    /// <summary>按章检测全部场景的边界（场景按 Order 排序）。</summary>
    public static IReadOnlyList<SceneMaterialBoundary> DetectForChapter(
        EntityTimelineView view,
        Novel novel,
        Chapter chapter,
        IReadOnlyList<SceneDefinition> scenes,
        IReadOnlyList<WorldRule>? worldRules = null,
        Func<EntityType, string, RuleFacts>? factsProvider = null,
        IReadOnlyList<OntologyAction>? recentActions = null
    )
    {
        var chapterOrder = novel
            .Chapters.GroupBy(c => c.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Order, StringComparer.Ordinal);
        if (!chapterOrder.ContainsKey(chapter.Id))
        {
            chapterOrder[chapter.Id] = chapter.Order;
        }

        var orderedScenes = scenes
            .Where(s => s.ChapterId is null || s.ChapterId == chapter.Id)
            .OrderBy(s => s.Order)
            .ToList();
        if (orderedScenes.Count == 0)
        {
            orderedScenes.Add(
                new SceneDefinition
                {
                    Id = $"chapter:{chapter.Id}",
                    NovelId = novel.Id,
                    Title = chapter.Title,
                    ChapterId = chapter.Id,
                    Order = 1,
                    ParticipantIds = chapter.CharacterIds.ToList(),
                    PlotlineIds = chapter.PlotlineIds.ToList(),
                    SceneType = SceneType.Dialogue,
                }
            );
        }

        var names = BuildNameMap(novel);
        var rules = (worldRules ?? novel.WorldRules)
            .Where(r => r.EntryKind == WorldRuleEntryKind.Rule)
            .ToList();
        var result = new List<SceneMaterialBoundary>(orderedScenes.Count);
        var seenPersisted = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < orderedScenes.Count; i++)
        {
            var scene = orderedScenes[i];
            var deltas = new List<MaterialDelta>();

            // 章级 persisted delta 只落第一个场景，避免同章多个场景重复提示。
            if (i == 0)
            {
                AddPersistedDeltas(
                    view,
                    novel,
                    chapter,
                    chapterOrder,
                    names,
                    scene,
                    deltas,
                    seenPersisted
                );
                AddPlotlineRevealDeltas(novel, chapter, names, deltas);
                AddSettingsDeltas(
                    novel,
                    chapter,
                    chapterOrder,
                    rules,
                    scene,
                    names,
                    deltas,
                    seenPersisted,
                    recentActions
                );
            }

            AddPlannedDeltas(scene, names, deltas);
            AddRuleDeltas(novel, chapter, scene, rules, chapterOrder, names, deltas, factsProvider);

            result.Add(new SceneMaterialBoundary(scene, Deduplicate(deltas)));
        }

        return result;
    }

    private static void AddPersistedDeltas(
        EntityTimelineView view,
        Novel novel,
        Chapter chapter,
        IReadOnlyDictionary<string, int> chapterOrder,
        IReadOnlyDictionary<(EntityType, string), string> names,
        SceneDefinition scene,
        List<MaterialDelta> deltas,
        HashSet<string> seen
    )
    {
        foreach (var (type, id) in Candidates(scene, chapter))
        {
            // 情节线没有 L0 出场记录；其状态变化由 AddPlotlineRevealDeltas 处理。
            if (type == EntityType.Plotline)
            {
                continue;
            }

            // 注意：L0 AppearanceRecord 目前把“名字命中”记为 Present；
            // 严格区分提及/在场需依赖 SceneDefinition.ParticipantIds（结构化场景）。
            var appearances = view.GetAppearancesAcrossChapters(type, id, chapterOrder)
                .Where(a => a.ChapterOrder < chapter.Order)
                .ToList();
            var lastPresent = appearances
                .Where(a => a.Mode == AppearanceMode.Present)
                .Select(a => (int?)a.ChapterOrder)
                .LastOrDefault();

            if (lastPresent is null)
            {
                var key = $"f:{type}:{id}";
                if (seen.Add(key))
                {
                    deltas.Add(
                        BuildDelta(
                            key,
                            PlannedDeltaKind.StateChange,
                            DeltaSource.Persisted,
                            type,
                            id,
                            names,
                            aspect: null,
                            from: null,
                            to: null,
                            summary: "首次出场",
                            need: FirstAppearanceNeed(type),
                            kinds: FirstAppearanceKinds(type),
                            queryTerms: ["首次登场", "外貌", "气质", "环境", "器物"],
                            calculationId: OntologyCalculations.FirstAppearance
                        )
                    );
                }
                continue;
            }

            var changes = view
                .AllStateChanges.Where(c =>
                    c.Lifecycle == ChangeLifecycle.Committed
                    && c.TargetEntityType == type
                    && c.TargetId == id
                    && chapterOrder.TryGetValue(c.ChapterId, out var order)
                    && order > lastPresent.Value
                    && order < chapter.Order
                )
                .GroupBy(c => c.Aspect)
                .Select(g =>
                    g.OrderBy(c => chapterOrder[c.ChapterId]).ThenBy(c => c.CreatedAt).Last()
                )
                .ToList();

            foreach (var change in changes)
            {
                var key = $"p:{type}:{id}:{change.Aspect}:{change.FromSummary}->{change.ToSummary}";
                if (!seen.Add(key))
                {
                    continue;
                }

                deltas.Add(
                    BuildDelta(
                        key,
                        PlannedDeltaKind.StateChange,
                        DeltaSource.Persisted,
                        type,
                        id,
                        names,
                        change.Aspect,
                        change.FromSummary,
                        change.ToSummary,
                        string.IsNullOrWhiteSpace(change.Summary)
                            ? $"{Names(names, type, id)}：{change.FromSummary ?? "?"} → {change.ToSummary ?? "?"}"
                            : change.Summary,
                        need: AspectNeed(change.Aspect),
                        kinds: AspectKinds(change.Aspect),
                        queryTerms: AspectTerms(
                            change.Aspect,
                            change.FromSummary,
                            change.ToSummary
                        ),
                        calculationId: OntologyCalculations.StateDelta
                    )
                );
            }
        }
    }

    private static void AddPlotlineRevealDeltas(
        Novel novel,
        Chapter chapter,
        IReadOnlyDictionary<(EntityType, string), string> names,
        List<MaterialDelta> deltas
    )
    {
        foreach (var plotline in novel.Plotlines)
        {
            if (
                plotline.PlannedRevealChapterId != chapter.Id
                && plotline.ResolvedAtChapterId != chapter.Id
            )
            {
                continue;
            }

            var action = plotline.PlannedRevealChapterId == chapter.Id ? "揭示" : "回收";
            deltas.Add(
                BuildDelta(
                    $"plot:{plotline.Id}:{chapter.Id}",
                    PlannedDeltaKind.PlotlineReveal,
                    DeltaSource.Persisted,
                    EntityType.Plotline,
                    plotline.Id,
                    names,
                    aspect: null,
                    from: null,
                    to: null,
                    summary: $"{plotline.Name}：本章{action}伏笔",
                    need: MaterialNeed.Strong,
                    kinds: ["plot_beat", "trivia"],
                    queryTerms: ["伏笔", "揭示", "回收", "反转", plotline.Name],
                    calculationId: OntologyCalculations.PlotlineReveal
                )
            );
        }
    }

    private static void AddPlannedDeltas(
        SceneDefinition scene,
        IReadOnlyDictionary<(EntityType, string), string> names,
        List<MaterialDelta> deltas
    )
    {
        for (var i = 0; i < scene.PlannedDeltas.Count; i++)
        {
            var planned = scene.PlannedDeltas[i];
            var (need, kinds, terms) = PlannedMapping(planned);
            if (need == MaterialNeed.None)
            {
                continue;
            }

            deltas.Add(
                BuildDelta(
                    $"pl:{scene.Id}:{i}:{planned.Kind}:{planned.TargetEntityType}:{planned.TargetId}:{planned.Aspect}",
                    planned.Kind,
                    DeltaSource.Planned,
                    planned.TargetEntityType,
                    planned.TargetId,
                    names,
                    planned.Aspect,
                    planned.FromSummary,
                    planned.ToSummary,
                    string.IsNullOrWhiteSpace(planned.Summary)
                        ? $"{planned.FromSummary ?? "?"} → {planned.ToSummary ?? "?"}"
                        : planned.Summary,
                    need,
                    kinds,
                    terms,
                    OntologyCalculations.PlannedDelta
                )
            );
        }
    }

    private static void AddSettingsDeltas(
        Novel novel,
        Chapter chapter,
        IReadOnlyDictionary<string, int> chapterOrder,
        IReadOnlyList<WorldRule> rules,
        SceneDefinition scene,
        IReadOnlyDictionary<(EntityType, string), string> names,
        List<MaterialDelta> deltas,
        HashSet<string> seen,
        IReadOnlyList<OntologyAction>? recentActions
    )
    {
        if (recentActions is null || recentActions.Count == 0)
        {
            return;
        }

        var baseline = novel
            .Chapters.Where(c => c.Order < chapter.Order && c.UpdatedAt != default)
            .Select(c => (DateTime?)c.UpdatedAt)
            .Max();
        var candidates = Candidates(scene, chapter).ToHashSet();
        var selected = new List<OntologyAction>();
        foreach (var action in recentActions)
        {
            if (!string.Equals(action.NovelId, novel.Id, StringComparison.Ordinal))
            {
                continue;
            }
            if (
                action.Element
                is not (OntologyElement.State or OntologyElement.Relation or OntologyElement.Rule)
            )
            {
                continue;
            }
            // 叙事状态记录已由 persisted delta 消费，避免重复。
            if (string.Equals(action.Field, "state-change", StringComparison.Ordinal))
            {
                continue;
            }
            if (
                action.TargetEntityType == EntityType.Scene
                && string.Equals(action.Field, "planned-deltas", StringComparison.Ordinal)
            )
            {
                continue;
            }
            if (
                baseline is { } baselineInstant
                && action.CreatedAt.ToUniversalTime() < baselineInstant.ToUniversalTime()
            )
            {
                continue;
            }
            selected.Add(action);
        }

        selected = selected
            .OrderBy(a => a.Element == OntologyElement.Rule ? 0 : 1)
            .ThenByDescending(a => a.CreatedAt)
            .Take(baseline is null ? 5 : 10)
            .ToList();

        var summaries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in selected)
        {
            if (action.Element == OntologyElement.Rule)
            {
                var rule = rules.FirstOrDefault(r =>
                    string.Equals(r.Id, action.TargetId, StringComparison.Ordinal)
                );
                if (rule is null)
                {
                    continue;
                }

                var ruleTitle = !string.IsNullOrWhiteSpace(rule.Description)
                    ? rule.Description
                    : rule.Type;
                var summary = $"规则「{ruleTitle}」已更新：{action.Summary}";
                var need = rule.Severity switch
                {
                    RuleSeverity.Constraint => MaterialNeed.Strong,
                    RuleSeverity.Warning => MaterialNeed.Optional,
                    _ => MaterialNeed.None,
                };
                if (need == MaterialNeed.None)
                {
                    continue;
                }

                if (rule.SubjectType is null)
                {
                    AddSettingsDelta(
                        $"settings:{action.Id}:novel:{novel.Id}",
                        EntityType.Novel,
                        novel.Id,
                        summary,
                        null,
                        null,
                        need,
                        OntologyCalculations.SettingsChange,
                        rule.Id,
                        names,
                        deltas,
                        seen,
                        summaries
                    );
                    continue;
                }

                foreach (var (type, id) in candidates)
                {
                    if (type != rule.SubjectType)
                    {
                        continue;
                    }
                    if (
                        !string.IsNullOrWhiteSpace(rule.SubjectId)
                        && !string.Equals(rule.SubjectId, id, StringComparison.Ordinal)
                    )
                    {
                        continue;
                    }
                    AddSettingsDelta(
                        $"settings:{action.Id}:{type}:{id}",
                        type,
                        id,
                        summary,
                        null,
                        null,
                        need,
                        OntologyCalculations.SettingsChange,
                        rule.Id,
                        names,
                        deltas,
                        seen,
                        summaries
                    );
                }
                continue;
            }

            var classification = OntologyFieldMap.Classify(
                action.TargetEntityType,
                action.Field ?? ""
            );
            var kind =
                classification.Element == OntologyElement.State
                    ? PlannedDeltaKind.StateChange
                    : PlannedDeltaKind.RelationChange;
            var aspect = classification.Aspect;
            var relationNeed =
                classification.Element == OntologyElement.Relation
                    ? MaterialNeed.Optional
                    : MaterialNeed.Strong;

            if (action.TargetEntityType == EntityType.Relationship)
            {
                var relationship = novel.Relationships.FirstOrDefault(r =>
                    string.Equals(r.Id, action.TargetId, StringComparison.Ordinal)
                );
                if (relationship is null)
                {
                    continue;
                }
                foreach (
                    var characterId in new[]
                    {
                        relationship.CharacterAId,
                        relationship.CharacterBId,
                    }
                )
                {
                    if (!candidates.Contains((EntityType.Character, characterId)))
                    {
                        continue;
                    }
                    AddSettingsDelta(
                        $"settings:{action.Id}:character:{characterId}",
                        EntityType.Character,
                        characterId,
                        action.Summary,
                        action.From,
                        action.To,
                        relationNeed,
                        OntologyCalculations.SettingsChange,
                        null,
                        names,
                        deltas,
                        seen,
                        summaries,
                        aspect,
                        PlannedDeltaKind.RelationChange
                    );
                }
                continue;
            }

            if (!candidates.Contains((action.TargetEntityType, action.TargetId)))
            {
                continue;
            }
            AddSettingsDelta(
                $"settings:{action.Id}:{action.TargetEntityType}:{action.TargetId}",
                action.TargetEntityType,
                action.TargetId,
                action.Summary,
                action.From,
                action.To,
                relationNeed,
                OntologyCalculations.SettingsChange,
                null,
                names,
                deltas,
                seen,
                summaries,
                aspect,
                kind
            );
        }
    }

    private static void AddSettingsDelta(
        string key,
        EntityType type,
        string id,
        string summary,
        string? from,
        string? to,
        MaterialNeed need,
        string calculationId,
        string? ruleId,
        IReadOnlyDictionary<(EntityType, string), string> names,
        List<MaterialDelta> deltas,
        HashSet<string> seen,
        HashSet<string> summaries,
        EvolutionAspect? aspect = null,
        PlannedDeltaKind kind = PlannedDeltaKind.StateChange
    )
    {
        if (!seen.Add(key) || !summaries.Add(summary))
        {
            return;
        }

        var isRelation = kind == PlannedDeltaKind.RelationChange;
        deltas.Add(
            BuildDelta(
                $"s:{key}",
                kind,
                DeltaSource.Settings,
                type,
                id,
                names,
                aspect,
                from,
                to,
                summary,
                need,
                isRelation ? ["plot_beat", "character"] : ["description", "character"],
                isRelation ? ["关系", "对话", "冲突", "结盟"] : ["状态", "伤病", "变化"],
                calculationId,
                ruleId
            )
        );
    }

    private static void AddRuleDeltas(
        Novel novel,
        Chapter chapter,
        SceneDefinition scene,
        IReadOnlyList<WorldRule> rules,
        IReadOnlyDictionary<string, int> chapterIdToOrder,
        IReadOnlyDictionary<(EntityType, string), string> names,
        List<MaterialDelta> deltas,
        Func<EntityType, string, RuleFacts>? factsProvider
    )
    {
        if (rules.Count == 0)
        {
            return;
        }

        var applicable = rules
            .Where(r => RuleEvaluator.AppliesToChapter(r, chapterIdToOrder, chapter.Order))
            .ToList();
        if (applicable.Count == 0)
        {
            return;
        }

        foreach (var (type, id) in CandidatesWithFactions(novel, scene, chapter))
        {
            var facts = factsProvider?.Invoke(type, id) ?? BuildRuleFacts(novel, type, id, scene);
            if (facts.Tokens.Count == 0 && facts.Numeric.Count == 0 && facts.Relations.Count == 0)
            {
                continue;
            }

            foreach (var rule in applicable)
            {
                var effect = RuleEvaluator.Evaluate(rule, type, id, facts);
                if (effect is null)
                {
                    continue;
                }

                var effectType = effect.TargetEntityType;
                var effectId = string.IsNullOrWhiteSpace(effect.TargetId) ? id : effect.TargetId;
                var need = rule.Severity switch
                {
                    RuleSeverity.Constraint => MaterialNeed.Strong,
                    RuleSeverity.Warning => MaterialNeed.Optional,
                    _ => MaterialNeed.None,
                };
                deltas.Add(
                    BuildDelta(
                        $"rule:{rule.Id}:{type}:{id}",
                        PlannedDeltaKind.RuleTrigger,
                        DeltaSource.Computed,
                        effectType,
                        effectId,
                        names,
                        effect.Aspect,
                        effect.FromSummary,
                        effect.ToSummary,
                        string.IsNullOrWhiteSpace(effect.Summary)
                            ? rule.Description
                            : effect.Summary,
                        need,
                        ["setting", "reference", "plot_beat"],
                        ["规则", "代价", "禁忌", rule.Type, rule.Description],
                        OntologyCalculations.RuleTrigger,
                        rule.Id
                    )
                );
            }
        }
    }

    private static RuleFacts BuildRuleFacts(
        Novel novel,
        EntityType type,
        string id,
        SceneDefinition scene
    )
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var numeric = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var relations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        switch (type)
        {
            case EntityType.Character:
                if (novel.Characters.FirstOrDefault(c => c.Id == id) is { } character)
                {
                    AddToken(tokens, character.Status.ToString());
                    AddToken(tokens, character.Role.ToString());
                    AddToken(tokens, character.Gender);
                    foreach (var tag in character.Tags)
                    {
                        AddToken(tokens, tag);
                    }
                    AddToken(tokens, character.Abilities);
                    foreach (var (key, value) in character.Metrics)
                    {
                        numeric[key] = value;
                    }
                    AddStatusToken(
                        tokens,
                        character.Status.ToString(),
                        CharacterStatusLabel(character.Status)
                    );
                    if (!string.IsNullOrWhiteSpace(character.FactionId))
                    {
                        relations.Add($"member-of|{character.FactionId}");
                    }
                    if (!string.IsNullOrWhiteSpace(character.CurrentLocationId))
                    {
                        relations.Add($"located-at|{character.CurrentLocationId}");
                    }
                    foreach (var relationship in novel.Relationships)
                    {
                        if (string.Equals(relationship.CharacterAId, id, StringComparison.Ordinal))
                        {
                            relations.Add(
                                $"relationship|{relationship.CharacterBId}|{relationship.Type.ToKebabCaseString()}"
                            );
                        }
                        else if (
                            string.Equals(relationship.CharacterBId, id, StringComparison.Ordinal)
                        )
                        {
                            relations.Add(
                                $"relationship|{relationship.CharacterAId}|{relationship.Type.ToKebabCaseString()}"
                            );
                        }
                    }
                    foreach (var ownedItem in novel.Items)
                    {
                        if (string.Equals(ownedItem.OwnerId, id, StringComparison.Ordinal))
                        {
                            relations.Add($"owns|{ownedItem.Id}");
                        }
                    }
                }
                break;
            case EntityType.Location:
                if (novel.Locations.FirstOrDefault(l => l.Id == id) is { } location)
                {
                    AddToken(tokens, location.Type.ToString());
                    AddStatusToken(
                        tokens,
                        location.Status.ToString(),
                        LocationStatusLabel(location.Status)
                    );
                    AddToken(tokens, location.Atmosphere);
                    foreach (var (key, value) in location.Metrics)
                    {
                        numeric[key] = value;
                    }
                    if (!string.IsNullOrWhiteSpace(location.ParentId))
                    {
                        relations.Add($"parent|{location.ParentId}");
                    }
                    foreach (var adjacent in location.AdjacentLocations ?? [])
                    {
                        relations.Add($"adjacent|{adjacent.TargetId}");
                    }
                }
                break;
            case EntityType.Item:
                if (novel.Items.FirstOrDefault(i => i.Id == id) is { } item)
                {
                    AddToken(tokens, item.Type.ToString());
                    AddStatusToken(tokens, item.Status.ToString(), ItemStatusLabel(item.Status));
                    AddToken(tokens, item.Description);
                    if (!string.IsNullOrWhiteSpace(item.LocationId))
                    {
                        relations.Add($"located-at|{item.LocationId}");
                    }
                    foreach (var (key, value) in item.Metrics)
                    {
                        numeric[key] = value;
                    }
                    if (!string.IsNullOrWhiteSpace(item.OwnerId))
                    {
                        relations.Add($"owned-by|{item.OwnerId}");
                    }
                    foreach (var period in item.OwnershipPeriods)
                    {
                        if (!string.IsNullOrWhiteSpace(period.OwnerId))
                        {
                            relations.Add($"owned-by|{period.OwnerId}");
                        }
                    }
                }
                break;
            case EntityType.Plotline:
                if (novel.Plotlines.FirstOrDefault(p => p.Id == id) is { } plotline)
                {
                    AddStatusToken(
                        tokens,
                        plotline.Status.ToString(),
                        PlotlineStatusLabel(plotline.Status)
                    );
                    AddToken(tokens, plotline.Tension.ToString());
                }
                break;
            case EntityType.Faction:
                if (novel.Factions.FirstOrDefault(f => f.Id == id) is { } faction)
                {
                    AddToken(tokens, faction.Type.ToString());
                    AddStatusToken(
                        tokens,
                        faction.Status.ToString(),
                        FactionStatusLabel(faction.Status)
                    );
                    AddToken(tokens, faction.Goal);
                    if (!string.IsNullOrWhiteSpace(faction.Leader))
                    {
                        relations.Add($"leader|{faction.Leader}");
                    }
                    foreach (var (key, value) in faction.Metrics)
                    {
                        numeric[key] = value;
                    }
                    foreach (var memberId in faction.Members)
                    {
                        relations.Add($"has-member|{memberId}");
                    }
                    if (!string.IsNullOrWhiteSpace(faction.HeadquartersId))
                    {
                        relations.Add($"headquarters|{faction.HeadquartersId}");
                    }
                }
                break;
        }

        foreach (var planned in scene.PlannedDeltas)
        {
            if (
                planned.Kind == PlannedDeltaKind.Action
                && planned.TargetEntityType == type
                && string.Equals(planned.TargetId, id, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(planned.ActionType)
            )
            {
                AddToken(tokens, planned.ActionType);
            }
        }

        return new RuleFacts(numeric, tokens) { Relations = relations };
    }

    /// <summary>加入枚举值 token 与对应中文标签（规则 token 匹配大小写不敏感）。</summary>
    private static void AddStatusToken(HashSet<string> tokens, string enumName, string label)
    {
        AddToken(tokens, enumName);
        AddToken(tokens, label);
    }

    private static string CharacterStatusLabel(CharacterStatus status) =>
        status switch
        {
            CharacterStatus.Active => "活跃",
            CharacterStatus.Deceased => "死亡",
            CharacterStatus.Missing => "失踪",
            _ => "转变",
        };

    private static string FactionStatusLabel(FactionStatus status) =>
        status switch
        {
            FactionStatus.Active => "活跃",
            FactionStatus.Dormant => "蛰伏",
            _ => "覆灭",
        };

    private static string ItemStatusLabel(ItemStatus status) =>
        status switch
        {
            ItemStatus.Active => "完好",
            ItemStatus.Lost => "遗失",
            _ => "损毁",
        };

    private static string LocationStatusLabel(LocationStatus status) =>
        status switch
        {
            LocationStatus.Active => "存在",
            LocationStatus.Destroyed => "毁灭",
            LocationStatus.Sealed => "封印",
            _ => "废弃",
        };

    private static string PlotlineStatusLabel(PlotlineStatus status) =>
        status switch
        {
            PlotlineStatus.Active => "进行中",
            PlotlineStatus.Resolved => "已解决",
            _ => "放弃",
        };

    /// <summary>把文本按常见分隔符切成 token（只做结构化匹配，不做语义推断）。</summary>
    private static void AddToken(HashSet<string> tokens, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        tokens.Add(raw.Trim());
        foreach (
            var part in raw.Split(
                ['、', ',', '，', ';', '；', ' ', '\n', '\r', '\t'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            tokens.Add(part);
        }
    }

    /// <summary>场景候选 + 在场角色所属组织（组织状态/关系规则需要有求值对象）。</summary>
    private static IEnumerable<(EntityType Type, string Id)> CandidatesWithFactions(
        Novel novel,
        SceneDefinition scene,
        Chapter? chapter
    )
    {
        var seen = new HashSet<(EntityType, string)>();
        var characters = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in Candidates(scene, chapter))
        {
            if (seen.Add(candidate))
                yield return candidate;
            if (candidate.Type == EntityType.Character)
                characters.Add(candidate.Id);
        }
        foreach (
            var factionId in novel
                .Characters.Where(c => characters.Contains(c.Id))
                .Select(c => c.FactionId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
        )
        {
            if (seen.Add((EntityType.Faction, factionId!)))
                yield return (EntityType.Faction, factionId!);
        }
    }

    private static IEnumerable<(EntityType Type, string Id)> Candidates(
        SceneDefinition scene,
        Chapter? chapter
    )
    {
        var seen = new HashSet<(EntityType, string)>();
        foreach (var id in scene.ParticipantIds)
        {
            if (seen.Add((EntityType.Character, id)))
                yield return (EntityType.Character, id);
        }
        if (
            !string.IsNullOrWhiteSpace(scene.LocationId)
            && seen.Add((EntityType.Location, scene.LocationId))
        )
        {
            yield return (EntityType.Location, scene.LocationId);
        }
        foreach (var id in scene.ItemIds)
        {
            if (seen.Add((EntityType.Item, id)))
                yield return (EntityType.Item, id);
        }
        foreach (var id in scene.PlotlineIds)
        {
            if (seen.Add((EntityType.Plotline, id)))
                yield return (EntityType.Plotline, id);
        }
        if (chapter is not null)
        {
            foreach (var id in chapter.CharacterIds)
            {
                if (seen.Add((EntityType.Character, id)))
                    yield return (EntityType.Character, id);
            }
            foreach (var id in chapter.PlotlineIds)
            {
                if (seen.Add((EntityType.Plotline, id)))
                    yield return (EntityType.Plotline, id);
            }
        }
        foreach (var planned in scene.PlannedDeltas)
        {
            if (seen.Add((planned.TargetEntityType, planned.TargetId)))
                yield return (planned.TargetEntityType, planned.TargetId);
        }
    }

    private static MaterialDelta BuildDelta(
        string id,
        PlannedDeltaKind kind,
        DeltaSource source,
        EntityType entityType,
        string entityId,
        IReadOnlyDictionary<(EntityType, string), string> names,
        EvolutionAspect? aspect,
        string? from,
        string? to,
        string summary,
        MaterialNeed need,
        IReadOnlyList<string> kinds,
        IReadOnlyList<string?> queryTerms,
        string calculationId,
        string? ruleId = null
    ) =>
        new(
            id,
            kind,
            source,
            entityType,
            entityId,
            Names(names, entityType, entityId),
            aspect,
            from,
            to,
            summary,
            need,
            kinds,
            queryTerms
                .Select(static t => t?.Trim())
                .OfType<string>()
                .Where(static t => t.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Take(8)
                .ToList(),
            calculationId,
            ruleId
        );

    private static IReadOnlyDictionary<(EntityType, string), string> BuildNameMap(Novel novel)
    {
        var map = new Dictionary<(EntityType, string), string>();
        foreach (var c in novel.Characters)
            map[(EntityType.Character, c.Id)] = c.Name;
        foreach (var l in novel.Locations)
            map[(EntityType.Location, l.Id)] = l.Name;
        foreach (var i in novel.Items)
            map[(EntityType.Item, i.Id)] = i.Name;
        foreach (var f in novel.Factions)
            map[(EntityType.Faction, f.Id)] = f.Name;
        foreach (var p in novel.Plotlines)
            map[(EntityType.Plotline, p.Id)] = p.Name;
        return map;
    }

    private static string Names(
        IReadOnlyDictionary<(EntityType, string), string> names,
        EntityType type,
        string id
    ) =>
        names.TryGetValue((type, id), out var name) && !string.IsNullOrWhiteSpace(name) ? name : id;

    private static List<MaterialDelta> Deduplicate(List<MaterialDelta> deltas)
    {
        var result = new List<MaterialDelta>(deltas.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var delta in deltas)
        {
            if (seen.Add(delta.Id))
            {
                result.Add(delta);
            }
        }
        return result;
    }

    private static MaterialNeed FirstAppearanceNeed(EntityType type) =>
        type switch
        {
            EntityType.Character or EntityType.Location or EntityType.Item => MaterialNeed.Strong,
            EntityType.Faction or EntityType.Plotline => MaterialNeed.Optional,
            _ => MaterialNeed.None,
        };

    private static IReadOnlyList<string> FirstAppearanceKinds(EntityType type) =>
        type switch
        {
            EntityType.Character => ["description", "character"],
            EntityType.Location => ["description"],
            EntityType.Item => ["description", "setting"],
            EntityType.Faction => ["setting", "reference"],
            EntityType.Plotline => ["plot_beat"],
            _ => [],
        };

    private static MaterialNeed AspectNeed(EvolutionAspect? aspect) =>
        aspect switch
        {
            EvolutionAspect.Status
            or EvolutionAspect.Personality
            or EvolutionAspect.Relationship
            or EvolutionAspect.Ability
            or EvolutionAspect.Location => MaterialNeed.Strong,
            EvolutionAspect.Motivation
            or EvolutionAspect.Possession
            or EvolutionAspect.Knowledge
            or EvolutionAspect.Behavior => MaterialNeed.Optional,
            _ => MaterialNeed.None,
        };

    private static IReadOnlyList<string> AspectKinds(EvolutionAspect? aspect) =>
        aspect switch
        {
            EvolutionAspect.Status => ["description", "plot_beat"],
            EvolutionAspect.Personality => ["character", "description"],
            EvolutionAspect.Motivation => ["plot_beat"],
            EvolutionAspect.Relationship => ["character", "plot_beat"],
            EvolutionAspect.Ability => ["technique", "setting"],
            EvolutionAspect.Possession => ["description", "setting"],
            EvolutionAspect.Location => ["description"],
            EvolutionAspect.Knowledge => ["plot_beat", "trivia"],
            EvolutionAspect.Behavior => ["character"],
            _ => [],
        };

    private static IReadOnlyList<string?> AspectTerms(
        EvolutionAspect? aspect,
        string? from,
        string? to
    ) =>
        aspect switch
        {
            EvolutionAspect.Status => ["重伤", "失血", "昏迷", "死亡", "蜕变", from, to],
            EvolutionAspect.Personality => ["性格", "情绪", "反应", from, to],
            EvolutionAspect.Motivation => ["动机", "目标", "抉择", from, to],
            EvolutionAspect.Relationship => ["结盟", "背叛", "对峙", "决裂", from, to],
            EvolutionAspect.Ability => ["功法", "招式", "突破", "反噬", from, to],
            EvolutionAspect.Possession => ["法宝", "器物", "易主", from, to],
            EvolutionAspect.Location => ["环境", "建筑", "天气", from, to],
            EvolutionAspect.Knowledge => ["秘密", "误解", "揭示", from, to],
            EvolutionAspect.Behavior => ["习惯", "动作", from, to],
            _ => [from, to],
        };

    private static (
        MaterialNeed Need,
        IReadOnlyList<string> Kinds,
        IReadOnlyList<string?> Terms
    ) PlannedMapping(PlannedDelta planned) =>
        planned.Kind switch
        {
            PlannedDeltaKind.StateChange => (
                AspectNeed(planned.Aspect),
                AspectKinds(planned.Aspect),
                AspectTerms(planned.Aspect, planned.FromSummary, planned.ToSummary)
            ),
            PlannedDeltaKind.RelationChange => (
                MaterialNeed.Strong,
                ["character", "plot_beat"],
                ["关系", "对话", "冲突", planned.FromSummary, planned.ToSummary]
            ),
            PlannedDeltaKind.RuleTrigger => (
                MaterialNeed.Strong,
                ["setting", "reference", "plot_beat"],
                ["规则", "代价", "禁忌", planned.Summary]
            ),
            PlannedDeltaKind.Action => (
                MaterialNeed.Optional,
                ["technique", "trivia"],
                ["动作", "战斗", "礼仪", "医术", planned.ActionType, planned.Summary]
            ),
            PlannedDeltaKind.PlotlineReveal => (
                MaterialNeed.Strong,
                ["plot_beat", "trivia"],
                ["伏笔", "揭示", "回收", "反转", planned.Summary]
            ),
            _ => (MaterialNeed.None, [], []),
        };
}
