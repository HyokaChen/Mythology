using System.Globalization;
using System.Text.Json;
using Core.Shared;

namespace Core.Data;

/// <summary>
/// 本体字段分类（唯一真源）：把「实体类型 + YAML 字段」映射到六要素。
/// 字段名必须与 <see cref="FileNovelStore"/> 写出的 dict 键一致（kebab-case）。
/// </summary>
public static class OntologyFieldMap
{
    /// <summary>字段分类结果。</summary>
    public readonly record struct Classification(OntologyElement Element, EvolutionAspect? Aspect);

    /// <summary>按实体类型与字段分类；未命中一律视为对象字段。</summary>
    public static Classification Classify(EntityType type, string field) =>
        (type, field) switch
        {
            (EntityType.Character, "status") => new(OntologyElement.State, EvolutionAspect.Status),
            (EntityType.Character, "current-location-id") => new(
                OntologyElement.State,
                EvolutionAspect.Location
            ),
            (EntityType.Character, "metrics") => new(OntologyElement.State, EvolutionAspect.Other),
            (EntityType.Character, "faction-id") => new(
                OntologyElement.Relation,
                EvolutionAspect.Relationship
            ),

            (EntityType.Faction, "status") => new(OntologyElement.State, EvolutionAspect.Status),
            (EntityType.Faction, "members" or "leader" or "headquarters-id") => new(
                OntologyElement.Relation,
                EvolutionAspect.Relationship
            ),
            (EntityType.Faction, "metrics") => new(OntologyElement.State, EvolutionAspect.Other),

            (EntityType.Location, "status") => new(OntologyElement.State, EvolutionAspect.Status),
            (EntityType.Location, "parent-id" or "adjacent-locations") => new(
                OntologyElement.Relation,
                EvolutionAspect.Location
            ),
            (EntityType.Location, "metrics") => new(OntologyElement.State, EvolutionAspect.Other),

            (EntityType.Item, "status") => new(OntologyElement.State, EvolutionAspect.Status),
            (EntityType.Item, "location-id") => new(
                OntologyElement.State,
                EvolutionAspect.Location
            ),
            (EntityType.Item, "owner-id" or "ownership-periods") => new(
                OntologyElement.Relation,
                EvolutionAspect.Possession
            ),
            (EntityType.Item, "metrics") => new(OntologyElement.State, EvolutionAspect.Other),

            (EntityType.Plotline, "status") => new(OntologyElement.State, EvolutionAspect.Status),
            (EntityType.Plotline, "planned-reveal-chapter-id" or "resolved-at-chapter-id") => new(
                OntologyElement.State,
                EvolutionAspect.Other
            ),

            (
                EntityType.Scene,
                "chapter-id"
                    or "location-id"
                    or "participant-ids"
                    or "plotline-ids"
                    or "item-ids"
            ) => new(OntologyElement.Relation, EvolutionAspect.Relationship),
            (EntityType.Scene, "planned-deltas") => new(OntologyElement.Action, null),

            (EntityType.Chapter, "volume-id" or "plotline-ids" or "character-ids") => new(
                OntologyElement.Relation,
                EvolutionAspect.Relationship
            ),
            (EntityType.Chapter, "status") => new(OntologyElement.State, EvolutionAspect.Other),

            (EntityType.Volume, "plotline-ids" or "character-ids") => new(
                OntologyElement.Relation,
                EvolutionAspect.Relationship
            ),

            (EntityType.Relationship or EntityType.FactionRelationship, _) => new(
                OntologyElement.Relation,
                EvolutionAspect.Relationship
            ),
            (EntityType.WorldRule, _) => new(OntologyElement.Rule, null),
            (EntityType.TimelineEvent, _) => new(OntologyElement.Action, null),
            _ => new(OntologyElement.Object, null),
        };

    /// <summary>创建/删除的默认要素（字段级更新走 <see cref="Classify"/>）。</summary>
    public static OntologyElement DefaultElement(EntityType type) =>
        type switch
        {
            EntityType.Relationship or EntityType.FactionRelationship => OntologyElement.Relation,
            EntityType.WorldRule => OntologyElement.Rule,
            EntityType.TimelineEvent => OntologyElement.Action,
            _ => OntologyElement.Object,
        };

    /// <summary>YAML 值 → 可比较、可展示的字符串。</summary>
    public static string FormatValue(object? value) =>
        value switch
        {
            null => "",
            string s => s,
            bool b => b ? "true" : "false",
            IEnumerable<string> list => list.Any() ? string.Join("、", list) : "",
            IDictionary<string, double> map => string.Join(
                "、",
                map.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => $"{kv.Key}={kv.Value.ToString(CultureInfo.InvariantCulture)}")
            ),
            System.Collections.IDictionary => JsonSerializer.Serialize(
                value,
                JsonHelper.JsonOptions
            ),
            IEnumerable<object?> list => list.Cast<object?>().Any()
                ? JsonSerializer.Serialize(list, JsonHelper.JsonOptions)
                : "",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        };
}
