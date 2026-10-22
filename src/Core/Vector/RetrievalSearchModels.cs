namespace Core.Vector;

/// <summary>
/// 检索请求：结构化过滤 + 文本查询 + 排序配置。
/// 替代旧的 typeFilter + novelId + topK 三元组。
/// </summary>
public sealed record RetrievalSearchRequest
{
    /// <summary>检索域（必填）。</summary>
    public required RetrievalDomain Domain { get; init; }

    /// <summary>用户 ID（必填，调用方不能伪造）。</summary>
    public required string OwnerId { get; init; }

    /// <summary>小说 ID（小说知识必填；用户记忆按策略可选）。</summary>
    public string? NovelId { get; init; }

    /// <summary>文档种类过滤（可选多选）。</summary>
    public List<DocumentKind>? DocumentKinds { get; init; }

    /// <summary>来源实体类型过滤（可选多选）。</summary>
    public List<string>? SourceEntityTypes { get; init; }

    /// <summary>来源实体 ID 过滤（精确限定）。</summary>
    public List<string>? SourceEntityIds { get; init; }

    /// <summary>章节序号范围过滤（小说知识域）。</summary>
    public (int Min, int Max)? ChapterOrderRange { get; init; }

    /// <summary>参与角色 ID 过滤（场景/章节块）。</summary>
    public List<string>? ParticipantCharacterIds { get; init; }

    /// <summary>情节线 ID 过滤。</summary>
    public List<string>? PlotlineIds { get; init; }

    /// <summary>地点 ID 过滤。</summary>
    public string? LocationId { get; init; }

    /// <summary>场景类型过滤。</summary>
    public List<string>? SceneTypes { get; init; }

    /// <summary>实体状态过滤。</summary>
    public List<string>? EntityStatuses { get; init; }

    /// <summary>文本查询（hybrid 时必填）。</summary>
    public string? Query { get; init; }

    /// <summary>排序策略（必填或由 Tool 固定）。</summary>
    public RankingProfile RankingProfile { get; init; } = RankingProfile.WriteContext;

    /// <summary>Dense 候选数。</summary>
    public int DenseTopK { get; init; } = 20;

    /// <summary>Lexical 候选数。</summary>
    public int LexicalTopK { get; init; } = 20;

    /// <summary>最终结果数上限。</summary>
    public int FinalTopK { get; init; } = 10;

    /// <summary>是否包含调试信息（各通道 rank、分数明细）。</summary>
    public bool IncludeDebugInfo { get; init; }
}

/// <summary>
/// 检索结果：带来源、通道、定位和 metadata 的结构化结果。
/// </summary>
public sealed record RetrievalSearchResult
{
    /// <summary>块 ID。</summary>
    public required string ChunkId { get; init; }

    /// <summary>所属文档 ID。</summary>
    public required string DocumentId { get; init; }

    /// <summary>检索域。</summary>
    public required RetrievalDomain Domain { get; init; }

    /// <summary>文档种类。</summary>
    public required DocumentKind DocumentKind { get; init; }

    /// <summary>来源实体类型。</summary>
    public string? SourceEntityType { get; init; }

    /// <summary>来源实体 ID。</summary>
    public string? SourceEntityId { get; init; }

    /// <summary>来源实体名称（用于展示）。</summary>
    public string? SourceEntityName { get; init; }

    /// <summary>小说 ID。</summary>
    public string? NovelId { get; init; }

    /// <summary>块文本内容。</summary>
    public string Text { get; init; } = "";

    /// <summary>块序号。</summary>
    public int ChunkOrdinal { get; init; }

    /// <summary>原文起始 offset。</summary>
    public int SourceOffsetStart { get; init; }

    /// <summary>原文结束 offset。</summary>
    public int SourceOffsetEnd { get; init; }

    /// <summary>章节序号（小说知识域）。</summary>
    public int? ChapterOrder { get; init; }

    /// <summary>章节 ID（小说知识域）。</summary>
    public string? ChapterId { get; init; }

    /// <summary>章节标题（小说知识域）。</summary>
    public string? ChapterTitle { get; init; }

    /// <summary>命中通道。</summary>
    public HitChannel HitChannel { get; init; }

    /// <summary>最终融合分数。</summary>
    public double Score { get; init; }

    /// <summary>本地 SystemOne 判定概率（0~1）；未判定（禁用/失败/缺答案）为 null。</summary>
    public double? JudgeProbability { get; init; }

    /// <summary>Dense rank（调试模式）。</summary>
    public int? DenseRank { get; init; }

    /// <summary>Lexical rank（调试模式）。</summary>
    public int? LexicalRank { get; init; }

    /// <summary>Dense 原始距离（调试模式）。</summary>
    public double? DenseDistance { get; init; }

    /// <summary>Lexical BM25 分数（调试模式）。</summary>
    public double? LexicalScore { get; init; }

    /// <summary>边界质量。</summary>
    public string? BoundaryQuality { get; init; }

    /// <summary>前一块 ID（可展开上下文）。</summary>
    public string? PrevChunkId { get; init; }

    /// <summary>后一块 ID（可展开上下文）。</summary>
    public string? NextChunkId { get; init; }

    /// <summary>Metadata 摘要（非完整 JSON）。</summary>
    public Dictionary<string, object?>? MetadataSummary { get; init; }

    /// <summary>索引状态。</summary>
    public IndexStatus IndexStatus { get; init; }
}
