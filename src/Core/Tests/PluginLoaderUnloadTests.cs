using System.Reflection;
using Core.Plugins;
using Core.Shared;
using Core.Tools;
using Core.Vector;
using Xunit;

namespace Core.Tests;

/// <summary>
/// PluginLoader.UnloadPlugin → OnPluginUnloaded 回调链路测试。
/// 通过反射注入 _loaded 条目（真实插件加载机制由生产代码覆盖），
/// 验证：卸载触发回调、工具从 registry 移除、回调后索引失效。
/// </summary>
public class PluginLoaderUnloadTests
{
    [Fact]
    /// <summary>卸载已加载插件应触发 OnPluginUnloaded 回调。</summary>
    public void UnloadPlugin_FiresOnPluginUnloadedCallback()
    {
        var loader = new PluginLoader();
        var registry = new ToolRegistry();
        var manifest = new PluginManifest { Id = "test-plugin", Name = "Test Plugin" };
        InjectLoadedEntry(loader, manifest, ["tool_a", "tool_b"]);

        var unloadedIds = new List<string>();
        loader.OnPluginUnloaded = id => unloadedIds.Add(id);

        var result = loader.UnloadPlugin("test-plugin", registry);

        Assert.True(result);
        Assert.Contains("test-plugin", unloadedIds);
    }

    [Fact]
    /// <summary>卸载插件应从 registry 移除该插件的工具。</summary>
    public void UnloadPlugin_RemovesToolsFromRegistry()
    {
        var loader = new PluginLoader();
        var registry = new ToolRegistry();
        var manifest = new PluginManifest { Id = "test-plugin", Name = "Test Plugin" };
        // 预注册工具
        registry.Register(new StubTool("tool_a", "A", "desc"), "Test Plugin");
        registry.Register(new StubTool("tool_b", "B", "desc"), "Test Plugin");
        registry.Register(new StubTool("other_tool", "O", "desc"), "Other Plugin");
        InjectLoadedEntry(loader, manifest, ["tool_a", "tool_b"]);

        loader.UnloadPlugin("test-plugin", registry);

        Assert.Null(registry.TryGet("tool_a"));
        Assert.Null(registry.TryGet("tool_b"));
        Assert.NotNull(registry.TryGet("other_tool"));
    }

    [Fact]
    /// <summary>卸载不存在的插件应返回 false 且不触发回调。</summary>
    public void UnloadPlugin_Nonexistent_ReturnsFalse_NoCallback()
    {
        var loader = new PluginLoader();
        var registry = new ToolRegistry();

        var callbackFired = false;
        loader.OnPluginUnloaded = _ => callbackFired = true;

        var result = loader.UnloadPlugin("nonexistent", registry);

        Assert.False(result);
        Assert.False(callbackFired);
    }

    [Fact]
    /// <summary>多个插件逐个卸载应各自触发回调。</summary>
    public void UnloadMultiplePlugins_FiresCallbackForEach()
    {
        var loader = new PluginLoader();
        var registry = new ToolRegistry();
        InjectLoadedEntry(loader, new PluginManifest { Id = "p1", Name = "P1" }, ["t1"]);
        InjectLoadedEntry(loader, new PluginManifest { Id = "p2", Name = "P2" }, ["t2"]);

        var unloadedIds = new List<string>();
        loader.OnPluginUnloaded = id => unloadedIds.Add(id);

        loader.UnloadPlugin("p1", registry);
        loader.UnloadPlugin("p2", registry);

        Assert.Equal(["p1", "p2"], unloadedIds);
    }

    [Fact]
    /// <summary>HostBootstrapper 接线场景：回调应使 ToolSearchEngine 索引失效。</summary>
    public void UnloadPlugin_Callback_InvalidatesToolSearchIndex()
    {
        var loader = new PluginLoader();
        var registry = new ToolRegistry();
        registry.Register(new StubTool("tool_a", "A", "desc"), "Test Plugin");
        var manifest = new PluginManifest { Id = "test-plugin", Name = "Test Plugin" };
        InjectLoadedEntry(loader, manifest, ["tool_a"]);

        var engine = new ToolSearchEngine(new DummyEmbeddingService());
        engine.BuildIndexAsync(registry, TestContext.Current.CancellationToken).Wait();
        Assert.True(engine.IsIndexed);

        // 模拟 HostBootstrapper 接线：OnPluginUnloaded = _ => engine.InvalidateIndex()
        loader.OnPluginUnloaded = _ => engine.InvalidateIndex();

        loader.UnloadPlugin("test-plugin", registry);

        Assert.False(engine.IsIndexed);
    }

    // ── 反射注入辅助 ──

    /// <summary>
    /// 向 PluginLoader._loaded 注入一个插件条目。
    /// 反射构造 private PluginLoadContext + 值元组列表。
    /// </summary>
    private static void InjectLoadedEntry(
        PluginLoader loader,
        PluginManifest manifest,
        string[] toolNames
    )
    {
        var ctxType = typeof(PluginLoader).GetNestedType(
            "PluginLoadContext",
            BindingFlags.NonPublic
        )!;
        var dllPath = Path.Combine(AppContext.BaseDirectory, "Core.dll");
        var ctx = Activator.CreateInstance(ctxType, [dllPath])!;

        var loadedField = typeof(PluginLoader).GetField(
            "_loaded",
            BindingFlags.NonPublic | BindingFlags.Instance
        )!;

        // 读取 _loaded 的 List 实例；若未初始化，创建同类型新 List
        if (loadedField.GetValue(loader) is not System.Collections.IList list)
        {
            var listType = loadedField.FieldType;
            list = (System.Collections.IList)Activator.CreateInstance(listType)!;
            loadedField.SetValue(loader, list);
        }

        // 从列表元素类型获取精确的 ValueTuple 泛型参数
        var elementType = list.GetType().GetGenericArguments()[0];
        var elementArgs = elementType.GetGenericArguments();
        var tupleType = typeof(ValueTuple<,,,,>).MakeGenericType(
            elementArgs[0], // PluginLoadContext
            elementArgs[1], // IToolPlugin
            elementArgs[2], // PluginManifest
            elementArgs[3], // PluginTrustLevel
            elementArgs[4] // string
        );

        var entry = Activator.CreateInstance(
            tupleType,
            [ctx, new StubPlugin(toolNames), manifest, PluginTrustLevel.External, dllPath]
        )!;
        list.Add(entry);
    }

    private sealed class StubTool(string name, string label, string description) : AgentTool
    {
        public override string Name => name;
        public override string Label => label;
        public override string Description => description;
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

    private sealed class StubPlugin(string[] toolNames) : IToolPlugin
    {
        public void Initialize(IServiceProvider sp) { }

        public IEnumerable<AgentTool> GetTools()
        {
            return toolNames.Select(n => new StubTool(n, n, "stub"));
        }
    }
}
