using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Core.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Core.Vector;

/// <summary>
/// 基于 SQLite 的检索存储：retrieval_documents + retrieval_chunks + vec0 + FTS5。
///
/// 核心设计：
/// - retrieval_documents: 文档元数据（域、种类、来源、版本、状态、metadata）
/// - retrieval_chunks: 块文本、offset、序号、边界质量
/// - vec_chunks: vec0 虚拟表，存储 chunk embedding
/// - retrieval_chunks_fts: FTS5 虚拟表，存储 chunk 文本用于词法检索
/// - metadata 前置过滤先筛出合法 chunk_id，再传给 dense/FTS 候选查询
/// </summary>
public sealed partial class SqliteRetrievalStore(
    string dbPath,
    int dimension = VectorDefaults.Dimension,
    ILogger? logger = null
) : IRetrievalStore
{
    /// <summary>序列化锁，保护所有 SQLite 操作。</summary>
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// SQLite 数据库连接，在 InitializeAsync 中打开。
    /// Pooling=False：每个库文件只持一条长生命周期连接，池化零收益，
    /// 却会让 DisposeAsync 后连接留在池中继续占用 OS 文件句柄（删除/备份/替换库文件时抛 IOException）。
    /// </summary>
    private readonly SqliteConnection _connection = new($"Data Source={dbPath};Pooling=False");

    /// <summary>是否已初始化（延迟初始化守卫）。</summary>
    private bool _initialized;

    /// <summary>中文分词扩展 simple（wangfenjin/simple）是否可用。</summary>
    private bool _simpleAvailable;

    /// <summary>每部小说一个 FTS 分区：novel_id → fts 表名。空串键代表 novel_id 为空的默认分区。</summary>
    private readonly Dictionary<string, string> _partitions = new(StringComparer.Ordinal);

    /// <summary>
    /// 初始化 SQLite 连接，加载 vec0 和 simple 分词扩展。
    /// 线程安全，仅首次调用实际执行；后续调用直接返回。
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized)
            return;

        await _lock.WaitAsync(ct);
        try
        {
            if (_initialized)
                return;

            await _connection.OpenAsync(ct);
            _connection.EnableExtensions();
            _connection.LoadVector();

            // 尝试加载中文分词扩展 simple（wangfenjin/simple）
            // 支持 jieba 分词和拼音搜索，大幅提升中文 FTS5 召回质量
            // 若加载失败则回退到 unicode61 tokenizer
            _simpleAvailable = TryLoadSimpleExtension();
            if (_simpleAvailable)
            {
                CoreLogger.RetrievalSimpleTokenizerLoaded(logger.Safe());
            }
            else
            {
                CoreLogger.RetrievalSimpleTokenizerNotAvailable(logger.Safe());
            }

            // 创建 schema（幂等：CREATE TABLE IF NOT EXISTS）
            await CreateSchemaAsync(ct);

            _initialized = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ══════════════════════════════════════════════════════
    //  写入
    // ══════════════════════════════════════════════════════

    /// <summary>
    /// 写入或更新文档及其所有块。
    /// 事务性操作：标记旧块 stale → upsert 文档行 → 插入新块，失败时回滚。
    /// </summary>
    public async Task UpsertDocumentAsync(
        RetrievalDocument document,
        List<RetrievalChunk> chunks,
        CancellationToken ct = default
    )
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(ct);

            await using var tx = await _connection.BeginTransactionAsync(ct);

            try
            {
                // 1. 标记旧文档的块为 stale（如果文档已存在）
                await MarkOldChunksStaleAsync(document.DocumentId, ct);

                // 2. Upsert 文档
                await UpsertDocumentRowAsync(document, ct);

                // 3. 写入新块
                foreach (var chunk in chunks)
                {
                    await InsertChunkRowAsync(chunk, document.NovelId, ct);
                }

                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 更新单个块的 embedding（写入 vec_chunks 表），同时将块状态标记为 Ready。
    /// </summary>
    public async Task UpdateChunkEmbeddingAsync(
        string chunkId,
        float[] embedding,
        CancellationToken ct = default
    )
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(ct);

            if (embedding.Length != dimension)
                throw new ArgumentException(
                    $"向量维度不匹配：期望 {dimension}，实际 {embedding.Length}"
                );
            var vectorStr = FormatVectorString(embedding);
            var rowid = DeriveRowId(chunkId);

            // vec0 虚拟表不支持 INSERT OR REPLACE，先删除旧条目再插入
            await using var delCmd = _connection.CreateCommand();
            delCmd.CommandText = "DELETE FROM vec_chunks WHERE rowid = $rowid";
            delCmd.Parameters.AddWithValue("$rowid", rowid);
            await delCmd.ExecuteNonQueryAsync(ct);

            await using var vecCmd = _connection.CreateCommand();
            vecCmd.CommandText =
                @"INSERT INTO vec_chunks(rowid, embedding, chunk_id)
                  VALUES ($rowid, $embedding, $chunk_id)";
            vecCmd.Parameters.AddWithValue("$rowid", rowid);
            vecCmd.Parameters.AddWithValue("$embedding", vectorStr);
            vecCmd.Parameters.AddWithValue("$chunk_id", chunkId);
            await vecCmd.ExecuteNonQueryAsync(ct);

            // 更新块状态为 ready
            await using var updCmd = _connection.CreateCommand();
            updCmd.CommandText =
                @"UPDATE retrieval_chunks SET index_status = $status WHERE chunk_id = $chunk_id";
            updCmd.Parameters.AddWithValue("$status", (int)IndexStatus.Ready);
            updCmd.Parameters.AddWithValue("$chunk_id", chunkId);
            await updCmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 逻辑删除文档及其所有块（标记为 Deleted），等待 PurgeDeletedAsync 物理清理。
    /// </summary>
    public async Task MarkDocumentDeletedAsync(string documentId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(ct);

            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                @"UPDATE retrieval_documents SET index_status = $status WHERE document_id = $document_id;
                  UPDATE retrieval_chunks SET index_status = $status WHERE document_id = $document_id";
            cmd.Parameters.AddWithValue("$status", (int)IndexStatus.Deleted);
            cmd.Parameters.AddWithValue("$document_id", documentId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 物理清理所有已标记为 Deleted 的文档和块：依次清理 vec0 条目、FTS 条目、块行、文档行。
    /// </summary>
    /// <summary>
    /// 仅按当前实体词典重建该小说的 FTS 分区（不重算 embedding）。
    /// 分区记录的版本已是目标版本时返回 0（启动对账幂等）。
    /// </summary>
    public async Task<int> RetokenizeNovelAsync(
        string novelId,
        string lexicalVersion,
        CancellationToken ct = default
    )
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(ct);
            var key = PartitionKey(novelId);
            var table = await EnsurePartitionAsync(key, ct);

            await using (var gate = _connection.CreateCommand())
            {
                gate.CommandText =
                    "SELECT lexical_version FROM retrieval_fts_partitions WHERE novel_id = $key";
                gate.Parameters.AddWithValue("$key", key);
                var current = await gate.ExecuteScalarAsync(ct) as string;
                if (string.Equals(current, lexicalVersion, StringComparison.Ordinal))
                    return 0;
            }

            var rows = new List<(string ChunkId, string Text)>();
            await using (var select = _connection.CreateCommand())
            {
                select.CommandText =
                    @"SELECT c.chunk_id, c.text
                        FROM retrieval_chunks c
                        JOIN retrieval_documents d ON d.document_id = c.document_id
                       WHERE d.novel_id = $novel AND d.index_status <> $deleted
                         AND c.index_status NOT IN ($stale)";
                select.Parameters.AddWithValue("$novel", novelId);
                select.Parameters.AddWithValue("$deleted", (int)IndexStatus.Deleted);
                select.Parameters.AddWithValue("$stale", (int)IndexStatus.Stale);
                await using var reader = await select.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    rows.Add((reader.GetString(0), reader.GetString(1)));
            }

            await using (var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(ct))
            {
                foreach (var (chunkId, text) in rows)
                {
                    await using var update = _connection.CreateCommand();
                    update.Transaction = tx;
                    update.CommandText =
                        "UPDATE retrieval_chunks SET tokens = $tokens WHERE chunk_id = $chunk_id";
                    update.Parameters.AddWithValue(
                        "$tokens",
                        LexicalText.ToIndexText(text, novelId)
                    );
                    update.Parameters.AddWithValue("$chunk_id", chunkId);
                    await update.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
            }

            await RebuildPartitionAsync(table, ct);
            await ExecuteAsync(
                @"UPDATE retrieval_fts_partitions
                     SET lexical_version = $version, chunk_count = $count, rebuilt_at = $now
                   WHERE novel_id = $key",
                ct,
                ("$version", lexicalVersion),
                ("$count", await CountPartitionChunksAsync(key, ct)),
                ("$now", DateTime.UtcNow.ToString("O", CultureInfoInvariant)),
                ("$key", key)
            );
            return rows.Count;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task PurgeDeletedAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(ct);

            // 收集待清理的块（chunk_id + 所属小说），用于清 vec0 与对应 FTS 分区
            var deletedChunks = new List<(string ChunkId, string NovelKey)>();
            await using (var selCmd = _connection.CreateCommand())
            {
                selCmd.CommandText =
                    @"SELECT c.chunk_id, COALESCE(d.novel_id, '')
                        FROM retrieval_chunks c
                        JOIN retrieval_documents d ON d.document_id = c.document_id
                       WHERE c.index_status = $status";
                selCmd.Parameters.AddWithValue("$status", (int)IndexStatus.Deleted);
                await using var reader = await selCmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    deletedChunks.Add((reader.GetString(0), reader.GetString(1)));
            }

            // 删除 vec0 条目
            foreach (var (chunkId, _) in deletedChunks)
            {
                await using var delVecCmd = _connection.CreateCommand();
                delVecCmd.CommandText = "DELETE FROM vec_chunks WHERE rowid = $rowid";
                delVecCmd.Parameters.AddWithValue("$rowid", DeriveRowId(chunkId));
                await delVecCmd.ExecuteNonQueryAsync(ct);
            }

            // 删除块和文档行（FTS 在其后按受影响分区 rebuild，external-content 因此保持一致）
            await using var delChunksCmd = _connection.CreateCommand();
            delChunksCmd.CommandText = "DELETE FROM retrieval_chunks WHERE index_status = $status";
            delChunksCmd.Parameters.AddWithValue("$status", (int)IndexStatus.Deleted);
            await delChunksCmd.ExecuteNonQueryAsync(ct);

            foreach (
                var key in deletedChunks.Select(x => x.NovelKey).Distinct(StringComparer.Ordinal)
            )
            {
                var table = await EnsurePartitionAsync(key, ct);
                await RebuildPartitionAsync(table, ct);
                await ExecuteAsync(
                    "UPDATE retrieval_fts_partitions SET chunk_count = $count WHERE novel_id = $key",
                    ct,
                    ("$count", await CountPartitionChunksAsync(key, ct)),
                    ("$key", key)
                );
            }

            await using var delDocsCmd = _connection.CreateCommand();
            delDocsCmd.CommandText = "DELETE FROM retrieval_documents WHERE index_status = $status";
            delDocsCmd.Parameters.AddWithValue("$status", (int)IndexStatus.Deleted);
            await delDocsCmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 更新文档的索引状态（如 Pending / Ready / Failed / Deleted）。
    /// </summary>
    public async Task UpdateDocumentStatusAsync(
        string documentId,
        IndexStatus status,
        CancellationToken ct = default
    )
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(ct);

            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "UPDATE retrieval_documents SET index_status = $status WHERE document_id = $document_id";
            cmd.Parameters.AddWithValue("$status", (int)status);
            cmd.Parameters.AddWithValue("$document_id", documentId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ══════════════════════════════════════════════════════
    //  读取
    // ══════════════════════════════════════════════════════

    /// <summary>
    /// 根据 document_id 获取单个文档，不存在时返回 null。
    /// </summary>
    public async Task<RetrievalDocument?> GetDocumentAsync(
        string documentId,
        CancellationToken ct = default
    )
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(ct);

            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM retrieval_documents WHERE document_id = $document_id";
            cmd.Parameters.AddWithValue("$document_id", documentId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            return ReadDocument(reader);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 获取指定文档的所有非删除块，按块序号升序排列。
    /// </summary>
    public async Task<List<RetrievalChunk>> GetChunksAsync(
        string documentId,
        CancellationToken ct = default
    )
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(ct);

            var chunks = new List<RetrievalChunk>();
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT * FROM retrieval_chunks WHERE document_id = $document_id AND index_status != $deleted ORDER BY chunk_ordinal";
            cmd.Parameters.AddWithValue("$document_id", documentId);
            cmd.Parameters.AddWithValue("$deleted", (int)IndexStatus.Deleted);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                chunks.Add(ReadChunk(reader));

            return chunks;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 获取指定小说中已索引的 NovelKnowledge 实体 ID 映射，按实体类型分组。
    /// 用于对比当前实体集合与已索引集合，决定哪些实体需要增/删索引。
    /// </summary>
    public async Task<Dictionary<string, HashSet<string>>> GetIndexedDocumentIdsAsync(
        string novelId,
        CancellationToken ct = default
    )
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(ct);

            var result = new Dictionary<string, HashSet<string>>();
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                @"SELECT source_entity_type, source_entity_id 
                  FROM retrieval_documents 
                  WHERE novel_id = $novel_id AND index_status != $deleted AND domain = $domain";
            cmd.Parameters.AddWithValue("$novel_id", novelId);
            cmd.Parameters.AddWithValue("$deleted", (int)IndexStatus.Deleted);
            cmd.Parameters.AddWithValue("$domain", (int)RetrievalDomain.NovelKnowledge);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var entityType = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var entityId = reader.IsDBNull(1) ? "" : reader.GetString(1);
                if (string.IsNullOrEmpty(entityType) || string.IsNullOrEmpty(entityId))
                    continue;

                if (!result.TryGetValue(entityType, out var set))
                {
                    set = [];
                    result[entityType] = set;
                }
                set.Add(entityId);
            }

            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 获取指定小说中某 DocumentKind 下已索引的实体 ID 集合。
    /// </summary>
    public async Task<HashSet<string>> GetIndexedDocumentIdsByKindAsync(
        string novelId,
        DocumentKind kind,
        CancellationToken ct = default
    )
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(ct);

            var ids = new HashSet<string>();
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                @"SELECT source_entity_id 
                  FROM retrieval_documents 
                  WHERE novel_id = $novel_id AND document_kind = $kind AND index_status != $deleted";
            cmd.Parameters.AddWithValue("$novel_id", novelId);
            cmd.Parameters.AddWithValue("$kind", (int)kind);
            cmd.Parameters.AddWithValue("$deleted", (int)IndexStatus.Deleted);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                ids.Add(reader.GetString(0));

            return ids;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 批量读取指定小说下所有已就绪文档的版本快照（document_id → revision + content hash）。
    /// 仅收录 Ready 状态：索引失败/部分索引的文档不满足「已最新」，必然重跑。
    /// </summary>
    public async Task<Dictionary<string, RetrievalDocumentVersion>> GetDocumentVersionsAsync(
        string novelId,
        CancellationToken ct = default
    )
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(ct);

            var versions = new Dictionary<string, RetrievalDocumentVersion>();
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                @"SELECT document_id, source_revision, content_hash, embedding_model_id
                  FROM retrieval_documents
                  WHERE novel_id = $novel_id AND index_status = $ready";
            cmd.Parameters.AddWithValue("$novel_id", novelId);
            cmd.Parameters.AddWithValue("$ready", (int)IndexStatus.Ready);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                versions[reader.GetString(0)] = new RetrievalDocumentVersion(
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3)
                );
            }
            return versions;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ══════════════════════════════════════════════════════
    //  检索
    // ══════════════════════════════════════════════════════

    /// <summary>
    /// 执行混合检索：metadata 前置过滤 → 并行 dense（vec0）+ lexical（FTS5）候选召回 → RRF 融合排序 → 完整信息组装。
    /// </summary>
    public async Task<List<RetrievalSearchResult>> SearchAsync(
        RetrievalSearchRequest request,
        float[] queryEmbedding,
        CancellationToken ct = default
    )
    {
        // 1. metadata 前置过滤 → 合法 chunk_id 集合
        var candidateChunkIds = await FilterCandidatesAsync(request, ct);
        if (candidateChunkIds.Count == 0)
            return [];

        // 2. 并行 dense + lexical 候选召回 → RRF 融合
        var config = RankingConfig.ForProfile(request.RankingProfile);
        var fused = await GetFusedResultsAsync(
            DenseSearchAsync(queryEmbedding, candidateChunkIds, request.DenseTopK, ct),
            request.Query is not null
                ? LexicalSearchAsync(
                    request.Query,
                    candidateChunkIds,
                    request.LexicalTopK,
                    request.NovelId,
                    ct,
                    // 一致性车道要精确（全部词项命中）；暗线/写作上下文用 OR 提高召回。
                    useOr: request.RankingProfile != RankingProfile.ConsistencyCheck
                )
                : Task.FromResult<List<(string ChunkId, double Score)>>([]),
            config
        );
        if (fused.Count == 0)
            return [];

        // 4. 获取完整块信息
        var topChunkIds = fused.Take(request.FinalTopK * 2).Select(f => f.ChunkId).ToHashSet();
        var chunkResults = await GetChunkResultsAsync(topChunkIds, ct);
        var chunkMap = chunkResults.ToDictionary(r => r.ChunkId);

        // 5. 组装最终结果
        var results = new List<RetrievalSearchResult>();
        foreach (
            var (
                chunkId,
                score,
                denseRank,
                lexicalRank,
                denseDist,
                lexScore,
                hitChannel
            ) in fused.Take(request.FinalTopK)
        )
        {
            if (!chunkMap.TryGetValue(chunkId, out var chunk))
                continue;

            results.Add(
                chunk with
                {
                    Score = score,
                    HitChannel = hitChannel,
                    DenseRank = request.IncludeDebugInfo ? denseRank : null,
                    LexicalRank = request.IncludeDebugInfo ? lexicalRank : null,
                    DenseDistance = request.IncludeDebugInfo ? denseDist : null,
                    LexicalScore = request.IncludeDebugInfo ? lexScore : null,
                }
            );
        }

        return results;
    }

    /// <summary>
    /// 并行执行 dense + lexical 搜索并执行 RRF 融合。
    /// </summary>
    private static async Task<
        List<(
            string ChunkId,
            double Score,
            int DenseRank,
            int LexicalRank,
            double? DenseDist,
            double? LexScore,
            HitChannel HitChannel
        )>
    > GetFusedResultsAsync(
        Task<List<(string ChunkId, double Distance)>> denseTask,
        Task<List<(string ChunkId, double Score)>> lexicalTask,
        RankingConfig config
    )
    {
        List<(string ChunkId, double Distance)> denseResults;
        List<(string ChunkId, double Score)> lexicalResults;
        try
        {
            await Task.WhenAll(denseTask, lexicalTask);
            denseResults = await denseTask;
            lexicalResults = await lexicalTask;
        }
        catch
        {
            return [];
        }

        if (denseResults.Count == 0 && lexicalResults.Count == 0)
            return [];

        return RrfFusion(denseResults, lexicalResults, config);
    }

    /// <summary>
    /// 在 vec0 上执行 dense 向量搜索。
    /// 先取 topK × 3 候选，再按 candidateChunkIds 过滤，最后返回 topK。
    /// 若 vec0 JOIN 查询失败，回退到 DenseSearchFallbackAsync（应用层过滤）。
    /// </summary>
    public async Task<List<(string ChunkId, double Distance)>> DenseSearchAsync(
        float[] queryEmbedding,
        IReadOnlySet<string> candidateChunkIds,
        int topK,
        CancellationToken ct = default
    )
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(ct);

            if (queryEmbedding.Length != dimension)
                throw new ArgumentException(
                    $"向量维度不匹配：期望 {dimension}，实际 {queryEmbedding.Length}"
                );

            if (candidateChunkIds.Count == 0)
                return [];

            var queryStr = FormatVectorString(queryEmbedding);
            var results = new List<(string ChunkId, double Distance)>();

            // vec0 搜索：先取 topK * 3，再在应用层过滤 candidateChunkIds
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                @"SELECT c.chunk_id, v.distance
                  FROM vec_chunks v
                  JOIN retrieval_chunks c ON v.rowid = $rowid_prefix || c.rowid
                  WHERE v.embedding MATCH $query AND k = $topK
                  AND c.index_status = $ready
                  AND c.chunk_id IN ($candidates)";
            cmd.Parameters.AddWithValue("$query", queryStr);
            cmd.Parameters.AddWithValue("$topK", Math.Min(topK * 3, 100));
            cmd.Parameters.AddWithValue("$ready", (int)IndexStatus.Ready);

            // 使用临时表传递候选 ID 集合
            await CreateTempCandidatesTableAsync(candidateChunkIds, ct);
            cmd.CommandText =
                @"SELECT v.chunk_id, v.distance
                  FROM vec_chunks v
                  WHERE v.embedding MATCH $query AND k = $topK
                  AND v.chunk_id IN (SELECT chunk_id FROM _search_candidates)
                  AND v.chunk_id IN (SELECT chunk_id FROM retrieval_chunks WHERE index_status = $ready)";

            try
            {
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var chunkId = reader.GetString(0);
                    var distance = reader.GetDouble(1);
                    if (candidateChunkIds.Contains(chunkId))
                        results.Add((chunkId, distance));
                }
            }
            catch (SqliteException)
            {
                // vec0 查询可能不支持 JOIN，回退到应用层过滤
                results = await DenseSearchFallbackAsync(queryStr, candidateChunkIds, topK, ct);
            }

            return results.Take(topK).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 在 FTS5 上执行词法搜索。
    /// FTS rank 返回值为负（BM25 分数取负），取负后作为正分数。
    /// 查询失败时返回空列表（不降级为全表扫描）。
    /// </summary>
    public Task<List<(string ChunkId, double Score)>> LexicalSearchAsync(
        string query,
        IReadOnlySet<string> candidateChunkIds,
        int topK,
        CancellationToken ct = default
    ) => LexicalSearchAsync(query, candidateChunkIds, topK, novelId: null, ct, useOr: true);

    /// <summary>词法检索（可指定 OR/AND 模式）。</summary>
    public Task<List<(string ChunkId, double Score)>> LexicalSearchAsync(
        string query,
        IReadOnlySet<string> candidateChunkIds,
        int topK,
        CancellationToken ct,
        bool useOr
    ) => LexicalSearchAsync(query, candidateChunkIds, topK, novelId: null, ct, useOr);

    /// <summary>词法检索（指定小说词典 + OR/AND 模式）。</summary>
    public async Task<List<(string ChunkId, double Score)>> LexicalSearchAsync(
        string query,
        IReadOnlySet<string> candidateChunkIds,
        int topK,
        string? novelId,
        CancellationToken ct,
        bool useOr
    )
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(ct);

            if (candidateChunkIds.Count == 0 || string.IsNullOrWhiteSpace(query))
                return [];

            var results = new List<(string ChunkId, double Score)>();

            await CreateTempCandidatesTableAsync(candidateChunkIds, ct);

            // 分区隔离：只在该小说的 FTS 分区上 MATCH，BM25 统计不再被别的书稀释
            var ftsTable = await EnsurePartitionAsync(PartitionKey(novelId), ct);
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                $@"SELECT f.chunk_id, f.rank
                   FROM {ftsTable} f
                   WHERE {ftsTable} MATCH $query
                   AND f.chunk_id IN (SELECT chunk_id FROM _search_candidates)
                   ORDER BY f.rank
                   LIMIT $topK";
            var terms = LexicalText.GetQueryTerms(query, novelId, useOr);
            if (terms.Length == 0)
            {
                // 没有可用实义词：保留旧的原始转义查询（单字/纯符号场景）。
                cmd.Parameters.AddWithValue("$query", EscapeFtsQuery(query));
            }
            else if (
                !useOr
                && terms.Length == 1
                && terms[0].Length <= 2
                && !NovelLexicon.IsNameFragment(terms[0])
            )
            {
                // 精确车道里唯一词项只剩「关系/场景」这类 2 字泛词时，AND 没有区分度，交给 dense。
                // 例外：实体名的 2 字片段（海野/木戸/折花）是稀有词项，必须照常查。
                return [];
            }
            else
            {
                cmd.Parameters.AddWithValue(
                    "$query",
                    LexicalText.BuildMatchQuery(query, novelId, useOr)
                );
            }
            cmd.Parameters.AddWithValue("$topK", topK);

            try
            {
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var chunkId = reader.GetString(0);
                    var score = reader.GetDouble(1);
                    results.Add((chunkId, -score)); // FTS rank 是负的 BM25
                }
            }
            catch (SqliteException)
            {
                // FTS 查询失败，返回空（不降级为全表扫描）
            }

            return results;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 获取多个 chunk_id 的完整检索结果（含文档元数据、章节信息）。
    /// 通过临时候选表 _search_candidates 批量查询。
    /// </summary>
    public async Task<List<RetrievalSearchResult>> GetChunkResultsAsync(
        IReadOnlySet<string> chunkIds,
        CancellationToken ct = default
    )
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(ct);

            var results = new List<RetrievalSearchResult>();
            if (chunkIds.Count == 0)
                return results;

            await CreateTempCandidatesTableAsync(chunkIds, ct);

            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                @"SELECT c.chunk_id, c.document_id, c.chunk_ordinal, c.text,
                         c.source_offset_start, c.source_offset_end, c.boundary_quality,
                         c.prev_chunk_id, c.next_chunk_id, c.index_status,
                         d.domain, d.document_kind, d.source_entity_type, d.source_entity_id,
                         d.novel_id, d.metadata_json
                  FROM retrieval_chunks c
                  JOIN retrieval_documents d ON c.document_id = d.document_id
                  WHERE c.chunk_id IN (SELECT chunk_id FROM _search_candidates)
                  AND c.index_status = $ready";
            cmd.Parameters.AddWithValue("$ready", (int)IndexStatus.Ready);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            // metadata_json 是 SELECT 最后一列（ordinal 15）；按列名读取，
            // 避免 SELECT 加列后 ordinal 漂移导致 ArgumentOutOfRangeException("ordinal")。
            var metadataOrdinal = reader.GetOrdinal("metadata_json");
            while (await reader.ReadAsync(ct))
            {
                var metadataJson = reader.IsDBNull(metadataOrdinal)
                    ? "{}"
                    : reader.GetString(metadataOrdinal);
                var metadata = ParseMetadata(metadataJson);

                results.Add(
                    new RetrievalSearchResult
                    {
                        ChunkId = reader.GetString(0),
                        DocumentId = reader.GetString(1),
                        ChunkOrdinal = reader.GetInt32(2),
                        Text = reader.GetString(3),
                        SourceOffsetStart = reader.GetInt32(4),
                        SourceOffsetEnd = reader.GetInt32(5),
                        BoundaryQuality = reader.IsDBNull(6) ? null : reader.GetString(6),
                        PrevChunkId = reader.IsDBNull(7) ? null : reader.GetString(7),
                        NextChunkId = reader.IsDBNull(8) ? null : reader.GetString(8),
                        IndexStatus = (IndexStatus)reader.GetInt32(9),
                        Domain = (RetrievalDomain)reader.GetInt32(10),
                        DocumentKind = (DocumentKind)reader.GetInt32(11),
                        SourceEntityType = reader.IsDBNull(12) ? null : reader.GetString(12),
                        SourceEntityId = reader.IsDBNull(13) ? null : reader.GetString(13),
                        NovelId = reader.IsDBNull(14) ? null : reader.GetString(14),
                        ChapterOrder = GetMetadataInt(metadata, "chapter-order"),
                        ChapterId = GetMetadataString(metadata, "chapter-id"),
                        ChapterTitle = GetMetadataString(metadata, "chapter-title"),
                        MetadataSummary = metadata,
                    }
                );
            }

            return results;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ══════════════════════════════════════════════════════
    //  释放
    // ══════════════════════════════════════════════════════

    /// <summary>
    /// 释放 SQLite 连接和序列化锁。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_initialized)
            await _connection.CloseAsync();
        await _connection.DisposeAsync();
        _lock.Dispose();
    }

    // ══════════════════════════════════════════════════════
    //  内部方法
    // ══════════════════════════════════════════════════════

    /// <summary>确保已初始化，未初始化时自动调用 InitializeAsync。</summary>
    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (!_initialized)
            await InitializeAsync(ct);
    }

    private async Task CreateSchemaAsync(CancellationToken ct)
    {
        // 1. retrieval_documents：文档元数据表，记录每个实体的域、种类、来源、版本、索引状态和 JSON metadata
        await using var docCmd = _connection.CreateCommand();
        docCmd.CommandText =
            @"CREATE TABLE IF NOT EXISTS retrieval_documents (
                document_id       TEXT PRIMARY KEY,
                domain            INTEGER NOT NULL,
                owner_id          TEXT,
                novel_id          TEXT,
                document_kind     INTEGER NOT NULL,
                source_entity_type TEXT,
                source_entity_id  TEXT,
                source_revision   INTEGER NOT NULL DEFAULT 0,
                content_hash      TEXT NOT NULL DEFAULT '',
                embedding_model_id TEXT NOT NULL DEFAULT '',
                embedding_dimension INTEGER NOT NULL DEFAULT 0,
                lexical_version   TEXT NOT NULL DEFAULT '',
                index_status      INTEGER NOT NULL DEFAULT 0,
                created_at        TEXT NOT NULL,
                indexed_at        TEXT,
                metadata_json     TEXT NOT NULL DEFAULT '{}'
              );
              CREATE INDEX IF NOT EXISTS idx_doc_novel_kind ON retrieval_documents(novel_id, document_kind, index_status);
              CREATE INDEX IF NOT EXISTS idx_doc_source ON retrieval_documents(source_entity_type, source_entity_id, index_status);
              CREATE INDEX IF NOT EXISTS idx_doc_novel_entity ON retrieval_documents(novel_id, source_entity_type, index_status);";
        await docCmd.ExecuteNonQueryAsync(ct);

        // 2. retrieval_chunks：块存储表，记录每个块的文本、offset、序号、边界质量、前后块链接和索引状态
        await using var chunkCmd = _connection.CreateCommand();
        chunkCmd.CommandText =
            @"CREATE TABLE IF NOT EXISTS retrieval_chunks (
                chunk_id          TEXT PRIMARY KEY,
                document_id       TEXT NOT NULL,
                chunk_ordinal     INTEGER NOT NULL DEFAULT 0,
                text              TEXT NOT NULL DEFAULT '',
                tokens            TEXT NOT NULL DEFAULT '',
                source_offset_start INTEGER NOT NULL DEFAULT 0,
                source_offset_end   INTEGER NOT NULL DEFAULT 0,
                content_hash      TEXT NOT NULL DEFAULT '',
                boundary_quality  TEXT NOT NULL DEFAULT 'sentence_end',
                prev_chunk_id     TEXT,
                next_chunk_id     TEXT,
                index_status      INTEGER NOT NULL DEFAULT 0,
                rowid             INTEGER,
                FOREIGN KEY (document_id) REFERENCES retrieval_documents(document_id)
              );
              CREATE INDEX IF NOT EXISTS idx_chunk_doc ON retrieval_chunks(document_id, chunk_ordinal);
              CREATE INDEX IF NOT EXISTS idx_chunk_status ON retrieval_chunks(index_status);";
        await chunkCmd.ExecuteNonQueryAsync(ct);

        // 3. vec_chunks：vec0 虚拟表，存储 chunk 的 embedding 向量，支持余弦距离 ANN 搜索
        // vec0 表一旦创建维度固定，无法 ALTER；若已有表维度与当前配置不一致，
        // 必须 DROP 重建，否则插入不同维度向量会抛 Dimension mismatch（向量索引是派生数据，可经 VectorSync 重建）。
        await DropVecTableIfDimensionMismatchAsync("vec_chunks", ct);
        await using var vecCmd = _connection.CreateCommand();
        vecCmd.CommandText =
            $@"CREATE VIRTUAL TABLE IF NOT EXISTS vec_chunks USING vec0(
                embedding float[{dimension}],
                chunk_id text
              );";
        await vecCmd.ExecuteNonQueryAsync(ct);

        // 4. 词法层：tokens 列 + 预分词 FTS5 + 同步触发器（含旧 text 索引迁移）
        await EnsureLexicalSchemaAsync(ct);
    }

    /// <summary>
    /// 确保 retrieval_chunks.tokens 与 retrieval_chunks_fts(tokens) 就绪。
    /// 旧库使用 text 列 + unicode61，中文整句会被当成一个 token；这里一次性重建为
    /// jieba 预分词后的词级 FTS，并回填既有 chunk。
    /// </summary>
    private async Task EnsureLexicalSchemaAsync(CancellationToken ct)
    {
        // 1) tokens 列（老库补列）
        if (!await ColumnExistsAsync("retrieval_chunks", "tokens", ct))
        {
            await ExecuteAsync(
                "ALTER TABLE retrieval_chunks ADD COLUMN tokens TEXT NOT NULL DEFAULT ''",
                ct
            );
            // 老库的 tokens 为空：用当前词典回填一遍（版本门之后仍会按小说词典重算）
            await BackfillTokensAsync(ct);
        }

        // 2) 分区元数据表
        await ExecuteAsync(
            @"CREATE TABLE IF NOT EXISTS retrieval_fts_partitions (
                novel_id        TEXT PRIMARY KEY,
                table_name      TEXT NOT NULL UNIQUE,
                chunk_count     INTEGER NOT NULL DEFAULT 0,
                lexical_version TEXT NOT NULL DEFAULT '',
                rebuilt_at      TEXT
              )",
            ct
        );

        // 3) 旧版全局 FTS 迁移：先删触发器（顺序不可反，否则 fts5 'delete' 会对不存在的行报 malformed），
        //    再按小说建分区并逐个 rebuild，最后删掉全局表。
        if (await TableExistsAsync("retrieval_chunks_fts", ct))
        {
            foreach (
                var trigger in new[] { "fts_chunk_insert", "fts_chunk_delete", "fts_chunk_update" }
            )
                await ExecuteAsync($"DROP TRIGGER IF EXISTS {trigger}", ct);

            foreach (var key in await DistinctNovelKeysAsync(ct))
            {
                var table = await EnsurePartitionAsync(key, ct);
                await RebuildPartitionAsync(table, ct);
            }

            await ExecuteAsync("DROP TABLE IF EXISTS retrieval_chunks_fts", ct);
        }

        // 4) 仍在用旧触发器的库：清掉残留触发器（分区由 C# 显式维护）
        foreach (
            var trigger in new[] { "fts_chunk_insert", "fts_chunk_delete", "fts_chunk_update" }
        )
            await ExecuteAsync($"DROP TRIGGER IF EXISTS {trigger}", ct);

        // 5) 文档级 lexical_version 列已被分区级版本取代（老库移除，避免死字段）
        if (await ColumnExistsAsync("retrieval_documents", "lexical_version", ct))
            await ExecuteAsync("ALTER TABLE retrieval_documents DROP COLUMN lexical_version", ct);

        // 6) 分区自愈：记录块数与实际不一致时按 tokens 重建
        await HealPartitionsAsync(ct);
    }

    /// <summary>分区键：null/空 → 默认分区。</summary>
    private static string PartitionKey(string? novelId) =>
        string.IsNullOrEmpty(novelId) ? "" : novelId;

    /// <summary>分区后缀：稳定哈希，SQL 安全。</summary>
    private static string PartitionSuffix(string key)
    {
        if (key.Length == 0)
            return "default";
        var hash = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(key)
        );
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    /// <summary>
    /// 确保该分区存在（view + external-content FTS 表 + 元数据行），返回 FTS 表名。
    /// view 用 novel_id 字面量过滤：SQLite 视图不能带参数，novel_id 做单引号转义。
    /// </summary>
    private async Task<string> EnsurePartitionAsync(string key, CancellationToken ct)
    {
        if (_partitions.TryGetValue(key, out var cached))
            return cached;

        var suffix = PartitionSuffix(key);
        var view = $"chunks_p_{suffix}";
        var table = $"fts_p_{suffix}";
        var filter =
            key.Length == 0 ? "d.novel_id IS NULL" : $"d.novel_id = '{key.Replace("'", "''")}'";

        // view 的 rowid 必须显式列出，external-content 需要 content_rowid
        await ExecuteAsync(
            $@"CREATE VIEW IF NOT EXISTS {view} AS
                 SELECT c.rowid AS rowid, c.chunk_id AS chunk_id, c.tokens AS tokens
                   FROM retrieval_chunks c
                   JOIN retrieval_documents d ON d.document_id = c.document_id
                  WHERE {filter}",
            ct
        );
        await ExecuteAsync(
            $@"CREATE VIRTUAL TABLE IF NOT EXISTS {table} USING fts5(
                 chunk_id UNINDEXED, tokens, content='{view}', content_rowid='rowid')",
            ct
        );
        await ExecuteAsync(
            @"INSERT INTO retrieval_fts_partitions(novel_id, table_name, chunk_count, lexical_version)
              VALUES($key, $table, 0, '')
              ON CONFLICT(novel_id) DO UPDATE SET table_name = excluded.table_name",
            ct,
            ("$key", key),
            ("$table", table)
        );

        _partitions[key] = table;
        return table;
    }

    /// <summary>按 tokens 列重建该分区索引（分区级，成本随该小说块数线性）。</summary>
    private async Task RebuildPartitionAsync(string table, CancellationToken ct)
    {
        await ExecuteAsync($"INSERT INTO {table}({table}) VALUES('rebuild')", ct);
    }

    /// <summary>统计该分区应有块数（join 文档，排除已删除文档）。</summary>
    private async Task<long> CountPartitionChunksAsync(string key, CancellationToken ct)
    {
        var filter =
            key.Length == 0 ? "d.novel_id IS NULL" : $"d.novel_id = '{key.Replace("'", "''")}'";
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            $@"SELECT COUNT(*) FROM retrieval_chunks c
                 JOIN retrieval_documents d ON d.document_id = c.document_id
                WHERE {filter} AND d.index_status <> $deleted";
        cmd.Parameters.AddWithValue("$deleted", (int)IndexStatus.Deleted);
        return (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
    }

    /// <summary>分区自愈：记录块数与实际不符时重建，并刷新计数。</summary>
    private async Task HealPartitionsAsync(CancellationToken ct)
    {
        var partitions = new List<(string Key, string Table, long Count)>();
        await using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText =
                "SELECT novel_id, table_name, chunk_count FROM retrieval_fts_partitions";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                partitions.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        }

        foreach (var (key, table, count) in partitions)
        {
            _partitions[key] = table;
            var actual = await CountPartitionChunksAsync(key, ct);
            if (actual == count)
                continue;
            await RebuildPartitionAsync(table, ct);
            await ExecuteAsync(
                "UPDATE retrieval_fts_partitions SET chunk_count = $count WHERE novel_id = $key",
                ct,
                ("$count", actual),
                ("$key", key)
            );
        }
    }

    /// <summary>库中出现的所有小说键（含默认分区）。</summary>
    private async Task<List<string>> DistinctNovelKeysAsync(CancellationToken ct)
    {
        var keys = new List<string>();
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            @"SELECT DISTINCT COALESCE(novel_id, '') FROM retrieval_documents WHERE index_status <> $deleted";
        cmd.Parameters.AddWithValue("$deleted", (int)IndexStatus.Deleted);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            keys.Add(reader.GetString(0));
        return keys;
    }

    /// <summary>老库 tokens 回填（默认词典；版本门之后会按小说词典重算）。</summary>
    private async Task BackfillTokensAsync(CancellationToken ct)
    {
        var rows = new List<(long RowId, string Text)>();
        await using (var select = _connection.CreateCommand())
        {
            select.CommandText = "SELECT rowid, text FROM retrieval_chunks WHERE text <> ''";
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rows.Add((reader.GetInt64(0), reader.GetString(1)));
        }
        foreach (var (rowId, text) in rows)
        {
            await ExecuteAsync(
                "UPDATE retrieval_chunks SET tokens = $tokens WHERE rowid = $rowid",
                ct,
                ("$tokens", LexicalText.ToIndexText(text)),
                ("$rowid", rowId)
            );
        }
    }

    private async Task<bool> TableExistsAsync(string name, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name";
        cmd.Parameters.AddWithValue("$name", name);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private async Task<bool> ColumnExistsAsync(string table, string column, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private async Task ExecuteAsync(
        string sql,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters
    )
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 检测 vec0 虚拟表列出的向量维度；与当前配置不一致时 DROP 重建。
    /// vec0 表创建后维度固定（无法 ALTER），维度漂移时必须在插入前重建，
    /// 否则写入不同维度向量会抛 SQLite Dimension mismatch。
    /// 只匹配本 store 管理的表名，避免误删其他向量表。
    /// </summary>
    private async Task DropVecTableIfDimensionMismatchAsync(string tableName, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = $name";
        cmd.Parameters.AddWithValue("$name", tableName);
        var sql = await cmd.ExecuteScalarAsync(ct) as string;
        if (string.IsNullOrEmpty(sql))
            return; // 表不存在，无需处理

        // 从 "embedding float[N]" 提取 N
        var match = EmbeddingRegex().Match(sql);
        if (!match.Success)
            return; // 无法解析维度，保守不删
        if (!int.TryParse(match.Groups[1].Value, out var existingDim))
            return;
        if (existingDim == dimension)
            return; // 维度一致，无需重建

        CoreLogger.RetrievalVecDimensionMismatch(logger.Safe(), tableName, existingDim, dimension);
        await using var dropCmd = _connection.CreateCommand();
        dropCmd.CommandText = $"DROP TABLE {tableName}";
        await dropCmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>将文档的现有 Ready 块标记为 Stale（在 upsert 新块前调用）。</summary>
    private async Task MarkOldChunksStaleAsync(string documentId, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            @"UPDATE retrieval_chunks SET index_status = $stale WHERE document_id = $document_id AND index_status = $ready";
        cmd.Parameters.AddWithValue("$stale", (int)IndexStatus.Stale);
        cmd.Parameters.AddWithValue("$document_id", documentId);
        cmd.Parameters.AddWithValue("$ready", (int)IndexStatus.Ready);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>插入或替换文档行（INSERT OR REPLACE）。</summary>
    private async Task UpsertDocumentRowAsync(RetrievalDocument doc, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            @"INSERT OR REPLACE INTO retrieval_documents
              (document_id, domain, owner_id, novel_id, document_kind, source_entity_type, source_entity_id,
               source_revision, content_hash, embedding_model_id, embedding_dimension,
               index_status, created_at, indexed_at, metadata_json)
              VALUES
              ($document_id, $domain, $owner_id, $novel_id, $document_kind, $source_entity_type, $source_entity_id,
               $source_revision, $content_hash, $embedding_model_id, $embedding_dimension,
               $index_status, $created_at, $indexed_at, $metadata_json)";
        cmd.Parameters.AddWithValue("$document_id", doc.DocumentId);
        cmd.Parameters.AddWithValue("$domain", (int)doc.Domain);
        cmd.Parameters.AddWithValue("$owner_id", (object?)doc.OwnerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$novel_id", (object?)doc.NovelId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$document_kind", (int)doc.DocumentKind);
        cmd.Parameters.AddWithValue(
            "$source_entity_type",
            (object?)doc.SourceEntityType ?? DBNull.Value
        );
        cmd.Parameters.AddWithValue(
            "$source_entity_id",
            (object?)doc.SourceEntityId ?? DBNull.Value
        );
        cmd.Parameters.AddWithValue("$source_revision", doc.SourceRevision);
        cmd.Parameters.AddWithValue("$content_hash", doc.ContentHash);
        cmd.Parameters.AddWithValue("$embedding_model_id", doc.EmbeddingModelId);
        cmd.Parameters.AddWithValue("$embedding_dimension", doc.EmbeddingDimension);
        cmd.Parameters.AddWithValue("$index_status", (int)doc.IndexStatus);
        cmd.Parameters.AddWithValue(
            "$created_at",
            doc.CreatedAt.ToString("O", CultureInfo.InvariantCulture)
        );
        cmd.Parameters.AddWithValue(
            "$indexed_at",
            doc.IndexedAt.HasValue
                ? doc.IndexedAt.Value.ToString("O", CultureInfo.InvariantCulture)
                : DBNull.Value
        );
        cmd.Parameters.AddWithValue("$metadata_json", doc.MetadataJson);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>插入一块行，若有 embedding 同时写入 vec0。</summary>
    private async Task InsertChunkRowAsync(
        RetrievalChunk chunk,
        string? novelId,
        CancellationToken ct
    )
    {
        // FTS 分区由 C# 显式维护（触发器无法按小说路由到动态表名）。
        // 先按旧行的 (rowid, tokens) 发 fts5 'delete' 命令，再删内容行，最后插入新行与新索引项。
        var ftsTable = await EnsurePartitionAsync(PartitionKey(novelId), ct);
        long? oldRowId = null;
        string? oldTokens = null;
        await using (var oldCmd = _connection.CreateCommand())
        {
            oldCmd.CommandText =
                "SELECT rowid, tokens FROM retrieval_chunks WHERE chunk_id = $chunk_id";
            oldCmd.Parameters.AddWithValue("$chunk_id", chunk.ChunkId);
            await using var reader = await oldCmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                oldRowId = reader.GetInt64(0);
                oldTokens = reader.GetString(1);
            }
        }

        if (oldRowId is { } rowIdToDelete)
        {
            await ExecuteAsync(
                $@"INSERT INTO {ftsTable}({ftsTable}, rowid, chunk_id, tokens)
                   VALUES('delete', $rowid, $chunk_id, $tokens)",
                ct,
                ("$rowid", rowIdToDelete),
                ("$chunk_id", chunk.ChunkId),
                ("$tokens", oldTokens ?? "")
            );
        }

        await using (var delete = _connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM retrieval_chunks WHERE chunk_id = $chunk_id";
            delete.Parameters.AddWithValue("$chunk_id", chunk.ChunkId);
            await delete.ExecuteNonQueryAsync(ct);
        }

        // 获取自增 rowid
        await using var rowidCmd = _connection.CreateCommand();
        rowidCmd.CommandText = "SELECT COALESCE(MAX(rowid), 0) + 1 FROM retrieval_chunks";
        var rowid = (long)(await rowidCmd.ExecuteScalarAsync(ct) ?? 1L);

        var status = chunk.Embedding is not null ? IndexStatus.Ready : IndexStatus.Pending;

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            @"INSERT INTO retrieval_chunks
              (chunk_id, document_id, chunk_ordinal, text, tokens, source_offset_start, source_offset_end,
               content_hash, boundary_quality, prev_chunk_id, next_chunk_id, index_status, rowid)
              VALUES
              ($chunk_id, $document_id, $chunk_ordinal, $text, $tokens, $source_offset_start, $source_offset_end,
               $content_hash, $boundary_quality, $prev_chunk_id, $next_chunk_id, $index_status, $rowid)";
        cmd.Parameters.AddWithValue("$chunk_id", chunk.ChunkId);
        cmd.Parameters.AddWithValue("$document_id", chunk.DocumentId);
        cmd.Parameters.AddWithValue("$chunk_ordinal", chunk.ChunkOrdinal);
        cmd.Parameters.AddWithValue("$text", chunk.Text);
        var tokens = LexicalText.ToIndexText(chunk.Text, novelId);
        cmd.Parameters.AddWithValue("$tokens", tokens);
        cmd.Parameters.AddWithValue("$source_offset_start", chunk.SourceOffsetStart);
        cmd.Parameters.AddWithValue("$source_offset_end", chunk.SourceOffsetEnd);
        cmd.Parameters.AddWithValue("$content_hash", chunk.ContentHash);
        cmd.Parameters.AddWithValue("$boundary_quality", chunk.BoundaryQuality);
        cmd.Parameters.AddWithValue("$prev_chunk_id", (object?)chunk.PrevChunkId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$next_chunk_id", (object?)chunk.NextChunkId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$index_status", (int)status);
        cmd.Parameters.AddWithValue("$rowid", rowid);
        await cmd.ExecuteNonQueryAsync(ct);

        await ExecuteAsync(
            $@"INSERT INTO {ftsTable}(rowid, chunk_id, tokens) VALUES($rowid, $chunk_id, $tokens)",
            ct,
            ("$rowid", rowid),
            ("$chunk_id", chunk.ChunkId),
            ("$tokens", tokens)
        );

        // 如果有 embedding，写入 vec0
        if (chunk.Embedding is not null)
        {
            var vectorStr = FormatVectorString(chunk.Embedding);
            var vecRowid = DeriveRowId(chunk.ChunkId);

            // vec0 虚拟表不支持 INSERT OR REPLACE，先删除旧条目再插入
            await using var delCmd = _connection.CreateCommand();
            delCmd.CommandText = "DELETE FROM vec_chunks WHERE rowid = $rowid";
            delCmd.Parameters.AddWithValue("$rowid", vecRowid);
            await delCmd.ExecuteNonQueryAsync(ct);

            await using var vecCmd = _connection.CreateCommand();
            vecCmd.CommandText =
                @"INSERT INTO vec_chunks(rowid, embedding, chunk_id)
                  VALUES ($rowid, $embedding, $chunk_id)";
            vecCmd.Parameters.AddWithValue("$rowid", vecRowid);
            vecCmd.Parameters.AddWithValue("$embedding", vectorStr);
            vecCmd.Parameters.AddWithValue("$chunk_id", chunk.ChunkId);
            await vecCmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// 构建过滤条件子句和参数列表。
    /// </summary>
    private static (string WhereClause, List<SqliteParameter> Parameters) BuildFilterClause(
        RetrievalSearchRequest request
    )
    {
        var sb = new StringBuilder();
        var parameters = new List<SqliteParameter>();

        // 基础过滤：仅查询 Ready 状态的块、未删除的文档、匹配的域
        sb.Append("WHERE c.index_status = $ready");
        parameters.Add(new SqliteParameter("$ready", (int)IndexStatus.Ready));
        sb.Append(" AND d.index_status != $deleted");
        parameters.Add(new SqliteParameter("$deleted", (int)IndexStatus.Deleted));
        sb.Append(" AND d.domain = $domain");
        parameters.Add(new SqliteParameter("$domain", (int)request.Domain));

        // OwnerId 过滤：匹配指定 owner 或全局条目（owner_id IS NULL）
        if (!string.IsNullOrEmpty(request.OwnerId))
        {
            sb.Append(" AND (d.owner_id = $owner_id OR d.owner_id IS NULL)");
            parameters.Add(new SqliteParameter("$owner_id", request.OwnerId));
        }

        // NovelId 过滤：限定到指定小说
        if (!string.IsNullOrEmpty(request.NovelId))
        {
            sb.Append(" AND d.novel_id = $novel_id");
            parameters.Add(new SqliteParameter("$novel_id", request.NovelId));
        }

        // DocumentKinds 过滤：限定文档种类列表
        if (request.DocumentKinds is { Count: > 0 })
        {
            var placeholders = string.Join(
                ",",
                request.DocumentKinds.Select((_, i) => $"$kind{i}")
            );
            sb.Append($" AND d.document_kind IN ({placeholders})");
            parameters.AddRange(
                request.DocumentKinds.Select((t, i) => new SqliteParameter($"$kind{i}", (int)t))
            );
        }

        // SourceEntityTypes 过滤：限定实体类型列表
        if (request.SourceEntityTypes is { Count: > 0 })
        {
            var placeholders = string.Join(
                ",",
                request.SourceEntityTypes.Select((_, i) => $"$set{i}")
            );
            sb.Append($" AND d.source_entity_type IN ({placeholders})");
            parameters.AddRange(
                request.SourceEntityTypes.Select((t, i) => new SqliteParameter($"$set{i}", t))
            );
        }

        // SourceEntityIds 过滤：限定实体 ID 列表
        if (request.SourceEntityIds is { Count: > 0 })
        {
            var placeholders = string.Join(
                ",",
                request.SourceEntityIds.Select((_, i) => $"$eid{i}")
            );
            sb.Append($" AND d.source_entity_id IN ({placeholders})");
            parameters.AddRange(
                request.SourceEntityIds.Select((t, i) => new SqliteParameter($"$eid{i}", t))
            );
        }

        // metadata 过滤（JSON 字段查询）
        // ChapterOrder 范围过滤：从 metadata JSON 的 chapter-order 字段做范围比较
        if (request.ChapterOrderRange is { } range)
        {
            sb.Append(" AND json_extract(d.metadata_json, '$.\"chapter-order\"') >= $co_min");
            sb.Append(" AND json_extract(d.metadata_json, '$.\"chapter-order\"') <= $co_max");
            parameters.Add(new SqliteParameter("$co_min", range.Min));
            parameters.Add(new SqliteParameter("$co_max", range.Max));
        }

        // 参与角色过滤：从 metadata JSON 的 participant-ids 数组做 LIKE 模糊匹配
        if (request.ParticipantCharacterIds is { Count: > 0 })
        {
            // JSON 数组包含检查
            foreach (var pid in request.ParticipantCharacterIds.Select((id, i) => (id, i)))
            {
                sb.Append(
                    $" AND json_extract(d.metadata_json, '$.\"participant-ids\"') LIKE $pid{pid.i}"
                );
                parameters.Add(new SqliteParameter($"$pid{pid.i}", $"%{pid.id}%"));
            }
        }

        // 情节线过滤：从 metadata JSON 的 plotline-ids 数组做 LIKE 模糊匹配
        if (request.PlotlineIds is { Count: > 0 })
        {
            foreach (var plid in request.PlotlineIds.Select((id, i) => (id, i)))
            {
                sb.Append(
                    $" AND json_extract(d.metadata_json, '$.\"plotline-ids\"') LIKE $plid{plid.i}"
                );
                parameters.Add(new SqliteParameter($"$plid{plid.i}", $"%{plid.id}%"));
            }
        }

        // 地点过滤：从 metadata JSON 的 location-id 精确匹配
        if (!string.IsNullOrEmpty(request.LocationId))
        {
            sb.Append(" AND json_extract(d.metadata_json, '$.\"location-id\"') = $loc_id");
            parameters.Add(new SqliteParameter("$loc_id", request.LocationId));
        }

        // 场景类型过滤：从 metadata JSON 的 scene-type 匹配类型列表
        if (request.SceneTypes is { Count: > 0 })
        {
            var placeholders = string.Join(",", request.SceneTypes.Select((_, i) => $"$st{i}"));
            sb.Append(
                $" AND json_extract(d.metadata_json, '$.\"scene-type\"') IN ({placeholders})"
            );
            parameters.AddRange(
                request.SceneTypes.Select((t, i) => new SqliteParameter($"$st{i}", t))
            );
        }

        // 实体状态过滤：从 metadata JSON 的 entity-status 匹配状态列表
        if (request.EntityStatuses is { Count: > 0 })
        {
            var placeholders = string.Join(",", request.EntityStatuses.Select((_, i) => $"$es{i}"));
            sb.Append(
                $" AND json_extract(d.metadata_json, '$.\"entity-status\"') IN ({placeholders})"
            );
            parameters.AddRange(
                request.EntityStatuses.Select((t, i) => new SqliteParameter($"$es{i}", t))
            );
        }

        return (sb.ToString(), parameters);
    }

    /// <summary>
    /// metadata 前置过滤：根据请求条件筛出合法 chunk_id 集合。
    /// </summary>
    private async Task<HashSet<string>> FilterCandidatesAsync(
        RetrievalSearchRequest request,
        CancellationToken ct
    )
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(ct);

            var (whereClause, filterParams) = BuildFilterClause(request);

            var sb = new StringBuilder();
            sb.Append(
                @"SELECT c.chunk_id
                  FROM retrieval_chunks c
                  JOIN retrieval_documents d ON c.document_id = d.document_id
                  "
            );
            sb.Append(whereClause);

            await using var cmd = _connection.CreateCommand();
            foreach (var p in filterParams)
                cmd.Parameters.Add(p);
            cmd.CommandText = sb.ToString();

            var chunkIds = new HashSet<string>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                chunkIds.Add(reader.GetString(0));

            return chunkIds;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 创建临时候选表（用于 vec0/FTS 查询的 IN 子句）。
    /// </summary>
    private async Task CreateTempCandidatesTableAsync(
        IReadOnlySet<string> candidateChunkIds,
        CancellationToken ct
    )
    {
        await using var dropCmd = _connection.CreateCommand();
        dropCmd.CommandText = "DROP TABLE IF EXISTS _search_candidates";
        await dropCmd.ExecuteNonQueryAsync(ct);

        await using var createCmd = _connection.CreateCommand();
        createCmd.CommandText = "CREATE TEMP TABLE _search_candidates(chunk_id TEXT PRIMARY KEY)";
        await createCmd.ExecuteNonQueryAsync(ct);

        if (candidateChunkIds.Count > 0)
        {
            var values = string.Join(",", candidateChunkIds.Select((id, i) => $"($id{i})"));
            var sb = new StringBuilder("INSERT INTO _search_candidates(chunk_id) VALUES ");
            sb.Append(values);
            await using var insCmd = _connection.CreateCommand();
            insCmd.CommandText = sb.ToString();
            var i = 0;
            foreach (var id in candidateChunkIds)
            {
                insCmd.Parameters.AddWithValue($"$id{i}", id);
                i++;
            }
            await insCmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Dense 搜索回退：先取大量候选，再在应用层过滤。
    /// </summary>
    private async Task<List<(string ChunkId, double Distance)>> DenseSearchFallbackAsync(
        string queryStr,
        IReadOnlySet<string> candidateChunkIds,
        int topK,
        CancellationToken ct
    )
    {
        var results = new List<(string ChunkId, double Distance)>();

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            @"SELECT chunk_id, distance FROM vec_chunks
              WHERE embedding MATCH $query AND k = $topK";
        cmd.Parameters.AddWithValue("$query", queryStr);
        cmd.Parameters.AddWithValue("$topK", Math.Min(topK * 5, 200));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var chunkId = reader.GetString(0);
            var distance = reader.GetDouble(1);
            if (candidateChunkIds.Contains(chunkId))
                results.Add((chunkId, distance));
        }

        return results.Take(topK).ToList();
    }

    /// <summary>
    /// RRF（Reciprocal Rank Fusion）融合：将 dense 排名和 lexical 排名合并为统一分数。
    ///
    /// 算法：对每个候选，在每通道的 RRF 分数 = weight / (k + rank)，各通道分数累加。
    /// RRF 优先两通道都命中的结果（双通道分数叠加），兼顾单通道高分。
    /// k 值（RrfK）控制排名衰减速度：k 越小，排名靠前的结果越占优势。
    /// DenseWeight / LexicalWeight 控制两通道的相对重要性。
    /// </summary>
    private static List<(
        string ChunkId,
        double Score,
        int DenseRank,
        int LexicalRank,
        double? DenseDistance,
        double? LexicalScore,
        HitChannel HitChannel
    )> RrfFusion(
        List<(string ChunkId, double Distance)> denseResults,
        List<(string ChunkId, double Score)> lexicalResults,
        RankingConfig config
    )
    {
        var k = config.RrfK;
        var scores =
            new Dictionary<
                string,
                (
                    double Score,
                    int DenseRank,
                    int LexicalRank,
                    double? DenseDistance,
                    double? LexicalScore,
                    HitChannel HitChannel
                )
            >();

        // Dense 通道
        for (var i = 0; i < denseResults.Count; i++)
        {
            var (chunkId, distance) = denseResults[i];
            var rank = i + 1;
            var rrfScore = config.DenseWeight / (k + rank);

            scores[chunkId] = scores.TryGetValue(chunkId, out var existing)
                ? (
                    existing.Score + rrfScore,
                    rank,
                    existing.LexicalRank,
                    distance,
                    existing.LexicalScore,
                    existing.HitChannel | HitChannel.Dense
                )
                : (rrfScore, rank, 0, distance, null, HitChannel.Dense);
        }

        // Lexical 通道
        for (var i = 0; i < lexicalResults.Count; i++)
        {
            var (chunkId, lexScore) = lexicalResults[i];
            var rank = i + 1;
            var rrfScore = config.LexicalWeight / (k + rank);

            scores[chunkId] = scores.TryGetValue(chunkId, out var existing)
                ? (
                    existing.Score + rrfScore,
                    existing.DenseRank,
                    rank,
                    existing.DenseDistance,
                    lexScore,
                    existing.HitChannel | HitChannel.Lexical
                )
                : (rrfScore, 0, rank, null, lexScore, HitChannel.Lexical);
        }

        return scores
            .Select(kv =>
                (
                    kv.Key,
                    kv.Value.Score,
                    kv.Value.DenseRank,
                    kv.Value.LexicalRank,
                    kv.Value.DenseDistance,
                    kv.Value.LexicalScore,
                    kv.Value.HitChannel
                )
            )
            .OrderByDescending(x => x.Score)
            .ToList();
    }

    // ── 辅助方法 ──

    /// <summary>从 SqliteDataReader 读取一行 RetrievalDocument。</summary>
    private static RetrievalDocument ReadDocument(SqliteDataReader reader)
    {
        return new RetrievalDocument
        {
            DocumentId = reader.GetString(reader.GetOrdinal("document_id")),
            Domain = (RetrievalDomain)reader.GetInt32(reader.GetOrdinal("domain")),
            OwnerId = reader.IsDBNull(reader.GetOrdinal("owner_id"))
                ? null
                : reader.GetString(reader.GetOrdinal("owner_id")),
            NovelId = reader.IsDBNull(reader.GetOrdinal("novel_id"))
                ? null
                : reader.GetString(reader.GetOrdinal("novel_id")),
            DocumentKind = (DocumentKind)reader.GetInt32(reader.GetOrdinal("document_kind")),
            SourceEntityType = reader.IsDBNull(reader.GetOrdinal("source_entity_type"))
                ? null
                : reader.GetString(reader.GetOrdinal("source_entity_type")),
            SourceEntityId = reader.IsDBNull(reader.GetOrdinal("source_entity_id"))
                ? null
                : reader.GetString(reader.GetOrdinal("source_entity_id")),
            SourceRevision = reader.GetInt64(reader.GetOrdinal("source_revision")),
            ContentHash = reader.GetString(reader.GetOrdinal("content_hash")),
            EmbeddingModelId = reader.GetString(reader.GetOrdinal("embedding_model_id")),
            EmbeddingDimension = reader.GetInt32(reader.GetOrdinal("embedding_dimension")),
            IndexStatus = (IndexStatus)reader.GetInt32(reader.GetOrdinal("index_status")),
            CreatedAt = DateTime.Parse(
                reader.GetString(reader.GetOrdinal("created_at")),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind
            ),
            IndexedAt = reader.IsDBNull(reader.GetOrdinal("indexed_at"))
                ? null
                : DateTime.Parse(
                    reader.GetString(reader.GetOrdinal("indexed_at")),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind
                ),
            MetadataJson = reader.GetString(reader.GetOrdinal("metadata_json")),
        };
    }

    /// <summary>从 SqliteDataReader 读取一行 RetrievalChunk。</summary>
    private static RetrievalChunk ReadChunk(SqliteDataReader reader)
    {
        return new RetrievalChunk
        {
            ChunkId = reader.GetString(reader.GetOrdinal("chunk_id")),
            DocumentId = reader.GetString(reader.GetOrdinal("document_id")),
            ChunkOrdinal = reader.GetInt32(reader.GetOrdinal("chunk_ordinal")),
            Text = reader.GetString(reader.GetOrdinal("text")),
            SourceOffsetStart = reader.GetInt32(reader.GetOrdinal("source_offset_start")),
            SourceOffsetEnd = reader.GetInt32(reader.GetOrdinal("source_offset_end")),
            ContentHash = reader.GetString(reader.GetOrdinal("content_hash")),
            BoundaryQuality = reader.GetString(reader.GetOrdinal("boundary_quality")),
            PrevChunkId = reader.IsDBNull(reader.GetOrdinal("prev_chunk_id"))
                ? null
                : reader.GetString(reader.GetOrdinal("prev_chunk_id")),
            NextChunkId = reader.IsDBNull(reader.GetOrdinal("next_chunk_id"))
                ? null
                : reader.GetString(reader.GetOrdinal("next_chunk_id")),
        };
    }

    /// <summary>解析 metadata JSON 字符串为字典，解析失败时返回空字典。</summary>
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

    /// <summary>
    /// 从 metadata 字典取字符串值。
    /// 反序列化为 Dictionary&lt;string, object?&gt; 后值是 JsonElement，
    /// 直接 ToString() 会带引号或抛异常，必须按 ValueKind 提取。
    /// </summary>
    private static string? GetMetadataString(Dictionary<string, object?> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value) || value is not JsonElement element)
            return null;
        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    /// <summary>
    /// 从 metadata 字典取整数值。
    /// JsonElement 不实现 IConvertible，Convert.ToInt32 会抛 InvalidCastException。
    /// </summary>
    private static int? GetMetadataInt(Dictionary<string, object?> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value) || value is not JsonElement element)
            return null;
        return element.ValueKind == JsonValueKind.Number ? element.GetInt32() : null;
    }

    /// <summary>
    /// 从 chunk_id 推导稳定的 vec0 rowid（SHA-256 截断，非运行时哈希）。
    /// </summary>
    private static long DeriveRowId(string chunkId)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(chunkId));
        return Math.Abs(BitConverter.ToInt64(bytes, 0));
    }

    /// <summary>
    /// FTS5 查询转义：移除特殊字符，保留中文和英文。
    /// </summary>
    private static string EscapeFtsQuery(ReadOnlySpan<char> query)
    {
        var sb = new StringBuilder(query.Length);
        foreach (var c in query)
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c >= 0x4E00 && c <= 0x9FFF)
                sb.Append(c);
            else if (c == ' ')
                sb.Append(' ');
            // 跳过 FTS5 特殊字符
        }
        return sb.ToString();
    }

    /// <summary>
    /// 高性能向量 → 字符串格式化。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string FormatVectorString(float[] vector)
    {
        var estimatedLen = vector.Length * 12 + 2;
        char[]? rented = null;
        var buffer =
            estimatedLen <= 512
                ? stackalloc char[512]
                : rented = ArrayPool<char>.Shared.Rent(estimatedLen);

        try
        {
            var pos = 0;
            buffer[pos++] = '[';

            for (var i = 0; i < vector.Length; i++)
            {
                if (i > 0)
                {
                    buffer[pos++] = ',';
                    buffer[pos++] = ' ';
                }
                vector[i].TryFormat(buffer[pos..], out var written, "G6", CultureInfoInvariant);
                pos += written;
            }

            buffer[pos++] = ']';
            return new string(buffer[..pos]);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// 尝试加载中文分词扩展 simple（wangfenjin/simple）。
    /// 从应用目录和 common 目录搜索 simple.dll/libsimple.so/libsimple.dylib。
    /// 加载失败时不抛出异常，调用方检查 _simpleAvailable 标志。
    /// </summary>
    private bool TryLoadSimpleExtension()
    {
        try
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "simple.dll"),
                Path.Combine(AppContext.BaseDirectory, "libsimple.so"),
                Path.Combine(AppContext.BaseDirectory, "libsimple.dylib"),
                Path.Combine(
                    AppContext.BaseDirectory,
                    "runtimes",
                    "win-x64",
                    "native",
                    "simple.dll"
                ),
                Path.Combine(
                    AppContext.BaseDirectory,
                    "runtimes",
                    "linux-x64",
                    "native",
                    "libsimple.so"
                ),
                Path.Combine(
                    AppContext.BaseDirectory,
                    "runtimes",
                    "osx-x64",
                    "native",
                    "libsimple.dylib"
                ),
            };

            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    _connection.LoadExtension(path);
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>CultureInfo.InvariantCulture 的缓存引用，用于格式化向量字符串。</summary>
    private static readonly IFormatProvider CultureInfoInvariant = CultureInfo.InvariantCulture;

    [GeneratedRegex(@"embedding\s+float\[\s*(\d+)\s*\]", RegexOptions.IgnoreCase, "zh-CN")]
    private static partial Regex EmbeddingRegex();
}
