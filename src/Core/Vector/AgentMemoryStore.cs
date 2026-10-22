using System.Text.Json;
using Core.Inference;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Core.Vector;

/// <summary>
/// Agent 记忆存储：基于 SQLite 的精确混合召回（向量 + BM25）记忆系统。
///
/// 核心设计移植自 remembrane：
/// - 评分 = w_sim·similarity + w_rec·recency + w_imp·importance + w_use·usefulness
/// - 精确暴力评分（非 ANN），确保完整召回
/// - 每次召回 touch（重置 recency 时钟），模拟 spaced repetition
/// - usefulness 从任务结果中学习（mark_useful / mark_useless）
///
/// 与现有 SqliteRetrievalStore 共享同一 vectors.db，但使用独立表。
/// SqliteRetrievalStore 处理小说知识（vec0 + FTS5 + RRF），
/// AgentMemoryStore 处理 agent 记忆（精确评分 + 反馈 + 日志）。
/// </summary>
/// <summary>
/// 记忆可见范围（读侧）。解决「novel_id 为 NULL = 全局记忆」这一维度在读写两侧语义不一致的问题：
/// 写侧（save_preference scope=global / 未打开小说时的 memory_store）产生 novel_id IS NULL 的全局记忆，
/// 读侧过去用精确匹配 `novel_id = @novelId`，导致打开小说后**全局记忆永远召不回**（与
/// DiffAsync 文档「非 null = 该小说 + 全局记忆」相矛盾）。
/// </summary>
public enum MemoryVisibility
{
    /// <summary>
    /// 默认：全局（novel_id IS NULL）∪ 指定小说；novelId 为空时=仅全局。
    /// 模型侧召回一律用它——用户偏好/显式规则多数是全局的，不能被小说过滤掉。
    /// </summary>
    NovelWithGlobal = 0,

    /// <summary>仅全局（novel_id IS NULL）。</summary>
    GlobalOnly,

    /// <summary>仅指定小说（不含全局）；novelId 为空时退化为仅全局。</summary>
    NovelOnly,

    /// <summary>该 owner 的全部记忆（跨小说）。仅诊断/运维（合并、快照对账）使用。</summary>
    All,
}

public sealed class AgentMemoryStore : IAsyncDisposable
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS agent_memories (
            id              TEXT PRIMARY KEY,
            owner_id        TEXT NOT NULL,
            novel_id        TEXT,
            session_id      TEXT,
            namespace       TEXT NOT NULL DEFAULT 'default',
            content         TEXT NOT NULL,
            importance      REAL NOT NULL DEFAULT 0.5,
            metadata_json   TEXT NOT NULL DEFAULT '{}',
            created_at      REAL NOT NULL,
            last_accessed_at REAL,
            access_count    INTEGER NOT NULL DEFAULT 0,
            usefulness      REAL NOT NULL DEFAULT 0.0,
            embedding       BLOB,
            memory_kind     INTEGER NOT NULL DEFAULT 0,
            confidence      REAL NOT NULL DEFAULT 1.0,
            priority        INTEGER NOT NULL DEFAULT 0,
            expires_at      REAL,
            evidence_refs   TEXT
        );

        CREATE INDEX IF NOT EXISTS idx_agent_mem_owner ON agent_memories(owner_id, namespace);
        CREATE INDEX IF NOT EXISTS idx_agent_mem_novel ON agent_memories(owner_id, novel_id);
        CREATE INDEX IF NOT EXISTS idx_agent_mem_session ON agent_memories(owner_id, session_id);

        CREATE TABLE IF NOT EXISTS agent_journal (
            seq             INTEGER PRIMARY KEY AUTOINCREMENT,
            ts              REAL NOT NULL,
            op              TEXT NOT NULL,
            memory_id       TEXT NOT NULL,
            namespace       TEXT NOT NULL DEFAULT 'default',
            payload_json    TEXT NOT NULL DEFAULT '{}'
        );

        CREATE INDEX IF NOT EXISTS idx_agent_journal_mem ON agent_journal(memory_id);

        CREATE TABLE IF NOT EXISTS agent_snapshots (
            label           TEXT PRIMARY KEY,
            ts              REAL NOT NULL
        );
        """;

    private const string MemCols =
        "id, owner_id, novel_id, session_id, namespace, content, importance, metadata_json, "
        + "created_at, last_accessed_at, access_count, usefulness, "
        + "memory_kind, confidence, priority, expires_at, evidence_refs";

    private const string InsertSql = """
        INSERT INTO agent_memories
            (id, owner_id, novel_id, session_id, namespace, content, importance, metadata_json,
             created_at, last_accessed_at, access_count, usefulness,
             embedding, memory_kind, confidence, priority, expires_at, evidence_refs)
        VALUES
            ($id, $owner_id, $novel_id, $session_id, $namespace, $content, $importance, $metadata_json,
             $created_at, $last_accessed_at, $access_count, $usefulness,
             $embedding, $memory_kind, $confidence, $priority, $expires_at, $evidence_refs)
        """;

    private readonly string _dbPath;
    private readonly int _dimension;

    // 非只读：允许冷重配置（仅在 ConversationLoop 停止时由 SettingsDialog 触发）
    private IEmbeddingService _embeddingService;
    private readonly ISystemOneClient? _systemOne;
    private readonly ILogger? _logger;

    private SqliteConnection? _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _initialized;

    // 分词结果缓存：key = "{ownerId}|{ns}|{novelId}" → {memoryId: 分词缓存}
    // jieba 分词是召回热路径的主要成本，内容不变时跳过重复分词。
    // 内容类写入（store/forget/resolve/consolidate）后整体失效。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        System.Collections.Concurrent.ConcurrentDictionary<string, TokenCacheEntry>
    > _tokenCache = new();

    // SQLite data_version：检测其他连接的外部写入，变更时清空缓存
    private long _dataVersion = -1;

    // 增量缓存版本号：写入操作自增，使 MemoryCache 和 Tacos 缓存一起失效
    private int _callCacheVersion;

    // Corpus 缓存：内存归一化向量矩阵 + 分词缓存（按 ID 失效）
    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        EmbeddingCorpus
    > _corpusCache = new();

    // Tacos 增量评分缓存数据库

    // ANN（vec0）状态与开关：加载失败静默退化为全量精确扫描
    private bool _vecAvailable;

    /// <summary>ANN 候选开关（internal 供测试切换）。</summary>
    internal bool UseAnn { get; set; } = true;

    /// <summary>行数低于此值全量精确更快，不用 ANN。</summary>
    internal int AnnMinRows { get; set; } = 2000;

    /// <summary>评分配置。</summary>
    public AgentMemoryScoringConfig ScoringConfig { get; }

    /// <summary>
    /// 创建 AgentMemoryStore。
    /// </summary>
    /// <param name="dbPath">SQLite 数据库文件路径。</param>
    /// <param name="dimension">向量维度（默认 1536）。</param>
    /// <param name="scoringConfig">评分配置（可选）。</param>
    /// <param name="embeddingService">嵌入服务（可选，未提供时降级为纯 BM25）。</param>
    /// <param name="systemOne">jev</param>
    /// <param name="logger">日志记录器（可选）。</param>
    public AgentMemoryStore(
        string dbPath,
        int dimension = VectorDefaults.Dimension,
        AgentMemoryScoringConfig? scoringConfig = null,
        IEmbeddingService? embeddingService = null,
        ISystemOneClient? systemOne = null,
        ILogger? logger = null
    )
    {
        _dbPath = dbPath;
        _dimension = dimension;
        ScoringConfig = scoringConfig ?? new AgentMemoryScoringConfig();
        // 未注入外部 embedding 服务时使用本地确定性 HashEmbedder（离线免费，见商业化边界）
        _embeddingService = embeddingService ?? new HashEmbedder(Math.Max(dimension, 64));
        _systemOne = systemOne;
        _logger = logger;
    }

    /// <summary>
    /// 冷重配置 embedding 服务（仅在 ConversationLoop 停止时由 SettingsDialog 调用）。
    /// 仅当新服务的 Dimension 与存储维度一致时切换成功；
    /// 维度不一致时返回 false（已索引的向量与新维度不兼容，需重启重建）。
    /// 清空分词缓存与评分缓存，确保召回重算。
    /// 不需要并发保护：调用时 agent loop 未运行。
    /// </summary>
    public bool TryUpdateEmbeddingService(IEmbeddingService newService)
    {
        ArgumentNullException.ThrowIfNull(newService);
        // 维度一致（或新服务没有明确维度约束）才允许替换
        if (newService.Dimension != 0 && newService.Dimension != _dimension)
            return false;
        _embeddingService = newService;
        // 失效分词缓存与 Tacos 评分缓存（向量语义变了，所有评分作废）
        _tokenCache.Clear();
        Interlocked.Increment(ref _callCacheVersion);
        _logger?.LogInformation("AgentMemoryStore embedding 服务已重配置 (dim={Dim})", _dimension);
        return true;
    }

    /// <summary>
    /// 公开初始化入口：未初始化时取锁并执行初始化核心。
    /// 注意：<see cref="_lock"/> 是普通 SemaphoreSlim（<b>不可重入</b>）——
    /// 已持锁的调用路径（StoreAsync/RecallAsync 等）只能走 <see cref="EnsureInitializedAsync"/>，
    /// 否则会在同一线程上自我死锁。
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
            await InitializeCoreAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>初始化核心：<b>调用方必须已持有 _lock</b>（或确认无并发）。</summary>
    private async Task InitializeCoreAsync(CancellationToken ct)
    {
        {
            // Pooling=False：本存储对每个库文件只持有一条长生命周期连接，连接池毫无收益，
            // 却会在 DisposeAsync 后把连接留在池中继续占用 OS 文件句柄，
            // 导致随后的删除/替换库文件（维度变更、测试清理）抛 IOException。
            _connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
            await _connection.OpenAsync(ct);

            // WAL 模式 + busy timeout 提升并发性能
            await using var walCmd = _connection.CreateCommand();
            walCmd.CommandText = "PRAGMA journal_mode=WAL";
            await walCmd.ExecuteNonQueryAsync(ct);

            await using var busyCmd = _connection.CreateCommand();
            busyCmd.CommandText = "PRAGMA busy_timeout=30000";
            await busyCmd.ExecuteNonQueryAsync(ct);

            // 创建 schema
            await using var schemaCmd = _connection.CreateCommand();
            schemaCmd.CommandText = Schema;
            await schemaCmd.ExecuteNonQueryAsync(ct);

            // ANN（vec0）初始化：加载失败静默退化为全量精确扫描（派生数据可重建，非故障）
            try
            {
                _connection.EnableExtensions();
                // vec0.dll 在 SDK 项目输出中位于 runtimes/{rid}/native/ 子目录（非扁平），
                // SQLite C API 的 LoadExtension 不走 .NET 程序集解析器，须按显式路径加载；
                // 宿主扁平布局（vec0.dll 在 BaseDirectory）时回退到裸名。
                var rid =
                    System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier
                    ?? "win-x64";
                var nativeDir = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native");
                var vecFileName =
                    OperatingSystem.IsWindows() ? "vec0.dll"
                    : OperatingSystem.IsMacOS() ? "libvec0.dylib"
                    : "libvec0.so";
                var vecPath = Path.Combine(nativeDir, vecFileName);
                if (File.Exists(vecPath))
                    _connection.LoadExtension(vecPath);
                else
                    _connection.LoadVector(); // 扁平布局回退（裸名 vec0）
                await AgentMemoryVec.EnsureTableAsync(_connection, _dimension, ct);
                await AgentMemoryVec.BackfillAsync(_connection, _dimension, ct); // 存量库一次性回填
                _vecAvailable = true;
            }
            catch (SqliteException ex)
            {
                _logger?.LogWarning(ex, "vec0 扩展加载失败，Agent 记忆退化为全量精确扫描");
                _vecAvailable = false;
            }

            _initialized = true;
            _logger?.LogInformation("AgentMemoryStore 初始化完成: {DbPath}", _dbPath);
        }
    }

    /// <summary>
    /// 确保已初始化（<b>在已持锁的调用路径里使用</b>）：直接执行初始化核心，不再取锁。
    /// </summary>
    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (!_initialized)
            await InitializeCoreAsync(ct);
    }

    // ══════════════════════════════════════════════════════
    //  写入
    // ══════════════════════════════════════════════════════

    /// <summary>存储一条 agent 记忆。</summary>
    public async Task<MemoryRecord> StoreAsync(
        string content,
        string ownerId,
        string? novelId = null,
        string? sessionId = null,
        string ns = "default",
        double importance = 0.5,
        AgentMemoryKind memoryKind = AgentMemoryKind.Explicit,
        double confidence = 1.0,
        int priority = 0,
        Dictionary<string, object?>? metadata = null,
        List<string>? evidenceRefs = null,
        double? expiresAt = null,
        string? memoryId = null,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(ownerId);

        var id = memoryId ?? Guid.NewGuid().ToString("N");
        var now = Now();
        var metadataJson = metadata is not null ? JsonSerializer.Serialize(metadata) : "{}";
        var evidenceRefsJson = evidenceRefs is not null
            ? JsonSerializer.Serialize(evidenceRefs)
            : null;

        // 计算 embedding（如有 embedder）
        float[]? embedding = null;
        if (_embeddingService is not null)
        {
            try
            {
                embedding = await _embeddingService.EmbedAsync(content, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "StoreAsync embedding 失败，降级为纯词法: {ContentId}", id);
            }
        }

        var mem = new MemoryRecord
        {
            Id = id,
            OwnerId = ownerId,
            NovelId = novelId,
            SessionId = sessionId,
            Namespace = ns,
            Content = content,
            Importance = Math.Clamp(importance, 0.0, 1.0),
            MetadataJson = metadataJson,
            CreatedAt = now,
            LastAccessedAt = null,
            AccessCount = 0,
            Usefulness = 0.0,
            MemoryKind = memoryKind,
            Confidence = confidence,
            Priority = priority,
            ExpiresAt = expiresAt,
            EvidenceRefsJson = evidenceRefsJson,
        };

        await _lock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(ct);
            ArgumentNullException.ThrowIfNull(_connection);

            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = InsertSql;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$owner_id", ownerId);
            cmd.Parameters.AddWithValue("$novel_id", (object?)novelId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$session_id", (object?)sessionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$namespace", ns);
            cmd.Parameters.AddWithValue("$content", content);
            cmd.Parameters.AddWithValue("$importance", mem.Importance);
            cmd.Parameters.AddWithValue("$metadata_json", metadataJson);
            cmd.Parameters.AddWithValue("$created_at", now);
            cmd.Parameters.AddWithValue("$last_accessed_at", DBNull.Value);
            cmd.Parameters.AddWithValue("$access_count", 0);
            cmd.Parameters.AddWithValue("$usefulness", 0.0);
            cmd.Parameters.AddWithValue(
                "$embedding",
                embedding is not null ? PackVector(embedding) : DBNull.Value
            );
            cmd.Parameters.AddWithValue("$memory_kind", (int)memoryKind);
            cmd.Parameters.AddWithValue("$confidence", confidence);
            cmd.Parameters.AddWithValue("$priority", priority);
            cmd.Parameters.AddWithValue("$expires_at", (object?)expiresAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue(
                "$evidence_refs",
                (object?)evidenceRefsJson ?? DBNull.Value
            );
            await cmd.ExecuteNonQueryAsync(ct);
            InvalidateTokenCache(new List<string> { id });
            _corpusCache.Clear(); // 写入失效（对齐 remembrane _invalidate 粗粒度）
            Interlocked.Increment(ref _callCacheVersion);
            // ANN 同步：vec 表是派生数据，失败不阻断主写入（Backfill 可重建）
            if (embedding is not null && _vecAvailable)
            {
                try
                {
                    await AgentMemoryVec.SyncUpsertAsync(_connection!, id, embedding, ct);
                }
                catch (SqliteException ex)
                {
                    _logger?.LogWarning(ex, "agent_memory_vec 同步写入失败（可重建）: {Id}", id);
                }
            }

            // 日志：完整记录重建所需字段（时间旅行回放用）
            await JournalAsync(
                "store",
                id,
                ns,
                $$"""
                {"content":"{{JsonEncode(content)}}","importance":{{Fmt(
                    importance
                )}},"memory-kind":{{(int)memoryKind}},"owner-id":"{{JsonEncode(
                    ownerId
                )}}","novel-id":"{{JsonEncode(novelId ?? "")}}","session-id":"{{JsonEncode(sessionId ?? "")}}","created-at":{{Fmt(
                    now
                )}},"expires-at":{{(
                    expiresAt is null ? "null" : Fmt(expiresAt.Value)
                )}},"confidence":{{Fmt(
                    confidence
                )}},"priority":{{priority}},"metadata":"{{JsonEncode(metadataJson)}}"}
                """,
                ct
            );
        }
        finally
        {
            _lock.Release();
        }

        return mem;
    }

    /// <summary>强化一条记忆：提升重要性（上限 1.0）并重置衰退时钟。</summary>
    public async Task<MemoryRecord?> ReinforceAsync(
        string memoryId,
        double importanceBoost = 0.1,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(memoryId);

        await _lock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(ct);
            ArgumentNullException.ThrowIfNull(_connection);

            var now = Now();

            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "UPDATE agent_memories SET importance = MIN(1.0, importance + $boost),"
                + " last_accessed_at = $now, access_count = access_count + 1"
                + " WHERE id = $id";
            cmd.Parameters.AddWithValue("$boost", importanceBoost);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$id", memoryId);
            await cmd.ExecuteNonQueryAsync(ct);

            var mem = await GetInternalAsync(memoryId, ct);
            if (mem is not null)
            {
                await JournalAsync(
                    "reinforce",
                    memoryId,
                    mem.Namespace,
                    $$"""{"importance":{{mem.Importance}},"boost":{{importanceBoost}}}""",
                    ct
                );
            }

            return mem;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>删除一条或多条记忆。</summary>
    /// <returns>删除数量。</returns>
    public async Task<int> ForgetAsync(
        string? memoryId = null,
        string? ownerId = null,
        string? ns = null,
        double? olderThanSeconds = null,
        CancellationToken ct = default
    )
    {
        var clauses = new List<string>();
        var parameters = new List<SqliteParameter>();

        if (memoryId is not null)
        {
            clauses.Add("id = $id");
            parameters.Add(new SqliteParameter("$id", memoryId));
        }
        if (ownerId is not null)
        {
            clauses.Add("owner_id = $owner_id");
            parameters.Add(new SqliteParameter("$owner_id", ownerId));
        }
        if (ns is not null)
        {
            clauses.Add("namespace = $ns");
            parameters.Add(new SqliteParameter("$ns", ns));
        }
        if (olderThanSeconds is not null)
        {
            var cutoff = Now() - olderThanSeconds.Value;
            clauses.Add("COALESCE(last_accessed_at, created_at) < $cutoff");
            parameters.Add(new SqliteParameter("$cutoff", cutoff));
        }

        if (clauses.Count == 0)
            throw new ArgumentException("Forget() 需要至少一个过滤条件");

        var where = string.Join(" AND ", clauses);

        await _lock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(ct);
            ArgumentNullException.ThrowIfNull(_connection);

            // 先查询被删除的记忆（用于日志和缓存失效）
            await using var selCmd = _connection.CreateCommand();
            selCmd.CommandText =
                $"SELECT id, owner_id, namespace FROM agent_memories WHERE {where}";
            foreach (var p in parameters)
                selCmd.Parameters.Add(p);
            var doomed = new List<(string Id, string OwnerId, string Namespace)>();
            await using (var reader = await selCmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    doomed.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }

            // 执行删除
            await using var delCmd = _connection.CreateCommand();
            delCmd.CommandText = $"DELETE FROM agent_memories WHERE {where}";
            foreach (var p in parameters)
                delCmd.Parameters.Add(p);
            var count = await delCmd.ExecuteNonQueryAsync(ct);

            // 日志 + 缓存失效
            foreach (var (id, oid, nsVal) in doomed)
            {
                await JournalAsync("forget", id, nsVal, "{}", ct);
                if (_vecAvailable)
                {
                    try
                    {
                        await AgentMemoryVec.SyncDeleteAsync(_connection!, id, ct);
                    }
                    catch (SqliteException ex)
                    {
                        _logger?.LogWarning(
                            ex,
                            "agent_memory_vec 删除同步失败（可重建）: {Id}",
                            id
                        );
                    }
                }
            }
            InvalidateTokenCache(doomed.ConvertAll(d => d.Id));
            _corpusCache.Clear(); // 写入失效（对齐 remembrane _invalidate 粗粒度）
            Interlocked.Increment(ref _callCacheVersion);

            return count;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ══════════════════════════════════════════════════════
    //  反馈
    // ══════════════════════════════════════════════════════

    /// <summary>记录任务结果反馈给一条记忆。</summary>
    private async Task<MemoryRecord?> FeedbackAsync(
        string memoryId,
        bool useful,
        double weight = 1.0,
        CancellationToken ct = default
    )
    {
        if (weight <= 0)
            throw new ArgumentException("weight 必须为正数", nameof(weight));

        var delta = useful ? weight : -weight;

        await _lock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(ct);
            ArgumentNullException.ThrowIfNull(_connection);

            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "UPDATE agent_memories SET usefulness = usefulness + $delta WHERE id = $id";
            cmd.Parameters.AddWithValue("$delta", delta);
            cmd.Parameters.AddWithValue("$id", memoryId);
            await cmd.ExecuteNonQueryAsync(ct);

            var mem = await GetInternalAsync(memoryId, ct);
            if (mem is not null)
            {
                await JournalAsync(
                    "feedback",
                    memoryId,
                    mem.Namespace,
                    $$"""{"useful":{{useful.ToString().ToLowerInvariant()}},"weight":{{weight}},"usefulness":{{mem.Usefulness}}}""",
                    ct
                );
            }

            return mem;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>标记一条记忆为有用（帮助完成了任务）。</summary>
    public Task<MemoryRecord?> MarkUsefulAsync(
        string memoryId,
        double weight = 1.0,
        CancellationToken ct = default
    ) => FeedbackAsync(memoryId, true, weight, ct);

    /// <summary>标记一条记忆为无用（被召回但未帮助完成任务）。</summary>
    public Task<MemoryRecord?> MarkUselessAsync(
        string memoryId,
        double weight = 1.0,
        CancellationToken ct = default
    ) => FeedbackAsync(memoryId, false, weight, ct);

    // ══════════════════════════════════════════════════════
    //  读取
    // ══════════════════════════════════════════════════════

    /// <summary>
    /// 执行精确混合召回。
    /// 对每条记忆计算  cosine + BM25 + recency + importance + usefulness 加权评分。
    /// </summary>
    /// <param name="query">自然语言查询。</param>
    /// <param name="ownerId">用户 ID。</param>
    /// <param name="k">返回结果数上限。</param>
    /// <param name="ns">命名空间过滤（null 搜索所有）。</param>
    /// <param name="novelId">小说 ID 过滤（可选）。</param>
    /// <param name="mode">检索模式：hybrid / vector / keyword。</param>
    /// <param name="minScore">最低综合评分。</param>
    /// <param name="minSimilarity">最低文本相似度（硬门槛，低于此值不返回）。</param>
    /// <param name="touch">是否更新访问统计。</param>
    /// <param name="now">覆盖当前时间戳（用于测试）。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<List<RecallResult>> RecallAsync(
        string query,
        string ownerId,
        int k = 5,
        string? ns = "default",
        string? novelId = null,
        string? sessionId = null,
        MemoryVisibility scope = MemoryVisibility.NovelWithGlobal,
        string mode = "hybrid",
        double minScore = 0.0,
        double minSimilarity = 0.0,
        bool touch = true,
        double? now = null,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(ownerId);

        if (mode is not "hybrid" and not "vector" and not "keyword")
            throw new ArgumentException("mode 必须为 hybrid / vector / keyword", nameof(mode));

        var effectiveNow = now ?? Now();

        // 1. 加载记忆行
        var rows = await LoadRowsAsync(ownerId, ns, novelId, sessionId, scope, ct);
        if (rows.Count == 0)
            return [];

        // 2. 计算查询 embedding（hybrid / vector 模式）
        //    默认使用本地 HashEmbedder（确定性、离线）；失败时降级纯词法
        float[]? qvec = null;
        if (mode is "hybrid" or "vector")
        {
            try
            {
                qvec = await _embeddingService.EmbedAsync(query, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "RecallAsync query embedding 失败，降级为纯词法");
                mode = "keyword";
            }
        }

        // 2b. 批量读取记忆向量（DB 中已存的 embedding，不再重新计算）
        Dictionary<string, float[]>? memVecs = null;
        if (mode is "hybrid" or "vector" && qvec is not null)
            memVecs = await LoadEmbeddingsAsync(rows.ConvertAll(r => r.Id), ct);

        // 3. 计算 BM25 分数（keyword / hybrid 模式，分词结果带缓存）
        var queryTerms = mode is "hybrid" or "keyword" ? AgentMemoryBm25.Tokenize(query) : null;
        double[]? bm25Scores = null;
        if (queryTerms is not null && queryTerms.Count > 0)
        {
            var docTokens = new List<List<string>>(rows.Count);
            var tfs = new List<Dictionary<string, int>>(rows.Count);
            var docLens = new List<int>(rows.Count);
            foreach (var row in rows)
            {
                var (tokens, tf, len) = GetTokensCached(ownerId, ns, novelId, row);
                docTokens.Add(tokens);
                tfs.Add(tf);
                docLens.Add(len);
            }
            var raw = AgentMemoryBm25.Bm25Scores(queryTerms, tfs: tfs, docLens: docLens);
            bm25Scores = AgentMemoryBm25.NormalizeScores(raw);
        }
        else if (queryTerms is { Count: 0 })
        {
            // 空查询（如纯中文）：BM25 分数全为 0
            bm25Scores = new double[rows.Count];
        }

        // 4. 打分（corpus 缓存 + SIMD 点积）
        var kwWeight = ScoringConfig.KeywordWeight;
        // ANN 两阶段（B 选项）：vec0 取 top-M 候选 → 候选子集精确点积重评。
        // 小库 / vec 扩展失败（_vecAvailable=false）/ 纯 keyword → annIds=null 走全量精确（原语义）。
        HashSet<string>? annIds = null;
        if (
            _vecAvailable
            && UseAnn
            && mode is "hybrid" or "vector"
            && qvec is not null
            && rows.Count >= AnnMinRows
        )
        {
            var m = Math.Max(500, 10 * k);
            var candidates = await AgentMemoryVec.SearchAsync(_connection!, qvec, m, ct);
            if (candidates is not null && candidates.Count > 0)
                annIds = candidates.Select(c => c.MemoryId).ToHashSet();
        }
        var candidateIds = rows.ConvertAll(r => r.Id); // 全量 corpus 键控；ANN 只过滤 vs 计算
        EmbeddingCorpus? corpus = null;
        var corpusKey = $"{ownerId}|{ns}|{novelId}";
        if (mode is "hybrid" or "vector" && qvec is not null && rows.Count > 0)
        {
            if (
                !_corpusCache.TryGetValue(corpusKey, out corpus)
                || (corpus != null && corpus.Rows != rows.Count)
            )
            {
                if (memVecs == null || memVecs.Count == 0)
                    memVecs = await LoadEmbeddingsAsync(rows.ConvertAll(r => r.Id), ct);
                var dim = qvec.Length;
                var n = rows.Count;
                var matrix = new float[n * dim];
                var norms = new float[n];
                for (var i = 0; i < n; i++)
                {
                    var id = rows[i].Id;
                    if (memVecs.TryGetValue(id, out var v) && v != null && v.Length == dim)
                    {
                        var off = i * dim;
                        var sum = 0f;
                        for (var d = 0; d < dim; d++)
                        {
                            var val = v[d];
                            matrix[off + d] = val;
                            sum += val * val;
                        }
                        norms[i] = MathF.Sqrt(sum);
                        if (norms[i] > 0f)
                            for (var d = 0; d < dim; d++)
                                matrix[off + d] /= norms[i];
                    }
                }

                corpus = new EmbeddingCorpus(corpusKey, dim, candidateIds, matrix, norms);
                _corpusCache[corpusKey] = corpus;
            }
        }
        Dictionary<int, float>? vsByIdx = null; // 行索引 → 向量分
        if (mode is "hybrid" or "vector" && qvec is not null && corpus != null)
        {
            // 归一化 qvec（corpus 行已归一化，cosine == dot）
            var qnorm = 0f;
            for (var d = 0; d < qvec.Length; d++)
                qnorm += qvec[d] * qvec[d];
            qnorm = MathF.Sqrt(qnorm);
            if (qnorm > 0f)
                for (var d = 0; d < qvec.Length; d++)
                    qvec[d] /= qnorm;

            if (annIds is not null)
            {
                // ANN 路径：只对候选行精确点积，O(M·dim)；非候选行 vs=0（BM25 通道仍全量）
                vsByIdx = new Dictionary<int, float>(Math.Min(annIds.Count, rows.Count));
                foreach (var mid in annIds)
                    if (corpus.TryGetRow(mid, out var idx))
                        vsByIdx[idx] = corpus.RowDot(idx, qvec.AsSpan());
            }
            else
            {
                // 全量精确路径：O(N·dim)，remembrane 原语义
                vsByIdx = new Dictionary<int, float>(rows.Count);
                for (var i = 0; i < rows.Count; i++)
                    vsByIdx[i] = corpus.RowDot(i, qvec.AsSpan());
            }
        }

        var scored =
            new List<(
                double Score,
                double Sim,
                double Rec,
                double Vs,
                double Ks,
                MemoryRecord Mem
            )>();

        for (var i = 0; i < rows.Count; i++)
        {
            var mem = rows[i];

            // 相似度计算
            double vs = 0,
                ks = 0;
            if (vsByIdx is not null && vsByIdx.TryGetValue(i, out var vsv))
                vs = vsv;

            if (mode is "hybrid" or "keyword")
            {
                if (bm25Scores is not null && i < bm25Scores.Length)
                {
                    ks = bm25Scores[i];
                }
            }

            var sim = mode switch
            {
                "hybrid" => AgentMemoryScoring.HybridSimilarity(vs, ks, kwWeight),
                "vector" => vs,
                "keyword" => ks,
                _ => 0.0,
            };

            // 硬规则：similarity 必须 > 0
            if (sim <= minSimilarity)
                continue;

            var (score, recency) = AgentMemoryScoring.CompositeScore(
                sim,
                mem,
                ScoringConfig,
                effectiveNow
            );

            if (score >= minScore)
                scored.Add((score, sim, recency, vs, ks, mem));
        }

        // 5. 排序取 top-k
        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        var top = scored.Take(k).ToList();

        // 6. Touch：更新访问统计
        if (touch && top.Count > 0)
        {
            await TouchMemoriesAsync(top.ConvertAll(r => r.Mem.Id), effectiveNow, ct);
        }

        return top.ConvertAll(r => new RecallResult
        {
            Memory = r.Mem,
            Score = r.Score,
            Similarity = r.Sim,
            Recency = r.Rec,
            VectorScore = r.Vs,
            KeywordScore = r.Ks,
        });
    }

    /// <summary>根据 ID 获取单条记忆。</summary>
    public async Task<MemoryRecord?> GetAsync(string memoryId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return await GetInternalAsync(memoryId, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>获取指定用户的所有记忆。</summary>
    public async Task<List<MemoryRecord>> GetAllAsync(
        string ownerId,
        string? ns = null,
        string? novelId = null,
        string? sessionId = null,
        MemoryVisibility scope = MemoryVisibility.NovelWithGlobal,
        CancellationToken ct = default
    )
    {
        var rows = await LoadRowsAsync(ownerId, ns, novelId, sessionId, scope, ct);
        return rows;
    }

    /// <summary>获取记忆统计。</summary>
    public async Task<MemoryStats> GetStatsAsync(string ownerId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(ct);
            ArgumentNullException.ThrowIfNull(_connection);

            var total = 0;
            var perNamespace = new Dictionary<string, int>();
            var perKind = new Dictionary<string, int>();
            double sumImportance = 0,
                sumUsefulness = 0;
            var expiredCount = 0;
            var now = Now();

            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT namespace, memory_kind, importance, usefulness, expires_at FROM agent_memories WHERE owner_id = $owner_id";
            cmd.Parameters.AddWithValue("$owner_id", ownerId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                total++;
                var ns = reader.GetString(0);
                var kind = reader.GetInt32(1);
                var imp = reader.GetDouble(2);
                var use = reader.GetDouble(3);

                perNamespace.TryGetValue(ns, out var nsCount);
                perNamespace[ns] = nsCount + 1;

                var kindName = ((AgentMemoryKind)kind).ToString();
                perKind.TryGetValue(kindName, out var kindCount);
                perKind[kindName] = kindCount + 1;

                sumImportance += imp;
                sumUsefulness += use;

                if (!reader.IsDBNull(4) && reader.GetDouble(4) <= now)
                    expiredCount++;
            }

            return new MemoryStats
            {
                TotalCount = total,
                PerNamespace = perNamespace,
                PerKind = perKind,
                AvgImportance = total > 0 ? sumImportance / total : 0,
                AvgUsefulness = total > 0 ? sumUsefulness / total : 0,
                ExpiredCount = expiredCount,
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    // ══════════════════════════════════════════════════════
    //  内部
    // ══════════════════════════════════════════════════════

    private async Task<MemoryRecord?> GetInternalAsync(string memoryId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(_connection);
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT {MemCols} FROM agent_memories WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", memoryId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadMemory(reader) : null;
    }

    private async Task<List<MemoryRecord>> LoadRowsAsync(
        string ownerId,
        string? ns,
        string? novelId,
        string? sessionId,
        MemoryVisibility scope,
        CancellationToken ct
    )
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(ct);
            ArgumentNullException.ThrowIfNull(_connection);

            // 外部连接写入检测：data_version 变化 → 清空分词缓存
            await using (var verCmd = _connection.CreateCommand())
            {
                verCmd.CommandText = "PRAGMA data_version";
                var version = Convert.ToInt64(
                    await verCmd.ExecuteScalarAsync(ct),
                    System.Globalization.CultureInfo.InvariantCulture
                );
                if (_dataVersion >= 0 && version != _dataVersion)
                    _corpusCache.Clear(); // 外部写入 → corpus 失效
                _tokenCache.Clear();
                _dataVersion = version;
            }

            var sql = $"SELECT {MemCols} FROM agent_memories WHERE owner_id = $owner_id";
            var parameters = new List<SqliteParameter> { new("$owner_id", ownerId) };

            if (ns is not null)
            {
                sql += " AND namespace = $ns";
                parameters.Add(new SqliteParameter("$ns", ns));
            }
            // 长期集合（scope 决定）+ 本会话的会话级记忆（sessionId 非空时并入）。
            // 会话级记忆（session_id NOT NULL）只在它自己的会话里可见：换会话即不可见。
            var longTerm = scope switch
            {
                MemoryVisibility.All => null, // 跨小说（诊断/运维）
                MemoryVisibility.NovelOnly when novelId is not null => "novel_id = $novel_id",
                MemoryVisibility.NovelWithGlobal when novelId is not null =>
                    "(novel_id IS NULL OR novel_id = $novel_id)",
                _ => "novel_id IS NULL", // GlobalOnly / 未打开小说
            };
            if (novelId is not null && longTerm is not null)
                parameters.Add(new SqliteParameter("$novel_id", novelId));

            if (longTerm is null)
            {
                // All：不过滤
            }
            else if (sessionId is not null)
            {
                sql += $" AND ((session_id IS NULL AND {longTerm}) OR session_id = $session_id)";
                parameters.Add(new SqliteParameter("$session_id", sessionId));
            }
            else
            {
                sql += $" AND session_id IS NULL AND {longTerm}";
            }

            // 过滤过期
            var now = Now();
            sql += " AND (expires_at IS NULL OR expires_at > $now)";
            parameters.Add(new SqliteParameter("$now", now));

            sql += " ORDER BY priority DESC, created_at DESC";

            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            foreach (var p in parameters)
                cmd.Parameters.Add(p);

            var rows = new List<MemoryRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rows.Add(ReadMemory(reader));

            return rows;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>分词缓存条目。</summary>
    private sealed record TokenCacheEntry(
        List<string> Tokens,
        Dictionary<string, int> Tf,
        int DocLen
    );

    /// <summary>
    /// 批量从数据库读取记忆的 embedding 向量。
    /// 缺失或损坏的返回空数组（调用方按无向量处理）。
    /// </summary>
    private async Task<Dictionary<string, float[]>> LoadEmbeddingsAsync(
        List<string> ids,
        CancellationToken ct
    )
    {
        var result = new Dictionary<string, float[]>(ids.Count);
        if (ids.Count == 0)
            return result;

        // SQLite 变量上限 ~999，分批查询
        for (var offset = 0; offset < ids.Count; offset += 500)
        {
            var chunk = ids.Skip(offset).Take(500).ToList();
            var placeholders = string.Join(",", chunk.Select((_, i) => $"$id{i}"));
            var sql = $"SELECT id, embedding FROM agent_memories WHERE id IN ({placeholders})";

            await _lock.WaitAsync(ct);
            try
            {
                await EnsureInitializedAsync(ct);
                ArgumentNullException.ThrowIfNull(_connection);
                await using var cmd = _connection.CreateCommand();
                cmd.CommandText = sql;
                for (var i = 0; i < chunk.Count; i++)
                    cmd.Parameters.AddWithValue($"$id{i}", chunk[i]);

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    if (reader.IsDBNull(1))
                        continue;
                    var bytes = (byte[])reader.GetValue(1);
                    if (bytes.Length is 0 or < 16 || bytes.Length % 4 != 0)
                        continue; // 损坏 BLOB，按无向量处理
                    var vec = UnpackVector(bytes);
                    // 维度一致性：混维向量（历史不同 embedding 配置）算余弦会出错，按无向量处理
                    if (vec.Length != _embeddingService.Dimension)
                        continue;
                    result[reader.GetString(0)] = vec;
                }
            }
            finally
            {
                _lock.Release();
            }
        }
        return result;
    }

    /// <summary>
    /// 取分词结果（带缓存）。返回 (tokens, tf, docLen)。
    /// </summary>
    private (List<string> Tokens, Dictionary<string, int> Tf, int DocLen) GetTokensCached(
        string ownerId,
        string? ns,
        string? novelId,
        MemoryRecord mem
    )
    {
        var key = $"{ownerId}|{ns}|{novelId}";
        var perNovel = _tokenCache.GetOrAdd(
            key,
            _ => new System.Collections.Concurrent.ConcurrentDictionary<string, TokenCacheEntry>()
        );
        if (perNovel.TryGetValue(mem.Id, out var cached))
            return (cached.Tokens, cached.Tf, cached.DocLen);

        var tokens = AgentMemoryBm25.Tokenize(mem.Content);
        var tf = AgentMemoryBm25.TermFrequencies(tokens);
        var entry = new TokenCacheEntry(tokens, tf, tokens.Count);
        perNovel[mem.Id] = entry;
        return (entry.Tokens, entry.Tf, entry.DocLen);
    }

    /// <summary>内容变更后按记忆 ID 失效分词缓存（避免每次写入全部 jieba 重分词）。</summary>
    /// <param name="memoryIds">被修改的记忆 ID；null 时整体清空（仅 embedding 服务重配 / 外部变更）。</param>
    private void InvalidateTokenCache(List<string>? memoryIds = null)
    {
        if (memoryIds is null || memoryIds.Count == 0)
        {
            _tokenCache.Clear();
            return;
        }
        foreach (var key in _tokenCache.Keys.ToArray())
        {
            if (!_tokenCache.TryGetValue(key, out var inner))
                continue;
            foreach (var id in memoryIds)
            {
                inner.TryRemove(id, out _);
            }
            if (inner.IsEmpty)
                _tokenCache.TryRemove(key, out _);
        }
    }

    private async Task TouchMemoriesAsync(List<string> ids, double now, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            ArgumentNullException.ThrowIfNull(_connection);
            foreach (var id in ids)
            {
                await using var cmd = _connection.CreateCommand();
                cmd.CommandText =
                    "UPDATE agent_memories SET last_accessed_at = $now, access_count = access_count + 1 WHERE id = $id";
                cmd.Parameters.AddWithValue("$now", now);
                cmd.Parameters.AddWithValue("$id", id);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task JournalAsync(
        string op,
        string memoryId,
        string ns,
        string payloadJson,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(_connection);
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "INSERT INTO agent_journal (ts, op, memory_id, namespace, payload_json) VALUES ($ts, $op, $memory_id, $namespace, $payload)";
        cmd.Parameters.AddWithValue("$ts", Now());
        cmd.Parameters.AddWithValue("$op", op);
        cmd.Parameters.AddWithValue("$memory_id", memoryId);
        cmd.Parameters.AddWithValue("$namespace", ns);
        cmd.Parameters.AddWithValue("$payload", payloadJson);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static MemoryRecord ReadMemory(SqliteDataReader reader)
    {
        return new MemoryRecord
        {
            Id = reader.GetString(0),
            OwnerId = reader.GetString(1),
            NovelId = reader.IsDBNull(2) ? null : reader.GetString(2),
            SessionId = reader.IsDBNull(3) ? null : reader.GetString(3),
            Namespace = reader.GetString(4),
            Content = reader.GetString(5),
            Importance = reader.GetDouble(6),
            MetadataJson = reader.IsDBNull(7) ? "{}" : reader.GetString(7),
            CreatedAt = reader.GetDouble(8),
            LastAccessedAt = reader.IsDBNull(9) ? null : reader.GetDouble(9),
            AccessCount = reader.GetInt32(10),
            Usefulness = reader.GetDouble(11),
            MemoryKind = (AgentMemoryKind)reader.GetInt32(12),
            Confidence = reader.GetDouble(13),
            Priority = reader.GetInt32(14),
            ExpiresAt = reader.IsDBNull(15) ? null : reader.GetDouble(15),
            EvidenceRefsJson = reader.IsDBNull(16) ? null : reader.GetString(16),
        };
    }

    private static byte[] PackVector(float[] vector)
    {
        var bytes = new byte[vector.Length * 4];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] UnpackVector(byte[] bytes)
    {
        var vector = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }

    /// <summary>
    /// JSON 字符串编码（用于 journal 日志，避免注入）。
    /// </summary>
    private static string JsonEncode(string s) =>
        s.Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");

    /// <summary>double 的 JSON 数字格式化（不变文化）。</summary>
    private static string Fmt(double d) =>
        d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// 当前 Unix 时间戳（微秒精度）。
    /// 毫秒精度会让同一毫秒内的连续操作（store→snapshot→forget）时间戳相同，
    /// 导致时间旅行回放（ts &lt;= 快照 ts）把同毫秒的后序操作也包含进去。
    /// Ticks 为 100ns；减去 Unix 纪元（1970-01-01）的 ticks 后除以 1e7 得微秒精度秒。
    /// </summary>
    private static double Now() =>
        (DateTimeOffset.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10_000_000.0;

    // ══════════════════════════════════════════════════════
    //  冲突检测
    // ══════════════════════════════════════════════════════

    /// <summary>
    /// 发现与 query 相关的记忆冲突。
    /// query 为 null 时扫描整个命名空间；非 null 时先召回再检测。
    /// 返回结构化证据，由 LLM 判断是 bug 还是 feature。
    /// </summary>
    public async Task<List<Conflict>> FindConflictsAsync(
        string? query,
        string ownerId,
        string? ns = "default",
        string? novelId = null,
        int k = 50,
        double similarityFloor = 0.35,
        double similarityCeiling = 0.92,
        string minConfidence = "possible",
        CancellationToken ct = default
    )
    {
        if (minConfidence is not "possible" and not "likely")
            throw new ArgumentException("minConfidence 必须为 'possible' 或 'likely'");

        // 获取候选记忆
        List<MemoryRecord> mems;
        if (query is not null)
        {
            var results = await RecallAsync(
                query,
                ownerId,
                k,
                ns,
                novelId,
                mode: "hybrid",
                touch: false,
                ct: ct
            );
            mems = results.ConvertAll(r => r.Memory);
        }
        else
        {
            mems = await GetAllAsync(
                ownerId,
                ns,
                novelId,
                sessionId: null,
                MemoryVisibility.NovelWithGlobal,
                ct
            );
        }

        if (mems.Count < 2)
            return [];

        // 获取向量（从数据库读存储的 embedding，确定性、零额外嵌入调用）
        var vectors = new List<float[]>(mems.Count);
        var vecs = await LoadEmbeddingsAsync(mems.ConvertAll(m => m.Id), ct);
        vectors.AddRange(
            mems.Select(mem => vecs.TryGetValue(mem.Id, out var v) && v.Length > 0 ? v : [])
        );

        var conflicts = AgentMemoryConflict.DetectConflicts(
            mems,
            vectors,
            similarityFloor,
            similarityCeiling
        );

        if (minConfidence == "likely")
            conflicts = conflicts.Where(c => c.Confidence == "likely").ToList();

        // 本地判定（可选）：启发式证据 → 四分类裁定；失败 fail-open（原样返回）
        if (_systemOne is { Enabled: true, Options.Conflicts.Enabled: true })
            conflicts = await AgentMemoryConflict.AdjudicateAsync(_systemOne, conflicts, ct);

        return conflicts;
    }

    /// <summary>
    /// 解决冲突：保留一条记忆，丢弃其他。
    /// 丢弃的记忆在 journal 中可追溯。
    /// </summary>
    public async Task ResolveConflictAsync(
        string keepId,
        string[] dropIds,
        string reason = "",
        CancellationToken ct = default
    )
    {
        var kept = await GetAsync(keepId, ct);
        if (kept is null)
            throw new KeyNotFoundException($"记忆 {keepId} 不存在");

        await _lock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(ct);
            ArgumentNullException.ThrowIfNull(_connection);

            foreach (var dropId in dropIds)
            {
                var mem = await GetInternalAsync(dropId, ct);
                if (mem is null)
                    continue;

                await using var delCmd = _connection.CreateCommand();
                delCmd.CommandText = "DELETE FROM agent_memories WHERE id = $id";
                delCmd.Parameters.AddWithValue("$id", dropId);
                await delCmd.ExecuteNonQueryAsync(ct);

                await JournalAsync(
                    "resolve",
                    keepId,
                    kept.Namespace,
                    $$"""{"dropped":"{{dropId}}","dropped-content":"{{JsonEncode(mem.Content)}}","reason":"{{JsonEncode(reason)}}"}""",
                    ct
                );
                InvalidateTokenCache(new List<string> { dropId });
                _corpusCache.Clear(); // 写入失效（对齐 remembrane _invalidate 粗粒度）
                if (_vecAvailable)
                {
                    try
                    {
                        await AgentMemoryVec.SyncDeleteAsync(_connection!, dropId, ct);
                    }
                    catch (SqliteException ex)
                    {
                        _logger?.LogWarning(
                            ex,
                            "agent_memory_vec 冲突裁决删除同步失败（可重建）: {Id}",
                            dropId
                        );
                    }
                }
                Interlocked.Increment(ref _callCacheVersion);

                await JournalAsync(
                    "forget",
                    dropId,
                    mem.Namespace,
                    $$"""{"reason":"resolved: {{JsonEncode(reason)}}"}""",
                    ct
                );
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    //  时间旅行
    // ══════════════════════════════════════════════════════

    /// <summary>记录一个命名时间点快照。</summary>
    public async Task SnapshotAsync(string label, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(ct);
            ArgumentNullException.ThrowIfNull(_connection);

            var ts = Now();
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "INSERT OR REPLACE INTO agent_snapshots (label, ts) VALUES ($label, $ts)";
            cmd.Parameters.AddWithValue("$label", label);
            cmd.Parameters.AddWithValue("$ts", ts);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>列出所有快照。</summary>
    public async Task<Dictionary<string, double>> SnapshotsAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(ct);
            ArgumentNullException.ThrowIfNull(_connection);

            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT label, ts FROM agent_snapshots ORDER BY ts";
            var result = new Dictionary<string, double>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result[reader.GetString(0)] = reader.GetDouble(1);
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>查看某个时间点的记忆状态。</summary>
    /// <param name="label">快照标签。</param>
    /// <param name="novelId">小说过滤（null=全部；非 null=该小说 + 全局记忆）。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<List<MemoryRecord>> AsOfAsync(
        string label,
        string? novelId = null,
        CancellationToken ct = default
    )
    {
        var ts = await ResolveSnapshotTsAsync(label, ct);
        var state = await StateAtAsync(ts, novelId, ct);
        return state.Values.ToList();
    }

    /// <summary>对比两个时间点之间的变化。</summary>
    /// <param name="beforeLabel">起始快照标签。</param>
    /// <param name="afterLabel">结束快照标签（null=当前）。</param>
    /// <param name="novelId">小说过滤（null=全部；非 null=该小说 + 全局记忆）。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<DiffResult> DiffAsync(
        string beforeLabel,
        string? afterLabel = null,
        string? novelId = null,
        CancellationToken ct = default
    )
    {
        var tsBefore = await ResolveSnapshotTsAsync(beforeLabel, ct);
        var tsAfter = afterLabel is not null ? await ResolveSnapshotTsAsync(afterLabel, ct) : Now();

        var before = await StateAtAsync(tsBefore, novelId, ct);
        var after = await StateAtAsync(tsAfter, novelId, ct);

        var added = new List<MemoryRecord>();
        var removed = new List<MemoryRecord>();
        var changed = new List<(MemoryRecord, double)>();

        foreach (var (id, mem) in after)
        {
            if (!before.ContainsKey(id))
                added.Add(mem);
            else if (Math.Abs(before[id].Importance - mem.Importance) > 0.001)
                changed.Add((mem, before[id].Importance));
        }

        foreach (var (id, mem) in before)
        {
            if (!after.ContainsKey(id))
                removed.Add(mem);
        }

        return new DiffResult
        {
            Added = added,
            Removed = removed,
            Changed = changed,
        };
    }

    /// <summary>查看 journal 历史。</summary>
    public async Task<List<JournalEntry>> GetJournalAsync(
        string? ns = null,
        int limit = 100,
        CancellationToken ct = default
    )
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(ct);
            ArgumentNullException.ThrowIfNull(_connection);

            var sql = ns is null
                ? "SELECT seq, ts, op, memory_id, namespace, payload_json FROM agent_journal ORDER BY seq DESC LIMIT $limit"
                : "SELECT seq, ts, op, memory_id, namespace, payload_json FROM agent_journal WHERE namespace = $ns ORDER BY seq DESC LIMIT $limit";

            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$limit", limit);
            if (ns is not null)
                cmd.Parameters.AddWithValue("$ns", ns);

            var entries = new List<JournalEntry>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                entries.Add(
                    new JournalEntry
                    {
                        Seq = reader.GetInt64(0),
                        Timestamp = reader.GetDouble(1),
                        Op = reader.GetString(2),
                        MemoryId = reader.GetString(3),
                        Namespace = reader.GetString(4),
                        PayloadJson = reader.IsDBNull(5) ? "{}" : reader.GetString(5),
                    }
                );
            }
            return entries;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>回放 journal 到指定时间点，重建记忆状态。</summary>
    /// <param name="ts">目标时间戳。</param>
    /// <param name="novelId">小说过滤：null 返回全部；非 null 仅保留该小说 + 全局（NovelId 为 null）记忆。</param>
    /// <param name="ct">取消令牌。</param>
    private async Task<Dictionary<string, MemoryRecord>> StateAtAsync(
        double ts,
        string? novelId,
        CancellationToken ct
    )
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(ct);
            ArgumentNullException.ThrowIfNull(_connection);

            await using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT op, memory_id, namespace, payload_json FROM agent_journal WHERE ts <= $ts ORDER BY seq";
            cmd.Parameters.AddWithValue("$ts", ts);

            var state = new Dictionary<string, MemoryRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var op = reader.GetString(0);
                var mid = reader.GetString(1);
                var ns = reader.GetString(2);
                var payloadJson = reader.IsDBNull(3) ? "{}" : reader.GetString(3);

                try
                {
                    switch (op)
                    {
                        case "store" when payloadJson.Length > 2:
                            var storePayload = JsonSerializer.Deserialize<JsonElement>(payloadJson);
                            var content = storePayload.TryGetProperty("content", out var c)
                                ? c.GetString() ?? ""
                                : "";
                            var importance = storePayload.TryGetProperty("importance", out var imp)
                                ? imp.GetDouble()
                                : 0.5;
                            // 完整重建（旧条目缺字段时回退默认值，保持向后兼容）
                            var ownerId2 = storePayload.TryGetProperty("owner-id", out var o)
                                ? o.GetString() ?? ""
                                : "";
                            var novelId2 =
                                storePayload.TryGetProperty("novel-id", out var n)
                                && n.ValueKind == JsonValueKind.String
                                && !string.IsNullOrEmpty(n.GetString())
                                    ? n.GetString()
                                    : null;
                            var createdAt2 = storePayload.TryGetProperty("created-at", out var ca)
                                ? ca.GetDouble()
                                : ts;
                            var expiresAt2 =
                                storePayload.TryGetProperty("expires-at", out var ea)
                                && ea.ValueKind == JsonValueKind.Number
                                    ? ea.GetDouble()
                                    : (double?)null;
                            var confidence2 = storePayload.TryGetProperty("confidence", out var cf)
                                ? cf.GetDouble()
                                : 1.0;
                            var priority2 = storePayload.TryGetProperty("priority", out var pr)
                                ? pr.GetInt32()
                                : 0;
                            var metadata2 = storePayload.TryGetProperty("metadata", out var md)
                                ? md.GetString() ?? "{}"
                                : "{}";
                            state[mid] = new MemoryRecord
                            {
                                Id = mid,
                                OwnerId = ownerId2,
                                NovelId = novelId2,
                                Namespace = ns,
                                Content = content,
                                Importance = importance,
                                MetadataJson = metadata2,
                                CreatedAt = createdAt2,
                                ExpiresAt = expiresAt2,
                                Confidence = confidence2,
                                Priority = priority2,
                            };
                            break;
                        case "forget":
                            state.Remove(mid);
                            break;
                        case "reinforce"
                        or "consolidate" when state.ContainsKey(mid) && payloadJson.Length > 2:
                            var impPayload = JsonSerializer.Deserialize<JsonElement>(payloadJson);
                            if (impPayload.TryGetProperty("importance", out var newImp))
                                state[mid] = state[mid] with { Importance = newImp.GetDouble() };
                            break;
                    }
                }
                catch
                {
                    // 忽略解析失败的 journal 条目
                }
            }

            // 小说过滤：仅保留该小说的记忆 + 全局记忆（NovelId 为 null）
            if (novelId is not null)
            {
                foreach (
                    var key in state
                        .Where(kv => kv.Value.NovelId is not null && kv.Value.NovelId != novelId)
                        .Select(kv => kv.Key)
                        .ToList()
                )
                    state.Remove(key);
            }

            return state;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>解析快照标签为时间戳。</summary>
    private async Task<double> ResolveSnapshotTsAsync(string label, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(ct);
            ArgumentNullException.ThrowIfNull(_connection);

            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT ts FROM agent_snapshots WHERE label = $label";
            cmd.Parameters.AddWithValue("$label", label);
            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is double ts)
                return ts;
            throw new KeyNotFoundException($"快照 '{label}' 不存在");
        }
        finally
        {
            _lock.Release();
        }
    }

    // ══════════════════════════════════════════════════════
    //  Token 预算打包
    // ══════════════════════════════════════════════════════

    /// <summary>
    /// 在给定 token 预算内选择最优记忆子集。
    /// 先召回候选 → 近重复抑制 → 0/1 背包选择。
    /// </summary>
    public async Task<List<RecallResult>> PackAsync(
        string query,
        string ownerId,
        int budgetTokens = 800,
        string? ns = "default",
        string? novelId = null,
        string mode = "hybrid",
        int candidates = 256,
        double dedupeThreshold = 0.92,
        Func<string, int>? tokenEstimator = null,
        bool touch = true,
        CancellationToken ct = default
    )
    {
        // 1. 召回候选项
        var results = await RecallAsync(
            query,
            ownerId,
            k: candidates,
            ns,
            novelId,
            sessionId: null,
            MemoryVisibility.NovelWithGlobal,
            mode,
            touch: false,
            ct: ct
        );
        if (results.Count == 0)
            return [];

        // 2. 近重复抑制（向量从数据库读，不再重新嵌入）
        var vectors = new List<float[]>(results.Count);
        var vecs = await LoadEmbeddingsAsync(results.ConvertAll(r => r.Memory.Id), ct);
        vectors.AddRange(
            results.Select(r => vecs.TryGetValue(r.Memory.Id, out var v) && v.Length > 0 ? v : [])
        );

        var kept = AgentMemoryPacking.Deduplicate(results, vectors, dedupeThreshold);
        var deduped = kept.Select(i => results[i]).ToList();

        // 3. 估算 token 并背包
        var est = tokenEstimator ?? AgentMemoryPacking.EstimateTokens;
        var scores = deduped.ConvertAll(r => r.Score).ToArray();
        var tokens = deduped.ConvertAll(r => est(r.Memory.Content)).ToArray();

        var (selected, _) = AgentMemoryPacking.KnapsackPack(scores, tokens, budgetTokens);

        var chosen = selected.Select(i => deduped[i] with { Tokens = tokens[i] }).ToList();
        chosen.Sort((a, b) => b.Score.CompareTo(a.Score));

        // 4. Touch
        if (touch && chosen.Count > 0)
        {
            var now = Now();
            await TouchMemoriesAsync(chosen.ConvertAll(r => r.Memory.Id), now, ct);
        }

        return chosen;
    }

    // ══════════════════════════════════════════════════════
    //  合并去重
    // ══════════════════════════════════════════════════════

    /// <summary>
    /// 合并近重复记忆：余弦相似度 ≥ threshold 的两条记忆合并为一条。
    /// 合并策略：保留指定 owner 的，吸收其他。
    /// </summary>
    public async Task<int> ConsolidateAsync(
        string ownerId,
        string? ns = null,
        double threshold = 0.92,
        CancellationToken ct = default
    )
    {
        var all = await GetAllAsync(ownerId, ns, scope: MemoryVisibility.All, ct: ct);
        // 会话级记忆不参与合并：合并会把会话专属内容并进长期记忆（或反之），破坏作用域语义。
        var mems = all.Where(m => m.SessionId is null).ToList();
        if (mems.Count < 2)
            return 0;

        // 获取向量（从数据库读存储的 embedding，确定性、零额外嵌入调用）
        var vectors = new List<float[]>(mems.Count);
        var vecs = await LoadEmbeddingsAsync(mems.ConvertAll(m => m.Id), ct);
        vectors.AddRange(
            mems.Select(mem => vecs.TryGetValue(mem.Id, out var v) && v.Length > 0 ? v : [])
        );

        var removedIds = new HashSet<string>();
        var merges = new List<(MemoryRecord Survivor, MemoryRecord Absorbed)>();

        for (var i = 0; i < mems.Count; i++)
        {
            if (removedIds.Contains(mems[i].Id))
                continue;
            for (var j = i + 1; j < mems.Count; j++)
            {
                if (removedIds.Contains(mems[j].Id))
                    continue;

                var sim =
                    vectors[i].Length > 0 && vectors[j].Length > 0
                        ? AgentMemoryScoring.CosineSimilarity(vectors[i], vectors[j])
                        : 0;
                if (sim >= threshold)
                {
                    // 保留创建时间早的作为 survivor
                    var (survivor, absorbed) =
                        mems[i].CreatedAt <= mems[j].CreatedAt
                            ? (mems[i], mems[j])
                            : (mems[j], mems[i]);
                    merges.Add((survivor, absorbed));
                    removedIds.Add(absorbed.Id);
                }
            }
        }

        if (merges.Count == 0)
            return 0;

        await _lock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync(ct);
            ArgumentNullException.ThrowIfNull(_connection);

            foreach (var (survivor, absorbed) in merges)
            {
                // 合并：max importance, sum access_count, sum usefulness
                var newImportance = Math.Max(survivor.Importance, absorbed.Importance);
                var newAccessCount = survivor.AccessCount + absorbed.AccessCount;
                var newUsefulness = survivor.Usefulness + absorbed.Usefulness;

                await using var updCmd = _connection.CreateCommand();
                updCmd.CommandText =
                    "UPDATE agent_memories SET importance = $imp, access_count = $acc, usefulness = $use WHERE id = $id";
                updCmd.Parameters.AddWithValue("$imp", newImportance);
                updCmd.Parameters.AddWithValue("$acc", newAccessCount);
                updCmd.Parameters.AddWithValue("$use", newUsefulness);
                updCmd.Parameters.AddWithValue("$id", survivor.Id);
                await updCmd.ExecuteNonQueryAsync(ct);

                // 删除被吸收的
                await using var delCmd = _connection.CreateCommand();
                delCmd.CommandText = "DELETE FROM agent_memories WHERE id = $id";
                delCmd.Parameters.AddWithValue("$id", absorbed.Id);
                await delCmd.ExecuteNonQueryAsync(ct);

                await JournalAsync(
                    "consolidate",
                    survivor.Id,
                    survivor.Namespace,
                    $$"""{"absorbed":"{{absorbed.Id}}","importance":{{newImportance}}}""",
                    ct
                );
                await JournalAsync(
                    "forget",
                    absorbed.Id,
                    absorbed.Namespace,
                    """{"reason":"consolidated"}""",
                    ct
                );
                InvalidateTokenCache(new List<string> { absorbed.Id });
                _corpusCache.Clear(); // 写入失效（对齐 remembrane _invalidate 粗粒度）
                if (_vecAvailable)
                {
                    try
                    {
                        await AgentMemoryVec.SyncDeleteAsync(_connection!, absorbed.Id, ct);
                    }
                    catch (SqliteException ex)
                    {
                        _logger?.LogWarning(
                            ex,
                            "agent_memory_vec 合并删除同步失败（可重建）: {Id}",
                            absorbed.Id
                        );
                    }
                }
            }
            Interlocked.Increment(ref _callCacheVersion);
        }
        finally
        {
            _lock.Release();
        }

        return merges.Count;
    }

    // ══════════════════════════════════════════════════════
    //  释放
    // ══════════════════════════════════════════════════════

    public async ValueTask DisposeAsync()
    {
        await _lock.WaitAsync(CancellationToken.None);
        try
        {
            if (_connection is not null)
                await _connection.DisposeAsync();
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }
}
