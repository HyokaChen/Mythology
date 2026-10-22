using System.Globalization;
using System.Text.Json;
using Core.Vector;
using Microsoft.Data.Sqlite;

namespace Core.Data.Migrations;

/// <summary>
/// 迁移 003：将 SqliteUserMemoryStore 数据迁移到 AgentMemoryStore。
///
/// 背景：Phase 1 新增了 AgentMemoryStore（agent_memories 表），
/// 替代了旧的 SqliteUserMemoryStore（user_memories 表）。
/// 此迁移读取 user_memories 全部记录，转换为 agent_memories 格式写入。
///
/// 幂等：检查 agent_memories 表已有数据则跳过。
/// 数据安全：不删除旧表，迁移完成后可回退。
/// </summary>
public sealed class MigrateUserMemory : IDataMigration
{
    public int Version => 3;
    public string Name => "003_MigrateUserMemory";
    public string Description =>
        "将 SqliteUserMemoryStore 数据迁移到 AgentMemoryStore 的 agent_memories 表";

    public TextWriter? Log { get; set; }

    public async Task UpAsync(string mythologyDir, string novelsRoot, CancellationToken ct)
    {
        var dbPath = Path.Combine(mythologyDir, "vectors.db");
        if (!File.Exists(dbPath))
        {
            Log?.WriteLine("  vectors.db 不存在，跳过迁移。");
            return;
        }

        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync(ct);

        // 检查 agent_memories 表是否存在且有数据（幂等）
        var tableExists = false;
        await using (var checkCmd = connection.CreateCommand())
        {
            checkCmd.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='agent_memories'";
            var result = await checkCmd.ExecuteScalarAsync(ct);
            tableExists = result is long count && count > 0;
        }

        if (!tableExists)
        {
            Log?.WriteLine("  agent_memories 表不存在，跳过迁移。");
            return;
        }

        // 检查 agent_memories 是否已有数据
        await using (var countCmd = connection.CreateCommand())
        {
            countCmd.CommandText = "SELECT COUNT(*) FROM agent_memories";
            var existing = await countCmd.ExecuteScalarAsync(ct);
            if (existing is long cnt && cnt > 0)
            {
                Log?.WriteLine($"  agent_memories 已有 {cnt} 条数据，跳过迁移（幂等）。");
                return;
            }
        }

        // 检查 user_memories 表是否存在
        await using (var checkUserCmd = connection.CreateCommand())
        {
            checkUserCmd.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='user_memories'";
            var userTableExists = await checkUserCmd.ExecuteScalarAsync(ct);
            if (userTableExists is not long utc || utc == 0)
            {
                Log?.WriteLine("  user_memories 表不存在，无需迁移。");
                return;
            }
        }

        // 读取 user_memories 全部记录
        var records =
            new List<(UserMemoryRecord Record, double CreatedAtUnix, double? ExpiresAtUnix)>();
        await using (var readCmd = connection.CreateCommand())
        {
            readCmd.CommandText = "SELECT * FROM user_memories";
            await using var reader = await readCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var record = ReadUserMemoryRecord(reader);
                var createdAt = ParseIsoToUnix(
                    reader.IsDBNull(reader.GetOrdinal("created_at"))
                        ? null
                        : reader.GetString(reader.GetOrdinal("created_at"))
                );
                double? expiresAt = reader.IsDBNull(reader.GetOrdinal("expires_at"))
                    ? null
                    : ParseIsoToUnix(reader.GetString(reader.GetOrdinal("expires_at")));
                records.Add((record, createdAt, expiresAt));
            }
        }

        if (records.Count == 0)
        {
            Log?.WriteLine("  user_memories 无数据，无需迁移。");
            return;
        }

        // 逐条写入 agent_memories
        var migrated = 0;
        foreach (var (record, createdAt, expiresAt) in records)
        {
            // 命名空间映射必须与查询侧一致（RetrievalKernel 显式规则查 ns:"rule"）：
            // Explicit 规则 → "rule"；其余按作用域 → default / novel-override
            var ns = record.SourceKind switch
            {
                MemorySourceKind.Explicit => "rule",
                _ => record.Scope == MemoryScope.NovelOverride ? "novel-override" : "default",
            };

            await using var insertCmd = connection.CreateCommand();
            insertCmd.CommandText = """
                INSERT OR IGNORE INTO agent_memories
                    (id, owner_id, novel_id, namespace, content, importance, metadata_json,
                     created_at, last_accessed_at, access_count, usefulness,
                     memory_kind, confidence, priority, expires_at, evidence_refs)
                VALUES
                    ($id, $owner_id, $novel_id, $namespace, $content, $importance, $metadata_json,
                     $created_at, NULL, 0, 0.0,
                     $memory_kind, $confidence, $priority, $expires_at, $evidence_refs)
                """;
            insertCmd.Parameters.AddWithValue("$id", record.Id);
            insertCmd.Parameters.AddWithValue("$owner_id", record.OwnerId);
            insertCmd.Parameters.AddWithValue("$novel_id", (object?)record.NovelId ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("$namespace", ns);
            insertCmd.Parameters.AddWithValue("$content", record.Text);
            insertCmd.Parameters.AddWithValue("$importance", 0.5); // 旧数据无此字段，默认值
            insertCmd.Parameters.AddWithValue("$metadata_json", "{}");
            insertCmd.Parameters.AddWithValue("$created_at", createdAt);
            insertCmd.Parameters.AddWithValue("$memory_kind", (int)record.SourceKind);
            insertCmd.Parameters.AddWithValue("$confidence", record.Confidence);
            insertCmd.Parameters.AddWithValue("$priority", record.Priority);
            insertCmd.Parameters.AddWithValue("$expires_at", (object?)expiresAt ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue(
                "$evidence_refs",
                record.EvidenceRefs is not null
                    ? JsonSerializer.Serialize(record.EvidenceRefs)
                    : DBNull.Value
            );

            await insertCmd.ExecuteNonQueryAsync(ct);
            migrated++;
        }

        Log?.WriteLine($"  迁移完成: {migrated} 条记录从 user_memories → agent_memories");
        Log?.WriteLine("  注意: 旧表 user_memories 未删除，作为回退备份。");
    }

    private static UserMemoryRecord ReadUserMemoryRecord(SqliteDataReader reader)
    {
        var evidenceRefsStr = reader.IsDBNull(reader.GetOrdinal("evidence_refs"))
            ? null
            : reader.GetString(reader.GetOrdinal("evidence_refs"));

        return new UserMemoryRecord
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            OwnerId = reader.GetString(reader.GetOrdinal("owner_id")),
            NovelId = reader.IsDBNull(reader.GetOrdinal("novel_id"))
                ? null
                : reader.GetString(reader.GetOrdinal("novel_id")),
            SourceKind = (MemorySourceKind)reader.GetInt32(reader.GetOrdinal("source_kind")),
            Scope = (MemoryScope)reader.GetInt32(reader.GetOrdinal("scope")),
            Text = reader.GetString(reader.GetOrdinal("text")),
            Confidence = reader.GetDouble(reader.GetOrdinal("confidence")),
            Priority = reader.GetInt32(reader.GetOrdinal("priority")),
            CreatedAt = DateTime.UtcNow,
            EvidenceRefs = evidenceRefsStr is not null
                ? JsonSerializer.Deserialize<List<string>>(evidenceRefsStr) ?? []
                : null,
        };
    }

    private static double ParseIsoToUnix(string? iso)
    {
        if (string.IsNullOrEmpty(iso))
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        if (
            DateTimeOffset.TryParse(
                iso,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var dto
            )
        )
            return dto.ToUnixTimeMilliseconds() / 1000.0;
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    }
}
