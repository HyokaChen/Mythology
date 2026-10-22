using Core.Shared;
using Core.Tools;
using Core.Vector;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 热加载全链路集成测试：注册插件 → 构建索引 → 搜索命中 → 卸载插件 →
/// 索引失效 → 惰性重建 → 已卸载工具消失 / 新工具可被发现。
/// 覆盖 ToolSearchEngine + ToolInjector + ToolRegistry 三者的协同（不含 PluginLoader 反射加载）。
/// </summary>
public class ToolSearchHotReloadIntegrationTests
{
    [Fact]
    /// <summary>卸载插件后重新搜索，已卸载插件的工具不再出现。</summary>
    public async Task PluginUnload_InvalidatesIndex_UnloadedToolsDisappearFromSearch()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索", [], []), "Core");
        registry.Register(
            new DummyTool("char_completeness", "角色完整性", "检查角色完整性", ["完整性"], []),
            "Plugin A"
        );
        registry.Register(
            new DummyTool("plot_check", "剧情检查", "检查剧情一致性", ["剧情"], []),
            "Plugin B"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);
        var injector = new ToolInjector(registry, engine);

        // 卸载前：两个插件的工具都可被搜索到
        var before = await injector.ExpandAsync(
            "完整性",
            topK: 5,
            TestContext.Current.CancellationToken
        );
        Assert.Contains("char_completeness", before);

        // 卸载 Plugin B：从 registry 移除 + 索引失效
        registry.UnregisterByPlugin("Plugin B");
        injector.InvalidateIndex();

        // 再次搜索：引擎惰性重建索引（只含 Plugin A + Core 工具）
        var after = await injector.ExpandAsync(
            "剧情",
            topK: 5,
            TestContext.Current.CancellationToken
        );
        Assert.DoesNotContain("plot_check", after);
        Assert.True(engine.IsIndexed); // 重建完成
    }

    [Fact]
    /// <summary>插件重载（卸载+重新注册）后新工具可被搜索发现。</summary>
    public async Task PluginReload_NewToolsAppearInSearch()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索", [], []), "Core");
        registry.Register(
            new DummyTool("old_tool", "旧工具", "旧工具描述", ["旧"], []),
            "Plugin C"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);
        var injector = new ToolInjector(registry, engine);

        // 卸载旧插件，注册新版本（工具名变化）
        registry.UnregisterByPlugin("Plugin C");
        injector.InvalidateIndex();
        registry.Register(
            new DummyTool("new_tool", "新工具", "新工具描述", ["新"], []),
            "Plugin C"
        );

        // 搜索新工具名 → 惰性重建后应命中
        var hits = await injector.ExpandAsync(
            "新工具",
            topK: 5,
            TestContext.Current.CancellationToken
        );
        Assert.Contains("new_tool", hits);
        Assert.DoesNotContain("old_tool", hits);
    }

    [Fact]
    /// <summary>卸载插件后 BuildRequestTools 不再注入已卸载工具。</summary>
    public async Task PluginUnload_RemovedFromBuildRequestTools()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索", [], []), "Core");
        registry.Register(
            new DummyTool("deferred_tool", "延迟工具", "仅搜索发现", [], []),
            "Deferred Plugin"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);
        var injector = new ToolInjector(
            registry,
            engine,
            deferredPluginNames: new HashSet<string>(
                ["Deferred Plugin"],
                StringComparer.OrdinalIgnoreCase
            )
        );

        // 展开延迟工具
        var expanded = await injector.ExpandAsync(
            "延迟工具",
            topK: 5,
            TestContext.Current.CancellationToken
        );
        Assert.Contains("deferred_tool", expanded);
        Assert.True(injector.IsToolAvailable("deferred_tool"));

        // 卸载插件（模拟）：工具从 registry 移除，但仍在 ExpandedTools 缓存中
        registry.UnregisterByPlugin("Deferred Plugin");
        injector.InvalidateIndex();

        // BuildRequestTools 应从 registry 查不到已卸载工具 → 常驻 + 已展开（TryGet 过滤掉不存在的）
        var names = injector.BuildRequestTools().Select(t => t.Name).ToArray();
        Assert.DoesNotContain("deferred_tool", names);
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
}
