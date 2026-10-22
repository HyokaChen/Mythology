using Core.Data;

namespace Core.Evolution;

/// <summary>
/// 联动计划器（纯函数）：把「将生效的状态变化」翻译成自动/可选联动建议。
/// 只识别 <c>field == "status"</c> 的效果；建议只是 StateEffect 形状，真正应用由发布事务完成。
/// </summary>
public static class CascadePlanner
{
    public const string RelationshipEndField = "until-chapter-id";

    public static List<CascadeSuggestion> Plan(
        Novel novel,
        Chapter chapter,
        IReadOnlyList<EntityStateChange> changes,
        IReadOnlyList<SceneDefinition> scenes,
        IReadOnlyList<Chapter> chapters
    )
    {
        var suggestions = new List<CascadeSuggestion>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var change in changes.OrderBy(c => c.Sequence))
        {
            foreach (
                var effect in change.Effects.Where(e =>
                    string.Equals(e.Field, "status", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(e.Value)
                )
            )
            {
                if (
                    !StateEffectValidator.TryParseStatus(
                        effect.TargetType,
                        effect.Value!,
                        out var parsed
                    )
                )
                    continue;

                switch (effect.TargetType)
                {
                    case EntityType.Character when parsed is CharacterStatus.Deceased:
                        PlanCharacterDeceased(
                            novel,
                            chapter,
                            change,
                            effect.TargetId,
                            suggestions,
                            seen
                        );
                        break;
                    case EntityType.Faction when parsed is FactionStatus.Dissolved:
                        PlanFactionDissolved(
                            novel,
                            chapter,
                            change,
                            effect.TargetId,
                            suggestions,
                            seen
                        );
                        break;
                    case EntityType.Item when parsed is ItemStatus.Lost or ItemStatus.Destroyed:
                        PlanItemLost(novel, change, effect.TargetId, suggestions, seen);
                        break;
                    case EntityType.Location when parsed is LocationStatus.Destroyed:
                        PlanLocationDestroyed(
                            novel,
                            chapter,
                            change,
                            effect.TargetId,
                            suggestions,
                            seen,
                            scenes,
                            chapters
                        );
                        break;
                    case EntityType.Plotline when parsed is PlotlineStatus.Resolved:
                        Add(
                            suggestions,
                            seen,
                            SuggestionId(
                                chapter,
                                change,
                                EntityType.Plotline,
                                effect.TargetId,
                                "resolved-at-chapter-id"
                            ),
                            EntityType.Plotline,
                            effect.TargetId,
                            EntityName(novel, EntityType.Plotline, effect.TargetId),
                            "resolved-at-chapter-id",
                            StateEffectOperation.Set,
                            chapter.Id,
                            $"情节线「{EntityName(novel, EntityType.Plotline, effect.TargetId)}」标记为第{chapter.Order}章解决",
                            automatic: true,
                            change.Id
                        );
                        break;
                    case EntityType.Plotline when parsed is PlotlineStatus.Active:
                        Add(
                            suggestions,
                            seen,
                            SuggestionId(
                                chapter,
                                change,
                                EntityType.Plotline,
                                effect.TargetId,
                                "resolved-at-chapter-id"
                            ),
                            EntityType.Plotline,
                            effect.TargetId,
                            EntityName(novel, EntityType.Plotline, effect.TargetId),
                            "resolved-at-chapter-id",
                            StateEffectOperation.Clear,
                            null,
                            $"情节线「{EntityName(novel, EntityType.Plotline, effect.TargetId)}」重新激活，清空解决章",
                            automatic: true,
                            change.Id
                        );
                        break;
                }
            }
        }
        return suggestions;
    }

    private static void PlanCharacterDeceased(
        Novel novel,
        Chapter chapter,
        EntityStateChange change,
        string characterId,
        List<CascadeSuggestion> suggestions,
        HashSet<string> seen
    )
    {
        var name = EntityName(novel, EntityType.Character, characterId);

        // 1. 结束所有涉及该角色的关系
        foreach (
            var relationship in novel.Relationships.Where(r =>
                string.Equals(r.CharacterAId, characterId, StringComparison.Ordinal)
                || string.Equals(r.CharacterBId, characterId, StringComparison.Ordinal)
            )
        )
        {
            // 已在更早章节结束的关系保留原结束章（计划 §4.2）。
            if (!string.IsNullOrEmpty(relationship.UntilChapterId))
                continue;
            Add(
                suggestions,
                seen,
                SuggestionId(
                    chapter,
                    change,
                    EntityType.Relationship,
                    relationship.Id,
                    RelationshipEndField
                ),
                EntityType.Relationship,
                relationship.Id,
                $"关系「{EntityName(novel, EntityType.Character, relationship.CharacterAId)}↔{EntityName(novel, EntityType.Character, relationship.CharacterBId)}」",
                RelationshipEndField,
                StateEffectOperation.Set,
                chapter.Id,
                $"「{name}」死亡，结束该关系",
                automatic: true,
                change.Id
            );
        }

        // 2. 移出所有组织并清空角色归属
        foreach (
            var faction in novel.Factions.Where(f =>
                f.Members.Contains(characterId, StringComparer.Ordinal)
            )
        )
        {
            Add(
                suggestions,
                seen,
                SuggestionId(chapter, change, EntityType.Faction, faction.Id, "members"),
                EntityType.Faction,
                faction.Id,
                faction.Name,
                "members",
                StateEffectOperation.Remove,
                characterId,
                $"「{name}」死亡，移出组织「{faction.Name}」",
                automatic: true,
                change.Id
            );

            if (string.Equals(faction.Leader, characterId, StringComparison.Ordinal))
            {
                Add(
                    suggestions,
                    seen,
                    SuggestionId(chapter, change, EntityType.Faction, faction.Id, "leader"),
                    EntityType.Faction,
                    faction.Id,
                    faction.Name,
                    "leader",
                    StateEffectOperation.Clear,
                    null,
                    $"「{name}」死亡，组织「{faction.Name}」首领空缺",
                    automatic: true,
                    change.Id
                );

                var remaining = faction
                    .Members.Where(m => !string.Equals(m, characterId, StringComparison.Ordinal))
                    .ToList();
                Add(
                    suggestions,
                    seen,
                    SuggestionId(chapter, change, EntityType.Faction, faction.Id, "status"),
                    EntityType.Faction,
                    faction.Id,
                    faction.Name,
                    "status",
                    StateEffectOperation.Set,
                    remaining.Count == 0
                        ? YamlHelperLike(FactionStatus.Dissolved)
                        : YamlHelperLike(FactionStatus.Dormant),
                    remaining.Count == 0
                        ? $"组织「{faction.Name}」失去全部成员，覆灭"
                        : $"组织「{faction.Name}」失去首领，进入蛰伏",
                    automatic: true,
                    change.Id
                );

                if (remaining.Count > 0)
                {
                    Add(
                        suggestions,
                        seen,
                        SuggestionId(chapter, change, EntityType.Faction, faction.Id, "leader:new"),
                        EntityType.Faction,
                        faction.Id,
                        faction.Name,
                        "leader",
                        StateEffectOperation.Set,
                        remaining[0],
                        $"指定「{EntityName(novel, EntityType.Character, remaining[0])}」为「{faction.Name}」新首领",
                        automatic: false,
                        change.Id
                    );
                }
            }
        }

        if (
            novel.Characters.Any(c =>
                string.Equals(c.Id, characterId, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(c.FactionId)
            )
        )
        {
            Add(
                suggestions,
                seen,
                SuggestionId(chapter, change, EntityType.Character, characterId, "faction-id"),
                EntityType.Character,
                characterId,
                name,
                "faction-id",
                StateEffectOperation.Clear,
                null,
                $"「{name}」死亡，清空其组织归属",
                automatic: true,
                change.Id
            );
        }

        // 3. 拥有的道具：清空持有者并遗失
        foreach (
            var item in novel.Items.Where(i =>
                string.Equals(i.OwnerId, characterId, StringComparison.Ordinal)
            )
        )
        {
            Add(
                suggestions,
                seen,
                SuggestionId(chapter, change, EntityType.Item, item.Id, "owner-id"),
                EntityType.Item,
                item.Id,
                item.Name,
                "owner-id",
                StateEffectOperation.Clear,
                null,
                $"「{name}」死亡，「{item.Name}」无主",
                automatic: true,
                change.Id
            );
            Add(
                suggestions,
                seen,
                SuggestionId(chapter, change, EntityType.Item, item.Id, "status"),
                EntityType.Item,
                item.Id,
                item.Name,
                "status",
                StateEffectOperation.Set,
                YamlHelperLike(ItemStatus.Lost),
                $"「{name}」死亡，「{item.Name}」遗失",
                automatic: true,
                change.Id
            );
        }

        // 4. 可选：建立纪念地（需要死亡地点且无同名地点）
        var deathLocationId = change.LocationId;
        if (!string.IsNullOrWhiteSpace(deathLocationId))
        {
            var location = novel.Locations.FirstOrDefault(l =>
                string.Equals(l.Id, deathLocationId, StringComparison.Ordinal)
            );
            if (location is not null)
            {
                var memorialName = $"{name}纪念地";
                if (
                    !novel.Locations.Any(l =>
                        string.Equals(l.Name, memorialName, StringComparison.Ordinal)
                    )
                )
                {
                    Add(
                        suggestions,
                        seen,
                        $"opt:Location:create:{change.Id}",
                        EntityType.Location,
                        "",
                        memorialName,
                        "name",
                        StateEffectOperation.Set,
                        memorialName,
                        $"在「{location.Name}」建立「{memorialName}」",
                        automatic: false,
                        change.Id,
                        isCreate: true
                    );
                }
            }
        }

        // 5. 可选：仍有未解决且本章推进的情节线 → 放弃
        foreach (
            var plotline in novel.Plotlines.Where(p =>
                p.Status == PlotlineStatus.Active
                && p.ResolvedAtChapterId is null
                && change.PlotlineIds.Contains(p.Id, StringComparer.Ordinal)
            )
        )
        {
            Add(
                suggestions,
                seen,
                SuggestionId(chapter, change, EntityType.Plotline, plotline.Id, "status"),
                EntityType.Plotline,
                plotline.Id,
                plotline.Name,
                "status",
                StateEffectOperation.Set,
                YamlHelperLike(PlotlineStatus.Abandoned),
                $"「{name}」死亡，情节线「{plotline.Name}」标记放弃",
                automatic: false,
                change.Id
            );
        }
    }

    private static void PlanFactionDissolved(
        Novel novel,
        Chapter chapter,
        EntityStateChange change,
        string factionId,
        List<CascadeSuggestion> suggestions,
        HashSet<string> seen
    )
    {
        var faction = novel.Factions.FirstOrDefault(f =>
            string.Equals(f.Id, factionId, StringComparison.Ordinal)
        );
        var name = faction?.Name ?? factionId;

        Add(
            suggestions,
            seen,
            SuggestionId(chapter, change, EntityType.Faction, factionId, "leader"),
            EntityType.Faction,
            factionId,
            name,
            "leader",
            StateEffectOperation.Clear,
            null,
            $"组织「{name}」覆灭，清空首领",
            automatic: true,
            change.Id
        );
        Add(
            suggestions,
            seen,
            SuggestionId(chapter, change, EntityType.Faction, factionId, "members"),
            EntityType.Faction,
            factionId,
            name,
            "members",
            StateEffectOperation.Clear,
            null,
            $"组织「{name}」覆灭，清空成员",
            automatic: true,
            change.Id
        );
        Add(
            suggestions,
            seen,
            SuggestionId(chapter, change, EntityType.Faction, factionId, "headquarters-id"),
            EntityType.Faction,
            factionId,
            name,
            "headquarters-id",
            StateEffectOperation.Clear,
            null,
            $"组织「{name}」覆灭，清空总部",
            automatic: true,
            change.Id
        );

        foreach (
            var character in novel.Characters.Where(c =>
                string.Equals(c.FactionId, factionId, StringComparison.Ordinal)
            )
        )
        {
            Add(
                suggestions,
                seen,
                SuggestionId(chapter, change, EntityType.Character, character.Id, "faction-id"),
                EntityType.Character,
                character.Id,
                character.Name,
                "faction-id",
                StateEffectOperation.Clear,
                null,
                $"组织「{name}」覆灭，清空「{character.Name}」的组织归属",
                automatic: true,
                change.Id
            );
        }

        var ownedItems = novel
            .Items.Where(i => string.Equals(i.OwnerId, factionId, StringComparison.Ordinal))
            .ToList();
        if (ownedItems.Count > 0)
        {
            foreach (var item in ownedItems)
            {
                Add(
                    suggestions,
                    seen,
                    SuggestionId(chapter, change, EntityType.Item, item.Id, "status"),
                    EntityType.Item,
                    item.Id,
                    item.Name,
                    "status",
                    StateEffectOperation.Set,
                    YamlHelperLike(ItemStatus.Lost),
                    $"组织「{name}」覆灭，其道具「{item.Name}」遗失",
                    automatic: false,
                    change.Id
                );
            }
        }
    }

    private static void PlanItemLost(
        Novel novel,
        EntityStateChange change,
        string itemId,
        List<CascadeSuggestion> suggestions,
        HashSet<string> seen
    )
    {
        var item = novel.Items.FirstOrDefault(i =>
            string.Equals(i.Id, itemId, StringComparison.Ordinal)
        );
        if (item?.OwnerId is null)
            return;
        Add(
            suggestions,
            seen,
            $"auto:Item:{itemId}:owner-id:{change.Id}",
            EntityType.Item,
            itemId,
            item.Name,
            "owner-id",
            StateEffectOperation.Clear,
            null,
            $"「{item.Name}」遗失/损毁，清空持有者",
            automatic: true,
            change.Id
        );
    }

    private static void PlanLocationDestroyed(
        Novel novel,
        Chapter chapter,
        EntityStateChange change,
        string locationId,
        List<CascadeSuggestion> suggestions,
        HashSet<string> seen,
        IReadOnlyList<SceneDefinition> scenes,
        IReadOnlyList<Chapter> chapters
    )
    {
        var location = novel.Locations.FirstOrDefault(l =>
            string.Equals(l.Id, locationId, StringComparison.Ordinal)
        );
        var name = location?.Name ?? locationId;

        foreach (
            var character in novel.Characters.Where(c =>
                string.Equals(c.CurrentLocationId, locationId, StringComparison.Ordinal)
            )
        )
        {
            Add(
                suggestions,
                seen,
                SuggestionId(
                    chapter,
                    change,
                    EntityType.Character,
                    character.Id,
                    "current-location-id"
                ),
                EntityType.Character,
                character.Id,
                character.Name,
                "current-location-id",
                StateEffectOperation.Clear,
                null,
                $"地点「{name}」毁灭，清空「{character.Name}」的所在地",
                automatic: true,
                change.Id
            );
        }
        foreach (
            var item in novel.Items.Where(i =>
                string.Equals(i.LocationId, locationId, StringComparison.Ordinal)
            )
        )
        {
            Add(
                suggestions,
                seen,
                SuggestionId(chapter, change, EntityType.Item, item.Id, "location-id"),
                EntityType.Item,
                item.Id,
                item.Name,
                "location-id",
                StateEffectOperation.Clear,
                null,
                $"地点「{name}」毁灭，清空「{item.Name}」的所在地",
                automatic: true,
                change.Id
            );
        }
        foreach (
            var faction in novel.Factions.Where(f =>
                string.Equals(f.HeadquartersId, locationId, StringComparison.Ordinal)
            )
        )
        {
            Add(
                suggestions,
                seen,
                SuggestionId(chapter, change, EntityType.Faction, faction.Id, "headquarters-id"),
                EntityType.Faction,
                faction.Id,
                faction.Name,
                "headquarters-id",
                StateEffectOperation.Clear,
                null,
                $"地点「{name}」毁灭，清空组织「{faction.Name}」的总部",
                automatic: true,
                change.Id
            );
        }

        // 可选：未来场景仍把该地点作为场景地点 → 清空
        var chapterOrderById = chapters.ToDictionary(
            c => c.Id,
            c => c.Order,
            StringComparer.Ordinal
        );
        foreach (
            var scene in scenes.Where(s =>
                string.Equals(s.LocationId, locationId, StringComparison.Ordinal)
            )
        )
        {
            if (
                scene.ChapterId is null
                || !chapterOrderById.TryGetValue(scene.ChapterId, out var order)
                || order <= chapter.Order
            )
                continue;
            Add(
                suggestions,
                seen,
                SuggestionId(chapter, change, EntityType.Scene, scene.Id, "location-id"),
                EntityType.Scene,
                scene.Id,
                scene.Title,
                "location-id",
                StateEffectOperation.Clear,
                null,
                $"地点「{name}」毁灭，清空未来场景「{scene.Title}」的地点",
                automatic: false,
                change.Id
            );
        }
    }

    private static string SuggestionId(
        Chapter chapter,
        EntityStateChange change,
        EntityType type,
        string targetId,
        string field
    ) => $"auto:{type}:{targetId}:{field}:{change.Id}";

    private static void Add(
        List<CascadeSuggestion> suggestions,
        HashSet<string> seen,
        string id,
        EntityType type,
        string targetId,
        string targetName,
        string field,
        StateEffectOperation operation,
        string? value,
        string summary,
        bool automatic,
        string sourceChangeId,
        bool isCreate = false
    )
    {
        var normalizedId = id;
        if (!seen.Add(normalizedId))
            return;
        suggestions.Add(
            new CascadeSuggestion(
                normalizedId,
                type,
                targetId,
                targetName,
                field,
                operation,
                value,
                summary,
                DefaultSelected: automatic,
                IsAutomatic: automatic,
                SourceChangeId: sourceChangeId,
                IsCreate: isCreate
            )
        );
    }

    /// <summary>枚举 → YAML 存储值（状态效果 Value 与 YAML 各枚举一致：小写）。</summary>
    private static string YamlHelperLike<T>(T value)
        where T : struct, Enum => value.ToString().ToLowerInvariant();

    private static string EntityName(Novel novel, EntityType type, string id) =>
        type switch
        {
            EntityType.Character => novel.Characters.FirstOrDefault(c => c.Id == id)?.Name ?? id,
            EntityType.Faction => novel.Factions.FirstOrDefault(f => f.Id == id)?.Name ?? id,
            EntityType.Location => novel.Locations.FirstOrDefault(l => l.Id == id)?.Name ?? id,
            EntityType.Item => novel.Items.FirstOrDefault(i => i.Id == id)?.Name ?? id,
            EntityType.Plotline => novel.Plotlines.FirstOrDefault(p => p.Id == id)?.Name ?? id,
            _ => id,
        };
}
