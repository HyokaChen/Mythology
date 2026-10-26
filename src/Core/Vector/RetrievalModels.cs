using System.Text.Json.Serialization;

namespace Core.Vector;

/// <summary>
/// 规范化检索文档：一份可检索来源的稳定身份、域、作用域、实体归属、版本和 metadata。
/// 不是关系表——不存实体之间的边，不承担图遍历；只定义"这条检索文本来自哪里"。
/// </summary>
public sealed record RetrievalDocument
{
    /// <summary>稳定业务标识。相同域、作用域、种类、来源实体和文档用途应稳定映射为同一 ID。</summary>
    public required string DocumentId { get; init; }

    /// <summary>检索域。</summary>
    public required RetrievalDomain Domain { get; init; }

    /// <summary>用户隔离。用户长期记忆必须有；小说域保留归属。</summary>
    public string? OwnerId { get; init; }

    /// <summary>小说隔离。小说知识必须有；全局用户记忆为空。</summary>
    public string? NovelId { get; init; }

    /// <summary>文档种类。</summary>
    public required DocumentKind DocumentKind { get; init; }

    /// <summary>来源实体类型（如 Character、Chapter 等）。</summary>
    public string? SourceEntityType { get; init; }

    /// <summary>来源实体 ID。</summary>
    public string? SourceEntityId { get; init; }

    /// <summary>事实源版本（实体 UpdatedAt 的 ticks）。</summary>
    public long SourceRevision { get; init; }

    /// <summary>内容哈希（对规范化后的待索引文本计算）。</summary>
    public string ContentHash { get; init; } = "";

    /// <summary>Embedding 模型标识。</summary>
    public string EmbeddingModelId { get; init; } = "";

    /// <summary>Embedding 维度。</summary>
    public int EmbeddingDimension { get; init; }

    /// <summary>实体词典/分词版本（名称集合哈希；变化时触发重建）。</summary>
    public string LexicalVersion { get; init; } = "";

    /// <summary>索引状态。</summary>
    public IndexStatus IndexStatus { get; init; } = IndexStatus.Pending;

    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>索引完成时间。</summary>
    public DateTime? IndexedAt { get; init; }

    /// <summary>结构化 metadata JSON（受控 schema，非自由文本）。</summary>
    public string MetadataJson { get; init; } = "{}";
}

/// <summary>
/// 检索块：文档被切分后的可引用文本单元。
/// 记录字符区间、序号、块类型、父级定位和 chunk hash。
/// </summary>
public sealed record RetrievalChunk
{
    /// <summary>块的稳定标识。由 document_id + chunk ordinal + content hash 可定位。</summary>
    public required string ChunkId { get; init; }

    /// <summary>所属文档 ID。</summary>
    public required string DocumentId { get; init; }

    /// <summary>块序号（从 0 开始）。</summary>
    public int ChunkOrdinal { get; init; }

    /// <summary>块文本内容。</summary>
    public string Text { get; init; } = "";

    /// <summary>原文起始 offset（字符位置）。</summary>
    public int SourceOffsetStart { get; init; }

    /// <summary>原文结束 offset（字符位置，不含）。</summary>
    public int SourceOffsetEnd { get; init; }

    /// <summary>内容哈希。</summary>
    public string ContentHash { get; init; } = "";

    /// <summary>边界质量：heading / scene_break / blank_line / sentence_end / forced。</summary>
    public string BoundaryQuality { get; init; } = "sentence_end";

    /// <summary>前一块 ID（用于展开上下文）。</summary>
    public string? PrevChunkId { get; init; }

    /// <summary>后一块 ID（用于展开上下文）。</summary>
    public string? NextChunkId { get; init; }

    /// <summary>Embedding 向量（写入时设置，读取时不加载）。</summary>
    [JsonIgnore]
    public float[]? Embedding { get; init; }
}

/// <summary>
/// 文档版本快照（增量同步用）：事实源版本（实体 UpdatedAt ticks）+ 内容哈希。
/// 一致 ⇒ 索引内容未变，可跳过重索引。
/// </summary>
public readonly record struct RetrievalDocumentVersion(
    long SourceRevision,
    string ContentHash,
    string EmbeddingModelId,
    string LexicalVersion
);
