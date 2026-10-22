using System.Text.Json;
using Core.Data.Migrations;
using Core.Vector;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Core.Tests;

/// <summary>
/// MigrationRunner 框架测试：版本发现、version.json 读写、stale 检测。
/// 注意：不执行实际迁移（UpAsync），只测框架逻辑。
/// </summary>
public class MigrationRunnerTests : IAsyncDisposable
{
    private readonly string _tempDir;

    public MigrationRunnerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"migration-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch { }
        return default;
    }

    [Fact]
    public void IDataMigration_Interface_HasRequiredMembers()
    {
        var t = typeof(IDataMigration);
        Assert.NotNull(t.GetProperty("Version"));
        Assert.NotNull(t.GetProperty("Description"));
        Assert.NotNull(t.GetProperty("Log"));
        Assert.NotNull(t.GetMethod("UpAsync"));
    }

    [Fact]
    public void DiscoveredMigrations_ImplementInterface()
    {
        var types = typeof(IDataMigration)
            .Assembly.GetTypes()
            .Where(t =>
                t is { IsAbstract: false, IsInterface: false }
                && typeof(IDataMigration).IsAssignableFrom(t)
            )
            .ToList();

        Assert.NotEmpty(types);
        Assert.All(types, t => Assert.True(typeof(IDataMigration).IsAssignableFrom(t)));
    }

    [Fact]
    public void MigrationVersions_AreUnique()
    {
        var migrations = typeof(IDataMigration)
            .Assembly.GetTypes()
            .Where(t =>
                t is { IsAbstract: false, IsInterface: false }
                && typeof(IDataMigration).IsAssignableFrom(t)
            )
            .Select(t => (IDataMigration)Activator.CreateInstance(t)!)
            .ToList();

        var versions = migrations.Select(m => m.Version).ToList();
        Assert.Equal(versions.Count, versions.Distinct().Count());
        Assert.All(versions, v => Assert.InRange(v, 1, 100));
        Assert.All(migrations, m => Assert.False(string.IsNullOrEmpty(m.Description)));
    }

    [Fact]
    public async Task RunAsync_NoMigrations_NoError()
    {
        var runner = new MigrationRunner(_tempDir, Path.Combine(_tempDir, "novels"));
        // No version.json exists → starts from version 0
        // If no migrations pending, should exit cleanly
        await runner.RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task VersionFile_AfterWrite_Readable()
    {
        var runner = new MigrationRunner(_tempDir, Path.Combine(_tempDir, "novels"));
        await runner.RunAsync(CancellationToken.None);

        // Should create version.json
        var versionFile = Path.Combine(_tempDir, "version.json");
        Assert.True(File.Exists(versionFile));
        var content = await File.ReadAllTextAsync(versionFile);
        Assert.Contains("\"version\"", content);
    }

    // ═══════════════════════════════════════════════════════════
    //  004_AgentMemorySessionScope：老库补 session_id 列（含幂等）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 造一个"没有 session_id 列"的旧 vectors.db → 跑迁移 → 列补齐、老行语义不变（NULL=长期记忆）、
    /// 迁移后 AgentMemoryStore 能写会话级记忆；重复执行幂等。
    /// </summary>
    [Fact]
    public async Task AgentMemorySessionScope_AddsColumnToLegacyDb_Idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbPath = Path.Combine(_tempDir, "vectors.db");

        await using (var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE agent_memories (
                    id TEXT PRIMARY KEY,
                    owner_id TEXT NOT NULL,
                    novel_id TEXT,
                    namespace TEXT NOT NULL DEFAULT 'default',
                    content TEXT NOT NULL,
                    importance REAL NOT NULL DEFAULT 0.5,
                    metadata_json TEXT NOT NULL DEFAULT '{}',
                    created_at REAL NOT NULL,
                    last_accessed_at REAL,
                    access_count INTEGER NOT NULL DEFAULT 0,
                    usefulness REAL NOT NULL DEFAULT 0.0,
                    embedding BLOB,
                    memory_kind INTEGER NOT NULL DEFAULT 0,
                    confidence REAL NOT NULL DEFAULT 1.0,
                    priority INTEGER NOT NULL DEFAULT 0,
                    expires_at REAL,
                    evidence_refs TEXT
                );
                INSERT INTO agent_memories (id, owner_id, content, created_at)
                VALUES ('m-old', 'local', '迁移前写入的长期记忆', 1.0);
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var runner = new MigrationRunner(_tempDir, Path.Combine(_tempDir, "novels"));
        await runner.RunAsync(ct);

        Assert.True(await HasSessionColumnAsync(dbPath, ct), "迁移应补上 session_id 列");

        // 老行 session_id 为 NULL（语义不变）；迁移后能写会话级记忆并只在同会话可见
        await using (
            var store = new AgentMemoryStore(dbPath, dimension: 32, embeddingService: null)
        )
        {
            var sessionMem = await store.StoreAsync(
                "会话级结论",
                "local",
                novelId: "novel-1",
                sessionId: "sess-1",
                ct: ct
            );
            Assert.Equal("sess-1", sessionMem.SessionId);

            var visibleInSession = await store.RecallAsync(
                "会话级",
                "local",
                novelId: "novel-1",
                sessionId: "sess-1",
                mode: "keyword",
                ct: ct
            );
            Assert.Contains(visibleInSession, r => r.Memory.Id == sessionMem.Id);

            var otherSession = await store.GetAllAsync(
                "local",
                novelId: "novel-1",
                sessionId: "sess-2",
                ct: ct
            );
            Assert.DoesNotContain(otherSession, m => m.Id == sessionMem.Id);
        }

        // 幂等：再跑一次不报错
        await runner.RunAsync(ct);
    }

    private static async Task<bool> HasSessionColumnAsync(string dbPath, CancellationToken ct)
    {
        await using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(agent_memories)";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (
                string.Equals(reader.GetString(1), "session_id", StringComparison.OrdinalIgnoreCase)
            )
                return true;
        }
        return false;
    }

    // ═══════════════════════════════════════════════════════════
    //  002_CompactBlocks：旧 compact 补 block-id + compressed-message-ids
    // ═══════════════════════════════════════════════════════════

    /// <summary>构造一个含旧格式 compact（无 block-id/ids）的会话分片。</summary>
    private async Task<(string SessionsDir, string SessionDir)> CreateLegacyCompactSessionAsync()
    {
        var sessionsDir = Path.Combine(_tempDir, "sessions");
        var novelDir = Path.Combine(sessionsDir, "--novels-testnovel--");
        var sessionDir = Path.Combine(novelDir, "legacy_session");
        Directory.CreateDirectory(sessionDir);

        var opts = Data.JsonlSerializer.LegacyReadOptions;

        // 分片 0：4 条消息（m0 order0 ~ m3 order3），无 compact
        var seg0Lines = new List<string>
        {
            JsonSerializer.Serialize(
                new Data.JsonlMessageEntry
                {
                    Id = "msg0",
                    Timestamp = DateTime.UtcNow.ToString("O"),
                    Role = "user",
                    Content = "消息0",
                    Order = 0,
                },
                opts
            ),
            JsonSerializer.Serialize(
                new Data.JsonlMessageEntry
                {
                    Id = "msg1",
                    Timestamp = DateTime.UtcNow.ToString("O"),
                    Role = "assistant",
                    Content = "回复1",
                    Order = 1,
                },
                opts
            ),
            JsonSerializer.Serialize(
                new Data.JsonlMessageEntry
                {
                    Id = "msg2",
                    Timestamp = DateTime.UtcNow.ToString("O"),
                    Role = "user",
                    Content = "消息2",
                    Order = 2,
                },
                opts
            ),
            JsonSerializer.Serialize(
                new Data.JsonlMessageEntry
                {
                    Id = "msg3",
                    Timestamp = DateTime.UtcNow.ToString("O"),
                    Role = "assistant",
                    Content = "回复3",
                    Order = 3,
                },
                opts
            ),
        };
        await File.WriteAllLinesAsync(Path.Combine(sessionDir, "000000.jsonl"), seg0Lines);

        // 分片 1：旧格式 compact（无 block-id/compressed-message-ids）+ 保留行（msg2, msg3）
        // firstKeptEntryId = msg2（order2），覆盖 msg0/msg1
        var seg1Lines = new List<string>
        {
            JsonSerializer.Serialize(
                new Data.JsonlCompactEntry
                {
                    Id = "compact1",
                    ParentId = "msg1",
                    Timestamp = DateTime.UtcNow.ToString("O"),
                    Summary = "旧摘要：压缩了前两条",
                    FirstKeptEntryId = "msg2",
                    MessagesBefore = 4,
                    Order = 4,
                    // 无 BlockId / CompressedMessageIds（旧格式）
                },
                opts
            ),
        };
        // 保留行（从分片 0 复制 msg2/msg3 原文行）
        seg1Lines.Add(seg0Lines[2]);
        seg1Lines.Add(seg0Lines[3]);
        await File.WriteAllLinesAsync(Path.Combine(sessionDir, "000001.jsonl"), seg1Lines);

        // MANIFEST
        var manifest = new Data.SessionManifest
        {
            SessionId = "legacy_session",
            NovelId = "testnovel",
            Segments =
            [
                new Data.ManifestSegmentRecord
                {
                    Seq = 0,
                    File = "000000.jsonl",
                    StartOrder = 0,
                    EndOrder = 3,
                    MessageCount = 4,
                    EntryCount = 4,
                },
                new Data.ManifestSegmentRecord
                {
                    Seq = 1,
                    File = "000001.jsonl",
                    StartOrder = 4,
                    EndOrder = 4,
                    MessageCount = 0,
                    EntryCount = 3,
                    HasCompact = true,
                },
            ],
            ActiveSegment = 1,
            CompactCount = 1,
        };
        await File.WriteAllTextAsync(
            Path.Combine(sessionDir, "MANIFEST.json"),
            JsonSerializer.Serialize(manifest, opts)
        );

        return (sessionsDir, sessionDir);
    }

    [Fact]
    /// <summary>002 迁移：旧 compact 补 block-id + compressed-message-ids，推导覆盖区间正确。</summary>
    public async Task CompactBlocksMigration_AddsBlockIdAndMessageIds()
    {
        await CreateLegacyCompactSessionAsync();

        var runner = new MigrationRunner(_tempDir, Path.Combine(_tempDir, "novels"));
        await runner.RunAsync(CancellationToken.None);

        // 验证迁移已记录
        var versionFile = Path.Combine(_tempDir, "version.json");
        Assert.True(File.Exists(versionFile));
        var vjson = await File.ReadAllTextAsync(versionFile);
        Assert.Contains("002_CompactBlocks", vjson);

        // 验证 compact 行已补字段
        var seg1Path = Path.Combine(
            _tempDir,
            "sessions",
            "--novels-testnovel--",
            "legacy_session",
            "000001.jsonl"
        );
        var lines = await File.ReadAllLinesAsync(seg1Path, TestContext.Current.CancellationToken);
        var compactLine = lines.First(l => l.Contains("\"compact1\""));
        using var doc = JsonDocument.Parse(compactLine);
        Assert.Equal("b1", doc.RootElement.GetProperty("block-id").GetString());
        var ids = doc
            .RootElement.GetProperty("compressed-message-ids")
            .EnumerateArray()
            .Select(x => x.GetString())
            .ToList();
        Assert.Equal(["msg0", "msg1"], ids);

        // 其他行不受影响（保留行仍在）
        Assert.Contains(lines, l => l.Contains("\"msg2\""));
        Assert.Contains(lines, l => l.Contains("\"msg3\""));
    }

    [Fact]
    /// <summary>002 迁移幂等：重复执行不报错、不重复修改。</summary>
    public async Task CompactBlocksMigration_Idempotent()
    {
        await CreateLegacyCompactSessionAsync();

        var runner = new MigrationRunner(_tempDir, Path.Combine(_tempDir, "novels"));
        await runner.RunAsync(CancellationToken.None);
        await runner.RunAsync(CancellationToken.None); // 第二次执行

        // 版本记录只有一条 002
        var vjson = await File.ReadAllTextAsync(Path.Combine(_tempDir, "version.json"));
        var count = System.Text.RegularExpressions.Regex.Matches(vjson, "002_CompactBlocks").Count;
        Assert.Equal(1, count);

        // compact 行只被改一次
        var seg1Path = Path.Combine(
            _tempDir,
            "sessions",
            "--novels-testnovel--",
            "legacy_session",
            "000001.jsonl"
        );
        var lines = await File.ReadAllLinesAsync(seg1Path, TestContext.Current.CancellationToken);
        var compactLine = lines.First(l => l.Contains("\"compact1\""));
        using var doc = JsonDocument.Parse(compactLine);
        Assert.Equal("b1", doc.RootElement.GetProperty("block-id").GetString());
    }

    [Fact]
    /// <summary>
    /// 失败记录不算「已应用」：修复后重启必须重跑该迁移。
    /// （历史 bug：失败也被计入 appliedVersions → 该迁移永不再跑，数据永久停在半迁移状态，
    /// 而错误信息却写着「修复后重新启动即可重试」。）
    /// </summary>
    public async Task FailedMigrationRecord_IsRetriedOnNextRun()
    {
        await CreateLegacyCompactSessionAsync();
        var versionPath = Path.Combine(_tempDir, "version.json");
        await File.WriteAllTextAsync(
            versionPath,
            """
            {
              "applied-migrations": [
                { "version": 1, "name": "001_SessionSegments", "description": "", "applied-at": "2026-01-01T00:00:00Z", "duration-ms": 0, "result": "success" },
                { "version": 2, "name": "002_CompactBlocks", "description": "", "applied-at": "2026-01-01T00:00:00Z", "duration-ms": 0, "result": "failed" }
              ]
            }
            """,
            TestContext.Current.CancellationToken
        );

        var runner = new MigrationRunner(_tempDir, Path.Combine(_tempDir, "novels"));
        await runner.RunAsync(CancellationToken.None);

        // 002 真的重跑了：compact 行补上了 block-id
        var seg = Path.Combine(
            _tempDir,
            "sessions",
            "--novels-testnovel--",
            "legacy_session",
            "000001.jsonl"
        );
        var compactLine = (
            await File.ReadAllLinesAsync(seg, TestContext.Current.CancellationToken)
        ).First(l => l.Contains("\"compact1\""));
        using var doc = JsonDocument.Parse(compactLine);
        Assert.Equal("b1", doc.RootElement.GetProperty("block-id").GetString());

        // 且账本里 002 只剩一条「成功」记录（同版本替换，不堆积失败记录）
        using var historyDoc = JsonDocument.Parse(
            await File.ReadAllTextAsync(versionPath, TestContext.Current.CancellationToken)
        );
        var records = historyDoc
            .RootElement.GetProperty("applied-migrations")
            .EnumerateArray()
            .Where(r => r.GetProperty("version").GetInt32() == 2)
            .ToList();
        var record = Assert.Single(records);
        Assert.Equal("success", record.GetProperty("result").GetString());
    }

    [Fact]
    /// <summary>002 迁移：decompress 语义闭环——迁移后可按 block-id 恢复原文。</summary>
    public async Task CompactBlocksMigration_AfterMigration_DecompressRecovers()
    {
        var (_, sessionDir) = await CreateLegacyCompactSessionAsync();

        var runner = new MigrationRunner(_tempDir, Path.Combine(_tempDir, "novels"));
        await runner.RunAsync(CancellationToken.None);

        // 用 FileSessionStore 验证：ReadCompactedMessagesAsync 恢复 msg0/msg1 原文
        var store = new Data.FileSessionStore(
            novelsRoot: Path.Combine(_tempDir, "novels"),
            mythologyDir: _tempDir
        );
        var recovered = await store.ReadCompactedMessagesAsync("legacy_session", "b1");
        Assert.NotNull(recovered);
        Assert.Equal(2, recovered!.Count);
        Assert.Equal("消息0", recovered[0].Content);
        Assert.Equal("回复1", recovered[1].Content);
    }
}
