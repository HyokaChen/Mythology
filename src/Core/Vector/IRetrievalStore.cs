namespace Core.Vector;

/// <summary>
/// 检索存储接口：新检索内核的存储契约。
/// 替代旧的 IVectorStore（typeFilter + novelId + topK 三元组）。
///
/// 核心原则：
/// - metadata 过滤是候选集约束，先于 vec0/FTS 候选生成
/// - 索引状态可观测，failed/stale/deleted 不参与正常召回
/// - 稳定业务键，不依赖运行时字符串哈希
/// </summary>
public interface IRetrievalStore : IAsyncDisposable
{
    /// <summary>初始化存储（建表、加载扩展）。</summary>
    Task InitializeAsync(CancellationToken ct = default);

    // ── 写入 ──

    /// <summary>
    /// 写入文档及其块（原子操作）。
    /// 如果文档已存在，先标记旧块为 stale，再写入新块。
    /// 块的 Embedding 字段为 null 时标记为 pending，非 null 时标记为 ready。
    /// </summary>
    Task UpsertDocumentAsync(
        RetrievalDocument document,
        List<RetrievalChunk> chunks,
        CancellationToken ct = default
    );

    /// <summary>
    /// 更新块的 embedding 向量（从 pending → ready）。
    /// </summary>
    Task UpdateChunkEmbeddingAsync(
        string chunkId,
        float[] embedding,
        CancellationToken ct = default
    );

    /// <summary>
    /// 标记文档及其所有块为 deleted。
    /// </summary>
    Task MarkDocumentDeletedAsync(string documentId, CancellationToken ct = default);

    /// <summary>
    /// 物理删除已标记为 deleted 的文档和块（后台清理）。
    /// </summary>
    Task PurgeDeletedAsync(CancellationToken ct = default);

    /// <summary>
    /// 标记文档索引状态。
    /// </summary>
    Task UpdateDocumentStatusAsync(
        string documentId,
        IndexStatus status,
        CancellationToken ct = default
    );

    /// <summary>
    /// 仅按当前实体词典重建该小说的 FTS 分区（不重算 embedding）。
    /// 该分区记录的版本已是 <paramref name="lexicalVersion"/> 时返回 0，保证对账幂等。
    /// </summary>
    /// <returns>重分词的块数。</returns>
    Task<int> RetokenizeNovelAsync(
        string novelId,
        string lexicalVersion,
        CancellationToken ct = default
    );

    // ── 读取 ──

    /// <summary>
    /// 获取文档（不含块）。
    /// </summary>
    Task<RetrievalDocument?> GetDocumentAsync(string documentId, CancellationToken ct = default);

    /// <summary>
    /// 获取文档的所有块。
    /// </summary>
    Task<List<RetrievalChunk>> GetChunksAsync(string documentId, CancellationToken ct = default);

    /// <summary>
    /// 获取指定小说下所有已索引的文档 ID（按来源实体类型分组）。
    /// 用于启动时对账。
    /// </summary>
    Task<Dictionary<string, HashSet<string>>> GetIndexedDocumentIdsAsync(
        string novelId,
        CancellationToken ct = default
    );

    /// <summary>
    /// 获取指定小说下指定文档种类的所有文档 ID。
    /// </summary>
    Task<HashSet<string>> GetIndexedDocumentIdsByKindAsync(
        string novelId,
        DocumentKind kind,
        CancellationToken ct = default
    );

    /// <summary>
    /// 批量读取指定小说下所有已就绪（Ready）文档的版本快照，键为 document_id。
    /// 用于启动增量同步：source_revision + content_hash 与当前一致 ⇒ 无需重索引。
    /// 仅收录 Ready ⇒ 上次索引失败/部分索引的文档必然重跑。
    /// </summary>
    Task<Dictionary<string, RetrievalDocumentVersion>> GetDocumentVersionsAsync(
        string novelId,
        CancellationToken ct = default
    );

    // ── 检索 ──

    /// <summary>
    /// 执行混合检索：metadata 前置过滤 → dense + FTS 并行候选 → RRF 融合。
    /// </summary>
    Task<List<RetrievalSearchResult>> SearchAsync(
        RetrievalSearchRequest request,
        float[] queryEmbedding,
        CancellationToken ct = default
    );

    /// <summary>
    /// 仅执行 dense 向量检索（在已过滤的 chunk_id 集合内）。
    /// 返回 (chunkId, distance) 列表。
    /// </summary>
    Task<List<(string ChunkId, double Distance)>> DenseSearchAsync(
        float[] queryEmbedding,
        IReadOnlySet<string> candidateChunkIds,
        int topK,
        CancellationToken ct = default
    );

    /// <summary>
    /// 仅执行 FTS5 词法检索（在已过滤的 chunk_id 集合内）。
    /// 返回 (chunkId, bm25Score) 列表。
    /// </summary>
    Task<List<(string ChunkId, double Score)>> LexicalSearchAsync(
        string query,
        IReadOnlySet<string> candidateChunkIds,
        int topK,
        CancellationToken ct = default
    );

    /// <summary>
    /// 获取指定块 ID 的完整块信息（含 metadata）。
    /// </summary>
    Task<List<RetrievalSearchResult>> GetChunkResultsAsync(
        IReadOnlySet<string> chunkIds,
        CancellationToken ct = default
    );
}
