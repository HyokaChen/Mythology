using System.Collections.Concurrent;
using Core.Inference;
using Microsoft.Extensions.Logging;

namespace Core.Vector;

/// <summary>
/// 检索内核门面：统一入口，协调 IRetrievalStore、IEmbeddingService、DocumentBuilder、AgentMemoryStore。
///
/// 职责：
/// - 搜索：metadata 过滤 → dense + FTS → RRF 融合 → 后处理（小说知识）
/// - Agent 记忆：精确混合召回 + 评分 + 反馈（偏好/规则/叙事契约）
/// - 索引：实体 → 文档/块 → embedding → 写入
/// - 删除：标记 deleted → 后台清理
/// - 实时索引：OnEntityChanged → 异步队列 → Worker
/// </summary>
public sealed class RetrievalKernel(
    IRetrievalStore store,
    IEmbeddingService embeddingService,
    AgentMemoryStore? agentMemoryStore = null,
    ISystemOneClient? systemOne = null,
    ILogger<RetrievalKernel>? logger = null
) : IAsyncDisposable
{
    // 底层检索存储
    // 向量嵌入服务：非只读，允许冷重配置（仅在 ConversationLoop 停止时由 SettingsDialog 触发）
    private IEmbeddingService _embeddingService = embeddingService;

    private readonly ILogger? _logger = logger; // 日志

    /// <summary>Agent 记忆存储（偏好、规则、叙事契约、协作模式）。</summary>
    public AgentMemoryStore? AgentMemory { get; } = agentMemoryStore;

    /// <summary>本地判定模型客户端（可选；检索重排与 UI 热更新共用同一实例）。</summary>
    public ISystemOneClient? SystemOne { get; } = systemOne;

    // 异步索引队列
    private readonly ConcurrentQueue<IndexTask> _indexQueue = new(); // 待处理索引任务队列
    private readonly SemaphoreSlim _queueSignal = new(0); // 队列信号量，用于唤醒 Worker
    private readonly CancellationTokenSource _workerCts = new(); // Worker 取消令牌
    private Task? _workerTask; // 后台 Worker 任务
    private int _isRunning; // Worker 运行状态标志（0=停止，1=运行）

    // 最大并发索引 Worker 数
    private const int MaxConcurrency = 2;

    /// <summary>检索存储实例。</summary>
    public IRetrievalStore Store => store;

    /// <summary>嵌入服务实例。</summary>
    public IEmbeddingService EmbeddingService => _embeddingService;

    /// <summary>当前嵌入模型标识（用于索引版本比较）。</summary>
    public string EmbeddingModelId => _embeddingService.ModelId;

    /// <summary>
    /// 冷重配置 embedding 服务（仅在 ConversationLoop 停止时由 SettingsDialog 调用）。
    /// - 维度一致：同步替换 kernel 与 AgentMemory 的 embedding，返回 SameDimension。
    /// - 维度不一致：保留旧服务，返回 DimensionMismatch；调用方应提示用户重启以重建索引。
    /// 不需要并发保护：调用时 agent loop 未运行。
    /// </summary>
    public EmbeddingReplaceResult UpdateEmbeddingService(IEmbeddingService newService)
    {
        ArgumentNullException.ThrowIfNull(newService);
        // kernel 自身的 store 维度固定（SqliteRetrievalStore 在 ctor 时已绑定维度）
        if (newService.Dimension != 0 && newService.Dimension != _embeddingService.Dimension)
            return EmbeddingReplaceResult.DimensionMismatch;
        _embeddingService = newService;
        // 若 AgentMemory 存在则同步重配置（维度由 AgentMemoryStore 自行校验）
        AgentMemory?.TryUpdateEmbeddingService(newService);
        _logger?.LogInformation(
            "RetrievalKernel embedding 已重配置 (dim={Dim})",
            newService.Dimension
        );
        return EmbeddingReplaceResult.SameDimension;
    }

    /// <summary>文档构建器实例。</summary>
    public DocumentBuilder Builder { get; } = new();

    // ══════════════════════════════════════════════════════
    //  搜索
    // ══════════════════════════════════════════════════════

    /// <summary>
    /// 执行混合检索。
    /// </summary>
    public async Task<List<RetrievalSearchResult>> SearchAsync(
        RetrievalSearchRequest request,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return [];

        // 生成查询 embedding
        var queryEmbedding = await _embeddingService.EmbedAsync(request.Query, ct);

        // 本地判定重排（SystemOne）：未启用时行为与关闭前完全一致（零放大、零判定）
        var useJudge = JudgeRerank.Applies(SystemOne, request);
        var finalTopK = request.FinalTopK;
        var effective = request;
        if (useJudge && SystemOne is not null)
        {
            var overfetch = JudgeRerank.Overfetch(SystemOne);
            if (overfetch > 1)
            {
                effective = request with
                {
                    FinalTopK = Math.Clamp(
                        request.FinalTopK * overfetch,
                        request.FinalTopK,
                        50 // 超过 50 会让 store 融合段的 Take(FinalTopK*2) 代价失控
                    ),
                };
            }
        }

        // 检查零向量（embedding 失败，如云端不可达）
        if (queryEmbedding.All(v => v == 0f))
        {
            // 降级为纯词法检索（FTS5 BM25），不中断记忆检索；判定仍适用（jegrep 同款：词法候选 + 判定）
            _logger?.LogWarning("查询 embedding 返回零向量，降级为纯词法检索");
            var lexicalOnly = effective with { DenseTopK = 0 };
            var lexicalResults = await store.SearchAsync(lexicalOnly, queryEmbedding, ct);
            var judgedLexical = await ApplyJudgeAsync(useJudge, request, lexicalResults, ct);
            var processedLexical = RetrievalPostProcessor.Process(
                judgedLexical,
                RankingConfig.ForProfile(request.RankingProfile)
            );
            return useJudge ? processedLexical.Take(finalTopK).ToList() : processedLexical;
        }

        // 执行检索
        var results = await store.SearchAsync(effective, queryEmbedding, ct);

        // 后处理
        var config = RankingConfig.ForProfile(request.RankingProfile);
        if (request.NovelId is not null)
        {
            // 设置当前章节序号（如果请求中有章节范围）
            config = config with
            {
                CurrentChapterOrder = request.ChapterOrderRange?.Min,
            };
        }

        var judged = await ApplyJudgeAsync(useJudge, request, results, ct);
        var processed = RetrievalPostProcessor.Process(judged, config);
        return useJudge ? processed.Take(finalTopK).ToList() : processed;
    }

    /// <summary>执行判定重排并按结果记日志；未启用/失败时原样返回。</summary>
    private async Task<List<RetrievalSearchResult>> ApplyJudgeAsync(
        bool useJudge,
        RetrievalSearchRequest request,
        List<RetrievalSearchResult> results,
        CancellationToken ct
    )
    {
        if (!useJudge || SystemOne is null)
            return results;

        var outcome = await JudgeRerank.ApplyAsync(SystemOne, request.Query!, results, ct);
        if (outcome.Aborted)
            _logger?.LogWarning("SystemOne 判定失败，跳过重排（结果保持原排序）");
        else
            _logger?.LogDebug(
                "SystemOne 重排: {Total} 候选 / {Judged} 判定",
                results.Count,
                outcome.Judged
            );
        return outcome.Results;
    }

    /// <summary>
    /// 用户记忆混合检索：使用 AgentMemoryStore 进行精确混合召回。
    /// 显式规则（keyword 模式）优先，然后 hybrid 搜索偏好，按优先级排序。
    /// 绝不返回候选推断（CandidateInference）。
    /// </summary>
    /// <param name="ownerId">用户 ID。</param>
    /// <param name="novelId">可选小说 ID，限定小说覆盖。</param>
    /// <param name="query">可选文本查询，为空时只返回显式规则。</param>
    /// <param name="topK">最终结果数上限。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<List<RetrievalSearchResult>> SearchUserMemoryAsync(
        string ownerId,
        string? novelId,
        string? query,
        int topK = 10,
        string? sessionId = null,
        CancellationToken ct = default
    )
    {
        if (AgentMemory is null)
            return [];

        var results = new List<RetrievalSearchResult>();

        // 1. 显式规则召回：有 query 时 keyword 精确匹配；无 query 时返回全部规则
        //    优先级：当前小说显式规则 > 全局显式规则
        if (string.IsNullOrWhiteSpace(query))
        {
            // 空 query：keyword 模式 BM25 全 0 会被 min_similarity 过滤 → 必须走 GetAllAsync
            var allRules = await AgentMemory.GetAllAsync(
                ownerId,
                ns: "rule",
                novelId: novelId,
                sessionId: sessionId,
                ct: ct
            );
            results.AddRange(
                allRules.Select(rule =>
                    ConvertToRetrievalResult(rule, score: 0, priorityBonus: 2000)
                )
            );
        }
        else
        {
            // 显式规则永不因相似度丢失（§8.1 Memory safety：显式规则遗漏率必须为 0）：
            // keyword 精确命中 ∪ 全量规则。keyword 命中的规则排最前。
            var ruleResults = await AgentMemory.RecallAsync(
                query,
                ownerId,
                k: topK,
                ns: "rule",
                novelId: novelId,
                sessionId: sessionId,
                mode: "keyword",
                ct: ct
            );

            // keyword 命中（确定性优先，§3.4）
            var hitIds = new HashSet<string>(ruleResults.Select(r => r.Memory.Id));
            results.AddRange(
                ruleResults.Select(r => ConvertToRetrievalResult(r, priorityBonus: 2000))
            );

            // 全量规则补齐：未被 keyword 命中的规则也注入（优先级低于命中的）
            var allRules = await AgentMemory.GetAllAsync(
                ownerId,
                ns: "rule",
                novelId: novelId,
                sessionId: sessionId,
                ct: ct
            );
            results.AddRange(
                allRules
                    .Where(r => !hitIds.Contains(r.Id))
                    .Select(rule => ConvertToRetrievalResult(rule, score: 0, priorityBonus: 1500))
            );
        }

        // 2. 偏好 hybrid 搜索（有 query 时才执行）
        if (!string.IsNullOrWhiteSpace(query))
        {
            var hybridResults = await AgentMemory.RecallAsync(
                query,
                ownerId,
                k: topK,
                ns: "default",
                novelId: novelId,
                sessionId: sessionId,
                mode: "hybrid",
                ct: ct
            );

            foreach (var r in hybridResults)
            {
                // 过滤候选推断，仅保留已确认的偏好
                if (r.Memory.MemoryKind == AgentMemoryKind.CandidateInference)
                    continue;

                // 已确认的偏好使用较低的优先级分数
                results.Add(ConvertToRetrievalResult(r, priorityBonus: 500));
            }
        }

        // 3. 按分数降序排序，取 topK
        return results.OrderByDescending(r => r.Score).Take(topK).ToList();
    }

    /// <summary>
    /// 将 AgentMemoryStore 的 RecallResult 转换为对外接口的 RetrievalSearchResult。
    /// </summary>
    private static RetrievalSearchResult ConvertToRetrievalResult(
        RecallResult recall,
        double priorityBonus
    ) => ConvertToRetrievalResult(recall.Memory, recall.Score, priorityBonus, recall.Recency);

    /// <summary>
    /// 将记忆记录转换为检索结果（score 为底层评分，最终分数 = score + priorityBonus）。
    /// </summary>
    private static RetrievalSearchResult ConvertToRetrievalResult(
        MemoryRecord memory,
        double score,
        double priorityBonus,
        double recency = 0
    )
    {
        var (kind, boundaryQuality) = memory.MemoryKind switch
        {
            AgentMemoryKind.Explicit => (DocumentKind.UserRule, "heading"),
            _ => (DocumentKind.UserPreference, "sentence_end"),
        };

        return new RetrievalSearchResult
        {
            ChunkId = $"{memory.Id}_c0",
            DocumentId = memory.Id,
            Domain = RetrievalDomain.UserMemory,
            DocumentKind = kind,
            SourceEntityType = "UserMemory",
            SourceEntityId = memory.Id,
            NovelId = memory.NovelId,
            Text = memory.Content,
            HitChannel = HitChannel.ExactFilter,
            Score = score + priorityBonus,
            BoundaryQuality = boundaryQuality,
            MetadataSummary = new Dictionary<string, object?>
            {
                ["memory_kind"] = memory.MemoryKind.ToString(),
                ["confidence"] = memory.Confidence,
                ["priority"] = memory.Priority,
                ["importance"] = memory.Importance,
                ["usefulness"] = memory.Usefulness,
                ["access_count"] = memory.AccessCount,
                ["recency"] = recency,
            },
            IndexStatus = IndexStatus.Ready,
        };
    }

    /// <summary>
    //  索引
    // ══════════════════════════════════════════════════════

    /// <summary>
    /// 索引文档及其块（同步等待 embedding 完成）。
    /// </summary>
    public async Task IndexDocumentAsync(
        RetrievalDocument document,
        List<RetrievalChunk> chunks,
        CancellationToken ct = default
    )
    {
        // 为每个块生成 embedding — 优先批量（一次 HTTP 往返），失败回退逐条
        var embeddedChunks = new List<RetrievalChunk>(chunks.Count);
        var allSucceeded = true;

        if (_embeddingService is IBatchEmbeddingService batchService && chunks.Count > 0)
        {
            try
            {
                var texts = new string[chunks.Count];
                for (var i = 0; i < chunks.Count; i++)
                    texts[i] = chunks[i].Text;

                var vectors = await batchService.EmbedBatchAsync(texts, ct);
                for (var i = 0; i < chunks.Count; i++)
                {
                    var vector = vectors[i];
                    // 检查零向量
                    if (vector.All(v => v == 0f))
                    {
                        _logger?.LogWarning(
                            "块 {ChunkId} embedding 返回零向量，标记为 failed",
                            chunks[i].ChunkId
                        );
                        embeddedChunks.Add(chunks[i]); // 不带 embedding，状态为 pending
                        allSucceeded = false;
                        continue;
                    }

                    embeddedChunks.Add(chunks[i] with { Embedding = vector });
                }
            }
            catch (Exception ex)
            {
                // 批量失败 → 清空结果，回退到逐条路径
                _logger?.LogError(ex, "批量 embedding 失败，回退逐条嵌入");
                embeddedChunks.Clear();
                allSucceeded = true;
            }
        }

        // 逐条路径（服务不支持批量，或批量调用失败后的回退）
        if (embeddedChunks.Count == 0 && chunks.Count > 0)
        {
            foreach (var chunk in chunks)
            {
                try
                {
                    var embedding = await _embeddingService.EmbedAsync(chunk.Text, ct);

                    // 检查零向量
                    if (embedding.All(v => v == 0f))
                    {
                        _logger?.LogWarning(
                            "块 {ChunkId} embedding 返回零向量，标记为 failed",
                            chunk.ChunkId
                        );
                        embeddedChunks.Add(chunk); // 不带 embedding，状态为 pending
                        allSucceeded = false;
                        continue;
                    }

                    embeddedChunks.Add(chunk with { Embedding = embedding });
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "块 {ChunkId} embedding 失败", chunk.ChunkId);
                    embeddedChunks.Add(chunk); // 不带 embedding
                    allSucceeded = false;
                }
            }
        }

        // 更新文档状态
        var finalDoc = document with
        {
            IndexStatus = allSucceeded ? IndexStatus.Ready : IndexStatus.Failed,
            EmbeddingModelId = _embeddingService.ModelId,
            EmbeddingDimension = _embeddingService.Dimension,
            IndexedAt = allSucceeded ? DateTime.UtcNow : null,
        };

        await store.UpsertDocumentAsync(finalDoc, embeddedChunks, ct);
    }

    /// <summary>
    /// 删除实体相关的所有文档。
    /// </summary>
    public async Task DeleteEntityAsync(
        string novelId,
        string sourceEntityType,
        string sourceEntityId,
        CancellationToken ct = default
    )
    {
        // 查找该实体的所有文档
        var doc = await store.GetDocumentAsync(
            StableId.DocumentId(
                RetrievalDomain.NovelKnowledge,
                DocumentKind.CharacterProfile,
                novelId,
                sourceEntityType,
                sourceEntityId
            ),
            ct
        );

        // 需要遍历所有可能的 DocumentKind
        foreach (var kind in Enum.GetValues<DocumentKind>())
        {
            var docId = StableId.DocumentId(
                RetrievalDomain.NovelKnowledge,
                kind,
                novelId,
                sourceEntityType,
                sourceEntityId
            );
            var existing = await store.GetDocumentAsync(docId, ct);
            if (existing is not null)
            {
                await store.MarkDocumentDeletedAsync(docId, ct);
            }
        }
    }

    // ══════════════════════════════════════════════════════
    //  异步索引队列
    // ══════════════════════════════════════════════════════

    /// <summary>
    /// 启动后台索引 Worker。
    /// </summary>
    public void StartWorker()
    {
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) == 1)
            return;

        _workerTask = Task.Run(() => WorkerLoop(_workerCts.Token));
    }

    /// <summary>
    /// 停止后台索引 Worker。
    /// </summary>
    public async Task StopWorkerAsync()
    {
        if (Interlocked.CompareExchange(ref _isRunning, 0, 1) == 0)
            return;

        await _workerCts.CancelAsync();
        _queueSignal.Release(); // 唤醒 Worker

        if (_workerTask is not null)
        {
            try
            {
                await _workerTask;
            }
            catch
            { /* ignore */
            }
        }
    }

    /// <summary>
    /// 将索引任务入队（由 OnEntityChanged 驱动）。
    /// 同一实体短时间多次保存会合并/去重：遍历旧队列，移除同实体（NovelId+EntityType+EntityId）的旧任务，
    /// 只保留最新的一个任务。这样连续保存同一实体不会堆积冗余索引操作。
    /// </summary>
    public void EnqueueIndexTask(IndexTask task)
    {
        // 去重：遍历旧队列，移除同实体（NovelId+EntityType+EntityId）的旧任务
        // 只保留最新的一个任务，避免连续保存同一实体时堆积冗余索引操作
        var newQueue = new ConcurrentQueue<IndexTask>();
        while (_indexQueue.TryDequeue(out var existing))
        {
            if (
                existing.NovelId != task.NovelId
                || existing.EntityType != task.EntityType
                || existing.EntityId != task.EntityId
            )
                newQueue.Enqueue(existing);
        }

        // 加入新任务
        newQueue.Enqueue(task);

        // 替换队列
        while (!_indexQueue.IsEmpty)
            _indexQueue.TryDequeue(out _);
        while (newQueue.TryDequeue(out var t))
            _indexQueue.Enqueue(t);

        _queueSignal.Release();
    }

    /// <summary>
    /// 获取队列中待处理的任务数。
    /// </summary>
    public int PendingTaskCount => _indexQueue.Count;

    private async Task WorkerLoop(CancellationToken ct)
    {
        // 并发控制：使用 SemaphoreSlim 限制同时处理的索引任务数（MaxConcurrency=2）
        // 避免 embedding 服务过载，同时利用并行加速多实体索引
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _queueSignal.WaitAsync(ct);

                // 限制并发
                using var semaphore = new SemaphoreSlim(MaxConcurrency);
                var tasks = new List<Task>();

                while (_indexQueue.TryDequeue(out var task) && !ct.IsCancellationRequested)
                {
                    await semaphore.WaitAsync(ct);
                    tasks.Add(
                        ProcessIndexTaskAsync(task, ct).ContinueWith(_ => semaphore.Release(), ct)
                    );
                }

                if (tasks.Count > 0)
                    await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "索引 Worker 异常");
                await Task.Delay(1000, ct);
            }
        }
    }

    private async Task ProcessIndexTaskAsync(IndexTask task, CancellationToken ct)
    {
        try
        {
            // 由调用方提供构建函数
            if (task.BuildFunc is null)
                return;

            var (doc, chunks) = await task.BuildFunc(Builder);
            await IndexDocumentAsync(doc, chunks, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "索引任务失败: {EntityType} {EntityId}",
                task.EntityType,
                task.EntityId
            );
        }
    }

    // ══════════════════════════════════════════════════════
    //  释放
    // ══════════════════════════════════════════════════════

    public async ValueTask DisposeAsync()
    {
        await StopWorkerAsync();
        _workerCts.Dispose();
        _queueSignal.Dispose();
        if (store is IAsyncDisposable disposable)
            await disposable.DisposeAsync();
        if (AgentMemory is IAsyncDisposable agentMemDisposable)
            await agentMemDisposable.DisposeAsync();
    }
}

/// <summary>UpdateEmbeddingService 的结果（冷重配置，仅 SettingsDialog 调用）。</summary>
public enum EmbeddingReplaceResult
{
    /// <summary>维度一致，已即时替换。</summary>
    SameDimension,

    /// <summary>维度不一致，未替换（需要重启以重建索引）。</summary>
    DimensionMismatch,
}

/// <summary>
/// 索引任务：携带域、来源实体、操作类型和构建函数。
/// </summary>
public sealed class IndexTask
{
    public required string NovelId { get; init; }
    public required string EntityType { get; init; }
    public required string EntityId { get; init; }
    public required string Action { get; init; } // "create", "update", "delete"

    /// <summary>
    /// 异步文档构建函数。在 Worker 线程中执行，允许异步读取存储。
    /// </summary>
    public Func<
        DocumentBuilder,
        Task<(RetrievalDocument Doc, List<RetrievalChunk> Chunks)>
    >? BuildFunc { get; init; }

    public DateTime EnqueuedAt { get; init; } = DateTime.UtcNow;
}
