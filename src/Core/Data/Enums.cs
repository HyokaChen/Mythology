using System.Text.Json.Serialization;

namespace Core.Data;

// ── 实体状态/类型枚举 ──

/// <summary>章节状态枚举。</summary>
public enum ChapterStatus
{
    /// <summary>草稿状态（默认）。</summary>
    Draft,

    /// <summary>
    /// 已发布状态（用户报障：章节状态只该有草稿/发布两态，审核/终稿没有意义，已移除）。
    /// 仅随章节 front matter 的 status 字段落进 yaml，不影响其他逻辑。
    /// </summary>
    Published,
}

/// <summary>角色状态枚举。</summary>
public enum CharacterStatus
{
    /// <summary>活跃状态。</summary>
    Active,

    /// <summary>已死亡。</summary>
    Deceased,

    /// <summary>失踪状态。</summary>
    Missing,

    /// <summary>已转变。</summary>
    Transformed,
}

/// <summary>
/// 状态变化生命周期：草稿章写入 = Pending（待发布），发布/补记后 = Committed（已生效）。
/// 只有 Committed 进入权威视图；Pending 只在审阅与发布预览中可见。
/// </summary>
public enum ChangeLifecycle
{
    /// <summary>待发布（草稿章中登记、尚未应用）。</summary>
    Pending,

    /// <summary>已生效（发布事务已应用，或设定变更即时生效）。</summary>
    Committed,
}

/// <summary>变更集（一次发布/补记事务）的状态。</summary>
public enum ChangeSetStatus
{
    /// <summary>正在应用。</summary>
    Applying,

    /// <summary>已应用（可撤回）。</summary>
    Applied,

    /// <summary>已撤回。</summary>
    Reverted,

    /// <summary>应用失败（已尽力回滚）。</summary>
    Failed,
}

/// <summary>变更集条目类型。</summary>
public enum ChangeSetEntryKind
{
    /// <summary>状态变化记录（EntityStateChange）。</summary>
    StateChange,

    /// <summary>实体快照更新（Before/After 指纹）。</summary>
    EntityUpdate,

    /// <summary>实体创建（仅纪念地）。</summary>
    EntityCreate,
}

/// <summary>组织状态。</summary>
public enum FactionStatus
{
    /// <summary>活跃。</summary>
    Active,

    /// <summary>蛰伏。</summary>
    Dormant,

    /// <summary>覆灭/解散。</summary>
    Dissolved,
}

/// <summary>道具状态。</summary>
public enum ItemStatus
{
    /// <summary>完好。</summary>
    Active,

    /// <summary>遗失。</summary>
    Lost,

    /// <summary>损毁。</summary>
    Destroyed,
}

/// <summary>地点状态。</summary>
public enum LocationStatus
{
    /// <summary>存在。</summary>
    Active,

    /// <summary>毁灭。</summary>
    Destroyed,

    /// <summary>封印。</summary>
    Sealed,

    /// <summary>废弃。</summary>
    Abandoned,
}

/// <summary>情节线状态。</summary>
public enum PlotlineStatus
{
    /// <summary>进行中。</summary>
    Active,

    /// <summary>已解决。</summary>
    Resolved,

    /// <summary>放弃。</summary>
    Abandoned,
}

/// <summary>状态效果操作。</summary>
public enum StateEffectOperation
{
    /// <summary>赋值为指定值。</summary>
    Set,

    /// <summary>向列表追加（去重）。</summary>
    Add,

    /// <summary>从列表移除全部相等项。</summary>
    Remove,

    /// <summary>清空列表 / 可空 ID 置 null / 文本置空。</summary>
    Clear,

    /// <summary>metrics 数值累加。</summary>
    Increment,
}

/// <summary>变化的可见性（公众可知 / 秘密 / 私密）。</summary>
public enum ChangeVisibility
{
    /// <summary>公开。</summary>
    Public,

    /// <summary>秘密（角色知情但世界不知）。</summary>
    Secret,

    /// <summary>私密（仅作者/模型记录）。</summary>
    Private,
}

/// <summary>出场强度枚举（ENTITY-EVOLUTION §3.1）。</summary>
public enum AppearanceMode
{
    /// <summary>在场：有戏份/参与对话/实际出场。</summary>
    Present,

    /// <summary>被提及：闪回/转述/他人谈及（未实际出场）。</summary>
    Mentioned,
}

/// <summary>
/// 变化面枚举（ENTITY-EVOLUTION §3.1）——本体「状态」的切面维度。
/// </summary>
public enum EvolutionAspect
{
    /// <summary>生存状态（Active/Deceased/Missing/Transformed 等）。</summary>
    Status,

    /// <summary>性格变化。</summary>
    Personality,

    /// <summary>动机/目标变化。</summary>
    Motivation,

    /// <summary>人物关系变化（敌→友等）。</summary>
    Relationship,

    /// <summary>能力获得/失去。</summary>
    Ability,

    /// <summary>持有物易主（道具/法宝归属变化）。</summary>
    Possession,

    /// <summary>所在地变化。</summary>
    Location,

    /// <summary>知晓/认知变化（信息差维度）。</summary>
    Knowledge,

    /// <summary>行为模式变化（习惯/做事方式）。</summary>
    Behavior,

    /// <summary>其他未归类变化面。</summary>
    Other,
}

/// <summary>计划 delta 类型（SceneDefinition.PlannedDeltas）——场景开始前声明的预期变化。</summary>
public enum PlannedDeltaKind
{
    /// <summary>本体状态变化（EntityStateChange 的计划版本）。</summary>
    StateChange,

    /// <summary>关系变化（敌→友、结盟、决裂等）。</summary>
    RelationChange,

    /// <summary>规则触发（条件满足 → Effect）。</summary>
    RuleTrigger,

    /// <summary>行动类型（战斗/礼仪/医术等）。</summary>
    Action,

    /// <summary>情节线揭示/回收（PlannedRevealChapterId / ResolvedAtChapterId）。</summary>
    PlotlineReveal,
}

/// <summary>素材边界 delta 来源。</summary>
public enum DeltaSource
{
    /// <summary>来自已持久化的出场/变化记录（含缺席期间发生的变化）。</summary>
    Persisted,

    /// <summary>来自 SceneDefinition.PlannedDeltas（本场景计划变化）。</summary>
    Planned,

    /// <summary>persisted 与 planned 完全一致。</summary>
    Both,

    /// <summary>由世界规则评估器计算得出（RuleTrigger）。</summary>
    Computed,

    /// <summary>来自设定变更日志（编辑器/工具改实体、关系、规则；非叙事检测）。</summary>
    Settings,
}

/// <summary>素材需求强度。</summary>
public enum MaterialNeed
{
    /// <summary>不需要素材。</summary>
    None,

    /// <summary>可选；由 Jev 决定是否取。</summary>
    Optional,

    /// <summary>强需求；nudge 优先提示。</summary>
    Strong,
}

/// <summary>世界规则模板类型（WorldRule.RuleKind）。</summary>
public enum RuleKind
{
    /// <summary>数值边界（travel-speed / location-distance 等既有规则）。</summary>
    Numeric,

    /// <summary>前置条件：行动需要某状态/持有物/境界。</summary>
    Requires,

    /// <summary>禁止：某类主体不能做某事/进入某地。</summary>
    Forbids,

    /// <summary>触发：条件事件发生 → Effect。</summary>
    Triggers,
}

/// <summary>世界条目类型：可执行规则 / 描述性设定。</summary>
public enum WorldRuleEntryKind
{
    /// <summary>可执行硬约束（RuleKind 四类模板）。</summary>
    Rule,

    /// <summary>描述性世界观（历史、文化、地理、力量体系等，不参与求值）。</summary>
    Setting,
}

/// <summary>世界规则/设定的领域分类（展示与筛选用，不参与求值）。</summary>
public enum WorldRuleCategory
{
    /// <summary>自然法则：地理、气候、生态、物理、时间。</summary>
    Natural,

    /// <summary>力量体系：魔法、修炼、科技、代价与上限。</summary>
    Power,

    /// <summary>社会制度：法律、禁忌、礼制、经济、语言。</summary>
    Social,

    /// <summary>叙事约束：视角、时间线、死亡不可逆、体裁承诺。</summary>
    Narrative,

    /// <summary>信息认知：谁知道什么、秘密、预言、记忆。</summary>
    Cognitive,

    /// <summary>其他/未分类。</summary>
    Other,
}

/// <summary>情节线类型枚举。</summary>
public enum PlotlineType
{
    /// <summary>主线。</summary>
    Main,

    /// <summary>支线。</summary>
    Side,

    /// <summary>隐藏线。</summary>
    Hidden,

    /// <summary>伏笔。</summary>
    Foreshadowing,
}

/// <summary>角色关系类型枚举。</summary>
public enum RelationshipType
{
    /// <summary>朋友。</summary>
    Friend,

    /// <summary>对手。</summary>
    Rival,

    /// <summary>家人。</summary>
    Family,

    /// <summary>恋人。</summary>
    Love,

    /// <summary>敌人。</summary>
    Enemy,

    /// <summary>导师。</summary>
    Mentor,

    /// <summary>主仆。</summary>
    MasterServant,

    /// <summary>同门。</summary>
    FellowDisciple,

    /// <summary>父母（A 是 B 的父母；有向）。</summary>
    ParentOf,

    /// <summary>子女（A 是 B 的子女；有向）。</summary>
    ChildOf,

    /// <summary>兄弟姐妹（无向）。</summary>
    Sibling,

    /// <summary>夫妻/道侣（无向）。</summary>
    Spouse,

    /// <summary>结拜（无向）。</summary>
    Sworn,

    /// <summary>徒弟（A 是 B 的徒弟；有向，与 Mentor/MasterServant 互为师徒）。</summary>
    Disciple,

    /// <summary>盟友（无向）。</summary>
    Ally,
}

/// <summary>
/// <see cref="RelationshipType"/> 的服务端词汇映射。
/// CHANGE_10 §2.5 的关系复合表（父∘兄=伯父、师∘徒=同门）要求作者侧能表达父/兄/夫妻/结拜；
/// 本映射把这些类型映射到云端 <c>CloudSetupInput.Relations[].Type</c> 的规范 token。
/// </summary>
public static class RelationshipTypeExtensions
{
    private static readonly Dictionary<RelationshipType, string> KebabCaseMap = new()
    {
        [RelationshipType.Friend] = "friend",
        [RelationshipType.Rival] = "rival",
        [RelationshipType.Family] = "family",
        [RelationshipType.Love] = "lover",
        [RelationshipType.Enemy] = "enemy",
        [RelationshipType.Mentor] = "mentor",
        [RelationshipType.MasterServant] = "master-servant",
        [RelationshipType.FellowDisciple] = "fellow-disciple",
        [RelationshipType.ParentOf] = "parent-of",
        [RelationshipType.ChildOf] = "child-of",
        [RelationshipType.Sibling] = "sibling",
        [RelationshipType.Spouse] = "spouse",
        [RelationshipType.Sworn] = "sworn",
        [RelationshipType.Disciple] = "disciple",
        [RelationshipType.Ally] = "ally",
    };

    /// <summary>历史写法 / 工具描述别名 → 枚举（迁移与工具入参用）。</summary>
    private static readonly Dictionary<string, RelationshipType> AliasMap = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["love"] = RelationshipType.Love,
        ["masterservant"] = RelationshipType.MasterServant,
        ["master_servant"] = RelationshipType.MasterServant,
        ["fellowdisciple"] = RelationshipType.FellowDisciple,
        ["fellow_disciple"] = RelationshipType.FellowDisciple,
    };

    /// <summary>枚举 → kebab-case 字符串（<c>relationships.yaml</c> 的 type 值与云端 token 同形）。</summary>
    public static string ToKebabCaseString(this RelationshipType type) =>
        KebabCaseMap.TryGetValue(type, out var s) ? s : type.ToString().ToLowerInvariant();

    /// <summary>kebab-case / 历史写法 → 枚举；解析失败返回 null。</summary>
    public static RelationshipType? ParseRelationshipType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        foreach (var (type, token) in KebabCaseMap)
        {
            if (string.Equals(token, value, StringComparison.OrdinalIgnoreCase))
            {
                return type;
            }
        }
        return AliasMap.TryGetValue(value, out var mapped) ? mapped
            : Enum.TryParse<RelationshipType>(value, ignoreCase: true, out var parsed) ? parsed
            : null;
    }
}

/// <summary>组织关系类型枚举。</summary>
public enum FactionRelationType
{
    /// <summary>盟友。</summary>
    Ally,

    /// <summary>敌对。</summary>
    Enemy,

    /// <summary>中立。</summary>
    Neutral,

    /// <summary>贸易关系。</summary>
    Trade,

    /// <summary>附庸关系。</summary>
    Vassal,
}

/// <summary>组织类型枚举。</summary>
public enum FactionType
{
    /// <summary>门派/宗门。</summary>
    Sect,

    /// <summary>公司/企业。</summary>
    Company,

    /// <summary>家族/宗族。</summary>
    Family,

    /// <summary>帮会/社团。</summary>
    Gang,

    /// <summary>政府/官方。</summary>
    Government,

    /// <summary>其他。</summary>
    Other,
}

/// <summary>地点类型枚举。</summary>
public enum LocationType
{
    /// <summary>城市/城镇。</summary>
    City,

    /// <summary>乡村/田园。</summary>
    Rural,

    /// <summary>室内/建筑。</summary>
    Indoor,

    /// <summary>荒野/自然。</summary>
    Wilderness,

    /// <summary>虚构/异界。</summary>
    Fictional,

    /// <summary>其他。</summary>
    Other,
}

/// <summary>道具类型枚举。</summary>
public enum ItemType
{
    /// <summary>武器/兵器。</summary>
    Weapon,

    /// <summary>法器/法宝。</summary>
    Artifact,

    /// <summary>信物/象征物。</summary>
    Token,

    /// <summary>消耗品/药品。</summary>
    Consumable,

    /// <summary>其他。</summary>
    Other,
}

/// <summary>情节线紧张度枚举。</summary>
public enum PlotlineTension
{
    /// <summary>低。</summary>
    Low,

    /// <summary>中。</summary>
    Medium,

    /// <summary>高。</summary>
    High,

    /// <summary>临界。</summary>
    Critical,
}

/// <summary>关系强度枚举。</summary>
public enum RelationshipStrength
{
    /// <summary>弱。</summary>
    Weak,

    /// <summary>中。</summary>
    Medium,

    /// <summary>强。</summary>
    Strong,

    /// <summary>极强。</summary>
    Extreme,
}

/// <summary>消息角色枚举。</summary>
public enum MessageRole
{
    /// <summary>用户。</summary>
    User,

    /// <summary>助手。</summary>
    Assistant,

    /// <summary>系统。</summary>
    System,

    /// <summary>工具。</summary>
    Tool,

    /// <summary>未知。</summary>
    Unknown,
}

/// <summary>审计操作类型枚举。</summary>
public enum AuditAction
{
    /// <summary>创建。</summary>
    Create,

    /// <summary>更新。</summary>
    Update,

    /// <summary>删除。</summary>
    Delete,
}

/// <summary>场景类型枚举。</summary>
public enum SceneType
{
    /// <summary>动作场景。</summary>
    Action,

    /// <summary>对话场景。</summary>
    Dialogue,

    /// <summary>反思场景。</summary>
    Reflection,

    /// <summary>过渡场景。</summary>
    Transition,
}

/// <summary>角色定位枚举。</summary>
public enum CharacterRole
{
    /// <summary>主角。</summary>
    Protagonist,

    /// <summary>配角。</summary>
    Supporting,

    /// <summary>龙套/路人。</summary>
    Minor,

    /// <summary>反派。</summary>
    Antagonist,
}

/// <summary>审计来源枚举。</summary>
public enum AuditSource
{
    /// <summary>AI 操作。</summary>
    Ai,

    /// <summary>用户操作。</summary>
    User,

    /// <summary>系统操作。</summary>
    System,
}

/// <summary>本体要素（六要素 + 行动日志）。</summary>
public enum OntologyElement
{
    /// <summary>对象：实体定义。</summary>
    Object,

    /// <summary>状态：生命周期/状态切面。</summary>
    State,

    /// <summary>关系：对象间连接。</summary>
    Relation,

    /// <summary>规则：触发条件与约束。</summary>
    Rule,

    /// <summary>行动：能做什么 / 做了什么。</summary>
    Action,

    /// <summary>计算：如何推导新事实（派生视图，不入日志）。</summary>
    Calculation,
}

/// <summary>本体动作日志的操作类型。</summary>
public enum OntologyOperation
{
    /// <summary>创建。</summary>
    Create,

    /// <summary>更新。</summary>
    Update,

    /// <summary>删除。</summary>
    Delete,

    /// <summary>撤回（变更集回滚；时间线显示「已回撤」）。</summary>
    Revert,
}

/// <summary>世界规则的严重度（决定 harness 是否阻断云端诊断）。</summary>
public enum RuleSeverity
{
    /// <summary>提示：只作参考。</summary>
    Info,

    /// <summary>警告：建议修正，但不阻断。</summary>
    Warning,

    /// <summary>硬约束：违反时阻断本轮云端诊断，优先本地修正。</summary>
    Constraint,
}

/// <summary>
/// 会话事件类型枚举。用于 <see cref="ChatMessage.EventType"/> 和 <see cref="SessionEvent.EventType"/>。
/// </summary>
public enum SessionEventType
{
    /// <summary>中断生成。</summary>
    Interrupt,

    /// <summary>LLM API 错误（持久化到 JSONL）。</summary>
    Error,

    /// <summary>模型切换。</summary>
    ModelChange,

    /// <summary>变体选择（Explore Tool 产生）。</summary>
    VariantSelected,

    /// <summary>变体集合生成（用于会话恢复时重新打开审阅）。</summary>
    VariantSetCreated,

    /// <summary>上下文压缩摘要（仅 ChatMessage.EventType 使用，非 JSONL event 条目）。</summary>
    Compact,
}

/// <summary>
/// 实体类型枚举。用于 EntityChangeEvent、审计日志和向量同步。
/// 序列化为 snake_case 字符串："chapter", "character", "plotline" 等。
/// </summary>
public enum EntityType
{
    /// <summary>章节。</summary>
    Chapter,

    /// <summary>角色。</summary>
    Character,

    /// <summary>情节线。</summary>
    Plotline,

    /// <summary>组织/势力。</summary>
    Faction,

    /// <summary>地点。</summary>
    Location,

    /// <summary>道具。</summary>
    Item,

    /// <summary>时间线事件。</summary>
    TimelineEvent,

    /// <summary>角色关系。</summary>
    Relationship,

    /// <summary>组织关系。</summary>
    FactionRelationship,

    /// <summary>场景。</summary>
    Scene,

    /// <summary>小说。</summary>
    Novel,

    /// <summary>出场记录（ENTITY-EVOLUTION）。</summary>
    Appearance,

    /// <summary>变化事件（ENTITY-EVOLUTION）。</summary>
    StateChange,

    /// <summary>分卷（大纲二层：卷纲）。</summary>
    Volume,

    /// <summary>世界规则（规则要素：约束型声明，存 world-rules.yaml）。</summary>
    WorldRule,

    /// <summary>作者随手记（书级自由文本，存 notes/*.yaml；只在本机，不参与云端同步）。</summary>
    Note,
}

/// <summary>
/// EntityType 的扩展方法，提供与 JSONL 序列化兼容的字符串转换（历史 snake_case 词汇，见 CHANGE_11 C11）。
/// </summary>
public static class EntityTypeExtensions
{
    private static readonly Dictionary<EntityType, string> KebabCaseMap = new()
    {
        [EntityType.Chapter] = "chapter",
        [EntityType.Character] = "character",
        [EntityType.Plotline] = "plotline",
        [EntityType.Faction] = "faction",
        [EntityType.Location] = "location",
        [EntityType.Item] = "item",
        [EntityType.TimelineEvent] = "timeline-event",
        [EntityType.Relationship] = "relationship",
        [EntityType.FactionRelationship] = "faction-relationship",
        [EntityType.Scene] = "scene",
        [EntityType.Novel] = "novel",
        [EntityType.Appearance] = "appearance",
        [EntityType.StateChange] = "state-change",
        [EntityType.Volume] = "volume",
        [EntityType.WorldRule] = "world-rule",
        [EntityType.Note] = "note",
    };
    private static readonly Dictionary<string, EntityType> ReverseMap = KebabCaseMap.ToDictionary(
        kvp => kvp.Value,
        kvp => kvp.Key
    );

    /// <summary>
    /// 将 EntityType 枚举值转换为 JSONL / 同步文档使用的字符串（kebab-case；历史 snake_case / 无分隔符写法由一次性迁移改写）。
    /// </summary>
    public static string ToKebabCaseString(this EntityType entityType) =>
        KebabCaseMap.TryGetValue(entityType, out var s)
            ? s
            : entityType.ToString().ToLowerInvariant();

    /// <summary>
    /// 将 kebab-case 字符串解析为 EntityType 枚举值。解析失败返回 null。
    /// </summary>
    public static EntityType? ParseEntityType(string? value) =>
        value is not null && ReverseMap.TryGetValue(value, out var et) ? et : null;
}

/// <summary>
/// JSONL 条目类型枚举。对应 JSONL 文件中每行的 type 字段。
/// 序列化为 kebab-case 字符串："message", "compact", "event", "leaf", "label", "branch-summary"。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<JsonlEntryType>))]
public enum JsonlEntryType
{
    /// <summary>对话消息（user/assistant/tool/system）。</summary>
    Message,

    /// <summary>上下文压缩摘要。</summary>
    Compact,

    /// <summary>事件条目（interrupt/error/model_change/variant_selected）。</summary>
    Event,

    /// <summary>活跃分支指针。</summary>
    Leaf,

    /// <summary>用户书签/标记。</summary>
    Label,

    /// <summary>被放弃分支的 AI 摘要。</summary>
    BranchSummary,

    /// <summary>上下文页起始标记（接续换页，不生成摘要）。</summary>
    Window,
}
