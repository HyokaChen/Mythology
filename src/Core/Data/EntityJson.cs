using System.Text.Json;
using System.Text.Json.Nodes;
using Core.Shared;

namespace Core.Data;

/// <summary>
/// 通用实体 JSON 快照（变更集 Before/After 与撤回恢复用）。
/// 与 YAML 持久化同构但不落盘：序列化用 kebab-case（<see cref="JsonHelper.JsonOptions"/>）。
/// </summary>
public static class EntityJson
{
    /// <summary>实体 → JSON（保留 created-at/updated-at）。</summary>
    public static string Serialize(Entity entity) =>
        JsonSerializer.Serialize(entity, entity.GetType(), JsonHelper.JsonOptions);

    /// <summary>实体指纹：移除顶层 created-at/updated-at 后的 JSON（比较内容是否被改动用）。</summary>
    public static string Fingerprint(Entity entity)
    {
        var node = JsonNode.Parse(Serialize(entity));
        if (node is JsonObject obj)
        {
            obj.Remove("created-at");
            obj.Remove("updated-at");
            return obj.ToJsonString(JsonHelper.JsonOptions);
        }
        return node?.ToJsonString(JsonHelper.JsonOptions) ?? "";
    }

    /// <summary>JSON → 实体（类型由 <paramref name="type"/> 指定；未知类型返回 null）。</summary>
    public static Entity? Deserialize(EntityType type, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return type switch
            {
                EntityType.Novel => JsonSerializer.Deserialize<Novel>(json, JsonHelper.JsonOptions),
                EntityType.Chapter => JsonSerializer.Deserialize<Chapter>(
                    json,
                    JsonHelper.JsonOptions
                ),
                EntityType.Character => JsonSerializer.Deserialize<Character>(
                    json,
                    JsonHelper.JsonOptions
                ),
                EntityType.Plotline => JsonSerializer.Deserialize<Plotline>(
                    json,
                    JsonHelper.JsonOptions
                ),
                EntityType.Faction => JsonSerializer.Deserialize<Faction>(
                    json,
                    JsonHelper.JsonOptions
                ),
                EntityType.Location => JsonSerializer.Deserialize<Location>(
                    json,
                    JsonHelper.JsonOptions
                ),
                EntityType.Item => JsonSerializer.Deserialize<Item>(json, JsonHelper.JsonOptions),
                EntityType.Relationship => JsonSerializer.Deserialize<Relationship>(
                    json,
                    JsonHelper.JsonOptions
                ),
                EntityType.Scene => JsonSerializer.Deserialize<SceneDefinition>(
                    json,
                    JsonHelper.JsonOptions
                ),
                EntityType.Volume => JsonSerializer.Deserialize<Volume>(
                    json,
                    JsonHelper.JsonOptions
                ),
                EntityType.WorldRule => JsonSerializer.Deserialize<WorldRule>(
                    json,
                    JsonHelper.JsonOptions
                ),
                EntityType.FactionRelationship => JsonSerializer.Deserialize<FactionRelationship>(
                    json,
                    JsonHelper.JsonOptions
                ),
                EntityType.TimelineEvent => JsonSerializer.Deserialize<TimelineEvent>(
                    json,
                    JsonHelper.JsonOptions
                ),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>状态变化指纹：移除 updated-at（每次整章写回都会刷新该字段）后的 JSON。</summary>
    public static string StateChangeFingerprint(EntityStateChange change)
    {
        var node = JsonNode.Parse(SerializeStateChange(change));
        if (node is JsonObject obj)
        {
            obj.Remove("updated-at");
            return obj.ToJsonString(JsonHelper.JsonOptions);
        }
        return node?.ToJsonString(JsonHelper.JsonOptions) ?? "";
    }

    /// <summary>状态变化记录 → JSON。</summary>
    public static string SerializeStateChange(EntityStateChange change) =>
        JsonSerializer.Serialize(change, JsonHelper.JsonOptions);

    /// <summary>JSON → 状态变化记录。</summary>
    public static EntityStateChange? DeserializeStateChange(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<EntityStateChange>(json, JsonHelper.JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>按具体实体类型分派保存/删除（变更集回滚与联动应用用）。</summary>
public static class EntitySaveDispatcher
{
    public static Task SaveAsync(
        INovelStore store,
        Entity entity,
        CancellationToken ct = default
    ) =>
        entity switch
        {
            Novel novel => store.SaveNovel(novel, ct),
            Chapter chapter => store.SaveChapter(chapter, ct),
            Character character => store.SaveCharacter(character, ct),
            Plotline plotline => store.SavePlotline(plotline, ct),
            Faction faction => store.SaveFaction(faction, ct),
            Location location => store.SaveLocation(location, ct),
            Item item => store.SaveItem(item, ct),
            Relationship relationship => store.SaveRelationship(relationship, ct),
            SceneDefinition scene => store.SaveScene(scene, ct),
            Volume volume => store.SaveVolume(volume, ct),
            FactionRelationship rel => store.SaveFactionRelationship(rel, ct),
            TimelineEvent evt => store.SaveTimelineEvent(evt, ct),
            WorldRule rule => store.SaveWorldRule(
                string.IsNullOrWhiteSpace(rule.NovelId) ? "" : rule.NovelId,
                rule,
                ct
            ),
            _ => throw new NotSupportedException($"不支持的实体保存类型：{entity.GetType().Name}"),
        };

    public static Task DeleteAsync(
        INovelStore store,
        string novelId,
        EntityType type,
        string id,
        CancellationToken ct = default
    ) =>
        type switch
        {
            EntityType.Chapter => store.DeleteChapter(novelId, id, ct),
            EntityType.Character => store.DeleteCharacter(novelId, id, ct),
            EntityType.Plotline => store.DeletePlotline(novelId, id, ct),
            EntityType.Faction => store.DeleteFaction(novelId, id, ct),
            EntityType.Location => store.DeleteLocation(novelId, id, ct),
            EntityType.Item => store.DeleteItem(novelId, id, ct),
            EntityType.Relationship => store.DeleteRelationship(novelId, id, ct),
            EntityType.Scene => store.DeleteScene(novelId, id, ct),
            EntityType.Volume => store.DeleteVolume(novelId, id, ct),
            EntityType.WorldRule => store.DeleteWorldRule(novelId, id, ct),
            EntityType.Novel => store.DeleteNovel(id, ct),
            _ => throw new NotSupportedException($"不支持的实体删除类型：{type}"),
        };
}
