namespace Core.Data;

/// <summary>
/// 关系本体的一条声明（叙事关系，作者可写）：客户端词表 → 域/值域 → 云端映射。
/// <para>
/// 域/值域是候选封锁的唯一依据（<see cref="RelationshipOntology.Allowed"/>）：只有存在至少一条
/// 允许该（源类型, 目标类型）的关系时，实体对才进入判定；判定候选集也只开放该类型对允许的 token。
/// </para>
/// <para>
/// 云端两条通道各自有 token 口径：<see cref="CloudEdgeType"/>（同步图 <c>云端图谱</c> 的边类型，
/// 关系类型写在 <c>Properties["type"]</c>）与 <see cref="CloudSetupToken"/>（设定检查
/// <c>CloudSetupInput.Relations[].Type</c>，主仆/同门在该通道有专用 token）。
/// </para>
/// </summary>
public sealed record RelationSpec(
    RelationshipType Type,
    string Token,
    string Label,
    string Description,
    IReadOnlyList<EntityType> SourceTypes,
    IReadOnlyList<EntityType> TargetTypes,
    bool Directed,
    string CloudEdgeType,
    string CloudSetupToken
);

/// <summary>
/// 结构性关系（云端 <c>云端图谱关系</c> 词汇）：由作者字段与同步投影产生，
/// 关系扫描器只把它用于候选封锁，不产出提案——客户端没有通用边的写入通道，宁可不报。
/// </summary>
public sealed record StructuralRelationSpec(
    string Token,
    string Description,
    IReadOnlyList<EntityType> SourceTypes,
    IReadOnlyList<EntityType> TargetTypes,
    bool Directed
);

/// <summary>
/// 关系本体（单一事实源）——提炼自 jevgraph <c>ontology.py</c> 的 domain/range 约束，
/// 词表基于本仓既有 <see cref="RelationshipType"/> 与 <see cref="EntityType"/>（不引入第二份关系名词表）。
///
/// 规则：
/// - 叙事关系（<see cref="Relations"/>）＝ 15 条 <see cref="RelationshipType"/>，域/值域一律角色×角色
///   （本地 <see cref="Relationship"/> 只有 CharacterAId/CharacterBId，扩展域属数据模型变更，不在本体里假装）。
/// - 结构性关系（<see cref="StructuralRelations"/>）＝ 同步图投影的边类型，覆盖角色/组织/地点/道具/
///   情节线/场景/章/卷/小说之间的结构边，供封锁与云端对齐使用。
/// - 保留结局：<see cref="NoneToken"/>（该句未表达稳定关系）与 <see cref="UncertainToken"/>
///   （证据不足，需前后文）——对齐 jevgraph 的 <c>none</c> / <c>insufficient_evidence</c>。
/// </summary>
public static class RelationshipOntology
{
    /// <summary>保留结局：句子未表达稳定关系（判定为 none → 拒绝）。</summary>
    public const string NoneToken = "none";

    /// <summary>保留结局：证据不足（判定为 uncertain → 待人工确认）。</summary>
    public const string UncertainToken = "uncertain";

    private static readonly EntityType[] CharacterOnly = [EntityType.Character];

    /// <summary>叙事关系表（15 条；token 与 relationships.yaml 同拼写）。</summary>
    public static IReadOnlyList<RelationSpec> Relations { get; } = BuildRelations();

    /// <summary>结构性关系表（同步图边词汇；只用于封锁与映射）。</summary>
    public static IReadOnlyList<StructuralRelationSpec> StructuralRelations { get; } =
        BuildStructuralRelations();

    private static readonly Dictionary<string, RelationSpec> ByToken = Relations.ToDictionary(
        spec => spec.Token,
        StringComparer.Ordinal
    );

    private static readonly Dictionary<RelationshipType, RelationSpec> ByType =
        Relations.ToDictionary(spec => spec.Type);

    /// <summary>叙事关系的全部 token（判定候选集的基底）。</summary>
    public static IReadOnlyList<string> Tokens { get; } = [.. Relations.Select(spec => spec.Token)];

    /// <summary>按 <see cref="RelationshipType"/> 取声明。</summary>
    public static RelationSpec Find(RelationshipType type) =>
        ByType.TryGetValue(type, out var spec)
            ? spec
            : throw new ArgumentOutOfRangeException(nameof(type), type, "未知的叙事关系类型");

    /// <summary>按 token 取声明（未知返回 null）。</summary>
    public static RelationSpec? ByTokenOrNull(string? token) =>
        token is not null && ByToken.TryGetValue(token, out var spec) ? spec : null;

    /// <summary>该（源类型, 目标类型）允许的叙事关系（候选封锁 + 判定候选集用）。</summary>
    public static IReadOnlyList<RelationSpec> Allowed(EntityType source, EntityType target) =>
        Relations
            .Where(spec => spec.SourceTypes.Contains(source) && spec.TargetTypes.Contains(target))
            .ToList();

    /// <summary>该类型对是否存在至少一条允许的叙事关系（域/值域门）。</summary>
    public static bool Allows(EntityType source, EntityType target) =>
        Relations.Any(spec =>
            spec.SourceTypes.Contains(source) && spec.TargetTypes.Contains(target)
        );

    /// <summary>设定检查通道（<c>CloudSetupInput.Relations[].Type</c>）的 token。</summary>
    public static string CloudSetupToken(RelationshipType type) => Find(type).CloudSetupToken;

    /// <summary>该关系是否在设定检查通道为有向（其余按无向处理，云端不判反向缺失）。</summary>
    public static bool Directed(RelationshipType type) => Find(type).Directed;

    private static List<RelationSpec> BuildRelations()
    {
        // 描述文案与 scan_relationships 的判定判据同源（此前后者自带一份，已收敛到本体）
        static RelationSpec Spec(
            RelationshipType type,
            string label,
            string description,
            bool directed = false,
            string? cloudSetupToken = null
        ) =>
            new(
                type,
                type.ToKebabCaseString(),
                label,
                description,
                CharacterOnly,
                CharacterOnly,
                directed,
                CloudEdgeType: "relationship",
                CloudSetupToken: cloudSetupToken ?? type.ToKebabCaseString()
            );

        return
        [
            Spec(RelationshipType.Friend, "朋友", "朋友（平等往来）"),
            Spec(RelationshipType.Rival, "对手", "对手（竞争/较劲）"),
            Spec(RelationshipType.Family, "家人", "家人（亲属统称）"),
            Spec(RelationshipType.Love, "恋人", "恋人"),
            Spec(RelationshipType.Enemy, "敌人", "敌人（敌对）"),
            Spec(RelationshipType.Mentor, "导师", "导师/师傅（传道指导）", directed: true),
            // 设定检查通道专用 token：云端关系复合表用 master（主仆）与 sect-mate（同门）
            Spec(
                RelationshipType.MasterServant,
                "主仆",
                "主仆",
                directed: true,
                cloudSetupToken: "master"
            ),
            Spec(
                RelationshipType.FellowDisciple,
                "同门",
                "同门（同一师门）",
                cloudSetupToken: "sect-mate"
            ),
            Spec(RelationshipType.ParentOf, "父母", "A 是 B 的父母（有向）", directed: true),
            Spec(RelationshipType.ChildOf, "子女", "A 是 B 的子女（有向）", directed: true),
            Spec(RelationshipType.Sibling, "兄弟姐妹", "兄弟姐妹"),
            Spec(RelationshipType.Spouse, "夫妻", "夫妻/道侣"),
            Spec(RelationshipType.Sworn, "结拜", "结拜"),
            Spec(RelationshipType.Disciple, "徒弟", "A 是 B 的徒弟（有向）", directed: true),
            Spec(RelationshipType.Ally, "盟友", "盟友"),
        ];
    }

    private static List<StructuralRelationSpec> BuildStructuralRelations()
    {
        EntityType[] chars = [EntityType.Character];
        EntityType[] factions = [EntityType.Faction];
        EntityType[] locations = [EntityType.Location];
        EntityType[] items = [EntityType.Item];
        EntityType[] plotlines = [EntityType.Plotline];
        EntityType[] scenes = [EntityType.Scene];
        EntityType[] chapters = [EntityType.Chapter];
        EntityType[] volumes = [EntityType.Volume];
        EntityType[] novels = [EntityType.Novel];
        EntityType[] timeline = [EntityType.TimelineEvent];
        EntityType[] worldRules = [EntityType.WorldRule];
        EntityType[] evolvable =
        [
            EntityType.Character,
            EntityType.Faction,
            EntityType.Location,
            EntityType.Item,
            EntityType.Plotline,
            EntityType.Scene,
        ];

        return
        [
            new("relationship", "角色关系（类型见 Properties.type）", chars, chars, true),
            new(
                "faction-relationship",
                "组织关系（类型见 Properties.type）",
                factions,
                factions,
                true
            ),
            new("membership", "角色归属组织", chars, factions, true),
            new("leadership", "组织领袖", factions, chars, true),
            new("headquarters", "组织驻地", factions, locations, true),
            new("contains", "地点层级（父包含子）", locations, locations, true),
            new("adjacent-to", "地点邻接（含距离）", locations, locations, false),
            new("ownership", "道具所有权（含时窗）", items, chars, true),
            new(
                "appears-in",
                "实体出场（角色/道具 → 场景）",
                [EntityType.Character, EntityType.Item],
                scenes,
                true
            ),
            new("located-at", "场景地点", scenes, locations, true),
            new("belongs-to", "场景归属情节线", scenes, plotlines, true),
            new("in-chapter", "场景所属章节", scenes, chapters, true),
            new("in-volume", "章所属卷", chapters, volumes, true),
            new(
                "advances",
                "章/卷推进情节线",
                [EntityType.Chapter, EntityType.Volume],
                plotlines,
                true
            ),
            new("features", "章/卷聚焦角色", [EntityType.Chapter, EntityType.Volume], chars, true),
            new("reveals-at", "情节线计划揭示章", plotlines, chapters, true),
            new("resolves-at", "情节线回收章", plotlines, chapters, true),
            new("occurs-in", "时间线事件发生章", timeline, chapters, true),
            new("evolves", "实体演化事件（→ 章）", evolvable, chapters, true),
            new("constrains", "世界规则约束小说", worldRules, novels, true),
        ];
    }
}
