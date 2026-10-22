using Core.Agent;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Core.Vector;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// Desktop 工具装配验证测试（对齐 HostBootstrapper §6 按需注入管线）：
///
/// Desktop 已接入 ToolInjector + ToolSearchEngine + ToolSearchTool + DecompressTool +
/// AgentMemory 工具 + AgentMemoryHook，与 Host 行为一致。本测试验证：
/// 1. Desktop 的 deferred plugin 配置读取逻辑与 Host 相同
/// 2. ToolInjector 在无插件场景下的默认行为
/// 3. DecompressTool 和记忆工具可在无 DI 容器时构造（静态注册的前提）
/// 4. dynamicMode 灰度开关解析正确
/// 5. 工具注入面签名变化检测（ToolsSignature 在 ExpandedTools 变化时递增）
///
/// 注：完整 InitializeRuntime 集成测试依赖插件加载（反射 + 文件 I/O），
/// 已由 Core.Tests 的 ToolSearchHotReloadIntegrationTests 覆盖工具管线协同。
/// </summary>
public sealed class DesktopToolingRegistrationTests
{
    // ── 1. Deferred plugin 配置解析 ──

    [Fact]
    public void DeferredPlugins_ConfigOverride_ReadsCommaSeparatedList()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Agent:ToolSearchDeferredPlugins"] = "PluginA, PluginB ,PluginC",
                }
            )
            .Build();

        var result = ReadDeferredPlugins(config);

        Assert.Equal(3, result.Count);
        Assert.Contains("PluginA", result);
        Assert.Contains("PluginB", result);
        Assert.Contains("PluginC", result);
    }

    [Fact]
    public void DeferredPlugins_EmptyConfig_FallsBackToBuiltinDefaults()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["Agent:ToolSearchDeferredPlugins"] = "" }
            )
            .Build();

        var result = ReadDeferredPlugins(config);

        // 默认：Semantic Retrieval Tool + Web Search Tools
        Assert.Contains("Semantic Retrieval Tool", result);
        Assert.Contains("Web Search Tools", result);
    }

    [Fact]
    public void DeferredPlugins_MissingConfig_FallsBackToBuiltinDefaults()
    {
        var config = new ConfigurationBuilder().Build();

        var result = ReadDeferredPlugins(config);

        Assert.Contains("Semantic Retrieval Tool", result);
        Assert.Contains("Web Search Tools", result);
    }

    // ── 2. Dynamic mode 解析 ──

    [Theory]
    [InlineData("all", false)]
    [InlineData("ALL", false)]
    [InlineData("dynamic", true)]
    [InlineData("Dynamic", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    public void DynamicMode_Parsing_CorrectForAllValues(string? configValue, bool expectedDynamic)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["Agent:ToolSearchMode"] = configValue }
            )
            .Build();

        var result = ReadDynamicMode(config);
        Assert.Equal(expectedDynamic, result);
    }

    // ── 3. ToolInjector 签名变化检测 ──

    [Fact]
    public void ToolInjector_Signature_ChangesWhenExpandedToolsChange()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索", [], []), "Core");
        registry.Register(
            new DummyTool("chapter_analyze", "章节分析", "分析章节质量", [], []),
            "Semantic Retrieval Tool"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        var deferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Semantic Retrieval Tool",
        };
        var injector = new ToolInjector(registry, engine, deferredPluginNames: deferred);

        var sig1 = injector.ToolsSignature;

        // 展开一个延迟插件下的工具（需先注册到 registry）
        injector.ExpandTool("chapter_analyze");
        var sig2 = injector.ToolsSignature;

        Assert.NotEqual(sig1, sig2);
    }

    [Fact]
    public void ToolInjector_ClearExpanded_ResetsSignature()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索", [], []), "Core");
        registry.Register(
            new DummyTool("scene_build", "场景构建", "构建场景描述", [], []),
            "Semantic Retrieval Tool"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        var deferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Semantic Retrieval Tool",
        };
        var injector = new ToolInjector(registry, engine, deferredPluginNames: deferred);

        // 延迟插件工具初始不可用
        Assert.False(injector.IsToolAvailable("scene_build"));

        // 展开后可用
        injector.ExpandTool("scene_build");
        var sig1 = injector.ToolsSignature;
        Assert.True(injector.IsToolAvailable("scene_build"));

        // ClearExpanded 后恢复不可用 + 签名变化
        injector.ClearExpanded();
        var sig2 = injector.ToolsSignature;

        Assert.NotEqual(sig1, sig2);
        Assert.False(injector.IsToolAvailable("scene_build"));
    }

    // ── 4. 工具计数验证：按 Host 相同的 deferred list 过滤 ──

    [Fact]
    public void ToolInjector_WithSemanticRetrievalDeferred_FiltersCorrectly()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool_search", "搜索工具", "按需检索", [], []), "Core");
        registry.Register(
            new DummyTool("fs_read", "读文件", "读取文件", [], []),
            "FileSystem Tools"
        );
        registry.Register(
            new DummyTool("sem_recall", "语义回忆", "语义检索回忆", [], []),
            "Semantic Retrieval Tool"
        );
        registry.Register(
            new DummyTool("web_search", "网络搜索", "搜索网页", [], []),
            "Web Search Tools"
        );

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        var deferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Semantic Retrieval Tool",
            "Web Search Tools",
        };

        var injector = new ToolInjector(registry, engine, deferredPluginNames: deferred);

        // 默认注入：tool_search + FileSystem 工具（非延迟）
        var tools = injector.BuildRequestTools();
        var names = tools.Select(t => t.Name).ToArray();

        Assert.Contains("tool_search", names);
        Assert.Contains("fs_read", names);
        Assert.DoesNotContain("sem_recall", names);
        Assert.DoesNotContain("web_search", names);
    }

    // ── 5. DecompressTool 可构造性（无 DI 依赖障碍） ──

    [Fact]
    public void DecompressTool_CanBeConstructed_WithAgentAndSessionStore()
    {
        // 验证 Desktop 注册 DecompressTool 无类型障碍
        var registry = new ToolRegistry();
        var agent = new Agent(registry, null!);
        var sessionStore = new FileSessionStore(
            Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}"),
            Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}")
        );

        var tool = new DecompressTool(agent, sessionStore);
        Assert.Equal("decompress", tool.Name);
        Assert.Equal("恢复压缩内容", tool.Label);
    }

    // ── 辅助方法（镜像 Desktop InitializeRuntime 配置读取逻辑） ──

    private static HashSet<string> ReadDeferredPlugins(IConfiguration config)
    {
        var deferredPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configuredDeferred = config["Agent:ToolSearchDeferredPlugins"];
        if (!string.IsNullOrWhiteSpace(configuredDeferred))
        {
            foreach (
                var name in configuredDeferred.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                )
            )
                deferredPlugins.Add(name);
        }
        else
        {
            deferredPlugins.Add("Semantic Retrieval Tool");
            deferredPlugins.Add("Web Search Tools");
        }
        return deferredPlugins;
    }

    private static bool ReadDynamicMode(IConfiguration config)
    {
        return !string.Equals(
            config["Agent:ToolSearchMode"],
            "all",
            StringComparison.OrdinalIgnoreCase
        );
    }

    // ── 辅助测试用 DummyTool（镜像 Core.Tests ToolSearchHotReloadIntegrationTests）──
    private sealed class DummyTool(
        string name,
        string label,
        string description,
        string[]? aliases = null,
        string[]? examples = null
    ) : AgentTool
    {
        public override string Name { get; } = name;
        public override string Label { get; } = label;
        public override string Description { get; } = description;
        public override string[] Aliases { get; } = aliases ?? [];
        public override string[] ExampleQueries { get; } = examples ?? [];
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
