using Microsoft.Data.Sqlite;

namespace CloudDiagnosis;

/// <summary>
/// vectors.db 连接工厂（预算池 / 决策历史 / 快照缓存共用同一库文件，Core 侧
/// AgentMemoryStore / SqliteRetrievalStore 也在写同一文件）。
/// 与 Core.Vector.AgentMemoryStore 相同的约定：journal_mode=WAL + busy_timeout=30s——
/// 多组件共写时等待而非立即失败（失败会被静默降级，造成内存与盘分叉）。
/// </summary>
internal static class CloudVectorsDb
{
    /// <summary>打开连接并设置 PRAGMA（journal_mode 幂等；busy_timeout 为 per-connection）。</summary>
    public static SqliteConnection Open(string? dbPath)
    {
        ArgumentNullException.ThrowIfNull(dbPath);
        var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using (var journal = conn.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode=WAL";
            _ = journal.ExecuteNonQuery();
        }
        using (var busy = conn.CreateCommand())
        {
            busy.CommandText = "PRAGMA busy_timeout=30000";
            _ = busy.ExecuteNonQuery();
        }
        return conn;
    }
}
