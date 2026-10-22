using Core.Shared;
using Core.Tools;
using Core.Vector;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 深度审计优化测试：覆盖本轮 Bug 修复与性能优化的可测行为。
/// 1. ToolsSignature 随展开变化（Bug A 检测基础）
/// 2. BuildRequestTools 签名缓存命中（Step 5）
/// 3. ExpandHits 消除双重检索（Step 4）
/// 4. EnsureIndexedAsync 惰性重建（Step 7）
/// 5. IsToolAvailable 覆盖非延迟工具（Step 6）
/// 6. dense 失败冷却（Step 9）
/// </summary>
public class ToolSearchDeepAuditTests
{
    [Fact]
    /// <summary>ExpandHits 展开工具后 ToolsSignature 变化（ConversationLoop 据此重建）。</summary>
    public async Task ToolsSignature_ChangesAfterExpand()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索", [], []), "Core");
        registry.Register(
            new DummyTool("check_char", "检查角色", "角色完整性检查", ["角色"], []),
            "NovelTools"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);
        var injector = new ToolInjector(registry, engine);

        var sigBefore = injector.ToolsSignature;
        await injector.ExpandAsync("角色", topK: 5, TestContext.Current.CancellationToken);
        var sigAfter = injector.ToolsSignature;

        Assert.NotEqual(sigBefore, sigAfter);
    }

    [Fact]
    /// <summary>展开/注册变化前后 ToolsSignature 不同；无变化时相同。</summary>
    public async Task ToolsSignature_StableWhenNoChange()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索", [], []), "Core");
        registry.Register(
            new DummyTool("check_char", "检查角色", "角色完整性检查", ["角色"], []),
            "NovelTools"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);
        var injector = new ToolInjector(registry, engine);

        var sig1 = injector.ToolsSignature;
        var sig2 = injector.ToolsSignature;
        Assert.Equal(sig1, sig2);

        // 注册新工具 → 签名变化
        registry.Register(new DummyTool("new_tool", "新工具", "新工具描述", [], []), "NovelTools");
        Assert.NotEqual(sig1, injector.ToolsSignature);
    }

    [Fact]
    /// <summary>BuildRequestTools 签名缓存：无变化时返回同一数组实例。</summary>
    public async Task BuildRequestTools_CacheHit_ReturnsSameInstance()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索", [], []), "Core");
        registry.Register(
            new DummyTool("check_char", "检查角色", "角色完整性检查", ["角色"], []),
            "NovelTools"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);
        var injector = new ToolInjector(registry, engine);

        var first = injector.BuildRequestTools();
        var second = injector.BuildRequestTools();

        Assert.Same(first, second);
    }

    [Fact]
    /// <summary>展开后 BuildRequestTools 缓存失效，返回包含新工具的新数组。</summary>
    public async Task BuildRequestTools_CacheInvalidatedOnExpand()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索", [], []), "Core");
        registry.Register(
            new DummyTool("check_char", "检查角色", "角色完整性检查", ["角色"], []),
            "NovelTools"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);
        // 显式延迟 NovelTools 插件（默认名单用 "Novel Writing Tools"，测试名是 "NovelTools"）
        var injector = new ToolInjector(
            registry,
            engine,
            deferredPluginNames: new HashSet<string>(
                ["NovelTools"],
                StringComparer.OrdinalIgnoreCase
            )
        );

        var before = injector.BuildRequestTools();
        Assert.DoesNotContain(before, t => t.Name == "check_char"); // NovelTools 被延迟

        // 展开后（未注册新工具）→ 缓存应失效并包含展开工具
        await injector.ExpandAsync("角色", topK: 5, TestContext.Current.CancellationToken);
        var after = injector.BuildRequestTools();

        Assert.Contains(after, t => t.Name == "check_char");
        Assert.NotSame(before, after);
    }

    [Fact]
    /// <summary>EnsureIndexedAsync 在索引失效后重建，IsIndexed 恢复。</summary>
    public async Task EnsureIndexedAsync_RebuildsAfterInvalidate()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索", [], []), "Core");
        registry.Register(
            new DummyTool("check_char", "检查角色", "角色完整性检查", ["角色"], []),
            "NovelTools"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);
        var injector = new ToolInjector(registry, engine);
        Assert.True(engine.IsIndexed);

        engine.InvalidateIndex();
        Assert.False(engine.IsIndexed);

        await injector.EnsureIndexedAsync(TestContext.Current.CancellationToken);
        Assert.True(engine.IsIndexed);

        // 重建后搜索仍命中
        var hits = await engine.SearchAsync("角色", topK: 5, TestContext.Current.CancellationToken);
        Assert.Contains(hits, h => h.ToolName == "check_char");
    }

    [Fact]
    /// <summary>IsToolAvailable 对非延迟注入工具返回 true（注入即可用）。</summary>
    public async Task IsToolAvailable_RegularInjectedTool_ReturnsTrue()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索", [], []), "Core");
        // read_file: DeferLoading=false 且不在延迟插件名单 → 始终注入
        registry.Register(new DummyTool("read_file", "读取文件", "读取文件", [], []), "FileSystem");

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);
        var injector = new ToolInjector(registry, engine);

        Assert.True(injector.IsToolAvailable("read_file"));
        Assert.True(injector.IsToolAvailable("tool_search"));
    }

    [Fact]
    /// <summary>dense 失败冷却：失败后立即再次搜索不再调用 EmbedAsync。</summary>
    public async Task DenseSearchAsync_Failure_EntersCooldown()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索", [], []), "Core");
        registry.Register(
            new DummyTool("check_char", "检查角色", "角色完整性检查", [], []),
            "NovelTools"
        );

        var throwingService = new ThrowingEmbeddingService();
        var engine = new ToolSearchEngine(throwingService);
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);

        // 第一次搜索：dense 失败 → 进入冷却
        var hits1 = await engine.SearchAsync(
            "检查",
            topK: 5,
            TestContext.Current.CancellationToken
        );
        Assert.NotNull(hits1);

        // 第二次搜索：冷却期内 dense 跳过 → EmbedAsync 不被调用
        var callsBefore = throwingService.CallCount;
        var hits2 = await engine.SearchAsync(
            "检查",
            topK: 5,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(callsBefore, throwingService.CallCount); // 冷却期内不再调用
        Assert.NotNull(hits2);
    }

    // ── 辅助类 ──
    [Fact]
    /// <summary>ClearExpanded 后 BuildRequestTools 不再返回旧展开工具（签名缓存显式失效）。</summary>
    public async Task ClearExpanded_InvalidatesBuildRequestToolsCache()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索", [], []), "Core");
        // 注册到默认延迟名单（"Novel Writing Tools"）→ 展开前不注入，ClearExpanded 后消失
        registry.Register(
            new DummyTool("check_char", "检查角色", "角色完整性检查", ["角色"], []),
            "Novel Writing Tools"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);
        // 显式延迟 Novel Writing Tools（默认构造名单为空 → 不过滤插件）
        var injector = new ToolInjector(
            registry,
            engine,
            deferredPluginNames: new HashSet<string>(
                ["Novel Writing Tools"],
                StringComparer.OrdinalIgnoreCase
            )
        );

        // 展开后：BuildRequestTools 缓存含 check_char
        await injector.ExpandAsync("角色", topK: 5, TestContext.Current.CancellationToken);
        var before = injector.BuildRequestTools();
        Assert.Contains(before, t => t.Name == "check_char");

        // 清空：签名缓存显式失效，返回不含 check_char 的新列表
        injector.ClearExpanded();
        var after = injector.BuildRequestTools();
        Assert.DoesNotContain(after, t => t.Name == "check_char");
    }

    [Fact]
    /// <summary>展开工具写入单字典原子化：并发展开无异常且 LRU 上限保持。</summary>
    public async Task ParallelExpand_NoCorruption()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索", [], []), "Core");
        for (var i = 0; i < 30; i++)
        {
            registry.Register(
                new DummyTool($"tool_{i}", $"工具{i}", $"第 {i} 个工具", [], []),
                "NovelTools"
            );
        }

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);
        var injector = new ToolInjector(registry, engine);

        // 并发展开 25 个不同工具
        var tasks = Enumerable
            .Range(0, 25)
            .Select(i =>
                Task.Run(() =>
                {
                    injector.ExpandTool($"tool_{i}");
                })
            );
        await Task.WhenAll(tasks);

        Assert.True(injector.ExpandedTools.Count <= 20); // LRU 上限
        var tools = injector.BuildRequestTools();
        Assert.Contains(tools, t => t.Name == "tool_24"); // 最近展开的保留
    }

    private sealed class DummyTool(
        string name,
        string label,
        string description,
        string[]? aliases = null,
        string[]? examples = null
    ) : AgentTool
    {
        public override string Name => name;
        public override string Label => label;
        public override string Description => description;
        public override string[] Aliases => aliases ?? [];
        public override string[] ExampleQueries => examples ?? [];
        public override System.Text.Json.JsonElement? Parameters => null;

        public override Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            System.Text.Json.JsonElement args,
            CancellationToken ct,
            IProgress<AgentToolResult>? onUpdate = null
        )
        {
            return Task.FromResult(
                new AgentToolResult { Content = [new TextContent($"ok: {name}")] }
            );
        }
    }

    /// <summary>测试用 embedding 服务：EmbedAsync 始终抛异常，统计调用次数。</summary>
    private sealed class ThrowingEmbeddingService : IEmbeddingService
    {
        private int _callCount;

        public int Dimension => VectorDefaults.Dimension;
        public int CallCount => Volatile.Read(ref _callCount);

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromException<float[]>(
                new InvalidOperationException("embedding unavailable")
            );
        }
    }
}
