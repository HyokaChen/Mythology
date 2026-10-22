using Core.Data;

namespace Core.Evolution;

/// <summary>
/// 已发布引用守卫：实体被已发布章节/场景/已生效状态变化引用时禁止删除，
/// 必须先撤回相关章节或修正引用，保证已发布叙事不会出现悬空引用。
/// </summary>
public static class PublishedReferenceGuard
{
    public const string Message = "实体被已发布章节引用，请先撤回相关章节。";

    /// <summary>命中已发布引用时抛 <see cref="InvalidOperationException"/>；抑制位下跳过。</summary>
    public static async Task EnsureCanDeleteAsync(
        INovelStore store,
        string novelId,
        EntityType entityType,
        string entityId,
        CancellationToken ct = default
    )
    {
        if (OntologyWriteContext.IsPublishedReferenceGuardSuppressed)
            return;
        if (string.IsNullOrWhiteSpace(novelId) || string.IsNullOrWhiteSpace(entityId))
            return;

        if (await IsReferencedByPublishedAsync(store, novelId, entityType, entityId, ct))
            throw new InvalidOperationException(Message);
    }

    private static async Task<bool> IsReferencedByPublishedAsync(
        INovelStore store,
        string novelId,
        EntityType entityType,
        string entityId,
        CancellationToken ct
    )
    {
        var chapters = await store.GetChapters(novelId, ct);
        var published = chapters.Where(c => c.Status == ChapterStatus.Published).ToList();
        if (published.Count == 0)
            return false;
        var publishedIds = published.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

        // 出场记录
        var appearances = await store.GetAppearances(novelId, ct);
        if (
            appearances.Any(a =>
                a.EntityType == entityType
                && string.Equals(a.EntityId, entityId, StringComparison.Ordinal)
                && publishedIds.Contains(a.ChapterId)
            )
        )
            return true;

        // 已生效的「叙事」状态变化（设定变更 ChapterId=="" 不绑定任何已发布章节，不构成引用）
        var changes = await store.GetEntityStateChanges(novelId, ct);
        if (
            changes.Any(c =>
                c.Lifecycle == ChangeLifecycle.Committed
                && !string.IsNullOrEmpty(c.ChapterId)
                && c.TargetEntityType == entityType
                && string.Equals(c.TargetId, entityId, StringComparison.Ordinal)
            )
        )
            return true;

        // 已发布章节的场景
        var scenes = await store.GetScenesByNovel(novelId, ct);
        var publishedScenes = scenes
            .Where(s => s.ChapterId is not null && publishedIds.Contains(s.ChapterId))
            .ToList();

        switch (entityType)
        {
            case EntityType.Character:
            {
                if (
                    publishedScenes.Any(s =>
                        s.ParticipantIds.Contains(entityId, StringComparer.Ordinal)
                    )
                )
                    return true;
                if (published.Any(c => c.CharacterIds.Contains(entityId, StringComparer.Ordinal)))
                    return true;
                var factions = await store.GetFactions(novelId, ct);
                if (
                    factions.Any(f =>
                        string.Equals(f.Leader, entityId, StringComparison.Ordinal)
                        || f.Members.Contains(entityId, StringComparer.Ordinal)
                    )
                )
                    return true;
                var relationships = await store.GetRelationshipsByNovel(novelId, ct);
                return relationships.Any(r =>
                    string.Equals(r.CharacterAId, entityId, StringComparison.Ordinal)
                    || string.Equals(r.CharacterBId, entityId, StringComparison.Ordinal)
                );
            }
            case EntityType.Location:
            {
                if (
                    publishedScenes.Any(s =>
                        string.Equals(s.LocationId, entityId, StringComparison.Ordinal)
                    )
                )
                    return true;
                var factions = await store.GetFactions(novelId, ct);
                if (
                    factions.Any(f =>
                        string.Equals(f.HeadquartersId, entityId, StringComparison.Ordinal)
                    )
                )
                    return true;
                var characters = await store.GetCharacters(novelId, ct);
                if (
                    characters.Any(c =>
                        string.Equals(c.CurrentLocationId, entityId, StringComparison.Ordinal)
                    )
                )
                    return true;
                var items = await store.GetItems(novelId, ct);
                if (items.Any(i => string.Equals(i.LocationId, entityId, StringComparison.Ordinal)))
                    return true;
                var locations = await store.GetLocations(novelId, ct);
                return locations.Any(l =>
                    string.Equals(l.ParentId, entityId, StringComparison.Ordinal)
                );
            }
            case EntityType.Item:
            {
                if (publishedScenes.Any(s => s.ItemIds.Contains(entityId, StringComparer.Ordinal)))
                    return true;
                return false;
            }
            case EntityType.Plotline:
            {
                if (published.Any(c => c.PlotlineIds.Contains(entityId, StringComparer.Ordinal)))
                    return true;
                return publishedScenes.Any(s =>
                    s.PlotlineIds.Contains(entityId, StringComparer.Ordinal)
                );
            }
            case EntityType.Faction:
            {
                var characters = await store.GetCharacters(novelId, ct);
                if (
                    characters.Any(c =>
                        string.Equals(c.FactionId, entityId, StringComparison.Ordinal)
                    )
                )
                    return true;
                var items = await store.GetItems(novelId, ct);
                return items.Any(i => string.Equals(i.OwnerId, entityId, StringComparison.Ordinal));
            }
            default:
                return false;
        }
    }
}
