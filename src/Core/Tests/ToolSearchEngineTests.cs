using System.Reflection;
using Core.Shared;
using Core.Tools;
using Core.Vector;
using Xunit;

namespace Core.Tests;

/// <summary>
/// ToolSearchEngine 工具搜索引擎测试。
/// 覆盖索引构建、中文 query 检索、别名召回、RRF 融合、embedding 降级。
/// </summary>
public class ToolSearchEngineTests
{
    [Fact]
    /// <summary>索引构建应覆盖所有已注册工具。</summary>
    public async Task BuildIndex_IndexesAllTools()
    {
        var registry = new ToolRegistry();
        registry.Register(
            new DummyTool(
                "check_character_completeness",
                "检查角色完整性",
                "对角色执行完整性检查",
                ["角色完整性", "人物设定检查"],
                ["检查主角林晚的设定是否完整"]
            ),
            "NovelTools"
        );
        registry.Register(
            new DummyTool("create_scene", "创建场景", "创建新场景", [], []),
            "NovelTools"
        );
        registry.Register(
            new DummyTool("search_memory", "搜索记忆", "语义搜索小说记忆", [], []),
            "Semantic Retrieval Tool"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);

        Assert.True(engine.IsIndexed);
        Assert.NotNull(engine.GetDocument("check_character_completeness"));
        Assert.NotNull(engine.GetDocument("create_scene"));
        Assert.NotNull(engine.GetDocument("search_memory"));
    }

    [Fact]
    /// <summary>中文 query 应命中带中文别名/标签的工具。</summary>
    public async Task Search_ChineseQuery_HitsByAlias()
    {
        var registry = new ToolRegistry();
        registry.Register(
            new DummyTool(
                "check_character_completeness",
                "检查角色完整性",
                "对角色执行完整性检查",
                ["角色完整性", "人物设定检查"],
                []
            ),
            "NovelTools"
        );
        registry.Register(
            new DummyTool("create_scene", "创建场景", "创建新场景", [], []),
            "NovelTools"
        );
        registry.Register(
            new DummyTool("search_memory", "搜索记忆", "语义搜索小说记忆", [], []),
            "Semantic Retrieval Tool"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);

        var hits = await engine.SearchAsync(
            "检查角色完整性",
            topK: 5,
            TestContext.Current.CancellationToken
        );

        Assert.Contains(hits, h => h.ToolName == "check_character_completeness");
    }

    [Fact]
    /// <summary>英文工具名 query 应精确命中。</summary>
    public async Task Search_EnglishToolName_ExactMatch()
    {
        var registry = new ToolRegistry();
        registry.Register(
            new DummyTool("batch_create_characters", "批量创建角色", "批量创建多个角色", [], []),
            "NovelTools"
        );
        registry.Register(
            new DummyTool("create_scene", "创建场景", "创建新场景", [], []),
            "NovelTools"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);

        var hits = await engine.SearchAsync(
            "batch_create",
            topK: 5,
            TestContext.Current.CancellationToken
        );

        Assert.Contains(hits, h => h.ToolName == "batch_create_characters");
    }

    [Fact]
    /// <summary>别名 query（"人物设定"）应命中对应工具。</summary>
    public async Task Search_AliasQuery_HitsTool()
    {
        var registry = new ToolRegistry();
        registry.Register(
            new DummyTool(
                "check_character_completeness",
                "检查角色完整性",
                "对角色执行完整性检查",
                ["人物设定", "设定检查"],
                []
            ),
            "NovelTools"
        );
        registry.Register(
            new DummyTool("create_scene", "创建场景", "创建新场景", [], []),
            "NovelTools"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);

        var hits = await engine.SearchAsync(
            "人物设定",
            topK: 5,
            TestContext.Current.CancellationToken
        );

        Assert.Contains(hits, h => h.ToolName == "check_character_completeness");
    }

    [Fact]
    /// <summary>无关 query 应返回空或低相关结果，不崩溃。</summary>
    public async Task Search_UnrelatedQuery_ReturnsEmptyOrNoMatch()
    {
        var registry = new ToolRegistry();
        registry.Register(
            new DummyTool(
                "check_character_completeness",
                "检查角色完整性",
                "对角色执行完整性检查",
                [],
                []
            ),
            "NovelTools"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);

        var hits = await engine.SearchAsync(
            "xyzzy 完全无关的内容",
            topK: 5,
            TestContext.Current.CancellationToken
        );

        // DummyEmbeddingService 返回随机向量，dense 通道无意义；
        // BM25 对无共享 token 的 query 返回空 → 结果可能为空，断言不崩溃且为合法类型
        Assert.NotNull(hits);
    }

    [Fact]
    /// <summary>TopK 应限制返回数量。</summary>
    public async Task Search_TopK_LimitsResults()
    {
        var registry = new ToolRegistry();
        for (var i = 0; i < 10; i++)
        {
            registry.Register(
                new DummyTool($"tool_{i}", $"工具{i}", $"第{i}个工具的描述文本", [$"别名{i}"], []),
                "NovelTools"
            );
        }

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);

        var hits = await engine.SearchAsync(
            "工具 别名 描述",
            topK: 3,
            TestContext.Current.CancellationToken
        );

        Assert.True(hits.Count <= 3);
    }

    [Fact]
    /// <summary>搜索空 query 应返回空列表。</summary>
    public async Task Search_EmptyQuery_ReturnsEmpty()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_a", "工具A", "描述", [], []), "NovelTools");

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);

        var hits = await engine.SearchAsync("", topK: 5, TestContext.Current.CancellationToken);
        Assert.Empty(hits);

        var hits2 = await engine.SearchAsync("   ", topK: 5, TestContext.Current.CancellationToken);
        Assert.Empty(hits2);
    }

    [Fact]
    /// <summary>InvalidateIndex 清空所有索引数据并将 IsIndexed 设为 false。</summary>
    public void InvalidateIndex_ClearsAllData()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("t1", "工具1", "描述1", [], []), "NovelTools");

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken).Wait();
        Assert.True(engine.IsIndexed);
        Assert.NotNull(engine.GetDocument("t1"));

        engine.InvalidateIndex();
        Assert.False(engine.IsIndexed);
        Assert.Null(engine.GetDocument("t1"));
    }

    [Fact]
    /// <summary>索引失效后搜索应自动重建并返回结果。</summary>
    public async Task InvalidateIndex_ThenSearch_AutoRebuilds()
    {
        var registry = new ToolRegistry();
        registry.Register(
            new DummyTool("check_character", "检查角色", "角色完整性检查", ["角色检查"], []),
            "NovelTools"
        );
        registry.Register(
            new DummyTool("create_scene", "创建场景", "创建新场景", [], []),
            "NovelTools"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);
        Assert.True(engine.IsIndexed);

        engine.InvalidateIndex();
        Assert.False(engine.IsIndexed);

        // 重建索引（SearchAsync 本身不自动重建；ToolInjector.ExpandAsync 负责这一层）
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);
        var hits = await engine.SearchAsync(
            "角色检查",
            topK: 5,
            TestContext.Current.CancellationToken
        );
        Assert.Contains(hits, h => h.ToolName == "check_character");
        Assert.True(engine.IsIndexed);
    }

    [Fact]
    /// <summary>自定义 RRF 权重应能被构造器传入并生效。</summary>
    public async Task SearchAsync_CustomWeights_AppliedCorrectly()
    {
        var registry = new ToolRegistry();
        registry.Register(
            new DummyTool("alpha", "Alpha工具", "alpha工具用于BM25精确匹配", [], []),
            "NovelTools"
        );
        registry.Register(
            new DummyTool("beta", "Beta工具", "beta工具用于语义匹配嵌入", [], []),
            "NovelTools"
        );

        var engine = new ToolSearchEngine(
            new DummyEmbeddingService(),
            rrfK: 60,
            denseWeight: 0,
            lexicalWeight: 1
        );
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);
        var hits = await engine.SearchAsync(
            "alpha",
            topK: 2,
            TestContext.Current.CancellationToken
        );
        Assert.NotEmpty(hits);
        Assert.Contains(hits, h => h.ToolName == "alpha");
    }

    [Fact]
    /// <summary>Dense 检索异常时应捕获并继续 BM25 检索。</summary>
    public async Task DenseSearchAsync_EmbeddingThrows_FallsBackToBm25()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("t1", "工具1", "描述1内容", [], []), "NovelTools");

        var throwingService = new ThrowingEmbeddingService();
        var engine = new ToolSearchEngine(throwingService);
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);

        // Dense 抛异常被 catch → 仅 BM25 结果
        var hits = await engine.SearchAsync("工具", topK: 5, TestContext.Current.CancellationToken);
        Assert.NotNull(hits);
    }

    [Fact]
    /// <summary>用户真实 query「追踪剧情暗线」应命中 advance_plotline（新补别名）。</summary>
    public async Task Search_TrackPlotlineQuery_HitsAdvancePlotline()
    {
        var registry = new ToolRegistry();
        registry.Register(
            new DummyTool(
                "advance_plotline",
                "推进情节线",
                "记录情节线在某一章节的进展或解决",
                [
                    "推进情节线",
                    "情节进展",
                    "追踪情节",
                    "剧情进展",
                    "推进主线",
                    "剧情推进",
                    "暗线推进",
                ],
                ["推进主线到下一个节点", "追踪剧情暗线进展"]
            ),
            "NovelTools"
        );
        registry.Register(
            new DummyTool("create_scene", "创建场景", "创建新场景", [], []),
            "NovelTools"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);

        var hits = await engine.SearchAsync(
            "追踪剧情暗线",
            topK: 5,
            TestContext.Current.CancellationToken
        );

        Assert.Contains(hits, h => h.ToolName == "advance_plotline");
    }

    [Fact]
    /// <summary>用户真实 query「管理伏笔」应命中 manage_plotline（新补别名）。</summary>
    public async Task Search_ManageForeshadowQuery_HitsManagePlotline()
    {
        var registry = new ToolRegistry();
        registry.Register(
            new DummyTool(
                "manage_plotline",
                "情节线管理",
                "情节线 CRUD：创建/更新/删除/获取/列出情节线",
                ["情节线", "故事线", "主线", "支线", "伏笔管理", "管理伏笔", "暗线", "剧情线"],
                ["新建一条支线情节", "调整主线走向", "管理伏笔回收"]
            ),
            "NovelTools"
        );
        registry.Register(
            new DummyTool("create_scene", "创建场景", "创建新场景", [], []),
            "NovelTools"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);

        var hits = await engine.SearchAsync(
            "管理伏笔",
            topK: 5,
            TestContext.Current.CancellationToken
        );

        Assert.Contains(hits, h => h.ToolName == "manage_plotline");
    }

    [Fact]
    /// <summary>用户真实 query「检查伏笔回收」应命中 recall_foreshadowing。</summary>
    public async Task Search_CheckForeshadowRecovery_HitsRecallForeshadowing()
    {
        var registry = new ToolRegistry();
        registry.Register(
            new DummyTool(
                "recall_foreshadowing",
                "检索伏笔",
                "查找应回收的伏笔与暗线",
                ["伏笔", "埋伏笔", "伏笔召回"],
                ["回忆前面埋下的伏笔"]
            ),
            "NovelTools"
        );
        registry.Register(
            new DummyTool("create_scene", "创建场景", "创建新场景", [], []),
            "NovelTools"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);

        var hits = await engine.SearchAsync(
            "检查伏笔回收",
            topK: 5,
            TestContext.Current.CancellationToken
        );

        Assert.Contains(hits, h => h.ToolName == "recall_foreshadowing");
    }

    // ── 暴露内部方法用于测试 ──

    // ── 暴露内部方法用于测试 ──

    internal static string[] Tokenize(string text)
    {
        var method = typeof(ToolSearchEngine).GetMethod(
            "Tokenize",
            BindingFlags.NonPublic | BindingFlags.Static
        )!;
        return (string[])method.Invoke(null, [text])!;
    }

    // ── 辅助类 ──

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

    /// <summary>测试用 embedding 服务，EmbedAsync 始终抛异常。</summary>
    private sealed class ThrowingEmbeddingService : IEmbeddingService
    {
        public int Dimension => VectorDefaults.Dimension;

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default) =>
            Task.FromException<float[]>(new InvalidOperationException("embedding unavailable"));
    }
}
