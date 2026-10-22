using Core.Agent;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Core.Vector;
using Plugins.ContextContinuation;
using Xunit;
using AgentType = Core.Agent.Agent;

namespace Plugins.ContextContinuation.Tests;

/// <summary>
/// history_search_contents 的语义通道开关：默认字面通道零 embedding 调用，
/// 只有 semantic=true 且注入 kernel 时才走向量检索；kernel 缺失时退化为字面并说明原因。
/// </summary>
[Collection("FileNovelStore")]
public sealed class HistorySearchSemanticChannelTests : IAsyncLifetime
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

    private static string TextOf(AgentToolResult result) =>
        Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;

    [Fact]
    public async Task SemanticFlag_OnlyPaysEmbeddingCostWhenEnabled()
    {
        var (nextOrder, lastEntryId) = await _store.GetSessionMetadataAsync(TestSessionId);
        await _store.SaveSessionMessages(
            TestSessionId,
            TestNovelId,
            [
                new LlmTornado.Chat.ChatMessage(
                    LlmTornado.Code.ChatMessageRoles.User,
                    "笔记：换页时的约定"
                ),
            ],
            lastParentEntryId: lastEntryId,
            startOrder: nextOrder
        );

        var tool = new HistorySearchContentsTool(_agent, _store, _kernel);

        var literal = await tool.ExecuteAsync(
            "c1",
            new HistorySearchParams { Query = "换页", Semantic = false },
            CancellationToken.None
        );
        Assert.False(literal.IsError);
        Assert.Equal(0, _embedder.Calls); // 默认通道零 embedding 调用

        var semanticOff = await tool.ExecuteAsync(
            "c2",
            new HistorySearchParams { Query = "换页" },
            CancellationToken.None
        );
        Assert.Equal(0, _embedder.Calls);

        // 显式开启语义检索 → 产生 embedding 调用（索引为空时结果为空，但通道被使用）
        await tool.ExecuteAsync(
            "c3",
            new HistorySearchParams { Query = "换页", Semantic = true },
            CancellationToken.None
        );
        Assert.True(_embedder.Calls > 0);

        // 未注入 kernel：semantic=true 退化为字面检索并说明原因
        var noKernel = new HistorySearchContentsTool(_agent, _store);
        var degraded = await noKernel.ExecuteAsync(
            "c4",
            new HistorySearchParams { Query = "换页", Semantic = true },
            CancellationToken.None
        );
        Assert.Contains("语义检索未启用", TextOf(degraded));
    }
}
