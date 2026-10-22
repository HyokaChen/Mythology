using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Core.Cloud;
using Microsoft.Extensions.Logging;

namespace CloudDiagnosis;

/// <summary>单本小说的跨章状态缓存条目（HARNESS-CLOUD §6.1）。</summary>
public sealed record SnapshotEntry
{
    /// <summary>云端原始快照（fiction.context.retrieve；Features 含 narrativeEntropy 等深特征）。</summary>
    public required AgentContextSnapshot Snapshot { get; init; }

    /// <summary>刷新时的目标章序（L2-4 stale 判定锚点）。</summary>
    public required int FetchedAtChapterOrder { get; init; }

    public required DateTimeOffset FetchedAtUtc { get; init; }

    /// <summary>本次快照的叙事熵（features.narrativeEntropy；IncludeFeatures=true 时云端返回）。</summary>
    public double? NarrativeEntropy { get; init; }

    /// <summary>
    /// 与上一次缓存的熵差值（H(t) − H(t−1) 廉价近似，纯减法，§6.2）。
    /// 首次刷新无前值时为 null。
    /// </summary>
    public double? NarrativeEntropyDelta { get; init; }
}

/// <summary>
/// 跨章状态缓存（HARNESS-CLOUD §6 组件 D）— 解决断点 2：
/// 为「配角衰减」「暗线超龄」「爽点间隔」提供廉价跨章判定输入，
/// 把「每次问云端」降为「本地阈值判断 + 命中才升级」。
/// P2 起支持 SQLite 持久化（vectors.db context_snapshots 表，跨会话衰减追踪）。
/// </summary>
public sealed class ContextSnapshotCache(
    IFictionHarnessClient client,
    string? persistenceDbPath = null,
    ILogger? logger = null,
    int requestTimeoutSeconds = 15
)
{
    private readonly ConcurrentDictionary<string, SnapshotEntry> _entries = new(
        StringComparer.Ordinal
    );

    /// <summary>P2 持久化：vectors.db 的 context_snapshots 表。低频路径，同步 SQLite 可接受。</summary>
    private readonly string? _dbPath = persistenceDbPath;

    /// <summary>单请求超时秒数（§3.4：RequestTimeoutSeconds=15；60s CloudApiTimeout 仅作安全网）。</summary>
    private readonly int _requestTimeoutSeconds = Math.Max(1, requestTimeoutSeconds);

    /// <summary>持久化载荷版本：结构升级时旧版本行直接丢弃、下次刷新重建（派生数据可重取）。</summary>
    private const int PersistedPayloadVersion = 1;

    /// <summary>持久化载荷封装（版本 + 快照）——替代裸快照 JSON，供结构演进时安全丢弃。</summary>
    private sealed record PersistedSnapshot(int Version, AgentContextSnapshot? Snapshot);

    /// <summary>
    /// 读取缓存条目：内存 miss 时从 vectors.db context_snapshots 表回源（P2 持久化）。
    /// </summary>
    public SnapshotEntry? TryGet(string novelId)
    {
        if (_entries.GetValueOrDefault(novelId) is { } inMemory)
            return inMemory;
        if (_dbPath is null)
            return null;

        var loaded = LoadFromDb(novelId);
        if (loaded is not null)
            _entries[novelId] = loaded;
        return loaded;
    }

    /// <summary>快照 stale 章序阈值（§6.2：距上次刷新 ≥ 此值 → L2-4；R-3 信号边界共用）。</summary>
    public const int StaleOrderThreshold = 5;

    /// <summary>失效判定（§6.2）：无条目 / 云端 ChapterCount 与本地不一致 /
    /// 当前 Order 距上次刷新 ≥ StaleOrderThreshold（L2-4）→ stale。调用方据此触发刷新或 L2-4 事件。</summary>
    public bool IsStale(string novelId, int currentMaxOrder, int localChapterCount)
    {
        if (TryGet(novelId) is not { } entry)
            return true;
        if (entry.Snapshot.ChapterCount > 0 && entry.Snapshot.ChapterCount != localChapterCount)
            return true;
        return currentMaxOrder - entry.FetchedAtChapterOrder >= StaleOrderThreshold;
    }

    /// <summary>
    /// 拉取并更新快照（基础档 fiction.context.retrieve，由 GetContextAsync 内部强制；
    /// request.Options.IncludeFeatures=true 以获取 narrativeEntropy）。
    /// 失败返回 null 并沿用旧值（§6.2：信号的「边界」段声明数据年龄）。
    /// 用户取消 OCE 向上抛出（§12 红线 5）。
    /// </summary>
    public async Task<SnapshotEntry?> RefreshAsync(
        string novelId,
        HarnessRequest request,
        int chapterOrder,
        CancellationToken ct
    )
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_requestTimeoutSeconds));
            var snapshot = await client.GetContextAsync(
                request,
                BuildSnapshotIdempotencyKey(novelId, chapterOrder, request),
                cts.Token
            );

            return UpdateFromSnapshot(novelId, snapshot, chapterOrder);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LogDegraded("快照刷新超时");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDegraded(ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 成功轮复用调用方已取得的快照更新缓存（免二次 context.retrieve 云端调用）。
    /// 存储语义与 <see cref="RefreshAsync"/> 一致：熵/熵差计算 → 内存 → SQLite。
    /// 缓存更新尽力而为：失败静默降级返回 null，不阻断调用方回合。
    /// </summary>
    public SnapshotEntry? UpdateFromSnapshot(
        string novelId,
        AgentContextSnapshot snapshot,
        int chapterOrder
    )
    {
        try
        {
            var entropy = ExtractEntropy(snapshot);
            _entries.TryGetValue(novelId, out var previous);
            var entry = new SnapshotEntry
            {
                Snapshot = snapshot,
                FetchedAtChapterOrder = chapterOrder,
                FetchedAtUtc = DateTimeOffset.UtcNow,
                NarrativeEntropy = entropy,
                NarrativeEntropyDelta =
                    entropy is { } current && previous?.NarrativeEntropy is { } last
                        ? Math.Round(current - last, 4)
                        : null,
            };
            _entries[novelId] = entry;
            SaveToDb(novelId, entry);
            return entry;
        }
        catch (Exception ex)
        {
            LogDegraded($"快照缓存更新失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// MIN-4 修复：快照刷新幂等键稳定化——弃用 {cts.GetHashCode()}（对象哈希不稳定）。
    /// 改「章节集指纹」（与 DiagnosisScheduler.BuildIdempotencyKey 同式）——
    /// 前文改写也产生新键；完整 SHA-256 替代 8 hex 截断。
    /// </summary>
    private static string BuildSnapshotIdempotencyKey(
        string novelId,
        int chapterOrder,
        HarnessRequest request
    )
    {
        var hash = ChapterSetFingerprint.Compute(request.Chapters);
        return $"snap:{novelId}:{chapterOrder}:{hash}";
    }

    /// <summary>从 Features JsonElement 提取 narrativeEntropy（缺省 null）。</summary>
    private static double? ExtractEntropy(AgentContextSnapshot snapshot)
    {
        if (
            snapshot.Features is JsonElement features
            && features.ValueKind == JsonValueKind.Object
            && features.TryGetProperty("narrative-entropy", out var el)
            && el.ValueKind == JsonValueKind.Number
        )
            return el.GetDouble();
        return null;
    }

    // ── P2 持久化（vectors.db context_snapshots 表；派生数据可重建，读写失败告警降级、不动已有表）──

    /// <summary>从 SQLite 回源缓存条目。任何失败返回 null（不阻断内存路径）。</summary>
    private SnapshotEntry? LoadFromDb(string novelId)
    {
        if (_dbPath is null)
            return null;
        try
        {
            using var conn = CloudVectorsDb.Open(_dbPath);
            EnsureTable(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT snapshot_json, fetched_at_chapter_order, fetched_at_utc, narrative_entropy, narrative_entropy_delta FROM context_snapshots WHERE novel_id = $id";
            cmd.Parameters.AddWithValue("$id", novelId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;

            var persisted = JsonSerializer.Deserialize<PersistedSnapshot>(reader.GetString(0));
            if (persisted is not { Version: PersistedPayloadVersion, Snapshot: { } snapshot })
                return null; // 旧/未知结构 → 丢弃（下次刷新重建）
            return new SnapshotEntry
            {
                Snapshot = snapshot,
                FetchedAtChapterOrder = reader.GetInt32(1),
                FetchedAtUtc = DateTimeOffset.Parse(
                    reader.GetString(2),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind
                ),
                NarrativeEntropy = reader.IsDBNull(3) ? null : reader.GetDouble(3),
                NarrativeEntropyDelta = reader.IsDBNull(4) ? null : reader.GetDouble(4),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDegraded($"持久化回源失败：{ex.Message}");
            return null;
        }
    }

    private void SaveToDb(string novelId, SnapshotEntry entry)
    {
        if (_dbPath is null)
            return;
        try
        {
            using var conn = CloudVectorsDb.Open(_dbPath);
            EnsureTable(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO context_snapshots (novel_id, snapshot_json, fetched_at_chapter_order, fetched_at_utc, narrative_entropy, narrative_entropy_delta)
                VALUES ($id, $json, $order, $utc, $entropy, $delta)
                ON CONFLICT(novel_id) DO UPDATE SET
                    snapshot_json = $json,
                    fetched_at_chapter_order = $order,
                    fetched_at_utc = $utc,
                    narrative_entropy = $entropy,
                    narrative_entropy_delta = $delta
                """;
            cmd.Parameters.AddWithValue("$id", novelId);
            cmd.Parameters.AddWithValue(
                "$json",
                JsonSerializer.Serialize(
                    new PersistedSnapshot(PersistedPayloadVersion, entry.Snapshot)
                )
            );
            cmd.Parameters.AddWithValue("$order", entry.FetchedAtChapterOrder);
            cmd.Parameters.AddWithValue("$utc", entry.FetchedAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$entropy", entry.NarrativeEntropy ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue(
                "$delta",
                entry.NarrativeEntropyDelta ?? (object)DBNull.Value
            );
            _ = cmd.ExecuteNonQuery();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDegraded($"持久化写入失败：{ex.Message}");
        }
    }

    private static void EnsureTable(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS context_snapshots (
                novel_id TEXT PRIMARY KEY,
                snapshot_json TEXT NOT NULL,
                fetched_at_chapter_order INTEGER NOT NULL,
                fetched_at_utc TEXT NOT NULL,
                narrative_entropy REAL,
                narrative_entropy_delta REAL
            )
            """;
        _ = cmd.ExecuteNonQuery();
    }

    private void LogDegraded(string reason) =>
        logger?.LogWarning("[云端诊断] 快照刷新失败：{Reason}", reason);
}
