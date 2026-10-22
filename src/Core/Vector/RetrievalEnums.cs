namespace Core.Vector;

/// <summary>
/// 检索域：逻辑隔离的两个知识域。
/// 每个查询必须显式指定域，默认不能搜索全部域。
/// </summary>
public enum RetrievalDomain
{
    /// <summary>小说实体知识域：角色、章节、场景、情节线等创作事实。</summary>
    NovelKnowledge = 0,

    /// <summary>用户长期记忆域：偏好、规则、习惯、语法等用户信息。</summary>
    UserMemory = 1,

    /// <summary>会话历史域：被封页的对话消息与工具输出（接续换页的检索通道）。</summary>
    ConversationHistory = 2,
}

/// <summary>
/// 文档种类：说明检索文档的语义类型。
/// 新实体类型加入时只需声明文档种类和构建策略。
/// </summary>
public enum DocumentKind
{
    /// <summary>小说概要。</summary>
    NovelProfile = 0,

    /// <summary>角色档案。</summary>
    CharacterProfile = 1,

    /// <summary>章节摘要（标题、序号、状态）。</summary>
    ChapterSummary = 2,

    /// <summary>章节正文语义块。</summary>
    ChapterBodyChunk = 3,

    /// <summary>场景定义。</summary>
    SceneDefinition = 4,

    /// <summary>情节线档案。</summary>
    PlotlineProfile = 5,

    /// <summary>地点档案。</summary>
    LocationProfile = 6,

    /// <summary>组织档案。</summary>
    FactionProfile = 7,

    /// <summary>道具档案。</summary>
    ItemProfile = 8,

    /// <summary>时间线事件。</summary>
    TimelineEvent = 9,

    /// <summary>角色关系陈述。</summary>
    RelationshipStatement = 10,

    /// <summary>组织关系陈述。</summary>
    FactionRelationshipStatement = 11,

    /// <summary>用户显式规则。</summary>
    UserRule = 12,

    /// <summary>用户偏好。</summary>
    UserPreference = 13,

    /// <summary>会话历史条目（换页后被封存的对话消息 / 工具输出）。</summary>
    SessionWindow = 14,

    /// <summary>作者随手记（远期推演的一条线索来源）。</summary>
    AuthorNote = 15,
}

public static class DocumentKindExtensions
{
    public static string ToChineseLabel(this DocumentKind kind) =>
        kind switch
        {
            DocumentKind.NovelProfile => "小说概要",
            DocumentKind.CharacterProfile => "角色档案",
            DocumentKind.ChapterSummary => "章节摘要",
            DocumentKind.ChapterBodyChunk => "章节正文",
            DocumentKind.SceneDefinition => "场景定义",
            DocumentKind.PlotlineProfile => "情节线",
            DocumentKind.LocationProfile => "地点档案",
            DocumentKind.FactionProfile => "组织档案",
            DocumentKind.ItemProfile => "道具档案",
            DocumentKind.TimelineEvent => "时间线事件",
            DocumentKind.RelationshipStatement => "角色关系",
            DocumentKind.FactionRelationshipStatement => "组织关系",
            DocumentKind.UserRule => "用户规则",
            DocumentKind.UserPreference => "用户偏好",
            DocumentKind.SessionWindow => "会话历史",
            DocumentKind.AuthorNote => "笔记",
            _ => kind.ToString(),
        };
}

/// <summary>
/// 索引状态：可观测的索引生命周期。
/// 失败条目不得参与正常召回。
/// </summary>
public enum IndexStatus
{
    /// <summary>已接收任务未完成。</summary>
    Pending = 0,

    /// <summary>dense 与 lexical 同版本可用。</summary>
    Ready = 1,

    /// <summary>事实源已更新，旧索引仍存在。</summary>
    Stale = 2,

    /// <summary>构建、embedding、数据库写入失败。</summary>
    Failed = 3,

    /// <summary>事实源已删除，等待物理清理。</summary>
    Deleted = 4,
}

/// <summary>
/// 排序策略：不同任务使用不同的 dense/lexical/filter/recency 权重。
/// 禁止每个工具私自实现融合公式。
/// </summary>
public enum RankingProfile
{
    /// <summary>写作上下文：偏重当前章节附近场景、相关人物、硬规则。</summary>
    WriteContext = 0,

    /// <summary>一致性检查：偏重角色事实、时间线和精确名称；更高 lexical 权重。</summary>
    ConsistencyCheck = 1,

    /// <summary>伏笔/暗线搜索：更高 dense、多样性与跨章节覆盖。</summary>
    ForeshadowingSearch = 2,

    /// <summary>偏好召回：显式规则先行；对偏好才执行 hybrid。</summary>
    PreferenceRecall = 3,
}

/// <summary>
/// 命中通道：说明结果由哪条检索路径命中。
/// </summary>
[Flags]
public enum HitChannel
{
    /// <summary>未命中。</summary>
    None = 0,

    /// <summary>稠密向量检索命中。</summary>
    Dense = 1,

    /// <summary>FTS5 词法检索命中。</summary>
    Lexical = 2,

    /// <summary>精确 metadata/ID 过滤命中。</summary>
    ExactFilter = 4,
}
