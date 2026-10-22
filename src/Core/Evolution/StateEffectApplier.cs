using Core.Data;

namespace Core.Evolution;

/// <summary>状态效果应用结果。</summary>
public sealed record StateEffectApplyResult(
    IReadOnlyList<string> Warnings,
    IReadOnlyList<Entity> Changed
);

/// <summary>
/// 通用状态效果应用器：把 <see cref="StateEffect"/> 逐条落到具体实体，最后统一执行一致性规则
/// （组织成员、道具所有权时窗、首领入会、情节线解决章、组织覆灭、地点毁灭）。
/// 不支持的类型/字段由预检拦截；运行时遇到只记 warning，不半途抛异常。
/// </summary>
public sealed class StateEffectApplier
{
    private readonly INovelStore _store;
    private readonly Novel _novel;
    private readonly Chapter _chapter;
    private readonly Dictionary<(EntityType Type, string Id), Entity> _cache = new();
    private readonly Dictionary<(EntityType Type, string Id), EntityState> _before = new();
    private readonly List<string> _warnings = [];
    private Action<EntityType, Entity>? _onBeforeModify;

    private sealed record EntityState(
        string? OldFactionId,
        string? OldOwnerId,
        IReadOnlyList<ItemOwnershipSpan> OwnershipPeriods
    );

    private StateEffectApplier(INovelStore store, Novel novel, Chapter chapter)
    {
        _store = store;
        _novel = novel;
        _chapter = chapter;
    }

    /// <summary>应用全部效果并保存被修改的实体（每个实体只保存一次）。</summary>
    public static async Task<StateEffectApplyResult> ApplyAllAsync(
        INovelStore store,
        Novel novel,
        Chapter chapter,
        IReadOnlyList<StateEffect> effects,
        CancellationToken ct = default,
        Action<EntityType, Entity>? onBeforeModify = null
    )
    {
        var applier = new StateEffectApplier(store, novel, chapter)
        {
            _onBeforeModify = onBeforeModify,
        };
        foreach (var effect in effects)
        {
            ct.ThrowIfCancellationRequested();
            await applier.ApplyOneAsync(effect, ct);
        }
        await applier.ApplyConsistencyAsync(ct);
        return new StateEffectApplyResult(applier._warnings, applier.ChangedEntities());
    }

    private List<Entity> ChangedEntities() =>
        _cache.Where(kv => _before.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();

    private async Task ApplyOneAsync(StateEffect effect, CancellationToken ct)
    {
        var normalized = StateEffectValidator.Normalize(effect);
        var entity = await LoadAsync(normalized.TargetType, normalized.TargetId, ct);
        if (entity is null)
        {
            _warnings.Add(
                $"状态效果目标不存在：{normalized.TargetType.ToKebabCaseString()} {normalized.TargetId}"
            );
            return;
        }

        if (!_before.ContainsKey((normalized.TargetType, normalized.TargetId)))
        {
            _before[(normalized.TargetType, normalized.TargetId)] = Snapshot(entity);
            _onBeforeModify?.Invoke(normalized.TargetType, entity);
        }

        var applied = entity switch
        {
            Character character => ApplyCharacter(character, normalized),
            Faction faction => ApplyFaction(faction, normalized),
            Item item => ApplyItem(item, normalized),
            Location location => ApplyLocation(location, normalized),
            Plotline plotline => ApplyPlotline(plotline, normalized),
            Relationship relationship => ApplyRelationship(relationship, normalized),
            SceneDefinition scene => ApplyScene(scene, normalized),
            _ => false,
        };
        if (!applied)
        {
            _warnings.Add(
                $"不支持的状态效果：{normalized.TargetType.ToKebabCaseString()}.{normalized.Field}"
            );
        }
    }

    private async Task<Entity?> LoadAsync(EntityType type, string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        if (_cache.TryGetValue((type, id), out var cached))
            return cached;

        Entity? entity = type switch
        {
            EntityType.Character => await _store.GetCharacter(_novel.Id, id, ct),
            EntityType.Faction => await _store.GetFaction(_novel.Id, id, ct),
            EntityType.Item => await _store.GetItem(_novel.Id, id, ct),
            EntityType.Location => await _store.GetLocation(_novel.Id, id, ct),
            EntityType.Plotline => await _store.GetPlotline(_novel.Id, id, ct),
            EntityType.Relationship => await _store.GetRelationship(_novel.Id, id, ct),
            EntityType.Scene => await _store.GetScene(_novel.Id, id, ct),
            _ => null,
        };
        if (entity is not null)
            _cache[(type, id)] = entity;
        return entity;
    }

    private async Task<Entity?> LoadAsync(
        EntityType type,
        string id,
        bool allowMissing,
        CancellationToken ct
    )
    {
        var entity = await LoadAsync(type, id, ct);
        if (entity is null && !allowMissing)
            _warnings.Add($"引用实体不存在：{type.ToKebabCaseString()} {id}");
        return entity;
    }

    private static EntityState Snapshot(Entity entity) =>
        entity switch
        {
            Character c => new EntityState(c.FactionId, null, []),
            Item i => new EntityState(null, i.OwnerId, i.OwnershipPeriods.ToList()),
            _ => new EntityState(null, null, []),
        };

    // ── 逐字段应用 ──

    private static bool ApplyCharacter(Character c, StateEffect e)
    {
        switch (e.Field)
        {
            case "status":
                if (
                    !StateEffectValidator.TryParseStatus(
                        EntityType.Character,
                        e.Value ?? "",
                        out var s
                    )
                )
                    return false;
                c.Status = (CharacterStatus)s;
                return true;
            case "faction-id":
                c.FactionId =
                    e.Operation == StateEffectOperation.Clear ? null : NullIfEmpty(e.Value);
                return true;
            case "current-location-id":
                c.CurrentLocationId =
                    e.Operation == StateEffectOperation.Clear ? null : NullIfEmpty(e.Value);
                return true;
            case "role":
                if (
                    !StateEffectValidator.TryParseEnumValue<CharacterRole>(
                        e.Value ?? "",
                        out var role
                    )
                )
                    return false;
                c.Role = role;
                return true;
            case "tags":
                ApplyList(c.Tags, e);
                return true;
            case "abilities":
                c.Abilities = e.Value ?? "";
                return true;
            case "personality":
                c.Personality = e.Value ?? "";
                return true;
            case "motivation":
                c.Motivation = e.Value ?? "";
                return true;
            case "background":
                c.Background = e.Value ?? "";
                return true;
            case "appearance":
                c.Appearance = e.Value ?? "";
                return true;
            case "weakness":
                c.Weakness = e.Value ?? "";
                return true;
            case "habits":
                c.Habits = e.Value ?? "";
                return true;
            case "age":
                c.Age = e.Value ?? "";
                return true;
            case "notes":
                c.Notes = e.Value ?? "";
                return true;
            default:
                return ApplyMetric(c.Metrics, e);
        }
    }

    private static bool ApplyFaction(Faction f, StateEffect e)
    {
        switch (e.Field)
        {
            case "status":
                if (
                    !StateEffectValidator.TryParseStatus(
                        EntityType.Faction,
                        e.Value ?? "",
                        out var s
                    )
                )
                    return false;
                f.Status = (FactionStatus)s;
                return true;
            case "leader":
                f.Leader = e.Operation == StateEffectOperation.Clear ? "" : e.Value ?? "";
                return true;
            case "headquarters-id":
                f.HeadquartersId =
                    e.Operation == StateEffectOperation.Clear ? null : NullIfEmpty(e.Value);
                return true;
            case "members":
                ApplyList(f.Members, e);
                return true;
            case "type":
                if (
                    !StateEffectValidator.TryParseEnumValue<FactionType>(
                        e.Value ?? "",
                        out var type
                    )
                )
                    return false;
                f.Type = type;
                return true;
            case "goal":
                f.Goal = e.Value ?? "";
                return true;
            case "description":
                f.Description = e.Value ?? "";
                return true;
            default:
                return ApplyMetric(f.Metrics, e);
        }
    }

    private static bool ApplyItem(Item item, StateEffect e)
    {
        switch (e.Field)
        {
            case "status":
                if (!StateEffectValidator.TryParseStatus(EntityType.Item, e.Value ?? "", out var s))
                    return false;
                item.Status = (ItemStatus)s;
                return true;
            case "owner-id":
                item.OwnerId =
                    e.Operation == StateEffectOperation.Clear ? null : NullIfEmpty(e.Value);
                return true;
            case "location-id":
                item.LocationId =
                    e.Operation == StateEffectOperation.Clear ? null : NullIfEmpty(e.Value);
                return true;
            case "type":
                if (!StateEffectValidator.TryParseEnumValue<ItemType>(e.Value ?? "", out var type))
                    return false;
                item.Type = type;
                return true;
            case "description":
                item.Description = e.Value ?? "";
                return true;
            default:
                return ApplyMetric(item.Metrics, e);
        }
    }

    private static bool ApplyLocation(Location location, StateEffect e)
    {
        switch (e.Field)
        {
            case "status":
                if (
                    !StateEffectValidator.TryParseStatus(
                        EntityType.Location,
                        e.Value ?? "",
                        out var s
                    )
                )
                    return false;
                location.Status = (LocationStatus)s;
                return true;
            case "parent-id":
                location.ParentId =
                    e.Operation == StateEffectOperation.Clear ? null : NullIfEmpty(e.Value);
                return true;
            case "type":
                if (
                    !StateEffectValidator.TryParseEnumValue<LocationType>(
                        e.Value ?? "",
                        out var type
                    )
                )
                    return false;
                location.Type = type;
                return true;
            case "atmosphere":
                location.Atmosphere = e.Value ?? "";
                return true;
            case "description":
                location.Description = e.Value ?? "";
                return true;
            default:
                return ApplyMetric(location.Metrics, e);
        }
    }

    private static bool ApplyPlotline(Plotline plotline, StateEffect e)
    {
        switch (e.Field)
        {
            case "status":
                if (
                    !StateEffectValidator.TryParseStatus(
                        EntityType.Plotline,
                        e.Value ?? "",
                        out var s
                    )
                )
                    return false;
                plotline.Status = (PlotlineStatus)s;
                return true;
            case "tension":
                if (
                    !StateEffectValidator.TryParseEnumValue<PlotlineTension>(
                        e.Value ?? "",
                        out var t
                    )
                )
                    return false;
                plotline.Tension = t;
                return true;
            case "planned-reveal-chapter-id":
                plotline.PlannedRevealChapterId =
                    e.Operation == StateEffectOperation.Clear ? null : NullIfEmpty(e.Value);
                return true;
            case "resolved-at-chapter-id":
                plotline.ResolvedAtChapterId =
                    e.Operation == StateEffectOperation.Clear ? null : NullIfEmpty(e.Value);
                return true;
            case "turnpoints":
                plotline.Turnpoints = e.Value ?? "";
                return true;
            case "description":
                plotline.Description = e.Value ?? "";
                return true;
            default:
                return false;
        }
    }

    private static bool ApplyRelationship(Relationship relationship, StateEffect e)
    {
        switch (e.Field)
        {
            case "type":
                if (
                    !StateEffectValidator.TryParseEnumValue<RelationshipType>(
                        e.Value ?? "",
                        out var t
                    )
                )
                    return false;
                relationship.Type = t;
                return true;
            case "strength":
                if (
                    !StateEffectValidator.TryParseEnumValue<RelationshipStrength>(
                        e.Value ?? "",
                        out var s
                    )
                )
                    return false;
                relationship.Strength = s;
                return true;
            case "description":
                relationship.Description = e.Value ?? "";
                return true;
            case "since-chapter-id":
                relationship.SinceChapterId =
                    e.Operation == StateEffectOperation.Clear ? null : NullIfEmpty(e.Value);
                return true;
            case "until-chapter-id":
                relationship.UntilChapterId =
                    e.Operation == StateEffectOperation.Clear ? null : NullIfEmpty(e.Value);
                return true;
            default:
                return false;
        }
    }

    private static bool ApplyScene(SceneDefinition scene, StateEffect e)
    {
        switch (e.Field)
        {
            case "location-id":
                scene.LocationId =
                    e.Operation == StateEffectOperation.Clear ? null : NullIfEmpty(e.Value);
                return true;
            case "time-description":
                scene.TimeDescription = e.Value ?? "";
                return true;
            case "participant-ids":
                ApplyList(scene.ParticipantIds, e);
                return true;
            case "plotline-ids":
                ApplyList(scene.PlotlineIds, e);
                return true;
            case "item-ids":
                ApplyList(scene.ItemIds, e);
                return true;
            case "scene-type":
                if (!StateEffectValidator.TryParseEnumValue<SceneType>(e.Value ?? "", out var t))
                    return false;
                scene.SceneType = t;
                return true;
            case "conflict":
                scene.Conflict = e.Value ?? "";
                return true;
            case "goal":
                scene.Goal = e.Value ?? "";
                return true;
            case "emotional-arc":
                scene.EmotionalArc = e.Value ?? "";
                return true;
            case "notes":
                scene.Notes = e.Value ?? "";
                return true;
            default:
                return false;
        }
    }

    private static bool ApplyMetric(Dictionary<string, double> metrics, StateEffect e)
    {
        if (!e.Field.StartsWith("metrics:", StringComparison.Ordinal))
            return false;
        var key = e.Field["metrics:".Length..];
        if (key.Length == 0)
            return false;
        if (
            !double.TryParse(
                e.Value ?? "",
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value
            )
        )
            return false;
        if (e.Operation == StateEffectOperation.Increment)
            metrics[key] = metrics.GetValueOrDefault(key) + value;
        else
            metrics[key] = value;
        return true;
    }

    private static void ApplyList(List<string> list, StateEffect e)
    {
        switch (e.Operation)
        {
            case StateEffectOperation.Clear:
                list.Clear();
                break;
            case StateEffectOperation.Add:
                if (!string.IsNullOrWhiteSpace(e.Value) && !list.Contains(e.Value))
                    list.Add(e.Value);
                break;
            case StateEffectOperation.Remove:
                if (!string.IsNullOrWhiteSpace(e.Value))
                    list.RemoveAll(item => string.Equals(item, e.Value, StringComparison.Ordinal));
                break;
            case StateEffectOperation.Set:
                list.Clear();
                if (!string.IsNullOrWhiteSpace(e.Value))
                    list.AddRange(
                        e.Value!.Split(
                            ',',
                            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                        )
                    );
                break;
        }
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ── 一致性规则（全部效果应用后统一执行） ──

    private async Task ApplyConsistencyAsync(CancellationToken ct)
    {
        // 组织解散 / 地点毁灭：先做实体自身的派生清理，再处理成员集合。
        foreach (var entity in _cache.Values.ToList())
        {
            switch (entity)
            {
                case Faction faction when faction.Status == FactionStatus.Dissolved:
                {
                    faction.Leader = "";
                    faction.HeadquartersId = null;
                    var members = faction.Members.ToList();
                    faction.Members.Clear();
                    foreach (var memberId in members)
                    {
                        if (
                            await LoadAsync(EntityType.Character, memberId, ct) is Character member
                            && string.Equals(member.FactionId, faction.Id, StringComparison.Ordinal)
                        )
                        {
                            member.FactionId = null;
                            _before.TryAdd((EntityType.Character, member.Id), Snapshot(member));
                        }
                    }
                    break;
                }
                case Location location when location.Status == LocationStatus.Destroyed:
                {
                    // 注意：已经加载/修改过的实体必须复用缓存实例，否则会把本次效果覆盖掉。
                    foreach (var stored in await _store.GetCharacters(_novel.Id, ct))
                    {
                        var character = Track(stored);
                        if (
                            string.Equals(
                                character.CurrentLocationId,
                                location.Id,
                                StringComparison.Ordinal
                            )
                        )
                            character.CurrentLocationId = null;
                    }
                    foreach (var stored in await _store.GetItems(_novel.Id, ct))
                    {
                        var item = Track(stored);
                        if (string.Equals(item.LocationId, location.Id, StringComparison.Ordinal))
                            item.LocationId = null;
                    }
                    foreach (var stored in await _store.GetFactions(_novel.Id, ct))
                    {
                        var faction = Track(stored);
                        if (
                            string.Equals(
                                faction.HeadquartersId,
                                location.Id,
                                StringComparison.Ordinal
                            )
                        )
                            faction.HeadquartersId = null;
                    }
                    break;
                }
                case Plotline plotline when plotline.Status == PlotlineStatus.Resolved:
                    if (!string.IsNullOrEmpty(_chapter.Id))
                        plotline.ResolvedAtChapterId ??= _chapter.Id;
                    break;
                case Plotline plotline when plotline.Status == PlotlineStatus.Active:
                    plotline.ResolvedAtChapterId = null;
                    break;
            }
        }

        // 角色换组织：老组织移除成员，新组织加入成员。
        var factionsCache = new Dictionary<string, Faction>(StringComparer.Ordinal);
        foreach (var entity in _cache.Values.ToList())
        {
            if (
                entity is not Character character
                || !_before.TryGetValue((EntityType.Character, character.Id), out var state)
            )
                continue;
            var oldFactionId = state.OldFactionId;
            var newFactionId = character.FactionId;
            if (string.Equals(oldFactionId, newFactionId, StringComparison.Ordinal))
                continue;

            if (!string.IsNullOrEmpty(oldFactionId))
            {
                var oldFaction = await LoadFactionAsync(oldFactionId, factionsCache, ct);
                if (oldFaction is not null)
                    oldFaction.Members.RemoveAll(m =>
                        string.Equals(m, character.Id, StringComparison.Ordinal)
                    );
            }
            if (!string.IsNullOrEmpty(newFactionId))
            {
                var newFaction = await LoadFactionAsync(newFactionId, factionsCache, ct);
                if (newFaction is not null && !newFaction.Members.Contains(character.Id))
                    newFaction.Members.Add(character.Id);
            }
        }

        // 道具换主 / 遗失：关闭旧开放时窗，非空新主追加新时窗。
        foreach (var entity in _cache.Values.ToList())
        {
            if (
                entity is not Item item
                || !_before.TryGetValue((EntityType.Item, item.Id), out var state)
            )
                continue;
            var ownerChanged = !string.Equals(
                state.OldOwnerId,
                item.OwnerId,
                StringComparison.Ordinal
            );
            var lostOrDestroyed = item.Status is ItemStatus.Lost or ItemStatus.Destroyed;
            if (!ownerChanged && !lostOrDestroyed)
                continue;

            if (
                ownerChanged
                && !string.IsNullOrEmpty(item.OwnerId)
                && !string.IsNullOrEmpty(_chapter.Id)
            )
                item.OwnershipPeriods.Add(
                    new ItemOwnershipSpan { OwnerId = item.OwnerId, SinceChapterId = _chapter.Id }
                );
            if ((lostOrDestroyed || ownerChanged) && !string.IsNullOrEmpty(_chapter.Id))
            {
                foreach (
                    var span in item.OwnershipPeriods.Where(s =>
                        s.UntilChapterId is null
                        && (
                            string.IsNullOrEmpty(item.OwnerId)
                            || !string.Equals(s.OwnerId, item.OwnerId, StringComparison.Ordinal)
                        )
                    )
                )
                {
                    span.UntilChapterId = _chapter.Id;
                }
            }
        }

        // 首领必须在成员列表中。
        foreach (var entity in _cache.Values.OfType<Faction>())
        {
            if (
                !string.IsNullOrWhiteSpace(entity.Leader) && !entity.Members.Contains(entity.Leader)
            )
                entity.Members.Add(entity.Leader);
        }

        // 保存全部被修改实体（每个一次）。
        foreach (var key in _before.Keys)
        {
            ct.ThrowIfCancellationRequested();
            if (_cache.TryGetValue(key, out var entity))
                await EntitySaveDispatcher.SaveAsync(_store, entity, ct);
        }
    }

    /// <summary>取缓存实例（已加载过则复用，避免用磁盘旧值覆盖本次修改）；首次出现时记 Before 快照。</summary>
    private T Track<T>(T entity)
        where T : Entity
    {
        var type = entity switch
        {
            Character => EntityType.Character,
            Item => EntityType.Item,
            Faction => EntityType.Faction,
            Location => EntityType.Location,
            _ => EntityType.Character,
        };
        var key = (type, entity.Id);
        if (_cache.TryGetValue(key, out var existing) && existing is T cached)
        {
            // 由 LoadAsync 装入缓存但尚未登记快照的实体（一致性阶段的关联实体）。
            if (_before.TryAdd(key, Snapshot(cached)))
                _onBeforeModify?.Invoke(type, cached);
            return cached;
        }
        if (_before.TryAdd(key, Snapshot(entity)))
            _onBeforeModify?.Invoke(type, entity);
        _cache[key] = entity;
        return entity;
    }

    private async Task<Faction?> LoadFactionAsync(
        string id,
        Dictionary<string, Faction> cache,
        CancellationToken ct
    )
    {
        if (cache.TryGetValue(id, out var cached))
            return cached;
        var loaded = await LoadAsync(EntityType.Faction, id, allowMissing: true, ct) as Faction;
        if (loaded is not null)
        {
            // 必须 Track：否则只在成员列表上做的修改不会进入 _before，永远不会被保存。
            loaded = Track(loaded);
            cache[id] = loaded;
        }
        return loaded;
    }
}
