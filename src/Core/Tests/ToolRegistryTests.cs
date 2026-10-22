using Core.Shared;
using Core.Tools;
using Xunit;

namespace Core.Tests;

/// <summary>
/// ToolRegistry 工具注册表测试。
/// 验证工具注册、查找和缓存失效。
/// </summary>
public class ToolRegistryTests
{
    [Fact]
    /// <summary>注册应将工具添加到注册表。</summary>
    public void Register_AddsTool()
    {
        var registry = new ToolRegistry();
        var tool = new DummyTool("test_tool", "测试", "测试工具");
        registry.Register(tool, "test_plugin");

        var found = registry.TryGet("test_tool");
        Assert.NotNull(found);
        Assert.Equal("test_tool", found.Name);
    }

    [Fact]
    /// <summary>同名工具注册应覆盖旧工具。</summary>
    public void Register_OverwritesSameName()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("tool", "旧", "旧工具"), "plugin");
        registry.Register(new DummyTool("tool", "新", "新工具"), "plugin");

        var found = registry.TryGet("tool");
        Assert.NotNull(found);
        Assert.Equal("新", found.Label);
    }

    [Fact]
    /// <summary>注册 null 工具应抛出异常。</summary>
    public void Register_ThrowsOnNull()
    {
        var registry = new ToolRegistry();
        Assert.Throws<ArgumentNullException>(() => registry.Register(null!, "plugin"));
    }

    [Fact]
    /// <summary>批量注册应添加多个工具。</summary>
    public void RegisterRange_AddsMultipleTools()
    {
        var registry = new ToolRegistry();
        registry.RegisterRange(
            [new DummyTool("tool1", "工具1", "描述1"), new DummyTool("tool2", "工具2", "描述2")],
            "plugin"
        );

        Assert.Equal(2, registry.Count);
    }

    [Fact]
    /// <summary>查找不存在的工具应返回 null。</summary>
    public void TryGet_ReturnsNullForMissingTool()
    {
        var registry = new ToolRegistry();
        Assert.Null(registry.TryGet("不存在"));
    }

    [Fact]
    /// <summary>GetAll 应返回所有已注册工具。</summary>
    public void GetAll_ReturnsAllTools()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("a", "A", "desc"), "plugin");
        registry.Register(new DummyTool("b", "B", "desc"), "plugin");

        var all = registry.GetAll();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    /// <summary>注册新工具后缓存应失效。</summary>
    public void GetAll_CacheInvalidatedOnRegister()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("a", "A", "desc"), "plugin");
        var first = registry.GetAll();

        registry.Register(new DummyTool("b", "B", "desc"), "plugin");
        var second = registry.GetAll();

        Assert.Single(first);
        Assert.Equal(2, second.Count);
    }

    [Fact]
    /// <summary>Count 应反映已注册工具数量。</summary>
    public void Count_ReflectsRegisteredTools()
    {
        var registry = new ToolRegistry();
        Assert.Equal(0, registry.Count);

        registry.Register(new DummyTool("a", "A", "desc"), "plugin");
        Assert.Equal(1, registry.Count);

        registry.Register(new DummyTool("b", "B", "desc"), "plugin");
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    /// <summary>注册新工具应使 Version 递增。</summary>
    public void Version_IncrementsOnRegister()
    {
        var registry = new ToolRegistry();
        Assert.Equal(0, registry.Version);

        registry.Register(new DummyTool("a", "A", "desc"), "plugin");
        Assert.Equal(1, registry.Version);

        registry.Register(new DummyTool("b", "B", "desc"), "plugin");
        Assert.Equal(2, registry.Version);
    }

    [Fact]
    /// <summary>卸载插件应使 Version 递增。</summary>
    public void Version_IncrementsOnUnregisterByPlugin()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("a", "A", "desc"), "plugin");
        registry.Register(new DummyTool("b", "B", "desc"), "other");
        var versionAfterRegisters = registry.Version;

        var removed = registry.UnregisterByPlugin("plugin");
        Assert.Equal(1, removed);
        Assert.Equal(versionAfterRegisters + 1, registry.Version);
    }

    [Fact]
    /// <summary>卸载不存在的插件不应递增 Version。</summary>
    public void Version_UnchangedOnUnregisterNonexistent()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("a", "A", "desc"), "plugin");
        var version = registry.Version;

        Assert.Equal(0, registry.UnregisterByPlugin("nonexistent"));
        Assert.Equal(version, registry.Version);
    }

    [Fact]
    /// <summary>SetDisabledPlugins 应使 Version 递增（工具可见性变化）。</summary>
    public void Version_IncrementsOnSetDisabledPlugins()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("a", "A", "desc"), "plugin");
        var version = registry.Version;

        registry.SetDisabledPlugins(["plugin"]);
        Assert.Equal(version + 1, registry.Version);
    }

    [Fact]
    /// <summary>纯读操作不应改变 Version。</summary>
    public void Version_UnchangedOnReadOperations()
    {
        var registry = new ToolRegistry();
        registry.Register(new DummyTool("a", "A", "desc"), "plugin");
        var version = registry.Version;

        _ = registry.GetAll();
        _ = registry.GetPluginNames();
        _ = registry.TryGet("a");
        _ = registry.Count;
        _ = registry.PluginCount;

        Assert.Equal(version, registry.Version);
    }

    // ── 辅助类：最小 AgentTool 实现用于测试 ──

    private sealed class DummyTool(string name, string label, string description) : AgentTool
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
            return Task.FromResult<AgentToolResult>(
                new AgentToolResult { Content = [new TextContent("ok")] }
            );
        }
    }
}
