namespace Core.Cloud;

/// <summary>
/// <c>POST /v1/knowledge/hint</c> 的请求契约。
/// 请求体由客户端用 camelCase 序列化（服务端手工读取 sceneGoal/currentText/... 字面量键）。
/// 自动素材预取只使用 SceneGoal / CurrentText / Genre / Limit / MaxChars；
/// <c>knowledge_hint</c> 工具额外使用 Tags / Kinds / Expand / Ontology。
/// </summary>
public sealed record KnowledgeHintRequest(
    string SceneGoal,
    string? CurrentText = null,
    string? Genre = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<string>? Kinds = null,
    int Limit = 5,
    int MaxChars = 1200,
    bool Expand = false,
    KnowledgeHintOntology? Ontology = null
);

/// <summary>工具侧本体增量（wire：camelCase）。</summary>
public sealed record KnowledgeHintOntology(
    int ChapterOrder,
    IReadOnlyList<KnowledgeHintOntologyDelta> Deltas
);

/// <summary>单条本体增量；字段与服务端 ReadOntologyDeltas 对齐。</summary>
public sealed record KnowledgeHintOntologyDelta(
    string Id,
    string Kind,
    string EntityType,
    string EntityId,
    string EntityName,
    string? Aspect,
    string? From,
    string? To,
    string Summary,
    string Need
);

/// <summary>
/// <c>POST /v1/knowledge/hint</c> 的响应契约（kebab-case wire）。
/// 自动预取只消费 Status / PromptBlock；完整 Hints 保留给 <c>knowledge_hint</c> 工具输出。
/// </summary>
public sealed record KnowledgeHintResponse(
    string Status,
    string PromptBlock,
    IReadOnlyList<KnowledgeHintHit> Hints,
    bool VectorUsed
);

/// <summary>素材命中的公开卡片摘要；不包含整篇文档正文。</summary>
public sealed record KnowledgeHintHit(
    Guid CardId,
    Guid? DocumentId,
    string Title,
    string Kind,
    IReadOnlyList<string> Tags,
    string Excerpt,
    string Suggestion,
    double Score,
    string SourceTitle,
    string? SourceUrl,
    IReadOnlyList<Guid>? RelatedCardIds,
    IReadOnlyList<string>? MatchedDeltaIds = null
);
