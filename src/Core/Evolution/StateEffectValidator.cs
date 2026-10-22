using Core.Data;

namespace Core.Evolution;

/// <summary>
/// 状态效果白名单与值解析（唯一真源）：发布预检与 record_evolution 共用。
/// 只允许下表中的「实体类型 + 字段 + 操作」组合；其余一律返回错误文本。
/// </summary>
public static class StateEffectValidator
{
    private sealed record Rule(StateEffectOperation[] Operations, string Note);

    private static readonly Dictionary<(EntityType, string), Rule> Allowed = BuildAllowed();

    private static Dictionary<(EntityType, string), Rule> BuildAllowed()
    {
        var set = new StateEffectOperation[] { StateEffectOperation.Set };
        var setOrClear = new[] { StateEffectOperation.Set, StateEffectOperation.Clear };
        var listOps = new[]
        {
            StateEffectOperation.Add,
            StateEffectOperation.Remove,
            StateEffectOperation.Clear,
        };
        var numeric = new[] { StateEffectOperation.Set, StateEffectOperation.Increment };

        var map = new Dictionary<(EntityType, string), Rule>
        {
            [(EntityType.Character, "status")] = new(set, "Active/Deceased/Missing/Transformed"),
            [(EntityType.Character, "faction-id")] = new(setOrClear, "组织 ID 或空"),
            [(EntityType.Character, "current-location-id")] = new(setOrClear, "地点 ID 或空"),
            [(EntityType.Character, "role")] = new(set, "Protagonist/Supporting/Minor/Antagonist"),
            [(EntityType.Character, "tags")] = new(listOps, "标签"),
            [(EntityType.Character, "abilities")] = new(set, "文本"),
            [(EntityType.Character, "personality")] = new(set, "文本"),
            [(EntityType.Character, "motivation")] = new(set, "文本"),
            [(EntityType.Character, "background")] = new(set, "文本"),
            [(EntityType.Character, "appearance")] = new(set, "文本"),
            [(EntityType.Character, "weakness")] = new(set, "文本"),
            [(EntityType.Character, "habits")] = new(set, "文本"),
            [(EntityType.Character, "age")] = new(set, "文本"),
            [(EntityType.Character, "notes")] = new(set, "文本"),
            [(EntityType.Faction, "status")] = new(set, "Active/Dormant/Dissolved"),
            [(EntityType.Faction, "leader")] = new(setOrClear, "角色 ID 或空"),
            [(EntityType.Faction, "headquarters-id")] = new(setOrClear, "地点 ID 或空"),
            [(EntityType.Faction, "members")] = new(listOps, "角色 ID"),
            [(EntityType.Faction, "type")] = new(set, "组织类型"),
            [(EntityType.Faction, "goal")] = new(set, "文本"),
            [(EntityType.Faction, "description")] = new(set, "文本"),
            [(EntityType.Item, "status")] = new(set, "Active/Lost/Destroyed"),
            [(EntityType.Item, "owner-id")] = new(setOrClear, "持有者 ID 或空"),
            [(EntityType.Item, "location-id")] = new(setOrClear, "地点 ID 或空"),
            [(EntityType.Item, "type")] = new(set, "道具类型"),
            [(EntityType.Item, "description")] = new(set, "文本"),
            [(EntityType.Location, "status")] = new(set, "Active/Destroyed/Sealed/Abandoned"),
            [(EntityType.Location, "parent-id")] = new(setOrClear, "地点 ID 或空"),
            [(EntityType.Location, "type")] = new(set, "地点类型"),
            [(EntityType.Location, "atmosphere")] = new(set, "文本"),
            [(EntityType.Location, "description")] = new(set, "文本"),
            [(EntityType.Plotline, "status")] = new(set, "Active/Resolved/Abandoned"),
            [(EntityType.Plotline, "tension")] = new(set, "Low/Medium/High/Critical"),
            [(EntityType.Plotline, "planned-reveal-chapter-id")] = new(setOrClear, "章节 ID 或空"),
            [(EntityType.Plotline, "resolved-at-chapter-id")] = new(setOrClear, "章节 ID 或空"),
            [(EntityType.Plotline, "turnpoints")] = new(set, "文本"),
            [(EntityType.Plotline, "description")] = new(set, "文本"),
            [(EntityType.Relationship, "type")] = new(set, "关系类型"),
            [(EntityType.Relationship, "strength")] = new(set, "Weak/Medium/Strong/Extreme"),
            [(EntityType.Relationship, "description")] = new(set, "文本"),
            [(EntityType.Relationship, "since-chapter-id")] = new(setOrClear, "章节 ID 或空"),
            [(EntityType.Relationship, "until-chapter-id")] = new(setOrClear, "章节 ID 或空"),
            [(EntityType.Scene, "location-id")] = new(setOrClear, "地点 ID 或空"),
            [(EntityType.Scene, "time-description")] = new(set, "文本"),
            [(EntityType.Scene, "participant-ids")] = new(listOps, "角色 ID"),
            [(EntityType.Scene, "plotline-ids")] = new(listOps, "情节线 ID"),
            [(EntityType.Scene, "item-ids")] = new(listOps, "道具 ID"),
            [(EntityType.Scene, "scene-type")] = new(set, "Action/Dialogue/Reflection/Transition"),
            [(EntityType.Scene, "conflict")] = new(set, "文本"),
            [(EntityType.Scene, "goal")] = new(set, "文本"),
            [(EntityType.Scene, "emotional-arc")] = new(set, "文本"),
            [(EntityType.Scene, "notes")] = new(set, "文本"),
        };
        return map;
    }

    /// <summary>字段规范化：去空白、小写；metrics:key 保留 key 原文。</summary>
    public static StateEffect Normalize(StateEffect effect)
    {
        var field = (effect.Field ?? "").Trim().ToLowerInvariant();
        if (field.StartsWith("metrics:", StringComparison.Ordinal))
        {
            var key = effect.Field!.Trim()["metrics:".Length..].Trim();
            field = key.Length == 0 ? "metrics:" : $"metrics:{key}";
        }
        var value = string.IsNullOrWhiteSpace(effect.Value) ? null : effect.Value.Trim();
        return new StateEffect
        {
            TargetType = effect.TargetType,
            TargetId = (effect.TargetId ?? "").Trim(),
            Field = field,
            Operation = effect.Operation,
            Value = value,
            From = effect.From,
            Cascade = effect.Cascade,
            SourceChangeId = effect.SourceChangeId,
        };
    }

    /// <summary>校验一条效果；合法返回 null，否则返回错误文本。</summary>
    public static string? Validate(StateEffect effect, Novel? novel = null)
    {
        var normalized = Normalize(effect);
        if (string.IsNullOrWhiteSpace(normalized.TargetId))
            return "状态效果缺少 target_id";
        if (string.IsNullOrWhiteSpace(normalized.Field))
            return "状态效果缺少 field";

        if (normalized.Field.StartsWith("metrics:", StringComparison.Ordinal))
        {
            if (
                normalized.TargetType
                is not (
                    EntityType.Character
                    or EntityType.Faction
                    or EntityType.Item
                    or EntityType.Location
                )
            )
            {
                return $"{normalized.TargetType.ToKebabCaseString()} 不支持 metrics 字段";
            }
            var key = normalized.Field["metrics:".Length..];
            if (key.Length == 0)
                return "metrics 字段缺少 key（写法 metrics:<key>）";
            if (
                normalized.Operation
                is not (StateEffectOperation.Set or StateEffectOperation.Increment)
            )
                return $"metrics:{key} 只支持 set/increment";
            if (normalized.Operation == StateEffectOperation.Set)
            {
                if (normalized.Value is null)
                    return $"metrics:{key} 的 set 需要数值";
                if (
                    !double.TryParse(
                        normalized.Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out _
                    )
                )
                    return $"metrics:{key} 的值不是数字：{normalized.Value}";
            }
            return null;
        }

        if (!Allowed.TryGetValue((normalized.TargetType, normalized.Field), out var rule))
            return $"不支持的状态字段：{normalized.TargetType.ToKebabCaseString()}.{normalized.Field}";
        if (!rule.Operations.Contains(normalized.Operation))
            return $"{normalized.Field} 不支持操作 {normalized.Operation}（允许：{string.Join("/", rule.Operations)}）";

        if (
            normalized.Operation
            is StateEffectOperation.Set
                or StateEffectOperation.Add
                or StateEffectOperation.Remove
        )
        {
            if (
                normalized.Operation != StateEffectOperation.Set
                && string.IsNullOrEmpty(normalized.Value)
                && normalized.Field is not ("metrics" or "")
            )
            {
                // Add/Remove 需要具体列表项
                if (normalized.Value is null)
                    return $"{normalized.Field} 的 {normalized.Operation} 需要 value";
            }
        }

        return ValidateValue(normalized);
    }

    private static string? ValidateValue(StateEffect effect)
    {
        if (effect.Operation is StateEffectOperation.Clear or StateEffectOperation.Remove)
            return null;

        var value = effect.Value;
        if (value is null)
        {
            return effect.Operation == StateEffectOperation.Set
                ? null // Set 空值等价 Clear（可空 ID / 文本）
                : $"{effect.Field} 需要 value";
        }

        switch (effect.Field)
        {
            case "status":
                return TryParseStatus(effect.TargetType, value, out _)
                    ? null
                    : $"未知的 {effect.TargetType.ToKebabCaseString()} 状态值：{value}";
            case "role":
                return TryParseEnumValue<CharacterRole>(value, out _)
                    ? null
                    : $"未知的角色定位：{value}";
            case "tension":
                return TryParseEnumValue<PlotlineTension>(value, out _)
                    ? null
                    : $"未知的紧张度：{value}";
            case "strength":
                return TryParseEnumValue<RelationshipStrength>(value, out _)
                    ? null
                    : $"未知的关系强度：{value}";
            case "scene-type":
                return TryParseEnumValue<SceneType>(value, out _)
                    ? null
                    : $"未知的场景类型：{value}";
            case "type":
                return effect.TargetType switch
                {
                    EntityType.Faction => TryParseEnumValue<FactionType>(value, out _)
                        ? null
                        : $"未知的组织类型：{value}",
                    EntityType.Location => TryParseEnumValue<LocationType>(value, out _)
                        ? null
                        : $"未知的地点类型：{value}",
                    EntityType.Item => TryParseEnumValue<ItemType>(value, out _)
                        ? null
                        : $"未知的道具类型：{value}",
                    EntityType.Relationship => TryParseEnumValue<RelationshipType>(value, out _)
                        ? null
                        : $"未知的关系类型：{value}",
                    _ => null,
                };
            default:
                return null;
        }
    }

    /// <summary>状态值解析（枚举名忽略大小写 + 中文/同义词）。</summary>
    public static bool TryParseStatus(EntityType type, string raw, out object value)
    {
        var text = raw.Trim().ToLowerInvariant();
        value = type switch
        {
            EntityType.Character => text switch
            {
                "active" or "活跃" or "存活" => CharacterStatus.Active,
                "deceased" or "dead" or "死亡" or "已死亡" => CharacterStatus.Deceased,
                "missing" or "失踪" => CharacterStatus.Missing,
                "transformed" or "转变" or "转化" => CharacterStatus.Transformed,
                _ => null!,
            },
            EntityType.Faction => text switch
            {
                "active" or "活跃" => FactionStatus.Active,
                "dormant" or "蛰伏" => FactionStatus.Dormant,
                "dissolved" or "覆灭" or "解散" => FactionStatus.Dissolved,
                _ => null!,
            },
            EntityType.Item => text switch
            {
                "active" or "完好" => ItemStatus.Active,
                "lost" or "遗失" => ItemStatus.Lost,
                "destroyed" or "损毁" => ItemStatus.Destroyed,
                _ => null!,
            },
            EntityType.Location => text switch
            {
                "active" or "存在" => LocationStatus.Active,
                "destroyed" or "毁灭" => LocationStatus.Destroyed,
                "sealed" or "封印" => LocationStatus.Sealed,
                "abandoned" or "废弃" => LocationStatus.Abandoned,
                _ => null!,
            },
            EntityType.Plotline => text switch
            {
                "active" or "进行中" => PlotlineStatus.Active,
                "resolved" or "已解决" => PlotlineStatus.Resolved,
                "abandoned" or "放弃" => PlotlineStatus.Abandoned,
                _ => null!,
            },
            _ => null!,
        };
        return value is not null;
    }

    /// <summary>枚举解析（忽略大小写）。</summary>
    public static bool TryParseEnumValue<T>(string raw, out T value)
        where T : struct, Enum
    {
        var exact = Enum.TryParse<T>(raw.Trim(), ignoreCase: true, out value);
        if (exact)
            return true;
        // 中文标签映射（角色定位/紧张度/强度/场景类型/组织/地点/道具类型）
        var label = raw.Trim();
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (string.Equals(LabelOf(candidate), label, StringComparison.Ordinal))
            {
                value = candidate;
                return true;
            }
        }
        return false;
    }

    private static string? LabelOf<T>(T value)
        where T : struct, Enum =>
        value switch
        {
            CharacterRole.Protagonist => "主角",
            CharacterRole.Supporting => "配角",
            CharacterRole.Minor => "龙套",
            CharacterRole.Antagonist => "反派",
            PlotlineTension.Low => "低",
            PlotlineTension.Medium => "中",
            PlotlineTension.High => "高",
            PlotlineTension.Critical => "临界",
            RelationshipStrength.Weak => "弱",
            RelationshipStrength.Medium => "中",
            RelationshipStrength.Strong => "强",
            RelationshipStrength.Extreme => "极强",
            SceneType.Action => "动作",
            SceneType.Dialogue => "对话",
            SceneType.Reflection => "内省",
            SceneType.Transition => "过渡",
            FactionType.Sect => "门派",
            FactionType.Company => "公司",
            FactionType.Family => "家族",
            FactionType.Gang => "帮会",
            FactionType.Government => "政府",
            FactionType.Other => "其他",
            LocationType.City => "城市",
            LocationType.Rural => "乡村",
            LocationType.Indoor => "室内",
            LocationType.Wilderness => "荒野",
            LocationType.Fictional => "虚构",
            LocationType.Other => "其他",
            ItemType.Weapon => "武器",
            ItemType.Artifact => "法器",
            ItemType.Token => "信物",
            ItemType.Consumable => "消耗品",
            ItemType.Other => "其他",
            _ => null,
        };

    /// <summary>错误里展示的允许值说明（record_evolution 报错用）。</summary>
    public static string DescribeAllowed(EntityType type, string field) =>
        Allowed.TryGetValue((type, field), out var rule) ? rule.Note : "";
}
