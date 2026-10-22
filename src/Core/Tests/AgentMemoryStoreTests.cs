using Core.Vector;
using Xunit;

namespace Core.Tests;

/// <summary>
/// AgentMemoryStore 综合测试：精确混合召回、评分、反馈、强化、过期、多用户隔离。
/// 注意：DummyEmbeddingService 返回随机向量，因此 vector 模式结果不可预测。
/// 测试用例以 keyword 模式为主，确保 BM25 词法匹配行为可预测。
/// </summary>
public sealed class AgentMemoryStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentMemoryStore _store;
    private bool _disposed;

    public AgentMemoryStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_agent_mem_{Guid.NewGuid():N}.db");
        _store = new AgentMemoryStore(
            _dbPath,
            dimension: 1536,
            embeddingService: new DummyEmbeddingService()
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

    // ══════════════════════════════════════════════════════
    //  StoreAsync
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task StoreAsync_Basic_RoundTrips()
    {
        var mem = await _store.StoreAsync(
            "user prefers short paragraphs",
            "user-1",
            ct: TestContext.Current.CancellationToken
        );
        Assert.NotNull(mem.Id);
        Assert.Equal("user prefers short paragraphs", mem.Content);
        Assert.Equal("user-1", mem.OwnerId);
        Assert.Equal("default", mem.Namespace);
        Assert.Equal(0.5, mem.Importance);
        Assert.True(mem.CreatedAt > 0);
    }

    [Fact]
    public async Task StoreAsync_WithCustomId_RespectsId()
    {
        var mem = await _store.StoreAsync(
            "content",
            "user-1",
            memoryId: "my-custom-id",
            ct: TestContext.Current.CancellationToken
        );
        Assert.Equal("my-custom-id", mem.Id);
    }

    [Fact]
    public async Task StoreAsync_WithMetadata_RoundTrips()
    {
        var metadata = new Dictionary<string, object?>
        {
            ["chapter"] = 5,
            ["theme"] = "dark",
            ["character"] = "protagonist",
        };
        var mem = await _store.StoreAsync(
            "memory with metadata",
            "user-1",
            metadata: metadata,
            ct: TestContext.Current.CancellationToken
        );
        var parsed = mem.ParseMetadata();
        Assert.NotNull(parsed);
        Assert.Equal("dark", parsed!["theme"]?.ToString());
    }

    [Fact]
    public async Task StoreAsync_WithImportance_StoredCorrectly()
    {
        var mem = await _store.StoreAsync(
            "important fact",
            "user-1",
            importance: 0.9,
            ct: TestContext.Current.CancellationToken
        );
        Assert.Equal(0.9, mem.Importance);
    }

    [Fact]
    public async Task StoreAsync_WithNovelId_ScopedToNovel()
    {
        var mem = await _store.StoreAsync(
            "novel specific",
            "user-1",
            novelId: "novel-1",
            ct: TestContext.Current.CancellationToken
        );
        Assert.Equal("novel-1", mem.NovelId);
    }

    [Fact]
    public async Task StoreAsync_WithNamespace_StoredInNamespace()
    {
        var mem = await _store.StoreAsync(
            "style memory",
            "user-1",
            ns: "style",
            ct: TestContext.Current.CancellationToken
        );
        Assert.Equal("style", mem.Namespace);
    }

    [Fact]
    public async Task StoreAsync_WithEvidenceRefs_RoundTrips()
    {
        var refs = new List<string> { "src-1", "src-2" };
        var mem = await _store.StoreAsync(
            "memory with evidence",
            "user-1",
            evidenceRefs: refs,
            ct: TestContext.Current.CancellationToken
        );
        Assert.NotNull(mem.EvidenceRefsJson);
    }

    [Fact]
    public async Task StoreAsync_WithExpiry_ExpiresCorrectly()
    {
        var expiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 10;
        await _store.StoreAsync(
            "expired memory",
            "user-1",
            expiresAt: expiresAt,
            ct: TestContext.Current.CancellationToken
        );
        var results = await _store.RecallAsync(
            "memory",
            "user-1",
            mode: "keyword",
            ct: TestContext.Current.CancellationToken
        );
        Assert.Empty(results);
    }

    [Fact]
    public async Task StoreAsync_ClampsImportance()
    {
        var mem = await _store.StoreAsync(
            "test",
            "user-1",
            importance: 5.0,
            ct: TestContext.Current.CancellationToken
        );
        Assert.Equal(1.0, mem.Importance);
        mem = await _store.StoreAsync(
            "test2",
            "user-1",
            importance: -1.0,
            ct: TestContext.Current.CancellationToken
        );
        Assert.Equal(0.0, mem.Importance);
    }

    [Fact]
    public async Task StoreAsync_MultipleOwners_Isolated()
    {
        await _store.StoreAsync(
            "user1 memory",
            "user-1",
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "user2 memory",
            "user-2",
            ct: TestContext.Current.CancellationToken
        );

        var u1 = await _store.GetAllAsync("user-1", ct: TestContext.Current.CancellationToken);
        var u2 = await _store.GetAllAsync("user-2", ct: TestContext.Current.CancellationToken);
        Assert.Single(u1);
        Assert.Single(u2);
        Assert.NotEqual(u1[0].Content, u2[0].Content);
    }

    [Fact]
    public async Task StoreAsync_ThrowsOnNullContent()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _store.StoreAsync(null!, "user-1", ct: TestContext.Current.CancellationToken)
        );
    }

    // ══════════════════════════════════════════════════════
    //  RecallAsync (keyword mode — deterministic BM25)
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task RecallAsync_EmptyStore_ReturnsEmpty()
    {
        var results = await _store.RecallAsync(
            "anything",
            "user-1",
            ct: TestContext.Current.CancellationToken
        );
        Assert.Empty(results);
    }

    [Fact]
    public async Task RecallAsync_KeywordMode_ReturnsRelevantFirst()
    {
        await _store.StoreAsync(
            "the protagonist likes apples",
            "user-1",
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "the weather is rainy today",
            "user-1",
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "apples are red and sweet",
            "user-1",
            ct: TestContext.Current.CancellationToken
        );

        var results = await _store.RecallAsync(
            "apples",
            "user-1",
            k: 3,
            mode: "keyword",
            ct: TestContext.Current.CancellationToken
        );
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Memory.Content.Contains("apples"));
    }

    [Fact]
    public async Task RecallAsync_KeywordMode_ExactMatchHighest()
    {
        await _store.StoreAsync(
            "the hero fights the dragon",
            "user-1",
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "the dragon guards the treasure",
            "user-1",
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "the weather is nice",
            "user-1",
            ct: TestContext.Current.CancellationToken
        );

        var results = await _store.RecallAsync(
            "dragon treasure",
            "user-1",
            k: 3,
            mode: "keyword",
            ct: TestContext.Current.CancellationToken
        );
        Assert.NotEmpty(results);
        // "dragon guards the treasure" matches both query terms
        var top = results[0].Memory.Content;
        Assert.Contains("dragon", top);
    }

    [Fact]
    public async Task RecallAsync_HybridMode_ReturnsResults()
    {
        await _store.StoreAsync(
            "the castle is guarded by magic",
            "user-1",
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "the knight draws his sword",
            "user-1",
            ct: TestContext.Current.CancellationToken
        );

        // hybrid mode with DummyEmbeddingService: vector is random, but keyword still works
        var results = await _store.RecallAsync(
            "castle magic",
            "user-1",
            k: 5,
            mode: "hybrid",
            ct: TestContext.Current.CancellationToken
        );
        Assert.NotEmpty(results);
    }

    [Fact]
    public async Task RecallAsync_NamespaceFilter_Isolates()
    {
        await _store.StoreAsync(
            "style short sentences",
            "user-1",
            ns: "style",
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "plot hero wins",
            "user-1",
            ns: "plot",
            ct: TestContext.Current.CancellationToken
        );

        var styleResults = await _store.RecallAsync(
            "short",
            "user-1",
            ns: "style",
            mode: "keyword",
            ct: TestContext.Current.CancellationToken
        );
        var plotResults = await _store.RecallAsync(
            "short",
            "user-1",
            ns: "plot",
            mode: "keyword",
            ct: TestContext.Current.CancellationToken
        );

        Assert.NotEmpty(styleResults);
        Assert.Empty(plotResults);
    }

    /// <summary>
    /// 可见集合语义：打开小说时召回 = 全局（novel_id IS NULL）∪ 当前小说，且**不含其它小说**。
    /// 修复前用精确匹配 `novel_id = @novelId`，全局记忆（scope=global 的偏好 / 未开小说时存的记忆）永远召不回。
    /// </summary>
    [Fact]
    public async Task RecallAsync_NovelScope_IncludesGlobal_ExcludesOtherNovels()
    {
        await _store.StoreAsync(
            "global rule: no classical Chinese",
            "user-1",
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "novel A plot fact",
            "user-1",
            novelId: "novel-a",
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "novel B plot fact",
            "user-1",
            novelId: "novel-b",
            ct: TestContext.Current.CancellationToken
        );

        var aResults = await _store.RecallAsync(
            "fact rule classical",
            "user-1",
            k: 10,
            novelId: "novel-a",
            mode: "keyword",
            ct: TestContext.Current.CancellationToken
        );

        Assert.Contains(aResults, r => r.Memory.NovelId is null); // 全局可见
        Assert.Contains(aResults, r => r.Memory.NovelId == "novel-a");
        Assert.DoesNotContain(aResults, r => r.Memory.NovelId == "novel-b"); // 其它小说不可见
    }

    /// <summary>GlobalOnly 只返回全局记忆；未打开小说时默认可见集合也就是全局。</summary>
    /// <summary>
    /// 会话级记忆：只在写入它的会话可见；换会话（或未提供 sessionId）不可见；
    /// 全局/小说级记忆在任何会话都可见（会话可见集合 = 全局 ∪ 当前小说 ∪ 本会话）。
    /// </summary>
    [Fact]
    public async Task RecallAsync_SessionScope_IsolatesToOwningSession()
    {
        await _store.StoreAsync(
            "session note: 第六章时间线待核对",
            "user-1",
            novelId: "novel-a",
            sessionId: "sess-1",
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "novel rule: 用第三人称",
            "user-1",
            novelId: "novel-a",
            ct: TestContext.Current.CancellationToken
        );

        // 本会话：两条都能召回
        var inSession = await _store.RecallAsync(
            "时间线 第三人称",
            "user-1",
            k: 10,
            novelId: "novel-a",
            sessionId: "sess-1",
            mode: "keyword",
            ct: TestContext.Current.CancellationToken
        );
        Assert.Contains(inSession, r => r.Memory.SessionId == "sess-1");
        Assert.Contains(inSession, r => r.Memory is { SessionId: null, NovelId: "novel-a" });

        // 另一个会话：会话级记忆不可见，小说级仍在
        var otherSession = await _store.RecallAsync(
            "时间线 第三人称",
            "user-1",
            k: 10,
            novelId: "novel-a",
            sessionId: "sess-2",
            mode: "keyword",
            ct: TestContext.Current.CancellationToken
        );
        Assert.DoesNotContain(otherSession, r => r.Memory.SessionId is not null);
        Assert.Contains(otherSession, r => r.Memory is { SessionId: null, NovelId: "novel-a" });

        // 完全不提供 sessionId（如维护路径）：会话级记忆也不可见
        var noSession = await _store.GetAllAsync(
            "user-1",
            novelId: "novel-a",
            ct: TestContext.Current.CancellationToken
        );
        Assert.DoesNotContain(noSession, m => m.SessionId is not null);
    }

    /// <summary>合并去重跳过会话级记忆（跨会话合并会破坏作用域）。</summary>
    [Fact]
    public async Task ConsolidateAsync_SkipsSessionScopedMemories()
    {
        var content = "same text for consolidation";
        await _store.StoreAsync(
            content,
            "user-1",
            sessionId: "sess-1",
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(content, "user-1", ct: TestContext.Current.CancellationToken);

        await _store.ConsolidateAsync("user-1", ct: TestContext.Current.CancellationToken);

        var sessionRows = await _store.GetAllAsync(
            "user-1",
            novelId: null,
            sessionId: "sess-1",
            scope: MemoryVisibility.All,
            ct: TestContext.Current.CancellationToken
        );
        Assert.Contains(sessionRows, m => m.SessionId == "sess-1");
    }

    [Fact]
    public async Task GetAllAsync_GlobalScope_ExcludesNovelScoped()
    {
        await _store.StoreAsync("global pref", "user-1", ct: TestContext.Current.CancellationToken);
        await _store.StoreAsync(
            "novel pref",
            "user-1",
            novelId: "novel-a",
            ct: TestContext.Current.CancellationToken
        );

        var globalOnly = await _store.GetAllAsync(
            "user-1",
            novelId: "novel-a",
            scope: MemoryVisibility.GlobalOnly,
            ct: TestContext.Current.CancellationToken
        );
        Assert.Single(globalOnly);
        Assert.Null(globalOnly[0].NovelId);

        // 未打开小说（novelId=null）时默认可见集合 = 仅全局
        var noNovel = await _store.GetAllAsync("user-1", ct: TestContext.Current.CancellationToken);
        Assert.Single(noNovel);
        Assert.Null(noNovel[0].NovelId);
    }

    [Fact]
    public async Task RecallAsync_NovelFilter_Isolates()
    {
        await _store.StoreAsync(
            "novel A fact",
            "user-1",
            novelId: "novel-a",
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "novel B fact",
            "user-1",
            novelId: "novel-b",
            ct: TestContext.Current.CancellationToken
        );

        var aResults = await _store.RecallAsync(
            "fact",
            "user-1",
            novelId: "novel-a",
            mode: "keyword",
            ct: TestContext.Current.CancellationToken
        );
        var bResults = await _store.RecallAsync(
            "fact",
            "user-1",
            novelId: "novel-b",
            mode: "keyword",
            ct: TestContext.Current.CancellationToken
        );

        Assert.Single(aResults);
        Assert.Single(bResults);
        Assert.Contains(aResults, r => r.Memory.Content.Contains('A'));
        Assert.Contains(bResults, r => r.Memory.Content.Contains('B'));
    }

    [Fact]
    public async Task RecallAsync_TouchEnabled_UpdatesAccessStats()
    {
        await _store.StoreAsync(
            "touching memory",
            "user-1",
            ct: TestContext.Current.CancellationToken
        );
        var before = await _store.GetAllAsync("user-1", ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, before[0].AccessCount);
        Assert.Null(before[0].LastAccessedAt);

        await Task.Delay(10, TestContext.Current.CancellationToken);
        await _store.RecallAsync(
            "touching",
            "user-1",
            mode: "keyword",
            touch: true,
            ct: TestContext.Current.CancellationToken
        );

        var after = await _store.GetAllAsync("user-1", ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, after[0].AccessCount);
        Assert.NotNull(after[0].LastAccessedAt);
    }

    [Fact]
    public async Task RecallAsync_TouchDisabled_DoesNotUpdateStats()
    {
        await _store.StoreAsync("no touch", "user-1", ct: TestContext.Current.CancellationToken);
        await _store.RecallAsync(
            "no touch",
            "user-1",
            mode: "keyword",
            touch: false,
            ct: TestContext.Current.CancellationToken
        );

        var after = await _store.GetAllAsync("user-1", ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, after[0].AccessCount);
    }

    [Fact]
    public async Task RecallAsync_RecallResult_HasExplainText()
    {
        await _store.StoreAsync(
            "explainable memory",
            "user-1",
            importance: 0.7,
            ct: TestContext.Current.CancellationToken
        );
        var results = await _store.RecallAsync(
            "explainable",
            "user-1",
            mode: "keyword",
            ct: TestContext.Current.CancellationToken
        );
        Assert.NotEmpty(results);
        var text = results[0].ExplainText();
        Assert.Contains("score", text);
        Assert.Contains("similarity", text);
        Assert.Contains("importance", text);
    }

    [Fact]
    public async Task RecallAsync_ThrowsOnInvalidMode()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.RecallAsync(
                "test",
                "user-1",
                mode: "invalid",
                ct: TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public async Task RecallAsync_ThrowsOnNullQuery()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _store.RecallAsync(null!, "user-1", ct: TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task RecallAsync_MultipleCalls_ReturnsConsistentRanking()
    {
        await _store.StoreAsync(
            "the dragon breathes fire",
            "user-1",
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "the knight fights bravely",
            "user-1",
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "fire is hot and bright",
            "user-1",
            ct: TestContext.Current.CancellationToken
        );

        var r1 = await _store.RecallAsync(
            "dragon fire",
            "user-1",
            k: 3,
            mode: "keyword",
            ct: TestContext.Current.CancellationToken
        );
        var r2 = await _store.RecallAsync(
            "dragon fire",
            "user-1",
            k: 3,
            mode: "keyword",
            ct: TestContext.Current.CancellationToken
        );

        Assert.Equal(r1.Count, r2.Count);
        for (var i = 0; i < r1.Count; i++)
            Assert.Equal(r1[i].Memory.Id, r2[i].Memory.Id);
    }

    // ══════════════════════════════════════════════════════
    //  ReinforceAsync
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task ReinforceAsync_IncreasesImportance()
    {
        var mem = await _store.StoreAsync(
            "reinforce me",
            "user-1",
            importance: 0.5,
            ct: TestContext.Current.CancellationToken
        );
        await _store.ReinforceAsync(
            mem.Id,
            importanceBoost: 0.2,
            ct: TestContext.Current.CancellationToken
        );

        var reinforced = await _store.GetAsync(mem.Id, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(reinforced);
        Assert.True(reinforced.Importance > 0.5);
    }

    [Fact]
    public async Task ReinforceAsync_CapsAtOne()
    {
        var mem = await _store.StoreAsync(
            "near cap",
            "user-1",
            importance: 0.95,
            ct: TestContext.Current.CancellationToken
        );
        await _store.ReinforceAsync(
            mem.Id,
            importanceBoost: 0.1,
            ct: TestContext.Current.CancellationToken
        );

        var reinforced = await _store.GetAsync(mem.Id, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(reinforced);
        Assert.Equal(1.0, reinforced.Importance);
    }

    [Fact]
    public async Task ReinforceAsync_ResetsRecencyClock()
    {
        var mem = await _store.StoreAsync(
            "recency reset",
            "user-1",
            ct: TestContext.Current.CancellationToken
        );
        Assert.Null(mem.LastAccessedAt);

        await Task.Delay(10, TestContext.Current.CancellationToken);
        await _store.ReinforceAsync(mem.Id, ct: TestContext.Current.CancellationToken);

        var reinforced = await _store.GetAsync(mem.Id, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(reinforced);
        Assert.NotNull(reinforced.LastAccessedAt);
        Assert.True(reinforced.LastAccessedAt > mem.CreatedAt);
    }

    [Fact]
    public async Task ReinforceAsync_NonExistent_ReturnsNull()
    {
        var result = await _store.ReinforceAsync(
            "non-existent-id",
            ct: TestContext.Current.CancellationToken
        );
        Assert.Null(result);
    }

    // ══════════════════════════════════════════════════════
    //  ForgetAsync
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task ForgetAsync_ById_RemovesMemory()
    {
        var mem = await _store.StoreAsync(
            "to forget",
            "user-1",
            ct: TestContext.Current.CancellationToken
        );
        var count = await _store.ForgetAsync(
            memoryId: mem.Id,
            ct: TestContext.Current.CancellationToken
        );
        Assert.Equal(1, count);

        var gone = await _store.GetAsync(mem.Id, ct: TestContext.Current.CancellationToken);
        Assert.Null(gone);
    }

    [Fact]
    public async Task ForgetAsync_ByNamespace_RemovesAllInNamespace()
    {
        await _store.StoreAsync(
            "style 1",
            "user-1",
            ns: "style",
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "style 2",
            "user-1",
            ns: "style",
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "plot 1",
            "user-1",
            ns: "plot",
            ct: TestContext.Current.CancellationToken
        );

        var count = await _store.ForgetAsync(
            ownerId: "user-1",
            ns: "style",
            ct: TestContext.Current.CancellationToken
        );
        Assert.Equal(2, count);

        var remaining = await _store.GetAllAsync(
            "user-1",
            ct: TestContext.Current.CancellationToken
        );
        Assert.Single(remaining);
    }

    [Fact]
    public async Task ForgetAsync_ByOwner_RemovesAll()
    {
        await _store.StoreAsync("mem1", "user-1", ct: TestContext.Current.CancellationToken);
        await _store.StoreAsync("mem2", "user-1", ct: TestContext.Current.CancellationToken);

        var count = await _store.ForgetAsync(
            ownerId: "user-1",
            ct: TestContext.Current.CancellationToken
        );
        Assert.Equal(2, count);

        var all = await _store.GetAllAsync("user-1", ct: TestContext.Current.CancellationToken);
        Assert.Empty(all);
    }

    [Fact]
    public async Task ForgetAsync_OlderThan_RemovesOldOnly()
    {
        // 先存一条"旧"记忆，等 1.1 秒让它确实变旧
        await _store.StoreAsync("old memory", "user-1", ct: TestContext.Current.CancellationToken);
        await Task.Delay(1100, TestContext.Current.CancellationToken);

        // 再存"新"记忆
        var mem = await _store.StoreAsync(
            "new memory",
            "user-1",
            ct: TestContext.Current.CancellationToken
        );
        await _store.ReinforceAsync(mem.Id, ct: TestContext.Current.CancellationToken);

        // 删除 1 秒前的记忆（应该只删除 old memory）
        var count = await _store.ForgetAsync(
            ownerId: "user-1",
            olderThanSeconds: 1.0,
            ct: TestContext.Current.CancellationToken
        );
        Assert.True(count >= 1);

        // new memory 应该还在
        var remaining = await _store.GetAllAsync(
            "user-1",
            ct: TestContext.Current.CancellationToken
        );
        Assert.Contains(remaining, r => r.Content.Contains("new"));
    }

    [Fact]
    public async Task ForgetAsync_ThrowsOnNoFilter()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.ForgetAsync(ct: TestContext.Current.CancellationToken)
        );
    }

    // ══════════════════════════════════════════════════════
    //  MarkUsefulAsync / MarkUselessAsync
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task MarkUsefulAsync_IncreasesUsefulness()
    {
        var mem = await _store.StoreAsync(
            "useful memory",
            "user-1",
            ct: TestContext.Current.CancellationToken
        );
        Assert.Equal(0.0, mem.Usefulness);

        await _store.MarkUsefulAsync(mem.Id, ct: TestContext.Current.CancellationToken);
        var updated = await _store.GetAsync(mem.Id, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.True(updated.Usefulness > 0);
    }

    [Fact]
    public async Task MarkUselessAsync_DecreasesUsefulness()
    {
        var mem = await _store.StoreAsync(
            "useless memory",
            "user-1",
            ct: TestContext.Current.CancellationToken
        );
        await _store.MarkUselessAsync(mem.Id, ct: TestContext.Current.CancellationToken);
        var updated = await _store.GetAsync(mem.Id, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.True(updated.Usefulness < 0);
    }

    [Fact]
    public async Task MarkUsefulAsync_WithWeight_Amplifies()
    {
        var mem = await _store.StoreAsync(
            "weighted",
            "user-1",
            ct: TestContext.Current.CancellationToken
        );
        await _store.MarkUsefulAsync(
            mem.Id,
            weight: 3.0,
            ct: TestContext.Current.CancellationToken
        );
        var updated = await _store.GetAsync(mem.Id, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.True(updated.Usefulness >= 3.0);
    }

    [Fact]
    public async Task MultipleFeedback_Accumulates()
    {
        var mem = await _store.StoreAsync(
            "accumulating",
            "user-1",
            ct: TestContext.Current.CancellationToken
        );
        await _store.MarkUsefulAsync(
            mem.Id,
            weight: 1.0,
            ct: TestContext.Current.CancellationToken
        );
        await _store.MarkUsefulAsync(
            mem.Id,
            weight: 1.0,
            ct: TestContext.Current.CancellationToken
        );
        await _store.MarkUselessAsync(
            mem.Id,
            weight: 0.5,
            ct: TestContext.Current.CancellationToken
        );

        var updated = await _store.GetAsync(mem.Id, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(updated);
        Assert.Equal(1.5, updated.Usefulness, 6);
    }

    [Fact]
    public async Task MarkUsefulAsync_NonExistent_ReturnsNull()
    {
        var result = await _store.MarkUsefulAsync(
            "non-existent",
            ct: TestContext.Current.CancellationToken
        );
        Assert.Null(result);
    }

    [Fact]
    public async Task MarkUsefulAsync_ThrowsOnZeroWeight()
    {
        var mem = await _store.StoreAsync(
            "test",
            "user-1",
            ct: TestContext.Current.CancellationToken
        );
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.MarkUsefulAsync(mem.Id, weight: 0, ct: TestContext.Current.CancellationToken)
        );
    }

    // ══════════════════════════════════════════════════════
    //  GetAsync / GetAllAsync / GetStatsAsync
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task GetAsync_Existing_ReturnsMemory()
    {
        var stored = await _store.StoreAsync(
            "get me",
            "user-1",
            ct: TestContext.Current.CancellationToken
        );
        var fetched = await _store.GetAsync(stored.Id, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Equal(stored.Content, fetched.Content);
    }

    [Fact]
    public async Task GetAsync_NonExistent_ReturnsNull()
    {
        var fetched = await _store.GetAsync(
            "non-existent",
            ct: TestContext.Current.CancellationToken
        );
        Assert.Null(fetched);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsAllForOwner()
    {
        await _store.StoreAsync("mem1", "user-1", ct: TestContext.Current.CancellationToken);
        await _store.StoreAsync("mem2", "user-1", ct: TestContext.Current.CancellationToken);
        await _store.StoreAsync("mem3", "user-2", ct: TestContext.Current.CancellationToken);

        var all = await _store.GetAllAsync("user-1", ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task GetAllAsync_WithNamespace_Filters()
    {
        await _store.StoreAsync(
            "global",
            "user-1",
            ns: "default",
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "style",
            "user-1",
            ns: "style",
            ct: TestContext.Current.CancellationToken
        );

        var defaultNs = await _store.GetAllAsync(
            "user-1",
            ns: "default",
            ct: TestContext.Current.CancellationToken
        );
        Assert.Single(defaultNs);
    }

    [Fact]
    public async Task GetStatsAsync_EmptyStore_ReturnsZeros()
    {
        var stats = await _store.GetStatsAsync("user-1", ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, stats.TotalCount);
    }

    [Fact]
    public async Task GetStatsAsync_WithMemories_ReturnsCorrectCounts()
    {
        await _store.StoreAsync(
            "style one",
            "user-1",
            ns: "style",
            importance: 0.8,
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "style two",
            "user-1",
            ns: "style",
            importance: 0.6,
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "plot one",
            "user-1",
            ns: "plot",
            importance: 0.9,
            memoryKind: AgentMemoryKind.Explicit,
            ct: TestContext.Current.CancellationToken
        );

        var stats = await _store.GetStatsAsync("user-1", ct: TestContext.Current.CancellationToken);
        Assert.Equal(3, stats.TotalCount);
        Assert.Equal(2, stats.PerNamespace["style"]);
        Assert.Equal(1, stats.PerNamespace["plot"]);
        Assert.True(stats.AvgImportance > 0);
    }

    // ══════════════════════════════════════════════════════
    //  积分工作流
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task FullWorkflow_StoreRecallReinforceFeedback_Integration()
    {
        await _store.StoreAsync(
            "the protagonist is afraid of spider",
            "user-1",
            importance: 0.9,
            ns: "default",
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "the castle is guarded by magic",
            "user-1",
            importance: 0.8,
            ns: "default",
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "the weather is sunny today",
            "user-1",
            importance: 0.2,
            ns: "default",
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "spider are small creatures",
            "user-1",
            importance: 0.5,
            ns: "default",
            ct: TestContext.Current.CancellationToken
        );

        var results = await _store.RecallAsync(
            "spider",
            "user-1",
            k: 5,
            mode: "keyword",
            ct: TestContext.Current.CancellationToken
        );
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Memory.Content.Contains("spider"));

        var protagonistFear = results.First(r => r.Memory.Content.Contains("protagonist"));
        await _store.ReinforceAsync(
            protagonistFear.Memory.Id,
            importanceBoost: 0.05,
            ct: TestContext.Current.CancellationToken
        );

        var weather = results.FirstOrDefault(r => r.Memory.Content.Contains("weather"));
        if (weather is not null)
            await _store.MarkUselessAsync(
                weather.Memory.Id,
                ct: TestContext.Current.CancellationToken
            );

        var results2 = await _store.RecallAsync(
            "spider",
            "user-1",
            k: 5,
            mode: "keyword",
            ct: TestContext.Current.CancellationToken
        );
        Assert.NotEmpty(results2);

        var stats = await _store.GetStatsAsync("user-1", ct: TestContext.Current.CancellationToken);
        Assert.Equal(4, stats.TotalCount);
    }

    // ══════════════════════════════════════════════════════
    //  多用户隔离
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task MultiUser_Isolation_NoCrossContamination()
    {
        await _store.StoreAsync(
            "user A prefers dark mode",
            "user-A",
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "user A likes short sentences",
            "user-A",
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "user B prefers light mode",
            "user-B",
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "user B likes long paragraphs",
            "user-B",
            ct: TestContext.Current.CancellationToken
        );

        var aResults = await _store.RecallAsync(
            "mode",
            "user-A",
            mode: "keyword",
            ct: TestContext.Current.CancellationToken
        );
        var bResults = await _store.RecallAsync(
            "mode",
            "user-B",
            mode: "keyword",
            ct: TestContext.Current.CancellationToken
        );

        Assert.All(aResults, r => Assert.Equal("user-A", r.Memory.OwnerId));
        Assert.All(bResults, r => Assert.Equal("user-B", r.Memory.OwnerId));

        // 记录 user-B 的 access_count 基线
        var bBefore = await _store.GetAllAsync("user-B", ct: TestContext.Current.CancellationToken);
        var bCountsBefore = bBefore.ToDictionary(m => m.Id, m => m.AccessCount);

        // ReinforceAsync 在 user-A 的记忆上
        await _store.ReinforceAsync(
            aResults[0].Memory.Id,
            ct: TestContext.Current.CancellationToken
        );

        // user-B 的 access_count 不应有任何变化
        var bAfter = await _store.GetAllAsync("user-B", ct: TestContext.Current.CancellationToken);
        Assert.All(bAfter, m => Assert.Equal(bCountsBefore[m.Id], m.AccessCount));
    }

    // ══════════════════════════════════════════════════════
    //  边界条件
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task Recall_WithExpiredMemories_FiltersThemOut()
    {
        var past = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 100;
        var future = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;

        await _store.StoreAsync(
            "expired memory",
            "user-1",
            expiresAt: past,
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "valid memory",
            "user-1",
            expiresAt: future,
            ct: TestContext.Current.CancellationToken
        );

        var results = await _store.RecallAsync(
            "memory",
            "user-1",
            k: 5,
            mode: "keyword",
            ct: TestContext.Current.CancellationToken
        );
        Assert.Single(results);
        Assert.Contains(results, r => r.Memory.Content.Contains("valid"));
    }

    [Fact]
    public async Task MultipleRecalls_IncreaseAccessCount()
    {
        var mem = await _store.StoreAsync(
            "popular memory",
            "user-1",
            ct: TestContext.Current.CancellationToken
        );

        await _store.RecallAsync(
            "popular",
            "user-1",
            mode: "keyword",
            touch: true,
            ct: TestContext.Current.CancellationToken
        );
        await _store.RecallAsync(
            "popular",
            "user-1",
            mode: "keyword",
            touch: true,
            ct: TestContext.Current.CancellationToken
        );
        await _store.RecallAsync(
            "popular",
            "user-1",
            mode: "keyword",
            touch: true,
            ct: TestContext.Current.CancellationToken
        );

        var fetched = await _store.GetAsync(mem.Id, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Equal(3, fetched.AccessCount);
    }

    [Fact]
    public async Task GetStats_WithMixedKinds_ReportsCorrectly()
    {
        await _store.StoreAsync(
            "explicit kind",
            "user-1",
            memoryKind: AgentMemoryKind.Explicit,
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "inference kind",
            "user-1",
            memoryKind: AgentMemoryKind.ConfirmedInference,
            ct: TestContext.Current.CancellationToken
        );
        await _store.StoreAsync(
            "candidate kind",
            "user-1",
            memoryKind: AgentMemoryKind.CandidateInference,
            ct: TestContext.Current.CancellationToken
        );

        var stats = await _store.GetStatsAsync("user-1", ct: TestContext.Current.CancellationToken);
        Assert.Equal(3, stats.TotalCount);
        Assert.Equal(3, stats.PerKind.Count);
    }

    [Fact]
    public async Task Store_ThenForget_ThenGet_ReturnsNull()
    {
        var mem = await _store.StoreAsync(
            "ephemeral",
            "user-1",
            ct: TestContext.Current.CancellationToken
        );
        await _store.ForgetAsync(memoryId: mem.Id, ct: TestContext.Current.CancellationToken);
        var fetched = await _store.GetAsync(mem.Id, ct: TestContext.Current.CancellationToken);
        Assert.Null(fetched);
    }

    // ══════════════════════════════════════════════════════
    //  并发安全
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task ConcurrentStore_DoesNotCorrupt()
    {
        var tasks = new List<Task>();
        for (var i = 0; i < 10; i++)
        {
            var idx = i;
            tasks.Add(
                Task.Run(
                    async () =>
                    {
                        await _store.StoreAsync(
                            $"concurrent memory {idx}",
                            "user-1",
                            ct: TestContext.Current.CancellationToken
                        );
                    },
                    TestContext.Current.CancellationToken
                )
            );
        }
        await Task.WhenAll(tasks);

        var all = await _store.GetAllAsync("user-1", ct: TestContext.Current.CancellationToken);
        Assert.Equal(10, all.Count);
    }

    [Fact]
    public async Task ConcurrentRecallAndStore_DoesNotDeadlock()
    {
        var storeTask = Task.Run(
            async () =>
            {
                for (var i = 0; i < 5; i++)
                    await _store.StoreAsync(
                        $"concurrent {i}",
                        "user-1",
                        ct: TestContext.Current.CancellationToken
                    );
            },
            TestContext.Current.CancellationToken
        );

        var recallTask = Task.Run(
            async () =>
            {
                for (var i = 0; i < 5; i++)
                    await _store.RecallAsync(
                        "concurrent",
                        "user-1",
                        k: 5,
                        mode: "keyword",
                        ct: TestContext.Current.CancellationToken
                    );
            },
            TestContext.Current.CancellationToken
        );

        await Task.WhenAll(storeTask, recallTask);

        // 并发未死锁且未丢失数据：5 条存储全部落库（Recall 并发读不破坏写入）
        var all = await _store.GetAllAsync("user-1", ct: TestContext.Current.CancellationToken);
        Assert.Equal(5, all.Count);
    }

    // ══════════════════════════════════════════════════════
    //  纯 BM25 模式（无 EmbeddingService）
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task PureBm25Store_RecallByKeyword_Works()
    {
        var bm25Path = Path.Combine(Path.GetTempPath(), $"test_bm25_{Guid.NewGuid():N}.db");
        try
        {
            // 存储必须先释放（关闭 SQLite 连接）才能删除库文件 → await using 作用域限定在 try 内
            await using var bm25Store = new AgentMemoryStore(bm25Path, dimension: 4);

            await bm25Store.InitializeAsync(ct: TestContext.Current.CancellationToken);
            await bm25Store.StoreAsync(
                "the dragon breathes fire",
                "user-1",
                ct: TestContext.Current.CancellationToken
            );
            await bm25Store.StoreAsync(
                "the knight draws his sword",
                "user-1",
                ct: TestContext.Current.CancellationToken
            );

            var results = await bm25Store.RecallAsync(
                "dragon fire",
                "user-1",
                mode: "keyword",
                ct: TestContext.Current.CancellationToken
            );
            Assert.NotEmpty(results);
            Assert.Contains(results, r => r.Memory.Content.Contains("dragon"));
        }
        finally
        {
            if (File.Exists(bm25Path))
                File.Delete(bm25Path);
        }
    }

    [Fact]
    public async Task PureBm25Store_NoEmbedder_RecallHybrid_DefaultsToKeyword()
    {
        var bm25Path = Path.Combine(Path.GetTempPath(), $"test_hybrid_{Guid.NewGuid():N}.db");
        try
        {
            await using var bm25Store = new AgentMemoryStore(bm25Path, dimension: 4);

            await bm25Store.InitializeAsync(ct: TestContext.Current.CancellationToken);
            await bm25Store.StoreAsync(
                "hybrid mode test",
                "user-1",
                ct: TestContext.Current.CancellationToken
            );

            var results = await bm25Store.RecallAsync(
                "hybrid",
                "user-1",
                mode: "hybrid",
                ct: TestContext.Current.CancellationToken
            );
            Assert.NotEmpty(results);
        }
        finally
        {
            if (File.Exists(bm25Path))
                File.Delete(bm25Path);
        }
    }

    // ══════════════════════════════════════════════════════
    //  向量通道（P0 修复后）— 本地 HashEmbedder，确定性、离线可用
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task Recall_VectorMode_UsesStoredEmbeddings_Offline()
    {
        // 不注入 embedding 服务 → 默认本地 HashEmbedder → vector 模式离线可用
        var path = Path.Combine(Path.GetTempPath(), $"test_vec_{Guid.NewGuid():N}.db");
        try
        {
            await using var store = new AgentMemoryStore(path, dimension: 128);
            await store.InitializeAsync(ct: TestContext.Current.CancellationToken);

            await store.StoreAsync(
                "主角怕蜘蛛",
                "user-1",
                ct: TestContext.Current.CancellationToken
            );
            await store.StoreAsync(
                "天气晴朗适合出行",
                "user-1",
                ct: TestContext.Current.CancellationToken
            );

            var results = await store.RecallAsync(
                "蜘蛛",
                "user-1",
                k: 5,
                mode: "vector",
                ct: TestContext.Current.CancellationToken
            );
            Assert.NotEmpty(results);
            // 词法哈希：共享词 → 相似度 > 0；无关文本（零相似度）被 min_similarity 过滤
            Assert.All(results, r => Assert.True(r.VectorScore > 0));
            Assert.Equal("主角怕蜘蛛", results[0].Memory.Content);
            Assert.Single(results); // 仅共享词的相关记忆被召回
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task Recall_Hybrid_IncludesVectorComponent()
    {
        // 同文本查询：向量分量 > 0 且最终相似度 > 纯 BM25 分量
        var path = Path.Combine(Path.GetTempPath(), $"test_hybvec_{Guid.NewGuid():N}.db");
        try
        {
            await using var store = new AgentMemoryStore(path, dimension: 128);
            await store.InitializeAsync(ct: TestContext.Current.CancellationToken);

            await store.StoreAsync(
                "主角怕蜘蛛",
                "user-1",
                ct: TestContext.Current.CancellationToken
            );

            var results = await store.RecallAsync(
                "主角怕蜘蛛",
                "user-1",
                k: 5,
                mode: "hybrid",
                ct: TestContext.Current.CancellationToken
            );
            var top = Assert.Single(results);
            Assert.True(top.VectorScore > 0, "向量分量必须 > 0");
            Assert.Equal(1.0, top.VectorScore, 3); // 同文本 → 余弦 ≈ 1.0
            // 单文档时 BM25 归一化 = 1.0，hybrid = 0.65×1.0 + 0.35×1.0 = 1.0（向量分量参与，非纯 BM25）
            Assert.Equal(1.0, top.Similarity, 3);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task Recall_VectorMode_Deterministic()
    {
        // 冻结时间 + 不 touch：同查询两次 → 相同分数与顺序（确定性）
        var path = Path.Combine(Path.GetTempPath(), $"test_det_{Guid.NewGuid():N}.db");
        try
        {
            await using var store = new AgentMemoryStore(path, dimension: 128);
            await store.InitializeAsync(ct: TestContext.Current.CancellationToken);

            await store.StoreAsync(
                "主角怕蜘蛛",
                "user-1",
                ct: TestContext.Current.CancellationToken
            );
            await store.StoreAsync(
                "古堡建于十三世纪",
                "user-1",
                ct: TestContext.Current.CancellationToken
            );
            await store.StoreAsync(
                "主角的武器是长剑",
                "user-1",
                ct: TestContext.Current.CancellationToken
            );

            const double frozenNow = 1_700_000_000.0;
            var r1 = await store.RecallAsync(
                "主角",
                "user-1",
                k: 3,
                mode: "vector",
                touch: false,
                now: frozenNow,
                ct: TestContext.Current.CancellationToken
            );
            var r2 = await store.RecallAsync(
                "主角",
                "user-1",
                k: 3,
                mode: "vector",
                touch: false,
                now: frozenNow,
                ct: TestContext.Current.CancellationToken
            );

            Assert.Equal(r1.Count, r2.Count);
            for (var i = 0; i < r1.Count; i++)
            {
                Assert.Equal(r1[i].Memory.Id, r2[i].Memory.Id);
                Assert.Equal(r1[i].Score, r2[i].Score, 6);
            }
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    // ══════════════════════════════════════════════════════
    //  时间旅行字段完整性（P1 修复后）
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task AsOf_ReconstructsFullFields()
    {
        await _store.StoreAsync(
            "第5章：主角进入古堡",
            "user-1",
            novelId: "novel-x",
            importance: 0.8,
            priority: 3,
            confidence: 0.9,
            ct: TestContext.Current.CancellationToken
        );
        await _store.SnapshotAsync("ch5-done", ct: TestContext.Current.CancellationToken);

        var mems = await _store.AsOfAsync("ch5-done", ct: TestContext.Current.CancellationToken);
        var mem = Assert.Single(mems);
        Assert.Equal("user-1", mem.OwnerId);
        Assert.Equal("novel-x", mem.NovelId);
        Assert.Equal("第5章：主角进入古堡", mem.Content);
        Assert.Equal(0.8, mem.Importance);
        Assert.Equal(3, mem.Priority);
        Assert.Equal(0.9, mem.Confidence);
    }

    [Fact]
    public async Task AsOf_AfterForget_RemovedFromState()
    {
        var mem = await _store.StoreAsync(
            "将被遗忘的设定",
            "user-1",
            ct: TestContext.Current.CancellationToken
        );
        await _store.SnapshotAsync("before", ct: TestContext.Current.CancellationToken);
        await _store.ForgetAsync(memoryId: mem.Id, ct: TestContext.Current.CancellationToken);
        await _store.SnapshotAsync("after", ct: TestContext.Current.CancellationToken);

        var before = await _store.AsOfAsync("before", ct: TestContext.Current.CancellationToken);
        var after = await _store.AsOfAsync("after", ct: TestContext.Current.CancellationToken);
        Assert.Contains(before, m => m.Id == mem.Id);
        Assert.DoesNotContain(after, m => m.Id == mem.Id);
    }

    // ══════════════════════════════════════════════════════
    //  分词缓存失效（P1 修复后）
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task TokenCache_InvalidatedOnForget()
    {
        var mem = await _store.StoreAsync(
            "cached memory content",
            "user-1",
            ct: TestContext.Current.CancellationToken
        );
        await _store.RecallAsync(
            "cached",
            "user-1",
            mode: "keyword",
            ct: TestContext.Current.CancellationToken
        ); // 填充缓存

        await _store.ForgetAsync(memoryId: mem.Id, ct: TestContext.Current.CancellationToken);

        var results = await _store.RecallAsync(
            "cached",
            "user-1",
            mode: "keyword",
            ct: TestContext.Current.CancellationToken
        );
        Assert.Empty(results);
    }
}
