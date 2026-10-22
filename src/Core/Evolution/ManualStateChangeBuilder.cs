using Core.Data;

namespace Core.Evolution;

/// <summary>
/// 人工直接改状态字段 → 设定变更（manual EntityStateChange）的效果构造（纯函数，不做 IO）。
/// 只覆盖发布事务同样会应用的白名单字段：其余自由文本编辑仍然只进 journal。
/// </summary>
public static class ManualStateChangeBuilder
{
    /// <summary>按实体类型区分「状态字段」白名单。</summary>
    private static readonly Dictionary<EntityType, string[]> StateFields = new()
    {
        // 只纳入「状态维度」字段：性格/描述等自由文本只写 journal，不生成设定变更（§3.6 末条）。
        [EntityType.Character] = ["status", "faction-id", "current-location-id", "role", "tags"],
        [EntityType.Faction] = ["status", "leader", "members", "headquarters-id", "type"],
        [EntityType.Item] = ["status", "owner-id", "location-id", "type"],
        [EntityType.Location] = ["status", "parent-id", "type"],
        [EntityType.Plotline] =
        [
            "status",
            "tension",
            "planned-reveal-chapter-id",
            "resolved-at-chapter-id",
        ],
        [EntityType.Scene] =
        [
            "location-id",
            "participant-ids",
            "plotline-ids",
            "item-ids",
            "scene-type",
        ],
        [EntityType.Relationship] = ["type", "strength", "since-chapter-id", "until-chapter-id"],
    };

    private static readonly HashSet<string> ListFields = new(StringComparer.Ordinal)
    {
        "tags",
        "members",
        "participant-ids",
        "plotline-ids",
        "item-ids",
    };

    /// <summary>
    /// 比较新旧 YAML dict，生成设定变更效果；无白名单字段变化返回空列表。
    /// </summary>
    public static List<StateEffect> Build(
        EntityType entityType,
        IReadOnlyDictionary<string, object?> oldValues,
        IReadOnlyDictionary<string, object?> newValues
    )
    {
        if (!StateFields.TryGetValue(entityType, out var fields))
            return [];

        var effects = new List<StateEffect>();
        foreach (var field in fields)
        {
            oldValues.TryGetValue(field, out var oldRaw);
            newValues.TryGetValue(field, out var newRaw);

            if (ListFields.Contains(field))
            {
                var oldItems = ToList(oldRaw);
                var newItems = ToList(newRaw);
                if (oldItems.SequenceEqual(newItems, StringComparer.Ordinal))
                    continue;

                var added = newItems.Except(oldItems, StringComparer.Ordinal).ToList();
                var removed = oldItems.Except(newItems, StringComparer.Ordinal).ToList();
                if (added.Count == 0 && removed.Count == 0)
                    continue;

                if (newItems.Count == 0)
                {
                    effects.Add(
                        NewEffect(
                            entityType,
                            field,
                            StateEffectOperation.Clear,
                            null,
                            Join(oldItems)
                        )
                    );
                    continue;
                }
                if (oldItems.Count == 0)
                {
                    foreach (var item in added)
                        effects.Add(
                            NewEffect(
                                entityType,
                                field,
                                StateEffectOperation.Add,
                                item,
                                Join(oldItems)
                            )
                        );
                    continue;
                }
                foreach (var item in added)
                    effects.Add(
                        NewEffect(entityType, field, StateEffectOperation.Add, item, Join(oldItems))
                    );
                foreach (var item in removed)
                    effects.Add(
                        NewEffect(
                            entityType,
                            field,
                            StateEffectOperation.Remove,
                            item,
                            Join(oldItems)
                        )
                    );
                continue;
            }

            var oldText = AsText(oldRaw);
            var newText = AsText(newRaw);
            if (string.Equals(oldText, newText, StringComparison.Ordinal))
                continue;

            effects.Add(
                string.IsNullOrEmpty(newText)
                    ? NewEffect(entityType, field, StateEffectOperation.Clear, null, oldText)
                    : NewEffect(entityType, field, StateEffectOperation.Set, newText, oldText)
            );
        }
        return effects;
    }

    private static StateEffect NewEffect(
        EntityType entityType,
        string field,
        StateEffectOperation operation,
        string? value,
        string? from
    ) =>
        new()
        {
            TargetType = entityType,
            Field = field,
            Operation = operation,
            Value = value,
            From = from,
        };

    private static string AsText(object? value) =>
        value switch
        {
            null => "",
            string s => s,
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
        };

    private static List<string> ToList(object? value) =>
        value switch
        {
            null => [],
            string s => string.IsNullOrWhiteSpace(s)
                ? []
                : s.Split(
                        ',',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    )
                    .ToList(),
            IEnumerable<object?> items => items.Select(AsText).Where(s => s.Length > 0).ToList(),
            System.Collections.IEnumerable enumerable => enumerable
                .Cast<object?>()
                .Select(AsText)
                .Where(s => s.Length > 0)
                .ToList(),
            _ => [AsText(value)],
        };

    private static string Join(List<string> items) =>
        items.Count == 0 ? "" : string.Join("、", items);
}
