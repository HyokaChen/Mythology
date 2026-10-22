using Core.Agent;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Core.Vector;
using Xunit;
using AgentType = Core.Agent.Agent;

namespace Core.Tests;

/// <summary>
/// 会话历史的向量检索通道（可选增强，默认关闭）：
/// 索引粒度 = 单条消息（命中 SourceEntityId 即 entryId，可 history_read_item 回读）；
/// semantic=false / 无 kernel 时零 embedding 调用。
/// </summary>
[Collection("FileNovelStore")]
public sealed class ConversationHistorySearchTests : IAsyncLifetime
{
    private string _tempRoot = "";
    private FileSessionStore _store = null!;
    private AgentType _agent = null!;
    private CountingEmbedder _embedder = null!;
    private RetrievalKernel _kernel = null!;
    private const string TestNovelId = "vecnovel";
    private const string TestSessionId = "vecsess00001";

    /// <summary>计数 embedding 服务（HashEmbedder 语义 + 调用计数）。</summary>
    private sealed class CountingEmbedder(int dimension) : IEmbeddingService, IBatchEmbeddingService
    {
        private readonly HashEmbedder _inner = new(dimension);
        private int _calls;

        public int Calls => _calls;

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return _inner.EmbedAsync(text, ct);
        }

        public async Task<float[][]> EmbedBatchAsync(
            IReadOnlyList<string> texts,
            CancellationToken ct = default
        )
        {
            Interlocked.Increment(ref _calls);
            var result = new float[texts.Count][];
            for (var i = 0; i < texts.Count; i++)
                result[i] = await _inner.EmbedAsync(texts[i], ct);
            return result;
        }

        public int Dimension => dimension;
    }

    public ValueTask InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"mythology-vec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _store = new FileSessionStore(
            novelsRoot: Path.Combine(_tempRoot, "novels"),
            mythologyDir: _tempRoot
        )
        {
            SkipConflictCheck = true,
        };
        _agent = new AgentType(new ToolRegistry())
        {
            SessionId = TestSessionId,
            CurrentNovelId = TestNovelId,
        };
        _embedder = new CountingEmbedder(128);
        _kernel = new RetrievalKernel(new InMemoryRetrievalStore(), _embedder);
        return default;
    }

    public ValueTask DisposeAsync()
    {
        AtomicFile.ForgetPath(_tempRoot);
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试
        }
        return default;
    }

    private async Task SeedAsync(params string[] texts)
    {
        var messages = texts
            .Select(t => new LlmTornado.Chat.ChatMessage(LlmTornado.Code.ChatMessageRoles.User, t))
            .ToList();
        var (nextOrder, lastEntryId) = await _store.GetSessionMetadataAsync(TestSessionId);
        await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            messages,
            lastParentEntryId: lastEntryId,
            startOrder: nextOrder
        );
    }

    [Fact]
    public async Task IndexedItems_AreSearchable_AndResolveBackToIds()
    {
        await SeedAsync("重构约定：接续换页时不要生成摘要", "无关的另一条消息");
        await _store.StartNewContextWindowAsync(TestSessionId, TestNovelId, "model", order: 2);

        var items = await _store.ListHistoryItemsAsync(
            TestSessionId,
            windowId: "w0",
            role: null,
            toolName: null,
            limit: 10,
            recentFirst: false,
            maxCharsPerItem: 0
        );
        Assert.Equal(2, items.Count);

        foreach (var item in items)
            await ConversationHistorySearch.IndexItemAsync(
                _kernel,
                TestNovelId,
                TestSessionId,
                item
            );

        var hits = await ConversationHistorySearch.SearchAsync(
            _kernel,
            _store,
            _agent,
            "接续换页",
            limit: 5
        );

        Assert.Contains(hits, h => h.ItemId == items[0].ItemId);
        Assert.All(hits, h => Assert.False(string.IsNullOrEmpty(h.ItemId)));

        // 命中可回读原文（[id] → history_read_item 闭环）
        var read = await _store.ReadHistoryItemAsync(TestSessionId, "", items[0].ItemId);
        Assert.NotNull(read);
        Assert.Contains("接续换页", read!.Value.FullContent);
    }

    [Fact]
    public async Task Search_IsScopedToCurrentSession()
    {
        // 同一本小说的两个会话：A 的语义检索不得命中 B 的条目
        const string otherSession = "vecsess00002";
        await SeedAsync("会话 A：接续换页的私有约定");
        var (nextOrder, lastEntryId) = await _store.GetSessionMetadataAsync(otherSession);
        await _store.SaveSessionMessages(
            otherSession,
            TestNovelId,
            [
                new LlmTornado.Chat.ChatMessage(
                    LlmTornado.Code.ChatMessageRoles.User,
                    "会话 B：接续换页的另一份约定"
                ),
            ],
            lastParentEntryId: lastEntryId,
            startOrder: nextOrder
        );

        var itemsA = await _store.ListHistoryItemsAsync(
            TestSessionId,
            null,
            null,
            null,
            10,
            false,
            0
        );
        var itemsB = await _store.ListHistoryItemsAsync(
            otherSession,
            null,
            null,
            null,
            10,
            false,
            0
        );
        Assert.Single(itemsB);

        foreach (var item in itemsA.Concat(itemsB))
            await ConversationHistorySearch.IndexItemAsync(
                _kernel,
                TestNovelId,
                item.ItemId == itemsB[0].ItemId ? otherSession : TestSessionId,
                item
            );

        var hits = await ConversationHistorySearch.SearchAsync(
            _kernel,
            _store,
            _agent,
            "接续换页",
            limit: 10
        );

        Assert.Contains(hits, h => h.ItemId == itemsA[0].ItemId);
        Assert.DoesNotContain(hits, h => h.ItemId == itemsB[0].ItemId);
    }

    [Fact]
    public async Task Search_WithoutSession_ReturnsEmpty()
    {
        var orphan = new AgentType(new ToolRegistry());

        var hits = await ConversationHistorySearch.SearchAsync(
            _kernel,
            _store,
            orphan,
            "任意查询",
            limit: 5
        );

        Assert.Empty(hits);
    }
}
