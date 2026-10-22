namespace Core.Data;

/// <summary>小说数据模型基类。</summary>
public abstract class Entity
{
    /// <summary>实体唯一标识符（12 字符 GUID）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>所属小说 ID。</summary>
    public string NovelId { get; set; } = "";

    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>更新时间。</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>小说实体。</summary>
public class Novel : Entity
{
    /// <summary>小说标题。</summary>
    public string Title { get; set; } = "";

    /// <summary>小说类型/流派。</summary>
    public string Genre { get; set; } = "";

    /// <summary>作者笔名（可空；展示于项目管理列表）。</summary>
    public string Author { get; set; } = "";

    /// <summary>目标字数（全书写作目标，>0 才显示进度用途）。</summary>
    public int TargetWords { get; set; }

    /// <summary>小说描述。</summary>
    public string Description { get; set; } = "";

    /// <summary>写作风格。</summary>
    public string WritingStyle { get; set; } = "";

    /// <summary>目标读者群（如 male_18_25, female_18_30, general）。</summary>
    public string TargetAudience { get; set; } = "";

    /// <summary>增强配置预设（commercial / literary / balanced / custom）。</summary>
    public string EnhancementProfile { get; set; } = "commercial";

    /// <summary>自动诊断模式（写完章节后自动注入诊断信号）。</summary>
    public bool AutoDiagnose { get; set; } = true;

    /// <summary>自动增强模式：none / semi / full。</summary>
    public string AutoEnhance { get; set; } = "semi";

    /// <summary>小说完结状态：ongoing（连载中）/ completed（已完结）。设定检查（CHANGE_10）据此启用伏笔回收等进度感知检查。</summary>
    public string CompletionStatus { get; set; } = "ongoing";

    /// <summary>总纲（大纲一层）：全书走向/主题/核心冲突/结局方向（作者内部规划，含剧透）。</summary>
    public string Outline { get; set; } = "";

    // 导航属性
    /// <summary>章节列表。</summary>
    public List<Chapter> Chapters { get; set; } = [];

    /// <summary>角色列表。</summary>
    public List<Character> Characters { get; set; } = [];

    /// <summary>情节线列表。</summary>
    public List<Plotline> Plotlines { get; set; } = [];

    /// <summary>组织列表。</summary>
    public List<Faction> Factions { get; set; } = [];

    /// <summary>地点列表。</summary>
    public List<Location> Locations { get; set; } = [];

    /// <summary>道具列表。</summary>
    public List<Item> Items { get; set; } = [];

    /// <summary>时间线事件列表。</summary>
    public List<TimelineEvent> TimelineEvents { get; set; } = [];

    /// <summary>关系列表。</summary>
    public List<Relationship> Relationships { get; set; } = [];

    /// <summary>世界规则列表（CHANGE_10 5.3：L2 精确算法的作者声明约束）。</summary>
    public List<WorldRule> WorldRules { get; set; } = [];

    /// <summary>场景列表。</summary>
    public List<SceneDefinition> Scenes { get; set; } = [];
}

/// <summary>章节实体。</summary>
public class Chapter : Entity
{
    /// <summary>章节标题。</summary>
    public string Title { get; set; } = "";

    /// <summary>章节序号。</summary>
    public int Order { get; set; }

    /// <summary>章节内容。</summary>
    public string Content { get; set; } = "";

    /// <summary>
    /// 正文字符数（派生字段，不持久化）。
    /// 结构索引读取（只读 frontmatter、不加载正文）时显式赋值；未赋值（-1）时回落到
    /// <see cref="Content"/>.Length——所有把正文装进 Content 的路径都无需额外维护该值。
    /// </summary>
    public int CharCount
    {
        get => _charCount >= 0 ? _charCount : Content.Length;
        set => _charCount = value;
    }

    private int _charCount = -1;

    /// <summary>章节状态。</summary>
    public ChapterStatus Status { get; set; } = ChapterStatus.Draft;

    /// <summary>所属分卷 ID（大纲二层归属；null = 未分卷）。</summary>
    public string? VolumeId { get; set; }

    /// <summary>细纲：本章梗概（大纲三层，与章节 1:1）。</summary>
    public string Summary { get; set; } = "";

    /// <summary>细纲：本章计划推进的情节线 ID 列表（对齐 SceneDefinition.PlotlineIds）。</summary>
    public List<string> PlotlineIds { get; set; } = [];

    /// <summary>细纲：本章计划出场角色 ID 列表（对齐 SceneDefinition.ParticipantIds）。</summary>
    public List<string> CharacterIds { get; set; } = [];

    /// <summary>细纲：写作备注（节奏/伏笔提示）。</summary>
    public string Notes { get; set; } = "";
}

/// <summary>
/// 分卷实体（大纲二层：卷纲）——一卷覆盖连续若干章（网文标准结构）。
/// 存储 volumes/{Name}-{Id}.yaml；章节通过 Chapter.VolumeId 归属本卷（单值，章节侧持有，
/// 避免卷侧列表与章节全局 Order 双写）。
/// </summary>
public class Volume : Entity
{
    /// <summary>卷名（如「风起云涌」）。</summary>
    public string Name { get; set; } = "";

    /// <summary>卷序号（从 1 开始）。</summary>
    public int Order { get; set; }

    /// <summary>本卷梗概（这一卷发生什么）。</summary>
    public string Summary { get; set; } = "";

    /// <summary>卷描述（本卷简介/看点；与卷纲（梗概）区分）。</summary>
    public string Description { get; set; } = "";

    /// <summary>本卷目标 / 核心冲突（读完这一卷读者获得什么）。</summary>
    public string Goal { get; set; } = "";

    /// <summary>本卷推进的情节线 ID 列表。</summary>
    public List<string> PlotlineIds { get; set; } = [];

    /// <summary>本卷关键角色 ID 列表。</summary>
    public List<string> CharacterIds { get; set; } = [];

    /// <summary>备注（节奏/伏笔/钩子提示）。</summary>
    public string Notes { get; set; } = "";
}

/// <summary>角色实体。</summary>
public class Character : Entity
{
    /// <summary>角色名称。</summary>
    public string Name { get; set; } = "";

    /// <summary>性别：男/女/其他/未知。</summary>
    public string Gender { get; set; } = "";

    /// <summary>年龄或年龄段，如「25」「少年」「中年」。</summary>
    public string Age { get; set; } = "";

    /// <summary>角色定位。</summary>
    public CharacterRole Role { get; set; } = CharacterRole.Supporting;

    /// <summary>性格描述。</summary>
    public string Personality { get; set; } = "";

    /// <summary>外貌描述。</summary>
    public string Appearance { get; set; } = "";

    /// <summary>背景故事。</summary>
    public string Background { get; set; } = "";

    /// <summary>动机/目标。</summary>
    public string Motivation { get; set; } = "";

    /// <summary>标签。</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>能力/技能。</summary>
    public string Abilities { get; set; } = "";

    /// <summary>弱点/缺陷。</summary>
    public string Weakness { get; set; } = "";

    /// <summary>习惯（口头禅、肢体习惯、行为模式）。</summary>
    public string Habits { get; set; } = "";

    /// <summary>备注。</summary>
    public string Notes { get; set; } = "";

    /// <summary>角色状态（由 AI 根据时间线事件自动维护）。</summary>
    public CharacterStatus Status { get; set; } = CharacterStatus.Active;

    /// <summary>所属组织 ID。</summary>
    public string? FactionId { get; set; }

    /// <summary>当前所在地点 ID（null = 未指定；随章节发布/设定变更更新）。</summary>
    public string? CurrentLocationId { get; set; }

    /// <summary>结构化数值指标（速度/境界/体温等），供 RuleEvaluator 的 Numeric 规则使用。</summary>
    public Dictionary<string, double> Metrics { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>情节线实体。</summary>
public class Plotline : Entity
{
    /// <summary>情节线名称。</summary>
    public string Name { get; set; } = "";

    /// <summary>情节线描述。</summary>
    public string Description { get; set; } = "";

    /// <summary>情节线类型。</summary>
    public PlotlineType Type { get; set; } = PlotlineType.Main;

    /// <summary>紧张度。</summary>
    public PlotlineTension Tension { get; set; } = PlotlineTension.Medium;

    /// <summary>计划揭示的章节 ID（写作规划，可选）。</summary>
    public string? PlannedRevealChapterId { get; set; }

    /// <summary>已解决的章节 ID（可选，写作规划）。</summary>
    public string? ResolvedAtChapterId { get; set; }

    /// <summary>关键转折（自由文本，如"第5章：真相揭露；第12章：盟友背叛；林默死亡"）。</summary>
    public string Turnpoints { get; set; } = "";

    /// <summary>情节线状态（Active/Resolved/Abandoned）。</summary>
    public PlotlineStatus Status { get; set; } = PlotlineStatus.Active;
}

/// <summary>组织/派系实体。</summary>
public class Faction : Entity
{
    /// <summary>组织名称。</summary>
    public string Name { get; set; } = "";

    /// <summary>组织类型。</summary>
    public FactionType Type { get; set; } = FactionType.Sect;

    /// <summary>组织描述。</summary>
    public string Description { get; set; } = "";

    /// <summary>领袖。</summary>
    public string Leader { get; set; } = "";

    /// <summary>总部地点 ID。</summary>
    public string? HeadquartersId { get; set; }

    /// <summary>组织目标。</summary>
    public string Goal { get; set; } = "";

    /// <summary>成员列表（ID 列表）。</summary>
    public List<string> Members { get; set; } = [];

    /// <summary>组织状态（Active/Dormant/Dissolved）。</summary>
    public FactionStatus Status { get; set; } = FactionStatus.Active;

    /// <summary>结构化数值指标（供 Numeric 规则使用）。</summary>
    public Dictionary<string, double> Metrics { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>地点实体。</summary>
public class Location : Entity
{
    /// <summary>地点名称。</summary>
    public string Name { get; set; } = "";

    /// <summary>地点类型。</summary>
    public LocationType Type { get; set; } = LocationType.City;

    /// <summary>氛围/情绪基调。</summary>
    public string Atmosphere { get; set; } = "";

    /// <summary>地点描述。</summary>
    public string Description { get; set; } = "";

    /// <summary>父地点 ID（用于层级结构）。</summary>
    public string? ParentId { get; set; }

    /// <summary>邻接地点列表（CHANGE_10 v4 · 旅行可行性 STP 数据源，每对距离单位 里）。</summary>
    public List<LocationAdjacency>? AdjacentLocations { get; set; } = [];

    /// <summary>结构化数值指标（温度/灵气浓度等，供 Numeric 规则使用）。</summary>
    public Dictionary<string, double> Metrics { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>地点状态（Active/Destroyed/Sealed/Abandoned）。</summary>
    public LocationStatus Status { get; set; } = LocationStatus.Active;
}

/// <summary>地点邻接（CHANGE_10 v4 · 1 里 = 0.5 公里 → 云端 里=km*2 换算）。</summary>
public sealed class LocationAdjacency
{
    /// <summary>邻接地点 ID。</summary>
    public string TargetId { get; set; } = "";

    /// <summary>距离（里）。</summary>
    public double DistanceLi { get; set; }
}

/// <summary>道具/物品实体。</summary>
public class Item : Entity
{
    /// <summary>道具名称。</summary>
    public string Name { get; set; } = "";

    /// <summary>道具类型。</summary>
    public ItemType Type { get; set; } = ItemType.Artifact;

    /// <summary>道具描述。</summary>
    public string Description { get; set; } = "";

    /// <summary>当前拥有者 ID（单值，便捷查询用）。</summary>
    public string? OwnerId { get; set; }

    /// <summary>所有权时段链（CHANGE_10 v4 · 缺陷 5.7：单值 OwnerId 无法表达传承/夺宝）。</summary>
    public List<ItemOwnershipSpan> OwnershipPeriods { get; set; } = [];

    /// <summary>结构化数值指标（速度/威力/耐久等，供 Numeric 规则使用）。</summary>
    public Dictionary<string, double> Metrics { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>道具状态（Active/Lost/Destroyed）。</summary>
    public ItemStatus Status { get; set; } = ItemStatus.Active;

    /// <summary>当前所在地点 ID（null = 未指定）。</summary>
    public string? LocationId { get; set; }
}

/// <summary>道具单项所有权时窗（CHANGE_10 v4 · 缺陷 5.7）。</summary>
public class ItemOwnershipSpan
{
    /// <summary>持有者 ID。</summary>
    public string OwnerId { get; set; } = "";

    /// <summary>持有开始章节 ID。</summary>
    public string? SinceChapterId { get; set; }

    /// <summary>持有结束章节 ID。</summary>
    public string? UntilChapterId { get; set; }
}

/// <summary>
/// 世界条目（CHANGE_10 5.3 · L2 硬前置）— 作者声明的世界规则/设定，
/// 无声明即无裁决（§6 原则 1）。存为小说目录下的 <c>world-rules.yaml</c>；
/// <see cref="EntryKind"/> 区分可执行规则与描述性设定。
/// </summary>
public class WorldRule : Entity
{
    /// <summary>条目类型：Rule=可执行规则；Setting=描述性世界设定。</summary>
    public WorldRuleEntryKind EntryKind { get; set; } = WorldRuleEntryKind.Rule;

    /// <summary>领域分类（展示/筛选；不参与 RuleEvaluator）。</summary>
    public WorldRuleCategory Category { get; set; } = WorldRuleCategory.Other;

    /// <summary>设定正文（EntryKind=Setting 用；规则条目留空）。</summary>
    public string Content { get; set; } = "";

    /// <summary>规则类型：规范键见 Core.Data.WorldRuleTypes（如 travel-speed / location-distance），开放词汇。</summary>
    public string Type { get; set; } = "";

    /// <summary>规则数值。</summary>
    public double Value { get; set; }

    /// <summary>单位（云端 travel-speed 只认「里/日」）。</summary>
    public string Unit { get; set; } = "";

    /// <summary>规则描述（location-distance 时为 "地点A>地点B"）。</summary>
    public string Description { get; set; } = "";

    /// <summary>规则模板类型；旧规则默认 Numeric（travel-speed / location-distance）。</summary>
    public RuleKind RuleKind { get; set; } = RuleKind.Numeric;

    /// <summary>规则主体类型（Requires/Forbids/Triggers 用）。</summary>
    public EntityType? SubjectType { get; set; }

    /// <summary>规则主体 ID；null = 该类型全部实体。</summary>
    public string? SubjectId { get; set; }

    /// <summary>数值边界/触发指标（Numeric/Triggers 用），如 speed。</summary>
    public string? Metric { get; set; }

    /// <summary>比较符：&gt; &gt;= &lt; &lt;= ==。</summary>
    public string? Operator { get; set; }

    /// <summary>阈值（Numeric 用）。</summary>
    public double? Threshold { get; set; }

    /// <summary>行动/条件 token（Requires/Forbids/Triggers 用），如 御剑/月圆。</summary>
    public string? Action { get; set; }

    /// <summary>所需状态 token（Requires 用），如 金丹。</summary>
    public string? RequiredState { get; set; }

    /// <summary>被禁止的行动 token（Forbids 用），如 御剑。</summary>
    public string? ForbiddenAction { get; set; }

    /// <summary>条件满足后的后果；复用 PlannedDelta，与普通状态变化同一条渲染/检索路径。</summary>
    public PlannedDelta? Effect { get; set; }

    /// <summary>严重度：Info=提示 / Warning=警告 / Constraint=硬约束（触发时阻断云端诊断）。</summary>
    public RuleSeverity Severity { get; set; } = RuleSeverity.Warning;

    /// <summary>规则生效起始章节 ID（null = 无下界）。</summary>
    public string? EffectiveFromChapterId { get; set; }

    /// <summary>规则生效结束章节 ID（null = 无上界）。</summary>
    public string? EffectiveUntilChapterId { get; set; }

    /// <summary>关系前置 token（member-of/has-member/relationship/owns/owned-by/parent/adjacent/headquarters）。</summary>
    public string? RequiredRelationType { get; set; }

    /// <summary>关系前置目标 ID（null = 任意目标）。</summary>
    public string? RequiredRelationTargetId { get; set; }
}

/// <summary>
/// 计划 delta（SceneDefinition.PlannedDeltas）——场景开始前声明的预期变化。
/// 与 EntityStateChange 的区别：后者是已发生事实，前者是写作前的计划；
/// 两者都进入 MaterialBoundaryDetector 的 delta 合并。
/// </summary>
public sealed class PlannedDelta
{
    /// <summary>delta 类型。</summary>
    public PlannedDeltaKind Kind { get; set; } = PlannedDeltaKind.StateChange;

    /// <summary>目标实体类型。</summary>
    public EntityType TargetEntityType { get; set; } = EntityType.Character;

    /// <summary>目标实体 ID。</summary>
    public string TargetId { get; set; } = "";

    /// <summary>变化面（StateChange/RelationChange 用）。</summary>
    public EvolutionAspect? Aspect { get; set; }

    /// <summary>变化前一句话。</summary>
    public string? FromSummary { get; set; }

    /// <summary>变化后一句话。</summary>
    public string? ToSummary { get; set; }

    /// <summary>一句话变化描述。</summary>
    public string Summary { get; set; } = "";

    /// <summary>规则 ID（RuleTrigger 用）。</summary>
    public string? RuleId { get; set; }

    /// <summary>行动类型（Action 用），如 battle/ceremony/medical。</summary>
    public string? ActionType { get; set; }
}

/// <summary>时间线事件实体。</summary>
public class TimelineEvent : Entity
{
    /// <summary>事件标题。</summary>
    public string Title { get; set; } = "";

    /// <summary>事件描述。</summary>
    public string Description { get; set; } = "";

    /// <summary>所在章节 ID。</summary>
    public string? ChapterId { get; set; }
}

/// <summary>笔记状态：收件箱 = 会话自动捕获的候选（不参与推演）；已确认/已落地/已废弃。</summary>
public enum NoteStatus
{
    /// <summary>候选（未确认，不参与远期推演）。</summary>
    Inbox,

    /// <summary>已确认（参与远期推演）。</summary>
    Active,

    /// <summary>已落地（写进正文）。</summary>
    Done,

    /// <summary>已废弃（软删除；文件保留便于追溯）。</summary>
    Dropped,
}

/// <summary>作者随手记（书级自由文本；是远期推演的一条线索来源）。</summary>
public class AuthorNote : Entity
{
    /// <summary>标题（可空 → 取正文前 16 字）。</summary>
    public string Title { get; set; } = "";

    /// <summary>正文（作者原话）。</summary>
    public string Body { get; set; } = "";

    /// <summary>标签。</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>状态（手工/显式捕获默认已确认）。</summary>
    public NoteStatus Status { get; set; } = NoteStatus.Active;

    /// <summary>记下时的写作进度（无章则 null）。</summary>
    public int? CapturedChapterOrder { get; set; }

    /// <summary>自动捕获来源会话；手工为空。</summary>
    public string SessionId { get; set; } = "";

    /// <summary>记录来源（Ai = 工具/自动捕获）。</summary>
    public AuditSource Source { get; set; } = AuditSource.User;
}

/// <summary>角色关系实体。</summary>
public class Relationship : Entity
{
    /// <summary>角色 A 的 ID。</summary>
    public string CharacterAId { get; set; } = "";

    /// <summary>角色 B 的 ID。</summary>
    public string CharacterBId { get; set; } = "";

    /// <summary>关系类型。</summary>
    public RelationshipType Type { get; set; } = RelationshipType.Friend;

    /// <summary>关系强度。</summary>
    public RelationshipStrength Strength { get; set; } = RelationshipStrength.Medium;

    /// <summary>关系描述。</summary>
    public string Description { get; set; } = "";

    /// <summary>关系起始章节 ID（时窗， CHANGE_10 v4）。</summary>
    public string? SinceChapterId { get; set; }

    /// <summary>关系结束章节 ID（时窗）。</summary>
    public string? UntilChapterId { get; set; }
}

/// <summary>场景定义实体，表示章节内一个场景单元。</summary>
public class SceneDefinition : Entity
{
    /// <summary>场景标题/名称。</summary>
    public string Title { get; set; } = "";

    /// <summary>所属章节ID（可选，场景可先于章节创建）。</summary>
    public string? ChapterId { get; set; }

    /// <summary>场景序号（章节内排序，从1开始）。</summary>
    public int Order { get; set; }

    /// <summary>地点ID（关联 Location）。</summary>
    public string? LocationId { get; set; }

    /// <summary>时间描述，如「黄昏」「午夜」「三天后」。</summary>
    public string TimeDescription { get; set; } = "";

    /// <summary>参与角色ID列表。</summary>
    public List<string> ParticipantIds { get; set; } = [];

    /// <summary>推进的情节线ID列表。一个场景可同时推进多条情节线。</summary>
    public List<string> PlotlineIds { get; set; } = [];

    /// <summary>场景中出现的道具ID列表（CHANGE_10 v3：契诃夫之枪检查数据源，可选）。</summary>
    public List<string> ItemIds { get; set; } = [];

    /// <summary>场景冲突/核心张力。</summary>
    public string Conflict { get; set; } = "";

    /// <summary>场景目标（此场景结束时达成什么）。</summary>
    public string Goal { get; set; } = "";

    /// <summary>情绪弧线描述，如「紧张→释放→希望」。</summary>
    public string EmotionalArc { get; set; } = "";

    /// <summary>场景类型: action / dialogue / reflection / transition。</summary>
    public SceneType SceneType { get; set; } = SceneType.Dialogue;

    /// <summary>备注/写作提示。</summary>
    public string Notes { get; set; } = "";

    /// <summary>
    /// 写作前声明的预期变化（本体 delta 的 planned 来源）。
    /// 与已持久化的 AppearanceRecord/EntityStateChange 一起进入素材边界检测。
    /// </summary>
    public List<PlannedDelta> PlannedDeltas { get; set; } = [];
}

/// <summary>组织关系实体。</summary>
public class FactionRelationship : Entity
{
    /// <summary>组织 A 的 ID。</summary>
    public string FactionAId { get; set; } = "";

    /// <summary>组织 B 的 ID。</summary>
    public string FactionBId { get; set; } = "";

    /// <summary>关系类型。</summary>
    public FactionRelationType Type { get; set; } = FactionRelationType.Neutral;

    /// <summary>关系描述。</summary>
    public string Description { get; set; } = "";
}

/// <summary>
/// 出场记录实体（ENTITY-EVOLUTION §3.1）——「实体×章节」出现矩阵的行，
/// 承载本体「对象/行动」的在场维度。按章分片存 appearances/ch{Order}.yaml。
/// </summary>
public class AppearanceRecord : Entity
{
    /// <summary>目标实体类型（谁出场：角色/地点/组织/道具/情节线/场景）。</summary>
    public EntityType EntityType { get; set; } = EntityType.Character;

    /// <summary>目标实体 ID。</summary>
    public string EntityId { get; set; } = "";

    /// <summary>所在章节 ID（与既有章节指针族一致，存 Id 非 Order）。</summary>
    public string ChapterId { get; set; } = "";

    /// <summary>出场强度：Present=在场有戏份 / Mentioned=被提及。</summary>
    public AppearanceMode Mode { get; set; } = AppearanceMode.Present;

    /// <summary>关联场景 ID 列表（场景未定义时仍可记录出场，可空）。</summary>
    public List<string>? SceneIds { get; set; }

    /// <summary>首次出现段落引文（PlotLens 式溯源，可空）。</summary>
    public string? EvidenceSpan { get; set; }

    /// <summary>记录来源：System=扫描 / Ai=抽取 / User=手工（复用审计三源）。</summary>
    public AuditSource Source { get; set; } = AuditSource.System;
}

/// <summary>
/// 变化事件实体（ENTITY-EVOLUTION §3.1）——本体「状态」维度载体。
/// 记录实体在某章发生的性格/行为/状态/关系等变化，按章分片存 evolution/ch{Order}.yaml。
/// </summary>
public class EntityStateChange : Entity
{
    /// <summary>变化目标实体类型。</summary>
    public EntityType TargetEntityType { get; set; } = EntityType.Character;

    /// <summary>变化目标实体 ID。</summary>
    public string TargetId { get; set; } = "";

    /// <summary>变化发生章节 ID。</summary>
    public string ChapterId { get; set; } = "";

    /// <summary>变化面（本体「状态」的切面）：状态/性格/动机/关系/能力/持有/位置/认知/行为/其他。</summary>
    public EvolutionAspect Aspect { get; set; } = EvolutionAspect.Other;

    /// <summary>变化前状态一句话（如「敌视」）。</summary>
    public string? FromSummary { get; set; }

    /// <summary>变化后状态一句话（如「信任」）。</summary>
    public string? ToSummary { get; set; }

    /// <summary>正文证据引文（可空）。</summary>
    public string? EvidenceQuote { get; set; }

    /// <summary>一句话变化描述（「第12章盟友背叛后性情大变」）。</summary>
    public string Summary { get; set; } = "";

    /// <summary>记录来源三源（同 AppearanceRecord.Source）。</summary>
    public AuditSource Source { get; set; } = AuditSource.System;

    /// <summary>结构化效果列表（发布时逐条应用；空表示仅叙事记录）。</summary>
    public List<StateEffect> Effects { get; set; } = [];

    /// <summary>章内发生顺序（1 起；设定变更为 0）。</summary>
    public int Sequence { get; set; }

    /// <summary>直接原因（一句话）。</summary>
    public string? Cause { get; set; }

    /// <summary>可见性（公开/秘密/私密）。</summary>
    public ChangeVisibility Visibility { get; set; } = ChangeVisibility.Public;

    /// <summary>在场者实体 ID 列表。</summary>
    public List<string> WitnessIds { get; set; } = [];

    /// <summary>发生地点 ID。</summary>
    public string? LocationId { get; set; }

    /// <summary>发生场景 ID。</summary>
    public string? SceneId { get; set; }

    /// <summary>故事内时间描述（如「黄昏」「三日后」）。</summary>
    public string? TimeDescription { get; set; }

    /// <summary>关联情节线 ID 列表。</summary>
    public List<string> PlotlineIds { get; set; } = [];

    /// <summary>生命周期：草稿章为 Pending，发布/设定变更后为 Committed。</summary>
    public ChangeLifecycle Lifecycle { get; set; } = ChangeLifecycle.Committed;

    /// <summary>发布/补记生效时刻；Pending 为 null。</summary>
    public DateTime? CommittedAt { get; set; }

    /// <summary>产生本记录的变更集 ID（发布/补记事务）。</summary>
    public string? ChangeSetId { get; set; }

    /// <summary>是否为联动生成（自动建议应用）。</summary>
    public bool IsCascade { get; set; }

    /// <summary>联动来源的原始变化 ID。</summary>
    public string? SourceChangeId { get; set; }

    /// <summary>记录写入时的会话 ID（跨会话摘要据此标注「上次会话」）。</summary>
    public string? SessionId { get; set; }
}

/// <summary>
/// 状态效果（EntityStateChange.Effects 的元素）——发布事务里逐条应用到目标实体的结构化修改。
/// <see cref="Field"/> 用 kebab 字段名（metrics 用 <c>metrics:key</c>）。
/// </summary>
public sealed class StateEffect
{
    /// <summary>目标实体类型。</summary>
    public EntityType TargetType { get; set; } = EntityType.Character;

    /// <summary>目标实体 ID。</summary>
    public string TargetId { get; set; } = "";

    /// <summary>字段名（kebab；metrics 用 metrics:key）。</summary>
    public string Field { get; set; } = "";

    /// <summary>操作。</summary>
    public StateEffectOperation Operation { get; set; } = StateEffectOperation.Set;

    /// <summary>新值（数值/枚举名/ID/文本；Add/Remove 为单个列表项）。</summary>
    public string? Value { get; set; }

    /// <summary>纪录用前值（不参与应用）。</summary>
    public string? From { get; set; }

    /// <summary>是否由联动计划生成。</summary>
    public bool Cascade { get; set; }

    /// <summary>联动来源的原始变化 ID。</summary>
    public string? SourceChangeId { get; set; }
}

/// <summary>
/// 一次章级发布/补记事务（变更集）——记录本次应用的全部条目，用于撤回时精确回滚。
/// 存于 {小说目录}/changesets/{chapterId}/{setId}.json。
/// </summary>
public sealed class ChapterChangeSet
{
    /// <summary>变更集 ID。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>小说 ID。</summary>
    public string NovelId { get; set; } = "";

    /// <summary>章节 ID。</summary>
    public string ChapterId { get; set; } = "";

    /// <summary>章节序号（撤回日志显示用）。</summary>
    public int ChapterOrder { get; set; }

    /// <summary>事务状态。</summary>
    public ChangeSetStatus Status { get; set; } = ChangeSetStatus.Applying;

    /// <summary>应用时刻。</summary>
    public DateTime AppliedAt { get; set; } = DateTime.UtcNow;

    /// <summary>撤回时刻。</summary>
    public DateTime? RevertedAt { get; set; }

    /// <summary>条目列表（状态变化 / 实体更新 / 实体创建）。</summary>
    public List<ChangeSetEntry> Entries { get; set; } = [];
}

/// <summary>变更集条目。</summary>
public sealed class ChangeSetEntry
{
    /// <summary>条目类型。</summary>
    public ChangeSetEntryKind Kind { get; set; }

    /// <summary>实体类型。</summary>
    public EntityType EntityType { get; set; }

    /// <summary>实体 ID。</summary>
    public string EntityId { get; set; } = "";

    /// <summary>实体显示名。</summary>
    public string EntityName { get; set; } = "";

    /// <summary>应用前快照（实体 JSON / 状态变化 JSON / 创建为 null）。</summary>
    public string? BeforeJson { get; set; }

    /// <summary>应用后快照（指纹或状态变化 JSON）。</summary>
    public string? AfterJson { get; set; }

    /// <summary>关联的状态变化记录 ID（StateChange 条目）。</summary>
    public string? StateChangeId { get; set; }

    /// <summary>是否联动生成。</summary>
    public bool Cascade { get; set; }

    /// <summary>一句话摘要。</summary>
    public string Summary { get; set; } = "";
}

/// <summary>
/// 本体动作日志（ont_action_log）——设定实体/关系/规则/状态记录的变更记录。
/// 追加式存储于 {小说目录}/ontology/actions.jsonl；不承载当前态，只记录变化与来源。
/// </summary>
public class OntologyAction
{
    /// <summary>日志条目 ID。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>所属小说 ID。</summary>
    public string NovelId { get; set; } = "";

    /// <summary>本体要素。</summary>
    public OntologyElement Element { get; set; } = OntologyElement.Object;

    /// <summary>操作类型。</summary>
    public OntologyOperation Operation { get; set; } = OntologyOperation.Update;

    /// <summary>目标实体类型。</summary>
    public EntityType TargetEntityType { get; set; } = EntityType.Novel;

    /// <summary>目标实体 ID。</summary>
    public string TargetId { get; set; } = "";

    /// <summary>目标实体显示名。</summary>
    public string TargetName { get; set; } = "";

    /// <summary>变更字段（kebab，与 YAML 键一致）；创建/删除为 null。</summary>
    public string? Field { get; set; }

    /// <summary>变化前值（截断到 200 字符）。</summary>
    public string? From { get; set; }

    /// <summary>变化后值（截断到 200 字符）。</summary>
    public string? To { get; set; }

    /// <summary>一句话变更描述。</summary>
    public string Summary { get; set; } = "";

    /// <summary>章节锚点（章节/场景/时间线事件/状态记录写入时有值，其余为 null）。</summary>
    public string? ChapterId { get; set; }

    /// <summary>正文证据引文（提案应用/状态记录时有值）。</summary>
    public string? Evidence { get; set; }

    /// <summary>若本次写入来自 Jev/LLM 提案，记录提案 ID。</summary>
    public string? ProposalId { get; set; }

    /// <summary>写入来源。</summary>
    public AuditSource Source { get; set; } = AuditSource.System;

    /// <summary>触发工具名称（AI 工具写入时有值）。</summary>
    public string? ToolName { get; set; }

    /// <summary>会话 ID。</summary>
    public string? SessionId { get; set; }

    /// <summary>写入时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>产生本动作的变更集 ID（发布/撤回事务）。</summary>
    public string? ChangeSetId { get; set; }

    /// <summary>发生地点 ID。</summary>
    public string? LocationId { get; set; }

    /// <summary>发生场景 ID。</summary>
    public string? SceneId { get; set; }

    /// <summary>关联情节线 ID 列表。</summary>
    public List<string> PlotlineIds { get; set; } = [];

    /// <summary>章内发生顺序。</summary>
    public int Sequence { get; set; }

    /// <summary>是否联动生成。</summary>
    public bool IsCascade { get; set; }

    /// <summary>联动来源的原始变化 ID。</summary>
    public string? SourceChangeId { get; set; }
}

/// <summary>一页上下文的元信息（history_list_windows 数据源）。</summary>
public sealed record SessionWindowInfo(
    string WindowId,
    string? PrevWindowId,
    int StartOrder,
    int ItemCount,
    string? EntryId,
    string? StartedAt,
    string? Reason,
    /// <summary>模型在 new_context 时写的交接说明（无则 null）。</summary>
    string? Note = null
);

/// <summary>
/// history 检索条目视图（history_* 工具输出用）。
/// ItemId = 会话 JSONL 的 entryId（12 位 hex，持久稳定，与 [id: ...] 标记一致）。
/// </summary>
public sealed record HistoryItemInfo(
    string ItemId,
    string WindowId,
    int Order,
    string Role,
    string? ToolName,
    string? Timestamp,
    string Content
);

/// <summary>Agent 会话实体（内存模型）。</summary>
public class AgentSession
{
    /// <summary>会话唯一标识符。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>关联的小说 ID。</summary>
    public string NovelId { get; set; } = "";

    /// <summary>会话标题，取自第一条 user message 前 40 字。</summary>
    public string Title { get; set; } = "";

    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>更新时间。</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 单条对话消息（内存模型）。
/// </summary>
public class ChatMessage
{
    /// <summary>消息 ID。</summary>
    public long Id { get; set; }

    /// <summary>会话 ID，标识当前对话。</summary>
    public string SessionId { get; set; } = "";

    /// <summary>消息角色: user / assistant / system / tool。</summary>
    public MessageRole Role { get; set; } = MessageRole.User;

    /// <summary>消息文本内容。</summary>
    public string Content { get; set; } = "";

    /// <summary>工具名称（仅 tool role 有值）。</summary>
    public string? ToolName { get; set; }

    /// <summary>工具调用 ID（仅 tool role 有值）。</summary>
    public string? ToolCallId { get; set; }

    /// <summary>是否为错误消息。</summary>
    public bool IsError { get; set; }

    /// <summary>推理/思考内容（仅 assistant role 有值）。</summary>
    public string? ReasoningContent { get; set; }

    /// <summary>插入顺序，表示该消息在会话中的位置。</summary>
    public int Order { get; set; }

    /// <summary>消息时间戳。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>工具调用列表 JSON（仅 assistant role 有值，从 ToolCalls 序列化）。</summary>
    public string? ToolCallsJson { get; set; }

    /// <summary>JSONL 条目唯一标识（uuidv7 尾部 8 位 hex），用于树形分支。</summary>
    public string? EntryId { get; set; }

    /// <summary>父条目 id，形成树结构；根条目为 null。</summary>
    public string? ParentEntryId { get; set; }

    /// <summary>
    /// 事件类型标记（仅 event/compact 条目有值）。
    /// 供 UI 层差异化渲染。非 event/compact 条目为 null。
    /// </summary>
    public SessionEventType? EventType { get; set; }

    /// <summary>
    /// 该 assistant 消息对应 API 调用时的完整上下文 token 数（Prompt+Completion）。
    /// 持久化为 JSONL contextTokens 字段，会话恢复时作为精确估算锚点。
    /// 仅最后一条 assistant 消息携带，旧数据缺失时为 null（降级为启发式估算）。
    /// </summary>
    public int? Tokens { get; set; }
}

/// <summary>
/// 会话事件条目（interrupt/error/model_change/variant_selected）。
/// 持久化到 JSONL 的 type="event" 行。
/// </summary>
public class SessionEvent
{
    /// <summary>事件类型。</summary>
    public SessionEventType EventType { get; set; }

    /// <summary>条目唯一标识（uuidv7 尾部 8 位 hex）。</summary>
    public string? EntryId { get; set; }

    /// <summary>父条目 id。</summary>
    public string? ParentEntryId { get; set; }

    /// <summary>插入顺序。</summary>
    public int Order { get; set; }

    /// <summary>错误码（仅 error 类型）：HTTP 状态码字符串（如 "429"/"500"）、"context_overflow"、"network"，
    /// 或循环可恢复停止原因 "empty_turn" / "tool_calls_not_continued" / "stability_tripped"。</summary>
    public string? ErrorCode { get; set; }

    /// <summary>错误/事件内容文本。</summary>
    public string? Content { get; set; }

    /// <summary>模型切换来源（仅 model_change 类型）。</summary>
    public string? From { get; set; }

    /// <summary>模型切换目标（仅 model_change 类型）。</summary>
    public string? To { get; set; }

    /// <summary>选中的变体索引（仅 variant_selected 类型）。</summary>
    public int SelectedVariantIndex { get; set; }

    /// <summary>选中的变体标签（仅 variant_selected 类型）。</summary>
    public string? SelectedLabel { get; set; }

    /// <summary>时间戳。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 叶子指针条目，指向当前活跃分支末端。
/// 持久化到 JSONL 的 type="leaf" 行。
/// </summary>
public class SessionLeaf
{
    /// <summary>指向当前活跃分支末端的条目 id。</summary>
    public string TargetId { get; set; } = "";
}

/// <summary>
/// 被放弃分支的 AI 摘要条目。
/// 持久化到 JSONL 的 type="branch_summary" 行。
/// </summary>
public class BranchSummary
{
    /// <summary>条目唯一标识。</summary>
    public string? EntryId { get; set; }

    /// <summary>父条目 id（挂在分支点下方）。</summary>
    public string? ParentEntryId { get; set; }

    /// <summary>被放弃分支的起始条目 id。</summary>
    public string? FromId { get; set; }

    /// <summary>AI 生成的摘要文本。</summary>
    public string Summary { get; set; } = "";

    /// <summary>插入顺序。</summary>
    public int Order { get; set; }

    /// <summary>时间戳。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 用户书签/标记条目。
/// 持久化到 JSONL 的 type="label" 行。
/// </summary>
public class SessionLabel
{
    /// <summary>条目唯一标识。</summary>
    public string? EntryId { get; set; }

    /// <summary>父条目 id。</summary>
    public string? ParentEntryId { get; set; }

    /// <summary>标记的目标条目 id。</summary>
    public string? TargetId { get; set; }

    /// <summary>用户自定义标签。</summary>
    public string Label { get; set; } = "";

    /// <summary>插入顺序。</summary>
    public int Order { get; set; }

    /// <summary>时间戳。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 会话树结构，供 TreeSelectorPanel 渲染使用。
/// 包含所有分支节点、活跃路径和标签信息。
/// </summary>
public class SessionTreeStructure
{
    /// <summary>所有树节点（按 order 排序）。</summary>
    public List<SessionTreeNode> Nodes { get; set; } = [];

    /// <summary>当前活跃路径上的 entryId 集合。</summary>
    public HashSet<string> ActiveEntryIds { get; set; } = [];

    /// <summary>当前活跃分支末端的 entryId（leaf targetId）。</summary>
    public string? ActiveLeafId { get; set; }

    /// <summary>是否存在分支（多于一条路径）。</summary>
    public bool HasBranches => Nodes.Any(n => n.ChildIds.Count > 1);
}

/// <summary>
/// 会话树节点，表示 JSONL 中的一个条目在树中的位置。
/// </summary>
public class SessionTreeNode
{
    /// <summary>条目唯一标识。</summary>
    public string EntryId { get; set; } = "";

    /// <summary>父条目 ID。</summary>
    public string? ParentEntryId { get; set; }

    /// <summary>子条目 ID 列表（按 order 排序）。</summary>
    public List<string> ChildIds { get; set; } = [];

    /// <summary>条目类型。</summary>
    public JsonlEntryType Type { get; set; }

    /// <summary>消息角色（仅 message 类型有值）。</summary>
    public string? Role { get; set; }

    /// <summary>显示文本（消息内容前 60 字 / 事件描述 / 标签文本）。</summary>
    public string DisplayText { get; set; } = "";

    /// <summary>插入顺序。</summary>
    public int Order { get; set; }

    /// <summary>标签列表（该节点上附加的 label 条目）。</summary>
    public List<string> Labels { get; set; } = [];

    /// <summary>是否为分支点（有多个子节点）。</summary>
    public bool IsFork => ChildIds.Count > 1;

    /// <summary>时间戳。</summary>
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// 实体变更审计日志（内存模型）。
/// </summary>
public class EntityAuditLog
{
    /// <summary>日志 ID。</summary>
    public long Id { get; set; }

    /// <summary>关联的小说 ID。</summary>
    public string NovelId { get; set; } = "";

    /// <summary>实体类型。</summary>
    public EntityType? EntityType { get; set; }

    /// <summary>实体 ID。</summary>
    public string EntityId { get; set; } = "";

    /// <summary>操作类型。</summary>
    public AuditAction Action { get; set; } = AuditAction.Create;

    /// <summary>字段名称（可选，用于字段级审计）。</summary>
    public string? FieldName { get; set; }

    /// <summary>旧值（可选）。</summary>
    public string? OldValue { get; set; }

    /// <summary>新值（可选）。</summary>
    public string? NewValue { get; set; }

    /// <summary>变更来源。</summary>
    public AuditSource Source { get; set; } = AuditSource.Ai;

    /// <summary>触发工具名称（可选）。</summary>
    public string? ToolName { get; set; }

    /// <summary>会话 ID（可选）。</summary>
    public string? SessionId { get; set; }

    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
