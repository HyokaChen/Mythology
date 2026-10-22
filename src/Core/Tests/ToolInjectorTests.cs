using Core.Shared;
using Core.Tools;
using Core.Vector;
using Xunit;

namespace Core.Tests;

/// <summary>
/// ToolInjector 工具注入器测试。
/// 覆盖常驻工具、展开缓存、LRU 淘汰、清空、BuildRequestTools 组合。
/// </summary>
public class ToolInjectorTests
{
    [Fact]
    /// <summary>BuildRequestTools 应包含常驻工具与非延迟加载工具。</summary>
    public async Task BuildRequestTools_IncludesAlwaysLoadedAndRegular()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索工具", [], []), "Core");
        registry.Register(
            new DummyTool("ask_user", "询问用户", "向用户提问", [], []),
            "FileSystem"
        );
        registry.Register(
            new DummyTool("todo_write", "任务追踪", "任务清单", [], []),
            "FileSystem"
        );
        registry.Register(
            new DummyTool("read_file", "读取文件", "读取文件内容", [], []),
            "FileSystem"
        );
        registry.Register(
            new DummyTool("deferred_tool", "延迟工具", "仅通过搜索发现", [], []) { Defer = true },
            "NovelTools"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);
        var injector = new ToolInjector(registry, engine);

        var tools = injector.BuildRequestTools();
        var names = tools.Select(t => t.Name).ToHashSet();

        // 常驻工具
        Assert.Contains("tool_search", names);
        Assert.Contains("ask_user", names);
        Assert.Contains("todo_write", names);

        // 非延迟加载工具全量注入（向后兼容）
        Assert.Contains("read_file", names);

        // 延迟加载工具不预注入
        Assert.DoesNotContain("deferred_tool", names);
    }

    [Fact]
    /// <summary>ExpandAsync 后命中工具应出现在 BuildRequestTools 中。</summary>
    public async Task ExpandAsync_AddsHitToolsToRequest()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索工具", [], []), "Core");
        registry.Register(
            new DummyTool(
                "check_character_completeness",
                "检查角色完整性",
                "对角色执行完整性检查",
                ["角色完整性"],
                []
            ),
            "NovelTools"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);
        var injector = new ToolInjector(registry, engine);

        var expanded = await injector.ExpandAsync(
            "检查角色完整性",
            topK: 5,
            TestContext.Current.CancellationToken
        );

        Assert.Contains("check_character_completeness", expanded);
        Assert.Contains(
            "check_character_completeness",
            injector.BuildRequestTools().Select(t => t.Name)
        );
    }

    [Fact]
    /// <summary>ExpandTool 手动展开指定工具。</summary>
    public void ExpandTool_AddsSpecifiedTool()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索工具", [], []), "Core");
        registry.Register(new DummyTool("my_tool", "我的工具", "描述", [], []), "NovelTools");

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        var injector = new ToolInjector(registry, engine);

        injector.ExpandTool("my_tool");

        Assert.True(injector.IsToolAvailable("my_tool"));
        Assert.Contains("my_tool", injector.BuildRequestTools().Select(t => t.Name));
    }

    [Fact]
    /// <summary>ClearExpanded 应清空展开缓存。</summary>
    public async Task ClearExpanded_RemovesExpandedTools()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索工具", [], []), "Core");
        registry.Register(new DummyTool("my_tool", "我的工具", "描述", [], []), "NovelTools");

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);
        var injector = new ToolInjector(registry, engine);

        await injector.ExpandAsync("我的工具", topK: 5, TestContext.Current.CancellationToken);
        Assert.True(injector.ExpandedTools.ContainsKey("my_tool"));

        injector.ClearExpanded();
        Assert.False(injector.ExpandedTools.ContainsKey("my_tool"));
    }

    [Fact]
    /// <summary>展开超过上限时按 LRU 淘汰最久未用的工具。</summary>
    public async Task ExpandBeyondLimit_EvictsOldest()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索工具", [], []), "Core");

        // 注册 25 个工具（上限 20），全部可被展开
        for (var i = 0; i < 25; i++)
        {
            registry.Register(
                new DummyTool(
                    $"evict_tool_{i}",
                    $"淘汰工具{i}",
                    $"用于 LRU 测试的工具{i}",
                    [$"淘汰{i}"],
                    []
                ),
                "NovelTools"
            );
        }

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);
        var injector = new ToolInjector(registry, engine);

        // 逐个展开
        for (var i = 0; i < 25; i++)
        {
            injector.ExpandTool($"evict_tool_{i}");
        }

        // 上限 20，最早的 5 个应从展开缓存淘汰（工具本身仍是非延迟注入，IsToolAvailable 恒 true）
        Assert.Equal(20, injector.ExpandedTools.Count);
        Assert.False(injector.ExpandedTools.ContainsKey("evict_tool_0"));
        Assert.True(injector.ExpandedTools.ContainsKey("evict_tool_24"));
    }

    [Fact]
    /// <summary>延迟插件名单中的工具不预注入，仅通过 tool_search 发现。</summary>
    public async Task BuildRequestTools_DeferredPlugin_ToolsNotInjected()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索工具", [], []), "Core");
        registry.Register(
            new DummyTool("read_file", "读取文件", "读取文件内容", [], []),
            "FileSystem"
        );
        registry.Register(
            new DummyTool("manage_character", "管理角色", "创建更新角色", [], []),
            "NovelTools"
        );
        registry.Register(
            new DummyTool("enhance_immersion", "增强沉浸", "沉浸感增强", [], []),
            "WritingTools"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);

        // 延迟名单：NovelTools + WritingTools
        var deferred = new HashSet<string>(
            ["NovelTools", "WritingTools"],
            StringComparer.OrdinalIgnoreCase
        );
        var injector = new ToolInjector(registry, engine, deferredPluginNames: deferred);

        var tools = injector.BuildRequestTools();
        var names = tools.Select(t => t.Name).ToHashSet();

        // 常驻 + FileSystem 工具注入
        Assert.Contains("tool_search", names);
        Assert.Contains("read_file", names);

        // 延迟插件工具不注入
        Assert.DoesNotContain("manage_character", names);
        Assert.DoesNotContain("enhance_immersion", names);

        // tool_search 命中后展开注入
        var expanded = await injector.ExpandAsync(
            "管理角色",
            topK: 5,
            TestContext.Current.CancellationToken
        );
        Assert.Contains("manage_character", expanded);
        Assert.Contains("manage_character", injector.BuildRequestTools().Select(t => t.Name));
    }

    [Fact]
    /// <summary>灰度开关 dynamicMode=false 时延迟插件工具也全量注入。</summary>
    public async Task BuildRequestTools_DynamicModeOff_InjectsAll()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索工具", [], []), "Core");
        registry.Register(
            new DummyTool("read_file", "读取文件", "读取文件内容", [], []),
            "FileSystem"
        );
        registry.Register(
            new DummyTool("manage_character", "管理角色", "创建更新角色", [], []),
            "Novel Writing Tools"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);

        // dynamicMode=false（全量模式）
        var deferred = new HashSet<string>(
            ["Novel Writing Tools"],
            StringComparer.OrdinalIgnoreCase
        );
        var injector = new ToolInjector(
            registry,
            engine,
            deferredPluginNames: deferred,
            dynamicMode: false
        );

        var names = injector.BuildRequestTools().Select(t => t.Name).ToHashSet();

        // 延迟插件工具也注入
        Assert.Contains("manage_character", names);
        Assert.Contains("read_file", names);
    }

    [Fact]
    /// <summary>注册表版本变更时 BuildRequestTools 应重建缓存，新工具出现在结果中。</summary>
    public async Task BuildRequestTools_RegistryVersionChanged_IncludesNewTools()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("t1", "工具1", "描述1", [], []), "P1");
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索工具", [], []), "Core");

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);
        var injector = new ToolInjector(registry, engine);

        var names1 = injector.BuildRequestTools().Select(t => t.Name).ToHashSet();
        Assert.Contains("t1", names1);

        // 注册新工具（版本递增）
        registry.Register(new DummyTool("t2", "工具2", "描述2", [], []), "P1");

        // 再次调用 BuildRequestTools，应自动发现新工具
        var names2 = injector.BuildRequestTools().Select(t => t.Name).ToHashSet();
        Assert.Contains("t2", names2);
    }

    [Fact]
    /// <summary>InvalidateIndex 通过 ToolInjector 调用应清空搜索索引。</summary>
    public void InvalidateIndex_PropagatesToSearchEngine()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("t1", "工具1", "描述1", [], []), "NovelTools");
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索工具", [], []), "Core");

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken).Wait();
        Assert.True(engine.IsIndexed);

        var injector = new ToolInjector(registry, engine);
        injector.InvalidateIndex();

        Assert.False(engine.IsIndexed);
    }

    [Fact]
    /// <summary>插件卸载后 ToolInjector 缓存应保留，但搜索索引被标记为失效。</summary>
    public async Task InvalidateIndex_AfterUnregister_CacheStaleButIndexRebuilds()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("t1", "工具1", "描述1", [], []), "NovelTools");
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索工具", [], []), "Core");

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);
        var injector = new ToolInjector(registry, engine);

        // 展开 t1
        await injector.ExpandAsync("工具1", topK: 5, TestContext.Current.CancellationToken);
        Assert.True(injector.IsToolAvailable("t1"));

        // 模拟插件卸载：从 registry 移除 + 使索引失效
        registry.UnregisterByPlugin("NovelTools");
        injector.InvalidateIndex();

        // 搜索应自动重建，但 t1 已不在 registry → 不会出现在 ExpandAsync 命中
        Assert.False(engine.IsIndexed);
    }

    [Fact]
    /// <summary>BuildToolDirectory 只列未注入插件（延迟插件），含插件名/数量/代表工具。</summary>
    public async Task BuildToolDirectory_ListsOnlyDeferredPlugins()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索", [], []), "Core");
        registry.Register(
            new DummyTool("read_file", "读取文件", "读取文件内容", [], []),
            "FileSystem Tools"
        );
        registry.Register(
            new DummyTool(
                "check_char",
                "检查角色完整性",
                "角色完整性检查",
                ["角色"],
                ["检查主角设定"]
            ),
            "Novel Writing Tools"
        );
        registry.Register(
            new DummyTool("search_memory", "搜索记忆", "语义搜索", [], []),
            "Semantic Retrieval Tool"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);
        // 显式延迟名单：Novel Writing Tools + Semantic Retrieval Tool（FileSystem/Core 常驻）
        var injector = new ToolInjector(
            registry,
            engine,
            deferredPluginNames: new HashSet<string>(
                ["Novel Writing Tools", "Semantic Retrieval Tool"],
                StringComparer.OrdinalIgnoreCase
            )
        );

        var dir = injector.BuildToolDirectory();

        // 含延迟插件名 + 工具数 + 代表工具
        Assert.Contains("Novel Writing Tools", dir);
        Assert.Contains("check_char", dir);
        Assert.Contains("Semantic Retrieval Tool", dir);
        Assert.Contains("search_memory", dir);
        // 不含已注入工具（FileSystem/Core 常驻，tool_search 仅出现在说明文字）
        Assert.DoesNotContain("read_file", dir);
        Assert.DoesNotContain("tool_search（", dir); // 工具列表行不含 tool_search 条目
    }

    [Fact]
    /// <summary>BuildToolDirectory 在插件卸载后自动更新（目录 = 实际加载集合）。</summary>
    public async Task BuildToolDirectory_UpdatesAfterPluginUnload()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索", [], []), "Core");
        registry.Register(
            new DummyTool("check_char", "检查角色完整性", "角色完整性检查", ["角色"], []),
            "Novel Writing Tools"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);
        var injector = new ToolInjector(
            registry,
            engine,
            deferredPluginNames: new HashSet<string>(
                ["Novel Writing Tools"],
                StringComparer.OrdinalIgnoreCase
            )
        );

        var before = injector.BuildToolDirectory();
        Assert.Contains("Novel Writing Tools", before);

        // 模拟插件卸载
        registry.UnregisterByPlugin("Novel Writing Tools");
        injector.InvalidateIndex();
        var after = injector.BuildToolDirectory();
        Assert.DoesNotContain("Novel Writing Tools", after);
    }

    [Fact]
    /// <summary>全量模式（dynamicMode=false）下无隐藏工具 → 目录为空。</summary>
    public async Task BuildToolDirectory_FullMode_ReturnsEmpty()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索", [], []), "Core");
        registry.Register(
            new DummyTool("check_char", "检查角色完整性", "角色完整性检查", ["角色"], []),
            "Novel Writing Tools"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        await engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken);
        // dynamicMode=false = 全量注入（非 DeferLoading 全部可见）
        var injector = new ToolInjector(
            registry,
            engine,
            deferredPluginNames: new HashSet<string>(
                ["Novel Writing Tools"],
                StringComparer.OrdinalIgnoreCase
            ),
            dynamicMode: false
        );

        var dir = injector.BuildToolDirectory();
        Assert.Equal("", dir);
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
        public bool Defer { get; init; }
        public override string Name => name;
        public override string Label => label;
        public override string Description => description;
        public override string[] Aliases => aliases ?? [];
        public override string[] ExampleQueries => examples ?? [];
        public override bool DeferLoading => Defer;
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
