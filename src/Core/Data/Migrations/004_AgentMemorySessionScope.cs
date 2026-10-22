using Microsoft.Data.Sqlite;

namespace Core.Data.Migrations;

/// <summary>
/// 迁移 004：为 agent_memories 增加 session_id（会话级记忆作用域）。
///
/// 背景：2026-09-12 引入 `memory_store(scope: session)` —— 会话进行中的结论只在写入它的会话可见，
/// 需要单独的 session_id 列（旧库由 CREATE TABLE IF NOT EXISTS 建表，不会自动加列）。
///
/// 幂等：列已存在（新建库由 Schema 直接带列）或表不存在时跳过。
/// 不修改既有行的语义：老记忆 session_id 为 NULL = 长期记忆，行为与迁移前一致。
/// </summary>
public sealed class AgentMemorySessionScope : IDataMigration
{
    public int Version => 4;
    public string Name => "004_AgentMemorySessionScope";
    public string Description => "为 agent_memories 增加 session_id 列与索引（会话级记忆作用域）";

    public TextWriter? Log { get; set; }

    public async Task UpAsync(string mythologyDir, string novelsRoot, CancellationToken ct)
    {
        var dbPath = Path.Combine(mythologyDir, "vectors.db");
        if (!File.Exists(dbPath))
        {
            Log?.WriteLine("  vectors.db 不存在，跳过迁移（新库建表时自带 session_id）。");
            return;
        }

        await using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await connection.OpenAsync(ct);

        // 表不存在 → 新库，AgentMemoryStore 建表时自带列
        await using (var checkCmd = connection.CreateCommand())
        {
            checkCmd.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='agent_memories'";
            if (await checkCmd.ExecuteScalarAsync(ct) is not long count || count == 0)
            {
                Log?.WriteLine("  agent_memories 表不存在，跳过迁移。");
                return;
            }
        }

        var hasColumn = false;
        await using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "PRAGMA table_info(agent_memories)";
            await using var reader = await probe.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (
                    string.Equals(
                        reader.GetString(1),
                        "session_id",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (hasColumn)
        {
            Log?.WriteLine("  session_id 列已存在，跳过。");
            return;
        }

        await using (var alter = connection.CreateCommand())
        {
            alter.CommandText = "ALTER TABLE agent_memories ADD COLUMN session_id TEXT";
            await alter.ExecuteNonQueryAsync(ct);
        }
        await using (var index = connection.CreateCommand())
        {
            index.CommandText =
                "CREATE INDEX IF NOT EXISTS idx_agent_mem_session ON agent_memories(owner_id, session_id)";
            await index.ExecuteNonQueryAsync(ct);
        }

        Log?.WriteLine("  已为 agent_memories 增加 session_id 列与 (owner_id, session_id) 索引。");
    }
}
