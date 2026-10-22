using System.Collections.Concurrent;
using System.Text.Json;

namespace Core.Vector;

/// <summary>
/// 基于内存的检索存储（降级 fallback）。
/// 不持久化，重启后数据丢失。仅用于 vec0 初始化失败时的降级。
/// </summary>
public sealed class InMemoryRetrievalStore : IRetrievalStore
{
    /// <summary>文档存储（内存字典）。</summary>
    private readonly ConcurrentDictionary<string, RetrievalDocument> _documents = new();

    /// <summary>块存储（内存字典）。</summary>
    private readonly ConcurrentDictionary<string, RetrievalChunk> _chunks = new();

    /// <summary>embedding 存储（内存字典）。</summary>
    private readonly ConcurrentDictionary<string, float[]> _embeddings = new();

    /// <summary>内存存储无需初始化。</summary>
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// 写入或更新文档及其所有块。
    /// 先移除旧块再写入新块，同时缓存 embedding。
    /// </summary>
    public Task UpsertDocumentAsync(
        RetrievalDocument document,
        List<RetrievalChunk> chunks,
        CancellationToken ct = default
    )
    {
        _documents[document.DocumentId] = document;

        // 移除旧块
        foreach (
            var kvp in _chunks.Where(kvp => kvp.Value.DocumentId == document.DocumentId).ToList()
        )
            _chunks.TryRemove(kvp.Key, out _);

        // 写入新块
        foreach (var chunk in chunks)
        {
            _chunks[chunk.ChunkId] = chunk;
            if (chunk.Embedding is not null)
                _embeddings[chunk.ChunkId] = chunk.Embedding;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 更新单个块的 embedding。
    /// </summary>
    public Task UpdateChunkEmbeddingAsync(
        string chunkId,
        float[] embedding,
        CancellationToken ct = default
    )
    {
        _embeddings[chunkId] = embedding;
        if (_chunks.TryGetValue(chunkId, out var chunk))
            _chunks[chunkId] = chunk; // Status update not tracked in memory
        return Task.CompletedTask;
    }

    /// <summary>
    /// 逻辑删除文档及其所有块（从字典中移除）。
    /// </summary>
    public Task MarkDocumentDeletedAsync(string documentId, CancellationToken ct = default)
    {
        _documents.TryRemove(documentId, out _);
        foreach (var kvp in _chunks.Where(kvp => kvp.Value.DocumentId == documentId).ToList())
        {
            _chunks.TryRemove(kvp.Key, out _);
            _embeddings.TryRemove(kvp.Key, out _);
        }
        return Task.CompletedTask;
    }

    /// <summary>内存存储无需物理清理。</summary>
    public Task PurgeDeletedAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// 更新文档的索引状态。
    /// </summary>
    public Task UpdateDocumentStatusAsync(
        string documentId,
        IndexStatus status,
        CancellationToken ct = default
    )
    {
        if (_documents.TryGetValue(documentId, out var doc))
            _documents[documentId] = doc with { IndexStatus = status };
        return Task.CompletedTask;
    }

    /// <summary>
    /// 根据 document_id 获取单个文档。
    /// </summary>
    public Task<RetrievalDocument?> GetDocumentAsync(
        string documentId,
        CancellationToken ct = default
    )
    {
        _documents.TryGetValue(documentId, out var doc);
        return Task.FromResult(doc);
    }

    /// <summary>
    /// 获取指定文档的所有块，按序号排序。
    /// </summary>
    public Task<List<RetrievalChunk>> GetChunksAsync(
        string documentId,
        CancellationToken ct = default
    )
    {
        var chunks = _chunks
            .Values.Where(c => c.DocumentId == documentId)
            .OrderBy(c => c.ChunkOrdinal)
            .ToList();
        return Task.FromResult(chunks);
    }

    /// <summary>
    /// 获取指定小说中已索引的实体 ID 映射（按实体类型分组）。
    /// </summary>
    public Task<Dictionary<string, HashSet<string>>> GetIndexedDocumentIdsAsync(
        string novelId,
        CancellationToken ct = default
    )
    {
        var result = _documents
            .Values.Where(d => d.NovelId == novelId && d.IndexStatus != IndexStatus.Deleted)
            .GroupBy(d => d.SourceEntityType ?? "")
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .ToDictionary(
                g => g.Key,
                g => new HashSet<string>(g.Select(d => d.SourceEntityId ?? ""))
            );
        return Task.FromResult(result);
    }

    /// <summary>
    /// 获取指定小说中某 DocumentKind 下已索引的实体 ID 集合。
    /// </summary>
    public Task<HashSet<string>> GetIndexedDocumentIdsByKindAsync(
        string novelId,
        DocumentKind kind,
        CancellationToken ct = default
    )
    {
        var ids = new HashSet<string>(
            _documents
                .Values.Where(d =>
                    d.NovelId == novelId
                    && d.DocumentKind == kind
                    && d.IndexStatus != IndexStatus.Deleted
                )
                .Select(d => d.SourceEntityId ?? "")
        );
        return Task.FromResult(ids);
    }

    /// <summary>
    /// 批量读取指定小说下所有已就绪文档的版本快照（document_id → revision + content hash）。
    /// </summary>
    public Task<Dictionary<string, RetrievalDocumentVersion>> GetDocumentVersionsAsync(
        string novelId,
        CancellationToken ct = default
    ) =>
        Task.FromResult(
            _documents
                .Values.Where(d => d.NovelId == novelId && d.IndexStatus == IndexStatus.Ready)
                .ToDictionary(
                    d => d.DocumentId,
                    d => new RetrievalDocumentVersion(d.SourceRevision, d.ContentHash)
                )
        );

    /// <summary>
    /// 执行混合检索：按过滤条件筛出候选块 → 余弦相似度排序 → 取 topK。
    /// 内存 fallback 仅支持 dense 搜索，不支持 FTS。
    /// </summary>
    public Task<List<RetrievalSearchResult>> SearchAsync(
        RetrievalSearchRequest request,
        float[] queryEmbedding,
        CancellationToken ct = default
    )
    {
        // 过滤链：按 Domain、NovelId、DocumentKinds、SourceEntityTypes、SourceEntityIds 逐级筛选候选块
        // 与 SqliteRetrievalStore.BuildFilterClause 保持相同语义
        // 内存 fallback：仅做 dense 搜索，无 FTS
        var candidates = _chunks
            .Values.Where(c =>
                c.DocumentId is not null
                && _documents.TryGetValue(c.DocumentId, out var d)
                && d.IndexStatus != IndexStatus.Deleted
                && d.Domain == request.Domain
                && (request.NovelId is null || d.NovelId == request.NovelId)
                && (
                    request.DocumentKinds is null
                    || request.DocumentKinds.Count == 0
                    || request.DocumentKinds.Contains(d.DocumentKind)
                )
                && (
                    request.SourceEntityTypes is null
                    || request.SourceEntityTypes.Count == 0
                    || (
                        d.SourceEntityType is not null
                        && request.SourceEntityTypes.Contains(d.SourceEntityType)
                    )
                )
                && (
                    request.SourceEntityIds is null
                    || request.SourceEntityIds.Count == 0
                    || (
                        d.SourceEntityId is not null
                        && request.SourceEntityIds.Contains(d.SourceEntityId)
                    )
                )
            )
            .ToList();

        // 结构化 metadata 前置过滤：与 SqliteRetrievalStore.BuildFilterClause 保持相同语义。
        // metadata 过滤是候选集约束，不得靠结果后过滤兜底（VEC-LOCAL §2.7）。
        if (
            request.ChapterOrderRange is { } range
            || request.ParticipantCharacterIds is { Count: > 0 }
            || request.PlotlineIds is { Count: > 0 }
            || !string.IsNullOrEmpty(request.LocationId)
            || request.SceneTypes is { Count: > 0 }
        )
        {
            candidates = candidates
                .Where(c =>
                {
                    var md = ParseMetadata(_documents[c.DocumentId].MetadataJson);
                    if (request.ChapterOrderRange is { } r)
                    {
                        if (
                            !md.TryGetValue("chapter-order", out var coValue)
                            || !int.TryParse(MetadataScalar(coValue), out var co)
                            || co < r.Min
                            || co > r.Max
                        )
                            return false;
                    }

                    if (
                        request.ParticipantCharacterIds is { Count: > 0 } pids
                        && !MetadataArrayContains(md, "participant-ids", pids)
                    )
                        return false;

                    if (
                        request.PlotlineIds is { Count: > 0 } plids
                        && !MetadataArrayContains(md, "plotline-ids", plids)
                    )
                        return false;

                    if (!string.IsNullOrEmpty(request.LocationId))
                    {
                        if (
                            !md.TryGetValue("location-id", out var loc)
                            || !string.Equals(
                                MetadataScalar(loc),
                                request.LocationId,
                                StringComparison.Ordinal
                            )
                        )
                            return false;
                    }

                    if (request.SceneTypes is { Count: > 0 } stypes)
                    {
                        if (
                            !md.TryGetValue("scene-type", out var st)
                            || !stypes.Contains(MetadataScalar(st) ?? "")
                        )
                            return false;
                    }

                    return true;
                })
                .ToList();
        }

        var results = new List<RetrievalSearchResult>();

        foreach (var chunk in candidates)
        {
            if (!_embeddings.TryGetValue(chunk.ChunkId, out var embedding))
                continue;

            var score = CosineSimilarity(queryEmbedding, embedding);
            var doc = _documents[chunk.DocumentId];
            var metadata = ParseMetadata(doc.MetadataJson);

            results.Add(
                new RetrievalSearchResult
                {
                    ChunkId = chunk.ChunkId,
                    DocumentId = chunk.DocumentId,
                    Domain = doc.Domain,
                    DocumentKind = doc.DocumentKind,
                    SourceEntityType = doc.SourceEntityType,
                    SourceEntityId = doc.SourceEntityId,
                    NovelId = doc.NovelId,
                    Text = chunk.Text,
                    ChunkOrdinal = chunk.ChunkOrdinal,
                    SourceOffsetStart = chunk.SourceOffsetStart,
                    SourceOffsetEnd = chunk.SourceOffsetEnd,
                    HitChannel = HitChannel.Dense,
                    Score = score,
                    BoundaryQuality = chunk.BoundaryQuality,
                    PrevChunkId = chunk.PrevChunkId,
                    NextChunkId = chunk.NextChunkId,
                    IndexStatus = IndexStatus.Ready,
                    ChapterOrder = metadata.TryGetValue("chapter-order", out var co)
                        ? int.TryParse(MetadataScalar(co), out var coInt)
                            ? coInt
                            : null
                        : null,
                    ChapterId = metadata.TryGetValue("chapter-id", out var ci)
                        ? MetadataScalar(ci)
                        : null,
                    MetadataSummary = metadata,
                }
            );
        }

        return Task.FromResult(
            results.OrderByDescending(r => r.Score).Take(request.FinalTopK).ToList()
        );
    }

    /// <summary>
    /// 在内存 embedding 上执行 dense 搜索。
    /// </summary>
    public Task<List<(string ChunkId, double Distance)>> DenseSearchAsync(
        float[] queryEmbedding,
        IReadOnlySet<string> candidateChunkIds,
        int topK,
        CancellationToken ct = default
    )
    {
        var results = new List<(string ChunkId, double Distance)>();
        foreach (var chunkId in candidateChunkIds)
        {
            if (!_embeddings.TryGetValue(chunkId, out var embedding))
                continue;
            var distance = 1.0 - CosineSimilarity(queryEmbedding, embedding);
            results.Add((chunkId, distance));
        }
        return Task.FromResult(results.OrderBy(r => r.Distance).Take(topK).ToList());
    }

    /// <summary>
    /// 内存 fallback 不支持 FTS 词法搜索，始终返回空。
    /// </summary>
    public Task<List<(string ChunkId, double Score)>> LexicalSearchAsync(
        string query,
        IReadOnlySet<string> candidateChunkIds,
        int topK,
        CancellationToken ct = default
    )
    {
        // 内存 fallback 不支持 FTS
        return Task.FromResult<List<(string ChunkId, double Score)>>([]);
    }

    /// <summary>
    /// 获取多个 chunk_id 的完整检索结果。
    /// </summary>
    public Task<List<RetrievalSearchResult>> GetChunkResultsAsync(
        IReadOnlySet<string> chunkIds,
        CancellationToken ct = default
    )
    {
        var results = new List<RetrievalSearchResult>();
        foreach (var chunkId in chunkIds)
        {
            if (!_chunks.TryGetValue(chunkId, out var chunk))
                continue;
            if (!_documents.TryGetValue(chunk.DocumentId, out var doc))
                continue;

            var metadata = ParseMetadata(doc.MetadataJson);
            results.Add(
                new RetrievalSearchResult
                {
                    ChunkId = chunk.ChunkId,
                    DocumentId = chunk.DocumentId,
                    Domain = doc.Domain,
                    DocumentKind = doc.DocumentKind,
                    SourceEntityType = doc.SourceEntityType,
                    SourceEntityId = doc.SourceEntityId,
                    NovelId = doc.NovelId,
                    Text = chunk.Text,
                    ChunkOrdinal = chunk.ChunkOrdinal,
                    SourceOffsetStart = chunk.SourceOffsetStart,
                    SourceOffsetEnd = chunk.SourceOffsetEnd,
                    BoundaryQuality = chunk.BoundaryQuality,
                    PrevChunkId = chunk.PrevChunkId,
                    NextChunkId = chunk.NextChunkId,
                    IndexStatus = IndexStatus.Ready,
                    ChapterOrder = metadata.TryGetValue("chapter-order", out var co)
                        ? int.TryParse(MetadataScalar(co), out var coInt)
                            ? coInt
                            : null
                        : null,
                    ChapterId = metadata.TryGetValue("chapter-id", out var ci)
                        ? MetadataScalar(ci)
                        : null,
                    MetadataSummary = metadata,
                }
            );
        }
        return Task.FromResult(results);
    }

    /// <summary>
    /// 释放所有内存字典。
    /// </summary>
    public ValueTask DisposeAsync()
    {
        _documents.Clear();
        _chunks.Clear();
        _embeddings.Clear();
        return ValueTask.CompletedTask;
    }

    /// <summary>计算两个向量的余弦相似度。</summary>
    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            return 0;
        double dot = 0,
            magA = 0,
            magB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        var denom = Math.Sqrt(magA) * Math.Sqrt(magB);
        return denom > 0 ? dot / denom : 0;
    }

    /// <summary>
    /// metadata JSON 数组字段是否包含目标集合中的任一值（OR 语义，同 SQLite LIKE 匹配）。
    /// </summary>
    private static bool MetadataArrayContains(
        Dictionary<string, object?> md,
        string key,
        List<string> wanted
    )
    {
        if (!md.TryGetValue(key, out var value) || value is null)
            return false;

        // System.Text.Json 反序列化数组为 JsonElement；测试/调用方也可能传 List<object>
        var items = value switch
        {
            JsonElement { ValueKind: JsonValueKind.Array } e => e.EnumerateArray()
                .Select(x => MetadataScalar(x) ?? "")
                .ToList(),
            IEnumerable<string> strs => strs.ToList(),
            _ => null,
        };

        return items?.Any(item => wanted.Contains(item, StringComparer.Ordinal))
            ?? wanted.Contains(MetadataScalar(value) ?? "", StringComparer.Ordinal);
    }

    /// <summary>
    /// 提取 metadata 标量值的字符串形式。
    /// System.Text.Json 反序列化到 object 后是 JsonElement：
    /// 字符串需 GetString()（ToString() 会带引号），数字需 GetRawText()。
    /// </summary>
    private static string? MetadataScalar(object? value) =>
        value switch
        {
            null => null,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            JsonElement { ValueKind: JsonValueKind.Number } e => e.GetRawText(),
            JsonElement e => e.ToString(),
            _ => value.ToString(),
        };

    private static Dictionary<string, object?> ParseMetadata(string json)
    {
        if (string.IsNullOrEmpty(json) || json == "{}")
            return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
