using Core.Data;

namespace Core.Cloud;

/// <summary>
/// 公共 Harness 请求 — 客户端 → 云端的关键 Fiction Harness 调用。
/// 只携带本次分析所需的最小上下文，不包含私有参数、本地路径或内部引擎类型。
/// </summary>
public sealed record HarnessRequest
{
    public string ContractVersion { get; init; } = "1.0";
    public string Capability { get; init; } = "";
    public string Depth { get; init; } = "standard";
    public string Locale { get; init; } = "zh-CN";
    public IReadOnlyList<ChapterInput> Chapters { get; init; } = [];

    /// <summary>
    /// 创作目标（P2 契约扩展，HARNESS-CLOUD §14 Q7）：scene.plan 规划语义的驱动字段。
    /// 缺省时云端注入 run.Goal 兜底（恒非空），score 分支不受影响。
    /// </summary>
    public string? SceneGoal { get; init; }

    /// <summary>本地小说 ID（云端每章派生特征持久化键控，HARNESS-CLOUD §6.4）。可选字段；缺省时云端不持久化。</summary>
    public string? NovelId { get; init; }

    /// <summary>
    /// 联想来源单元（仅 <c>fiction.associative.link</c> 使用）：一次提交的章节/场景/正文块。
    /// 缺省时该能力无输入（<c>quick</c> 档允许空 = 纯召回，用 <see cref="SceneGoal"/> 作查询）。
    /// </summary>
    public IReadOnlyList<AssociativeSourceInput>? Sources { get; init; }

    /// <summary>
    /// 用户写作配置（公开字段：题材 + 增强档位）— 云端 WritingProfileInput 镜像。
    /// 题材同时保留在 Context.PlotHints[0] 的向后兼容通道；Tone
    /// （commercial/literary/balanced）驱动云端增强档位与评分阈值选择。
    /// </summary>
    public WritingProfileInput? Profile { get; init; }

    public AnalysisContextInput? Context { get; init; }
    public AnalysisOptionsInput? Options { get; init; }

    /// <summary>
    /// §5.5：本地 LLM 语义提取结果（可选）— 当前只承载交互事件（from→to），
    /// 云端 story.check 网络层 N1/N2 优先以其重建角色交互图；
    /// 缺省或交互为空时云端回落 jieba + 共现（提取只是增强不是必需）。
    /// </summary>
    public SemanticExtractionInput? SemanticExtraction { get; init; }
}

/// <summary>
/// 联想来源单元（云端 <c>fiction.associative.link</c> 输入的一项，镜像服务端契约）。
/// 一个可抽取物 = 章节 / 场景 / 正文块；<see cref="Kind"/> 决定服务端用哪套提示词。
/// </summary>
public sealed record AssociativeSourceInput(
    string Kind,
    string Text = "",
    string Title = "",
    string Summary = "",
    string PlotlinesText = "",
    string StateChangesText = "",
    string Conflict = "",
    string Goal = "",
    string EmotionalArc = "",
    string TimeDescription = "",
    string LocationName = "",
    string Participants = "",
    string PlotlineNames = "",
    string Notes = "",
    string KnownEntities = "",
    string SourceEntityType = "Chapter",
    string SourceEntityId = "",
    string? SourceChapterId = null,
    int? SourceChapterOrder = null,
    int? ChunkOrdinal = null
);

/// <summary>
/// 联想召回命中（云端返回）。多词字段用显式 kebab 属性名——Core 不引用 ClientPlatform，
/// 无法复用 ClientJsonDefaults，故在契约上钉死 wire 名。
/// </summary>
public sealed record AssociativeHitDto(
    string Family,
    string? Channel,
    string Concept,
    string Bridge,
    double Confidence,
    double Cosine,
    [property: System.Text.Json.Serialization.JsonPropertyName("source-entity-type")]
        string SourceEntityType,
    [property: System.Text.Json.Serialization.JsonPropertyName("source-entity-id")]
        string SourceEntityId,
    [property: System.Text.Json.Serialization.JsonPropertyName("source-chapter-order")]
        int? SourceChapterOrder
);

/// <summary>
/// <c>fiction.associative.link</c> 结果载荷。
/// <paramref name="Status"/> 为 pass / warning；warning 时 <paramref name="Message"/> 说明原因
/// （未配置 LLM、embedding 不可用、缺少上下文等），此时索引未变更。
/// </summary>
public sealed record AssociativeLinkResultDto(
    string Status = "",
    string? Message = null,
    int Indexed = 0,
    IReadOnlyList<AssociativeHitDto>? Hits = null
);

public sealed record ChapterInput(
    string ClientChapterRef,
    int Order,
    string Title,
    string Role,
    string Content
);

public sealed record AnalysisContextInput(
    string PreviousChapterMode = "none",
    IReadOnlyList<ChapterInput>? PreviousChapters = null,
    IReadOnlyList<string>? CharacterHints = null,
    IReadOnlyList<string>? PlotHints = null,
    IReadOnlyList<CloudCharacterInput>? CharacterDetails = null,
    IReadOnlyList<CloudPlotlineInput>? Plotlines = null,
    IReadOnlyList<CloudItemInput>? Items = null,
    string? NovelTitle = null,
    string? CharacterVersion = null,
    string? PlotlineVersion = null,
    SetupCheckInput? Setup = null, // CHANGE_10 · fiction.setup.check 设定图（kebab wire 键 setup）
    IReadOnlyList<CloudTimelineEventInput>? Timeline = null // PlotHoleDetector 时间线冲突（kebab wire 键 timeline）
);

/// <summary>
/// 时间线事件输入 — 云端 PlotHoleDetector.DetectTimelineConflicts 的数据源（Id/标题/描述 + 归属章节引用）。
/// 章节引用用客户端章 ID（与服务端 ChapterInput.ClientChapterRef 同一命名空间）。
/// </summary>
public sealed record CloudTimelineEventInput(
    string Id,
    string Title = "",
    string Description = "",
    string? ChapterId = null
);

/// <summary>
/// 结构化角色输入 — 云端 FictionAnalyzers 需要角色性格/背景/动机等字段才能完整分析；
/// 仅名字（CharacterHints）时 CharacterArc/Immersion/Naming/PowerSystem 等分析器会降级。
/// 由本地客户端从 Novel.Characters 上传。
/// </summary>
public sealed record CloudCharacterInput(
    string Name,
    string Role = "unknown",
    string Gender = "",
    string Age = "",
    string Personality = "",
    string Background = "",
    string Motivation = "",
    string Abilities = "",
    string Weakness = "",
    string Habits = ""
);

/// <summary>结构化情节线输入 — 云端 PlotHoleDetector 依赖（埋设/回收章节）。</summary>
public sealed record CloudPlotlineInput(
    string Name,
    string Type = "sub",
    string? ResolvedAtChapterId = null,
    string? PlannedRevealChapterId = null
);

/// <summary>物品输入 — 云端 PowerSystemAnalyzer 统计物品数量。</summary>
public sealed record CloudItemInput(string Name);

public sealed record AnalysisOptionsInput(
    bool IncludeEvidence = true,
    bool IncludeFeatures = false,
    bool RetainResult = false,
    IReadOnlyList<string>? AnalyzerFocus = null,
    bool IncludeMaterialHints = false,
    int MaterialHintMaxChars = 800
);

/// <summary>
/// 用户写作配置（公开字段）— 云端 WritingProfileInput 的客户端镜像。
/// Tone 承载增强档位（commercial/literary/balanced），驱动云端增强配置与评分阈值选择。
/// </summary>
public sealed record WritingProfileInput(
    string Genre = "",
    string Tone = "",
    string WritingProfileVersion = "",
    IReadOnlyList<string>? CustomRules = null
)
{
    /// <summary>从本地写作配置构建：题材 + 增强档位 → Tone；版本/自定义规则本地暂无来源。</summary>
    public static WritingProfileInput FromProfile(WritingProfile profile) =>
        new(Genre: profile.Genre ?? "", Tone: profile.EnhancementProfile ?? "");
}

/// <summary>
/// LLM 语义提取结果（§5.5）— 本地提取动作的产物，随云端请求上传。
/// 只承载引擎真实消费的交互事件（story.check 网络层 N1/N2 交互图输入）；
/// 缺省或交互为空时云端回落 jieba 共现，提取只是增强不是必需。
/// </summary>
public sealed record SemanticExtractionInput(
    IReadOnlyList<CharacterInteractionInput>? Interactions = null
);

/// <summary>角色交互事件（LLM 提取）— from→to，type 为中文交互类型（冲突/合作/情感交换/对话/命令/密谋）。</summary>
public sealed record CharacterInteractionInput(
    string From,
    string To,
    string Type,
    int ParagraphIndex = -1
);

/// <summary>
/// 设定图输入（CHANGE_10 · fiction.setup.check）— 云端 CloudSetupInput 的客户端镜像。
/// 属性名 PascalCase，经 ClientJsonDefaults.Cloud kebab 序列化后 wire 键与云端绑定一致：
/// characters / relations / world-rules / scenes / plotlines / chapter-anchors / status /
/// novel-title / asserted-facts。设定是作者 IP，只上传结构化字段，不上传正文原文。
/// </summary>
public sealed record SetupCheckInput(
    IReadOnlyList<SetupCharacterInput>? Characters = null,
    IReadOnlyList<SetupRelationInput>? Relations = null,
    IReadOnlyList<WorldRuleInput>? WorldRules = null,
    IReadOnlyList<SetupSceneInput>? Scenes = null,
    IReadOnlyList<SetupPlotlineInput>? Plotlines = null,
    IReadOnlyList<SetupChapterAnchorInput>? ChapterAnchors = null,
    string Status = "ongoing",
    string? NovelTitle = null,
    IReadOnlyList<AssertedFactInput>? AssertedFacts = null,
    IReadOnlyList<SetupLocationInput>? Locations = null, // v2 一等实体（反漂移锚点）
    IReadOnlyList<SetupOrganizationInput>? Organizations = null, // v2 一等实体
    IReadOnlyList<SetupItemInput>? Items = null, // v2 一等实体（所有权链）
    // entity-evolution-lifecycle §6.1 通道①（E4 镜像；默认不上传，见 SetupDigestBuilder 开关）
    IReadOnlyList<CloudEntityAppearanceInput>? EntityAppearances = null,
    IReadOnlyList<CloudEvolutionEventInput>? EntityEvolutionEvents = null
);

/// <summary>
/// 实体出场摘要（entity-evolution-lifecycle §6.1）— 云端 CloudEntityAppearance 镜像。
/// 稠密出现摘要：每实体 + 其出现过的 ch_{Order} 列表 + mode 列表（一一对应）。
/// </summary>
public sealed record CloudEntityAppearanceInput(
    string EntityId,
    string EntityType, // character/location/faction/item/plotline/scene
    IReadOnlyList<string>? ChapterOrders = null, // ch_{Order} 规范化
    IReadOnlyList<string>? Modes = null // present/mentioned，与 ChapterOrders 一一对应
);

/// <summary>
/// 变化事件（entity-evolution-lifecycle §6.1）— 云端 CloudEvolutionEvent 镜像。
/// 稀疏语义事件：目标实体 + 发生章 + 变化面 + 前后态 + 证据。
/// </summary>
public sealed record CloudEvolutionEventInput(
    string TargetId,
    string TargetType, // character/location/faction/item/plotline/scene
    string ChapterOrder, // ch_{Order}
    string Aspect, // status/personality/motivation/relationship/ability/possession/location/knowledge/behavior/other
    string? From = null,
    string? To = null,
    string Summary = "",
    string? Evidence = null
);

/// <summary>设定角色（云端 CloudSetupCharacter 镜像）。</summary>
public sealed record SetupCharacterInput(
    string Id,
    string Name,
    int? Generation = null,
    string Status = "active",
    string? FirstAppearanceChapterId = null,
    string? DeathChapterId = null,
    IReadOnlyList<string>? Organizations = null,
    IReadOnlyList<string>? Abilities = null,
    string? Role = null, // protagonist/supporting/minor/antagonist
    string? Personality = null, // v2 写作锚点：缺失 → harness 编造性格 = 漂移
    string? Motivation = null, // v2 写作锚点
    string? Background = null, // v2 写作锚点
    string? Habits = null // v3 写作锚点：口头禅/习惯 → 声音一致性
);

/// <summary>设定关系（云端 CloudSetupRelation 镜像）。</summary>
public sealed record SetupRelationInput(
    string SourceId,
    string TargetId,
    string Type,
    bool Directional = true,
    string? SinceChapterId = null,
    string? UntilChapterId = null,
    string? Strength = null
);

/// <summary>世界规则（云端 CloudWorldRule 镜像；客户端暂不上传，恒空）。</summary>
public sealed record WorldRuleInput(
    string RuleId,
    string Type,
    double Value,
    string? Unit = null,
    string? Description = null
);

/// <summary>设定场景（云端 CloudSetupScene 镜像）。</summary>
public sealed record SetupSceneInput(
    string Id,
    string Name,
    string? ChapterId = null,
    string? LocationId = null,
    IReadOnlyList<string>? ParticipantIds = null,
    IReadOnlyList<string>? PlotlineIds = null,
    string? TimeDescription = null,
    IReadOnlyList<string>? ItemIds = null // v3：出场道具（契诃夫之枪检查）
);

/// <summary>带结构化 beat 的情节线（云端 CloudPlotlineWithBeats 镜像；Beats 恒 null 待 LLM 层）。</summary>
public sealed record SetupPlotlineInput(
    string Id,
    string Name,
    string Type = "sub",
    string? PlannedRevealChapterId = null,
    string? ResolvedAtChapterId = null,
    IReadOnlyList<BeatEntryInput>? Beats = null
);

/// <summary>结构化 beat（云端 CloudBeatEntry 镜像）。</summary>
public sealed record BeatEntryInput(
    string ChapterId,
    string Type,
    double Tension = 0.5,
    string? Description = null
);

/// <summary>章节锚点（云端 CloudChapterAnchor 镜像；ChapterId 必须是 ch_{Order} 规范形式）。</summary>
public sealed record SetupChapterAnchorInput(
    string ChapterId,
    int Order,
    string? StoryTimePoint = null,
    DateTimeOffset? AbsoluteTime = null,
    string? LocationId = null,
    int? WordCount = null // v3：章体量 → 均衡度 Gini
);

/// <summary>地点实体（云端 CloudSetupLocation 镜像，v2 反漂移锚点）。</summary>
public sealed record SetupLocationInput(
    string Id,
    string Name,
    string? Region = null,
    string? Description = null,
    IReadOnlyList<LocationLinkInput>? AdjacentLocations = null // v4 邻接（距离 里）
);

/// <summary>地点邻接（v4，距离单位 里；云端 1里=0.5km 换算后对比旅行速度）。</summary>
public sealed record LocationLinkInput(string TargetId, double DistanceLi);

/// <summary>组织实体（云端 CloudSetupOrganization 镜像，v2）。</summary>
public sealed record SetupOrganizationInput(
    string Id,
    string Name,
    IReadOnlyList<string>? MemberIds = null,
    string? Description = null
);

/// <summary>道具所有权时段（云端 CloudItemOwnership 镜像，5.7）。</summary>
public sealed record OwnershipInput(
    string OwnerId,
    string? SinceChapterId = null,
    string? UntilChapterId = null
);

/// <summary>道具实体（云端 CloudSetupItem 镜像，v2；Ownership 传单段=当前持有，无时窗 → 云端重叠检查静默）。</summary>
public sealed record SetupItemInput(
    string Id,
    string Name,
    IReadOnlyList<OwnershipInput>? Ownership = null,
    string? LocationId = null,
    string? FirstAppearanceChapterId = null
);

/// <summary>声明式断言（云端 CloudAssertedFact 镜像；客户端暂不上传，恒 null）。</summary>
public sealed record AssertedFactInput(
    string EntityId,
    string Attribute,
    string Value,
    string ChapterId,
    int ParagraphIndex = -1,
    string? TextSpan = null,
    double Confidence = 1.0
);

/// <summary>
/// Harness 执行结果 — 面向 Agent Loop 的结构化判断。
/// </summary>
public sealed record HarnessResult
{
    [System.Text.Json.Serialization.JsonPropertyName("invocation-id")]
    public string InvocationId { get; init; } = "";
    public string Status { get; init; } = "";
    public string Capability { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("cloud-work-units")]
    public int CloudWorkUnits { get; init; }

    /// <summary>
    /// 提交报价：202 响应携带的预估消耗（服务端预留前的估计值）。
    /// 实际结算值见 <see cref="CloudWorkUnits"/>；两者同源于服务端计费公式，
    /// 报价用于「预计 vs 实际」对账展示。
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("estimated-units")]
    public int EstimatedUnits { get; init; }

    public object? Result { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Agent 上下文快照 — 云端跨章节状态摘要。
/// </summary>
public sealed record AgentContextSnapshot
{
    public string TargetChapterRef { get; init; } = "";
    public int ChapterCount { get; init; }
    public int PreviousChapterCount { get; init; }
    public int ParagraphCount { get; init; }
    public object? Features { get; init; }

    /// <summary>
    /// 云端 context.retrieve 的 signals[]（JsonElement 数组；HARNESS-CLOUD §6.1）。
    /// 跨仓库缺口 1.5：ToSnapshot 之前丢弃此字段，基础档维度规则（F-1~F-5）
    /// 依赖 dimensionScores、S-1 依赖 signals——现补齐透传。
    /// </summary>
    public object? Signals { get; init; }

    /// <summary>
    /// 云端 context.retrieve 的 dimensionScores（JsonElement 对象，16 中文键；
    /// HARNESS-CLOUD §6.1/Q3）。基础档维度预警（F-1~F-5）数据源。
    /// </summary>
    public object? DimensionScores { get; init; }

    /// <summary>
    /// 云端 context.retrieve 的 material-hints 对象（status / prompt-block / hints）。
    /// 仅在请求 options.include-material-hints=true 时出现；缺失 = 未开启或无可召回。
    /// </summary>
    public object? MaterialHints { get; init; }
}

/// <summary>
/// 云端连接状态 — 客户端 UI/Agent 可据此调整行为，云端失败不影响本地创作。
/// </summary>
public enum CloudConnectionState
{
    Connected,
    Authenticating,
    RateLimited,
    QuotaExceeded,
    Unavailable,
    Disabled,
}
