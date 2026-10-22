using Core.Tools;
using Xunit;

namespace Core.Tests;

/// <summary>
/// ToolRegistry 扩展测试：插件分组、禁用插件、反注册、规范化ID。
/// 基础 CRUD 测试见 ToolRegistryTests.cs。
/// </summary>
public class ToolRegistryExtendedTests
{
    [Fact]
    public void GetPluginNames_ReturnsUniqueSortedNames()
    {
        var registry = new ToolRegistry();
        registry.Register(new StubTool("a", "A", ""), "PluginB");
        registry.Register(new StubTool("b", "B", ""), "PluginA");
        registry.Register(new StubTool("c", "C", ""), "PluginB");

        var names = registry.GetPluginNames();
        Assert.Equal(["PluginA", "PluginB"], names);
    }

    [Fact]
    public void GetToolsByPlugin_ReturnsCorrectTools()
    {
        var registry = new ToolRegistry();
        registry.Register(new StubTool("t1", "T1", ""), "PluginX");
        registry.Register(new StubTool("t2", "T2", ""), "PluginX");
        registry.Register(new StubTool("t3", "T3", ""), "PluginY");

        var pluginXTools = registry.GetToolsByPlugin("PluginX");
        Assert.Equal(2, pluginXTools.Count);
        Assert.All(pluginXTools, t => Assert.Equal("PluginX", registry.GetPluginName(t.Name)));
    }

    [Fact]
    public void GetToolsByPlugin_NonexistentPlugin_ReturnsEmpty()
    {
        var registry = new ToolRegistry();
        Assert.Empty(registry.GetToolsByPlugin("NoSuchPlugin"));
    }

    [Fact]
    public void NormalizePluginId_ConvertsCorrectly()
    {
        Assert.Equal("my-plugin", ToolRegistry.NormalizePluginId("My Plugin"));
        Assert.Equal("my-plugin", ToolRegistry.NormalizePluginId("My_Plugin"));
        Assert.Equal("simple", ToolRegistry.NormalizePluginId("simple"));
        Assert.Equal("abc-def", ToolRegistry.NormalizePluginId("ABC DEF"));
    }

    [Fact]
    public void GetPluginNameById_FindsPlugin()
    {
        var registry = new ToolRegistry();
        registry.Register(new StubTool("t", "T", ""), "My Test Plugin");

        var found = registry.GetPluginNameById("my-test-plugin");
        Assert.Equal("My Test Plugin", found);
    }

    [Fact]
    public void GetPluginNameById_NotFound_ReturnsNull()
    {
        var registry = new ToolRegistry();
        Assert.Null(registry.GetPluginNameById("nonexistent"));
    }

    [Fact]
    public void GetPluginIds_ReturnsNormalizedIds()
    {
        var registry = new ToolRegistry();
        registry.Register(new StubTool("t", "T", ""), "Alpha Beta");

        var ids = registry.GetPluginIds();
        Assert.Contains("alpha-beta", ids);
    }

    [Fact]
    public void DisabledPlugins_FiltersGetAll()
    {
        var registry = new ToolRegistry();
        registry.Register(new StubTool("t1", "T1", ""), "EnabledPlugin");
        registry.Register(new StubTool("t2", "T2", ""), "DisabledPlugin");

        registry.SetDisabledPlugins(["DisabledPlugin"]);

        var all = registry.GetAll();
        Assert.Single(all);
        Assert.Equal("t1", all.First().Name);
    }

    [Fact]
    public void DisabledPlugins_FiltersTryGet()
    {
        var registry = new ToolRegistry();
        registry.Register(new StubTool("enabled", "E", ""), "EnabledPlugin");
        registry.Register(new StubTool("disabled", "D", ""), "DisabledPlugin");

        registry.SetDisabledPlugins(["DisabledPlugin"]);

        Assert.NotNull(registry.TryGet("enabled"));
        Assert.Null(registry.TryGet("disabled"));
    }

    [Fact]
    public void DisabledPlugins_IsPluginDisabled_ReportsCorrectly()
    {
        var registry = new ToolRegistry();
        registry.SetDisabledPlugins(["OffPlugin"]);

        Assert.True(registry.IsPluginDisabled("OffPlugin"));
        Assert.False(registry.IsPluginDisabled("OnPlugin"));
    }

    [Fact]
    public void GetDisabledPlugins_ReturnsDisabledSet()
    {
        var registry = new ToolRegistry();
        registry.SetDisabledPlugins(["P1", "P2"]);

        var disabled = registry.GetDisabledPlugins();
        Assert.Contains("P1", disabled);
        Assert.Contains("P2", disabled);
    }

    [Fact]
    public void SetDisabledPlugins_ReplacesPrevious()
    {
        var registry = new ToolRegistry();
        registry.SetDisabledPlugins(["Old"]);
        registry.SetDisabledPlugins(["New"]);

        var disabled = registry.GetDisabledPlugins();
        Assert.DoesNotContain("Old", disabled);
        Assert.Contains("New", disabled);
    }

    [Fact]
    public void UnregisterByPlugin_RemovesToolsAndPluginMapping()
    {
        var registry = new ToolRegistry();
        registry.Register(new StubTool("t1", "T1", ""), "ToRemove");
        registry.Register(new StubTool("t2", "T2", ""), "ToKeep");

        var removed = registry.UnregisterByPlugin("ToRemove");
        Assert.Equal(1, removed);
        Assert.Null(registry.TryGet("t1"));
        Assert.NotNull(registry.TryGet("t2"));
        Assert.Equal(1, registry.PluginCount);
    }

    [Fact]
    public void UnregisterByPlugin_Nonexistent_ReturnsZero()
    {
        var registry = new ToolRegistry();
        Assert.Equal(0, registry.UnregisterByPlugin("Fake"));
    }

    [Fact]
    public void GetAllByPlugin_GroupsCorrectly()
    {
        var registry = new ToolRegistry();
        registry.Register(new StubTool("a1", "A1", ""), "PluginA");
        registry.Register(new StubTool("a2", "A2", ""), "PluginA");
        registry.Register(new StubTool("b1", "B1", ""), "PluginB");

        var grouped = registry.GetAllByPlugin();
        Assert.Equal(2, grouped.Count);
        Assert.Equal("PluginA", grouped[0].PluginName);
        Assert.Equal(2, grouped[0].Tools.Count);
        Assert.Equal("PluginB", grouped[1].PluginName);
        Assert.Single(grouped[1].Tools);
    }

    [Fact]
    public void Count_And_PluginCount_AreConsistent()
    {
        var registry = new ToolRegistry();
        Assert.Equal(0, registry.Count);
        Assert.Equal(0, registry.PluginCount);

        registry.Register(new StubTool("t1", "T1", ""), "P1");
        registry.Register(new StubTool("t2", "T2", ""), "P2");
        registry.Register(new StubTool("t3", "T3", ""), "P1");

        Assert.Equal(3, registry.Count);
        Assert.Equal(2, registry.PluginCount);
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
                new AgentToolResult { Content = [new Shared.TextContent("ok")] }
            );
        }
    }
}
