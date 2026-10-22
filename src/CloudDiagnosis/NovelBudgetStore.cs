using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CloudDiagnosis;

/// <summary>
/// 单本小说的跨会话预算状态（HARNESS-CLOUD §16.5 方案 B/C）。
/// 方案 B：每小说预算池（DeepBudgetPerNovel，默认 100），持久化跨会话累计；
/// 方案 C：预算银行——前期未使用预算累积到后期（会话结余存入银行，
/// 后续会话先用银行再花本会话配额）。
/// </summary>
public sealed record NovelBudgetState
{
    /// <summary>小说 ID。</summary>
    public required string NovelId { get; init; }

    /// <summary>方案 B：本小说持久预算池剩余（跨会话累计，初始 = DeepBudgetPerNovel）。</summary>
    public int PoolRemaining { get; init; }

    /// <summary>方案 C：预算银行余额（会话未用完的配额结余累积，可跨会话透支使用）。</summary>
    public int BankBalance { get; init; }

    /// <summary>本会话已用（用于会话内告警与幂等判定）。</summary>
    public int SessionUsed { get; init; }

    /// <summary>本小说可用的预算总量（池 + 银行）。</summary>
    public int TotalRemaining => Math.Max(0, PoolRemaining + BankBalance);
}

/// <summary>
/// 每小说预算池 + 预算银行的 SQLite 持久化（HARNESS-CLOUD §16.5 方案 B/C）。
/// 与 ContextSnapshotCache 共用 vectors.db，独立表 novel_budget；
/// 派生数据可重建；读写失败告警降级、不动已有表（对齐 VEC.md 哲学）。
/// </summary>
public sealed class NovelBudgetStore(string? persistenceDbPath = null, ILogger? logger = null)
{
    private readonly ConcurrentDictionary<string, NovelBudgetState> _states = new(
        StringComparer.Ordinal
    );

    /// <summary>持久化路径（vectors.db）。null = 仅内存（测试/未配置）。</summary>
    private readonly string? _dbPath = persistenceDbPath;

    /// <summary>
    /// 读取/初始化一本小说的预算状态。首次使用按 DeepBudgetPerNovel 初始化池。
    /// 内存 miss 时从 SQLite 回源；会话结余自动转入银行（方案 C）。
    /// 传入 <paramref name="deepBudgetPerSession"/> 时启用「跨会话自愈」：宿主退出不显式
    /// SettleSession 的场景下，DB 回源发现上会话残留 SessionUsed &gt; 0 → 自动把
    /// 未用配额转入银行并清零（回源即跨会话边界，等价宿主退出隐式结算）。
    /// 自愈采用与 <see cref="SettleSession"/> 相同的总量守恒语义：
    /// 池 − leftover、银行 + leftover，Pool+Bank 恒等于 DeepBudgetPerNovel − 累计消耗。
    /// </summary>
    public NovelBudgetState GetOrCreate(
        string novelId,
        int deepBudgetPerNovel,
        int? deepBudgetPerSession = null
    )
    {
        if (_states.TryGetValue(novelId, out var existing))
            return existing;

        var loaded = LoadFromDb(novelId, deepBudgetPerNovel);
        if (deepBudgetPerSession is { } quota && loaded.SessionUsed > 0)
        {
            // 自愈结算：未用配额从池转入银行（总量守恒），SessionUsed 清零
            var leftover = Math.Min(Math.Max(0, quota - loaded.SessionUsed), loaded.PoolRemaining);
            loaded = loaded with
            {
                PoolRemaining = loaded.PoolRemaining - leftover,
                BankBalance = loaded.BankBalance + leftover,
                SessionUsed = 0,
            };
            _states[novelId] = loaded;
            SaveToDb(novelId, loaded);
            return loaded;
        }

        _states[novelId] = loaded;
        return loaded;
    }

    /// <summary>
    /// 消费一次深度诊断。返回是否消费成功（池+银行总余量 &gt; 0）。
    /// 消费顺序：本会话配额优先，随后银行，最后持久池（方案 C 先用银行）。
    /// 消费成功后同步落盘。
    /// </summary>
    public bool TryConsume(string novelId, int deepBudgetPerNovel)
    {
        var state = GetOrCreate(novelId, deepBudgetPerNovel);
        if (state.TotalRemaining <= 0)
            return false;

        int pool,
            bank,
            session;
        if (state.BankBalance > 0)
        {
            // 方案 C：先用银行（历史结余）
            pool = state.PoolRemaining;
            bank = state.BankBalance - 1;
            session = state.SessionUsed;
        }
        else
        {
            // 银行耗尽 → 用持久池（方案 B）
            pool = state.PoolRemaining - 1;
            bank = state.BankBalance;
            session = state.SessionUsed;
        }

        var updated = state with
        {
            PoolRemaining = pool,
            BankBalance = bank,
            SessionUsed = session + 1,
        };
        _states[novelId] = updated;
        SaveToDb(novelId, updated);
        return true;
    }

    /// <summary>
    /// 方案 C：会话结束结算（总量守恒语义，2026-08-28 产品决策修正）。
    /// 会话边界 = 宿主进程生命周期（TUI/Desktop 运行期）；本会话未用完的配额
    /// 从「池」转入「银行」——Pool+Bank 恒等于 DeepBudgetPerNovel − 累计消耗，杜绝膨胀。
    /// 未用配额不超过池剩余（池可能已被其他路径消耗）；SessionUsed 清零。
    /// </summary>
    public NovelBudgetState SettleSession(
        string novelId,
        int deepBudgetPerNovel,
        int deepBudgetPerSession
    )
    {
        var state = GetOrCreate(novelId, deepBudgetPerNovel);
        var leftover = Math.Min(
            Math.Max(0, deepBudgetPerSession - state.SessionUsed),
            state.PoolRemaining
        );
        var updated = state with
        {
            PoolRemaining = state.PoolRemaining - leftover,
            BankBalance = state.BankBalance + leftover,
            SessionUsed = 0, // 新会话清零
        };
        _states[novelId] = updated;
        SaveToDb(novelId, updated);
        return updated;
    }

    /// <summary>会话级预算是否耗尽（本小说池 + 银行 + 本会话配额剩余均考虑）。</summary>
    public bool IsExhausted(string novelId, int deepBudgetPerNovel, int deepBudgetPerSession)
    {
        var state = GetOrCreate(novelId, deepBudgetPerNovel);
        return state.TotalRemaining <= 0 && state.SessionUsed >= deepBudgetPerSession;
    }

    // ── SQLite 持久化（novel_budget 表；读写失败告警降级、不动已有表）──

    private NovelBudgetState LoadFromDb(string novelId, int deepBudgetPerNovel)
    {
        if (_dbPath is null)
            return new NovelBudgetState
            {
                NovelId = novelId,
                PoolRemaining = deepBudgetPerNovel,
                BankBalance = 0,
                SessionUsed = 0,
            };

        try
        {
            using var conn = CloudVectorsDb.Open(_dbPath);
            EnsureTable(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT pool_remaining, bank_balance, session_used FROM novel_budget WHERE novel_id = $id";
            cmd.Parameters.AddWithValue("$id", novelId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return new NovelBudgetState
                {
                    NovelId = novelId,
                    PoolRemaining = deepBudgetPerNovel,
                    BankBalance = 0,
                    SessionUsed = 0,
                };
            return new NovelBudgetState
            {
                NovelId = novelId,
                PoolRemaining = reader.GetInt32(0),
                BankBalance = reader.GetInt32(1),
                SessionUsed = reader.GetInt32(2),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDegraded($"预算回源失败：{ex.Message}");
            return new NovelBudgetState
            {
                NovelId = novelId,
                PoolRemaining = deepBudgetPerNovel,
                BankBalance = 0,
                SessionUsed = 0,
            };
        }
    }

    private void SaveToDb(string novelId, NovelBudgetState state)
    {
        if (_dbPath is null)
            return;
        try
        {
            using var conn = CloudVectorsDb.Open(_dbPath);
            EnsureTable(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO novel_budget (novel_id, pool_remaining, bank_balance, session_used, updated_at)
                VALUES ($id, $pool, $bank, $session, $utc)
                ON CONFLICT(novel_id) DO UPDATE SET
                    pool_remaining = $pool,
                    bank_balance = $bank,
                    session_used = $session,
                    updated_at = $utc
                """;
            cmd.Parameters.AddWithValue("$id", novelId);
            cmd.Parameters.AddWithValue("$pool", state.PoolRemaining);
            cmd.Parameters.AddWithValue("$bank", state.BankBalance);
            cmd.Parameters.AddWithValue("$session", state.SessionUsed);
            cmd.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
            _ = cmd.ExecuteNonQuery();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDegraded($"预算持久化失败：{ex.Message}");
        }
    }

    private static void EnsureTable(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS novel_budget (
                novel_id TEXT PRIMARY KEY,
                pool_remaining INTEGER NOT NULL,
                bank_balance INTEGER NOT NULL,
                session_used INTEGER NOT NULL,
                updated_at TEXT NOT NULL
            )
            """;
        _ = cmd.ExecuteNonQuery();
    }

    private void LogDegraded(string reason) =>
        logger?.LogWarning("[云端诊断] 预算存储失败：{Reason}", reason);
}
