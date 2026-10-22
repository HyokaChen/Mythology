using Core.Vector;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Core.Tests;

/// <summary>
/// AgentMemoryVec（vec0 ANN 候选表）测试：生命周期、回填、维度漂移、ANN 开/关 parity、降级。
/// parity 测试用确定性 HashEmbedder（DummyEmbeddingService 是随机的，不可用于对拍）。
/// </summary>
public sealed class AgentMemoryVecTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentMemoryStore _store;
    private bool _disposed;

    public AgentMemoryVecTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_agent_vec_{Guid.NewGuid():N}.db");
        _store = new AgentMemoryStore(
            _dbPath,
            dimension: 256,
            embeddingService: new HashEmbedder(256)
        );
        _store.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _store.DisposeAsync().GetAwaiter().GetResult();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
    }

    private async Task<int> CountVecRowsAsync()
    {
        // 直接查 vec0 表行数（辅助列 memory_id 非 null 即一条）
        await using var conn = await OpenVecConnectionAsync(_dbPath);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM agent_memory_vec";
        return Convert.ToInt32(
            await cmd.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture
        );
    }

    [Fact]
    public async Task SyncUpsert_Search_Delete_Lifecycle()
    {
        var mem = await _store.StoreAsync(
            "角色林晚喜欢喝龙井茶",
            "local",
            ns: "character",
            ct: TestContext.Current.CancellationToken
        );

        Assert.Equal(1, await CountVecRowsAsync());

        // 查询自身 → 应命中
        await using var conn = await OpenConnAsync();
        var cands = await AgentMemoryVec.SearchAsync(
            conn,
            new HashEmbedder(256).EmbedAsync("林晚 龙井茶").GetAwaiter().GetResult(),
            m: 10,
            ct: TestContext.Current.CancellationToken
        );
        Assert.NotNull(cands);
        Assert.Contains(cands!, c => c.MemoryId == mem.Id);

        // 删除 → 同步移除
        await _store.ForgetAsync(mem.Id, ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, await CountVecRowsAsync());
    }

    [Fact]
    public async Task Initialize_BackfillsLegacyRows()
    {
        // 构造一个"旧库"：只有 agent_memories（无 vec 表 / vec 表无数据）
        var legacyPath = Path.Combine(
            Path.GetTempPath(),
            $"test_agent_vec_legacy_{Guid.NewGuid():N}.db"
        );
        try
        {
            var store = new AgentMemoryStore(
                legacyPath,
                dimension: 256,
                embeddingService: new HashEmbedder(256)
            );
            await store.InitializeAsync(TestContext.Current.CancellationToken);
            await store.StoreAsync(
                "世界设定：大陆被星海环绕",
                "local",
                ns: "setting",
                ct: TestContext.Current.CancellationToken
            );
            // 手工删 vec 表模拟存量库（保留 agent_memories）
            await using (var conn = await OpenVecConnectionAsync(legacyPath))
            {
                await using var drop = conn.CreateCommand();
                drop.CommandText = "DROP TABLE IF EXISTS agent_memory_vec";
                await drop.ExecuteNonQueryAsync();
            }
            await store.DisposeAsync();

            // 重新打开 → BackfillAsync 重建 vec 表
            var reopened = new AgentMemoryStore(
                legacyPath,
                dimension: 256,
                embeddingService: new HashEmbedder(256)
            );
            await reopened.InitializeAsync(TestContext.Current.CancellationToken);

            var results = await reopened.RecallAsync(
                "星海 大陆",
                "local",
                k: 5,
                ns: "setting",
                mode: "vector",
                ct: TestContext.Current.CancellationToken
            );
            Assert.NotEmpty(results); // 向量通道能召回 = 回填成功
            await reopened.DisposeAsync();
        }
        finally
        {
            if (File.Exists(legacyPath))
                File.Delete(legacyPath);
        }
    }

    [Fact]
    public async Task EnsureTable_DimensionDrift_Rebuilds()
    {
        var driftPath = Path.Combine(
            Path.GetTempPath(),
            $"test_agent_vec_drift_{Guid.NewGuid():N}.db"
        );
        try
        {
            // 1536 维建表 + 写入
            await using (var conn = await OpenVecConnectionAsync(driftPath))
            {
                await AgentMemoryVec.EnsureTableAsync(
                    conn,
                    1536,
                    TestContext.Current.CancellationToken
                );
                await AgentMemoryVec.SyncUpsertAsync(
                    conn,
                    "drift-1",
                    new float[1536],
                    TestContext.Current.CancellationToken
                );
            }

            // 1024 维打开同一库 → EnsureTable 应 DROP 重建，写入不抛 Dimension mismatch
            await using (var conn = await OpenVecConnectionAsync(driftPath))
            {
                await AgentMemoryVec.EnsureTableAsync(
                    conn,
                    1024,
                    TestContext.Current.CancellationToken
                );
                await AgentMemoryVec.SyncUpsertAsync(
                    conn,
                    "drift-2",
                    new float[1024],
                    TestContext.Current.CancellationToken
                );

                // 重建验证：DROP 后旧 1536 维数据（drift-1）清空，表内只剩重建后写入的 drift-2
                await using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT count(*), count(memory_id) FROM agent_memory_vec WHERE memory_id = $id";
                cmd.Parameters.AddWithValue("$id", "drift-1");
                await using var reader = await cmd.ExecuteReaderAsync(
                    TestContext.Current.CancellationToken
                );
                Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
                Assert.Equal(0L, reader.GetInt64(1)); // drift-1 已被 DROP 清除
                Assert.Equal(0L, reader.GetInt64(0));
            }
        }
        finally
        {
            if (File.Exists(driftPath))
                File.Delete(driftPath);
        }
    }

    [Fact]
    public async Task Recall_AnnOn_Off_Parity()
    {
        // 600 条互异记忆，AnnMinRows=0 强制走 ANN；HashEmbedder 确定性
        for (var i = 0; i < 600; i++)
        {
            await _store.StoreAsync(
                $"伏笔第{i}号：主角在{i}岁时遇见师父并约定三年后山顶相见",
                "local",
                ns: "plot",
                priority: i % 7,
                ct: TestContext.Current.CancellationToken
            );
        }

        var query = "主角与师父约定山顶相见";
        _store.AnnMinRows = 0;

        // 关闭 ANN：全量精确
        _store.UseAnn = false;
        var swOff = System.Diagnostics.Stopwatch.StartNew();
        var offResults = await _store.RecallAsync(
            query,
            "local",
            k: 5,
            ns: "plot",
            mode: "hybrid",
            ct: TestContext.Current.CancellationToken
        );
        swOff.Stop();

        // 开启 ANN：候选精确
        _store.UseAnn = true;
        var swOn = System.Diagnostics.Stopwatch.StartNew();
        var onResults = await _store.RecallAsync(
            query,
            "local",
            k: 5,
            ns: "plot",
            mode: "hybrid",
            ct: TestContext.Current.CancellationToken
        );
        swOn.Stop();

        Assert.NotEmpty(offResults);
        Assert.NotEmpty(onResults);

        var offIds = offResults.Select(r => r.Memory.Id).ToHashSet();
        var onIds = onResults.Select(r => r.Memory.Id).ToHashSet();

        // 计划约定：top-5 集合一致（若排序漂移，放宽为重叠 ≥4/5 并注释近似边界）
        Assert.True(
            onIds.SetEquals(offIds) || onIds.Intersect(offIds).Count() >= 4,
            $"ANN 与全量 top-5 漂移: on={string.Join(",", onIds.Select(x => x[..8]))} off={string.Join(",", offIds.Select(x => x[..8]))}"
        );

        // 延迟记录（计划要求可复现）
        Console.WriteLine(
            $"[ANN parity] 全量精确 {offResults.Count} 条: {swOff.ElapsedMilliseconds}ms; ANN {onResults.Count} 条: {swOn.ElapsedMilliseconds}ms"
        );
    }

    [Fact]
    public async Task Recall_VecTableEmptied_StillReturnsResults()
    {
        await _store.StoreAsync(
            "主角性格冷静，擅长布局",
            "local",
            ns: "character",
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "反派动机：夺回失去的领地",
            "local",
            ns: "plot",
            ct: TestContext.Current.CancellationToken
        );

        // 清空 vec 表模拟 ANN 数据丢失
        await using (var conn = await OpenVecConnectionAsync(_dbPath))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM agent_memory_vec";
            await cmd.ExecuteNonQueryAsync();
        }

        _store.AnnMinRows = 0; // 强制走 ANN 路径（候选为空 → 回退全量）
        var results = await _store.RecallAsync(
            "布局 布局",
            "local",
            k: 5,
            ns: "character",
            mode: "hybrid",
            ct: TestContext.Current.CancellationToken
        );
        Assert.NotEmpty(results); // 降级不炸，仍能召回
    }

    [Fact]
    public async Task InvalidateCorpus_OnOverwrite()
    {
        // BUG-A 回归：写操作（store/forget）后 corpus 缓存必须失效。
        // StoreAsync 是 INSERT 语义（同 ID 二次写撞 UNIQUE），用 forget+store 组合验证
        // "删除旧内容 → 存入新内容 → 召回只见新内容"。
        const string id = "fixed-memory-id-123";
        await _store.StoreAsync(
            "旧内容：主角是剑客",
            "local",
            ns: "character",
            memoryId: id,
            ct: TestContext.Current.CancellationToken
        );
        var r1 = await _store.RecallAsync(
            "剑客",
            "local",
            k: 5,
            ns: "character",
            mode: "keyword",
            ct: TestContext.Current.CancellationToken
        );
        Assert.Contains(r1, r => r.Memory.Id == id);

        // 删除旧内容 → 同 ID 存入新内容
        await _store.ForgetAsync(id, ownerId: "local", ct: TestContext.Current.CancellationToken);
        await _store.StoreAsync(
            "新内容：主角是药师",
            "local",
            ns: "character",
            memoryId: id,
            ct: TestContext.Current.CancellationToken
        );
        var r2 = await _store.RecallAsync(
            "药师",
            "local",
            k: 5,
            ns: "character",
            mode: "keyword",
            ct: TestContext.Current.CancellationToken
        );
        Assert.Contains(r2, r => r.Memory.Id == id && r.Memory.Content.Contains("药师"));

        // 旧内容关键词不应再命中该记忆（corpus/分词缓存均已失效）
        var r3 = await _store.RecallAsync(
            "剑客",
            "local",
            k: 5,
            ns: "character",
            mode: "keyword",
            ct: TestContext.Current.CancellationToken
        );
        Assert.DoesNotContain(r3, r => r.Memory.Id == id);
    }

    private async Task<SqliteConnection> OpenConnAsync() => await OpenVecConnectionAsync(_dbPath);

    /// <summary>打开连接并加载 vec0 扩展（与 AgentMemoryStore.InitializeAsync 同路径逻辑）。</summary>
    private static async Task<SqliteConnection> OpenVecConnectionAsync(string dbPath)
    {
        var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False;Default Timeout=30");
        await conn.OpenAsync();
        conn.EnableExtensions();
        await using (var busy = conn.CreateCommand())
        {
            busy.CommandText = "PRAGMA busy_timeout=30000";
            await busy.ExecuteNonQueryAsync();
        }
        var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier ?? "win-x64";
        var vecPath = Path.Combine(
            AppContext.BaseDirectory,
            "runtimes",
            rid,
            "native",
            OperatingSystem.IsWindows() ? "vec0.dll" : "libvec0.so"
        );
        if (File.Exists(vecPath))
            conn.LoadExtension(vecPath);
        else
            conn.LoadVector();
        return conn;
    }
}
