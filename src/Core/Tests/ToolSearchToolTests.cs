using System.Text.Json;
using Core.Shared;
using Core.Tools;
using Core.Vector;
using Xunit;

namespace Core.Tests;

/// <summary>
/// ToolSearchTool 端到端测试：验证"tool_search 命中 → 展开 → 下一轮注入"完整链路。
/// 对应设计文档 §13.2 集成测试的核心语义（不依赖真实 LLM）。
/// </summary>
public class ToolSearchToolTests
{
    [Fact]
    /// <summary>调用 tool_search 后，命中工具应加入下一轮请求工具列表。</summary>
    public async Task ExecuteAsync_ExpandsTools_ForNextRequest()
    {
        // 组装：registry + engine + injector + tool
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索工具", [], []), "Core");
        registry.Register(
            new DummyTool(
                "check_character_completeness",
                "检查角色完整性",
                "对角色执行 20 项完整性检查",
                ["角色完整性", "人物设定检查"],
                ["检查主角林晚的设定是否完整"]
            ),
            "NovelTools"
        );
        registry.Register(
            new DummyTool("create_scene", "创建场景", "创建新场景", [], []),
            "NovelTools"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);
        var injector = new ToolInjector(registry, engine);
        var tool = new ToolSearchTool(engine, injector);

        // 执行 tool_search
        var args = JsonDocument.Parse("""{"query":"检查角色完整性"}""").RootElement;
        var result = await tool.ExecuteAsync("call-1", args, TestContext.Current.CancellationToken);

        // 1. 返回命中工具名
        var details = Assert.IsType<AgentToolResult<ToolSearchDetails>>(result).TypedDetails;
        Assert.NotNull(details);
        Assert.Contains("check_character_completeness", details!.ToolNames);

        // 2. 返回文本包含工具摘要
        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains("check_character_completeness", text);
        Assert.Contains("检查角色完整性", text);

        // 3. 下一轮 BuildRequestTools 包含命中工具
        var nextRequestTools = injector.BuildRequestTools();
        Assert.Contains(nextRequestTools, t => t.Name == "check_character_completeness");
    }

    [Fact]
    /// <summary>空 query 应返回错误结果。</summary>
    public async Task ExecuteAsync_EmptyQuery_ReturnsError()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索工具", [], []), "Core");

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);
        var injector = new ToolInjector(registry, engine);
        var tool = new ToolSearchTool(engine, injector);

        var args = JsonDocument.Parse("""{"query":""}""").RootElement;
        var result = await tool.ExecuteAsync("call-1", args, TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Empty(
            Assert.IsType<AgentToolResult<ToolSearchDetails>>(result).TypedDetails!.ToolNames
        );
    }

    [Fact]
    /// <summary>无匹配 query 应返回空结果且不崩溃。</summary>
    public async Task ExecuteAsync_NoMatch_ReturnsEmpty()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索工具", [], []), "Core");
        registry.Register(
            new DummyTool("create_scene", "创建场景", "创建新场景", [], []),
            "NovelTools"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);
        var injector = new ToolInjector(registry, engine);
        var tool = new ToolSearchTool(engine, injector);

        var args = JsonDocument.Parse("""{"query":"完全不相关的天文物理问题"}""").RootElement;
        var result = await tool.ExecuteAsync("call-1", args, TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        Assert.Empty(
            Assert.IsType<AgentToolResult<ToolSearchDetails>>(result).TypedDetails!.ToolNames
        );
    }

    [Fact]
    /// <summary>TopK 参数应限制返回数量。</summary>
    public async Task ExecuteAsync_TopK_LimitsResults()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索工具", [], []), "Core");
        for (var i = 0; i < 12; i++)
        {
            registry.Register(
                new DummyTool(
                    $"novel_tool_{i}",
                    $"小说工具{i}",
                    $"第{i}个小说创作工具",
                    [$"创作{i}"],
                    []
                ),
                "NovelTools"
            );
        }

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);
        var injector = new ToolInjector(registry, engine);
        var tool = new ToolSearchTool(engine, injector);

        var args = JsonDocument.Parse("""{"query":"小说 创作 工具","top-k":3}""").RootElement;
        var result = await tool.ExecuteAsync("call-1", args, TestContext.Current.CancellationToken);

        var typed = Assert.IsType<AgentToolResult<ToolSearchDetails>>(result).TypedDetails!;
        Assert.True(
            typed.ToolNames.Length <= 3,
            $"ToolNames=[{string.Join(",", typed.ToolNames)}] count={typed.ToolNames.Length}"
        );
    }

    [Fact]
    /// <summary>搜索调用超过阈值应熔断并返回收敛提示。</summary>
    public async Task ExecuteAsync_ExceedsRateLimit_ReturnsTripNotice()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索工具", [], []), "Core");
        registry.Register(
            new DummyTool("create_scene", "创建场景", "创建新场景", [], []),
            "NovelTools"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);
        var injector = new ToolInjector(registry, engine);
        var tool = new ToolSearchTool(engine, injector);

        // 连续调用超过阈值（8 次）
        for (var i = 0; i < 8; i++)
        {
            var args = JsonDocument.Parse("""{"query":"场景"}""").RootElement;
            await tool.ExecuteAsync($"call-{i}", args, TestContext.Current.CancellationToken);
        }

        // 第 9 次熔断
        var tripArgs = JsonDocument.Parse("""{"query":"场景"}""").RootElement;
        var result = await tool.ExecuteAsync(
            "call-9",
            tripArgs,
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains("频繁", text);
        Assert.Empty(
            Assert.IsType<AgentToolResult<ToolSearchDetails>>(result).TypedDetails!.ToolNames
        );

        // 会话切换重置后可再次搜索
        tool.ResetSearchTrip();
        var resetArgs = JsonDocument.Parse("""{"query":"创建场景"}""").RootElement;
        var afterReset = await tool.ExecuteAsync(
            "call-10",
            resetArgs,
            TestContext.Current.CancellationToken
        );
        Assert.Contains(
            "create_scene",
            Assert.IsType<AgentToolResult<ToolSearchDetails>>(afterReset).TypedDetails!.ToolNames
        );
    }

    // ── 辅助类 ──
    [Fact]
    /// <summary>LRU 淘汰展开工具后，返回文本仍含参数提示（来自索引文档而非 ExpandedTools）。</summary>
    public async Task ExecuteAsync_EvictedTool_StillShowsParamNames()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索工具", [], []), "Core");
        var schema = JsonDocument
            .Parse(
                """{"type":"object","properties":{"novel-id":{"type":"string"},"character-id":{"type":"string"}}}"""
            )
            .RootElement;
        registry.Register(
            new DummyTool(
                "check_character_completeness",
                "检查角色完整性",
                "对角色执行 20 项完整性检查",
                ["角色完整性", "人物设定检查"],
                ["检查主角林晚的设定是否完整"],
                schema
            ),
            "NovelTools"
        );
        // 再注册 21 个工具把上面的挤出 LRU（MaxExpandedTools=20）
        for (var i = 0; i < 21; i++)
        {
            registry.Register(
                new DummyTool($"evict_tool_{i}", $"工具{i}", $"第 {i} 个工具", [], []),
                "NovelTools"
            );
        }

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);
        var injector = new ToolInjector(registry, engine);
        var tool = new ToolSearchTool(engine, injector);

        // 先展开 21 个 evict 工具占满 LRU
        var args = JsonDocument.Parse("""{"query":"工具"}""").RootElement;
        var result = await tool.ExecuteAsync("call-1", args, TestContext.Current.CancellationToken);
        Assert.False(result.IsError);

        // 再搜 check_character_completeness——即使它随后被 evict，返回文本仍应有参数名
        var args2 = JsonDocument.Parse("""{"query":"检查角色完整性"}""").RootElement;
        var result2 = await tool.ExecuteAsync(
            "call-2",
            args2,
            TestContext.Current.CancellationToken
        );
        Assert.False(result2.IsError);

        var text = Assert.IsType<TextContent>(Assert.Single(result2.Content)).Text;
        // 即使工具不在 ExpandedTools（LRU 淘汰），参数提示仍来自索引文档 ParamNames
        Assert.Contains("check_character_completeness", text);
        Assert.Contains("[参数] novel-id, character-id", text);
    }

    [Fact]
    /// <summary>rateLimit 参数生效：超过阈值触发熔断。</summary>
    public async Task ExecuteAsync_RateLimit_TriggersTrip()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索工具", [], []), "Core");
        registry.Register(
            new DummyTool("create_scene", "创建场景", "创建新场景", [], []),
            "NovelTools"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);
        var injector = new ToolInjector(registry, engine);
        var tool = new ToolSearchTool(engine, injector, rateLimit: 2);

        var args = JsonDocument.Parse("""{"query":"创建场景"}""").RootElement;

        // 前 2 次正常
        var r1 = await tool.ExecuteAsync("call-1", args, TestContext.Current.CancellationToken);
        Assert.False(r1.IsError);
        var r2 = await tool.ExecuteAsync("call-2", args, TestContext.Current.CancellationToken);
        Assert.False(r2.IsError);

        // 第 3 次（count=3 > 2）触发熔断：返回收敛提示，Content 不含工具名
        var r3 = await tool.ExecuteAsync("call-3", args, TestContext.Current.CancellationToken);
        Assert.False(r3.IsError);
        var text = Assert.IsType<TextContent>(Assert.Single(r3.Content)).Text;
        Assert.DoesNotContain("create_scene", text);
        Assert.Contains("搜索调用过于频繁", text);

        // 会话重置后恢复
        tool.ResetSearchTrip();
        var r4 = await tool.ExecuteAsync("call-4", args, TestContext.Current.CancellationToken);
        Assert.False(r4.IsError);
        var text4 = Assert.IsType<TextContent>(Assert.Single(r4.Content)).Text;
        Assert.Contains("create_scene", text4);
    }

    private sealed class DummyTool(
        string name,
        string label,
        string description,
        string[]? aliases = null,
        string[]? examples = null,
        JsonElement? parameters = null
    ) : AgentTool
    {
        public override string Name => name;
        public override string Label => label;
        public override string Description => description;
        public override string[] Aliases => aliases ?? [];
        public override string[] ExampleQueries => examples ?? [];
        public override JsonElement? Parameters => parameters;

        public override Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            JsonElement args,
            CancellationToken ct,
            IProgress<AgentToolResult>? onUpdate = null
        )
        {
            return Task.FromResult(
                new AgentToolResult { Content = [new TextContent($"ok: {name}")] }
            );
        }
    }
}
