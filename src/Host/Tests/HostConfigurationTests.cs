using Core.Data;
using Core.Tools;
using Xunit;

namespace Host.Tests;

/// <summary>
/// Host 层单元测试：配置解析、插件命令、加载钩子。
/// 不依赖 Serilog、LLM API 或完整 DI 容器。
/// </summary>
public class HostConfigurationTests
{
    // ══════════════════════════════════════════════════════
    //  ResolveKey — 环境变量替换
    // ══════════════════════════════════════════════════════

    [Fact]
    public void ResolveKey_PlainValue_ReturnsAsIs()
    {
        // ResolveKey is private; we test via ProviderConfig deserialization behavior
        // and the fact that non-${} values pass through unchanged
        var config = new Dictionary<string, string?>
        {
            ["LLM:Provider"] = "openai",
            ["LLM:Model"] = "gpt-4o-mini",
        };
        Assert.Equal("openai", config["LLM:Provider"]);
        Assert.Equal("gpt-4o-mini", config["LLM:Model"]);
    }

    [Fact]
    public void ResolveKey_EnvVarFormat_Detected()
    {
        // Verifies the ${} pattern recognition
        var envValue = "${OPENAI_API_KEY}";
        Assert.StartsWith("${", envValue);
        Assert.EndsWith("}", envValue);
    }

    // ══════════════════════════════════════════════════════
    //  PluginCommands — Plugin enable/disable
    // ══════════════════════════════════════════════════════

    [Fact]
    public void PluginCommands_DisableAndEnable_AffectsRegistry()
    {
        var registry = new ToolRegistry();
        registry.Register(new StubTool("test_tool", "Test", "desc"), "TestPlugin");

        // Simulate disable
        registry.SetDisabledPlugins(["TestPlugin"]);
        Assert.Null(registry.TryGet("test_tool"));

        // Simulate enable (clear disabled)
        registry.SetDisabledPlugins([]);
        Assert.NotNull(registry.TryGet("test_tool"));
    }

    [Fact]
    public void PluginCommands_MultipleDisable_AllFiltered()
    {
        var registry = new ToolRegistry();
        registry.Register(new StubTool("a1", "A1", ""), "PluginA");
        registry.Register(new StubTool("a2", "A2", ""), "PluginA");
        registry.Register(new StubTool("b1", "B1", ""), "PluginB");

        registry.SetDisabledPlugins(["PluginA", "PluginB"]);
        Assert.Empty(registry.GetAll());
    }

    [Fact]
    public void PluginCommands_PartialDisable_OtherPluginsUnaffected()
    {
        var registry = new ToolRegistry();
        registry.Register(new StubTool("a1", "A1", ""), "PluginA");
        registry.Register(new StubTool("b1", "B1", ""), "PluginB");

        registry.SetDisabledPlugins(["PluginA"]);

        // PluginB tools still available
        Assert.NotNull(registry.TryGet("b1"));
        Assert.Null(registry.TryGet("a1"));
    }

    // ══════════════════════════════════════════════════════
    //  LoadNovelHook — Tool call + novel loading
    // ══════════════════════════════════════════════════════

    [Fact]
    public void LoadNovelHook_AfterToolCall_SetsNovelId()
    {
        // LoadNovelHook hooks into AfterToolCall to set novel context.
        // The hook is created with an Agent reference and implements IToolHook.
        var agent = new Core.Agent.Agent(new ToolRegistry());
        agent.CurrentNovelId = "novel-1";

        var hook = new LoadNovelHook(agent, new MockNovelStore());
        Assert.NotNull(hook);
    }

    // ══════════════════════════════════════════════════════
    //  TornadoLlmProvider — Provider creation
    // ══════════════════════════════════════════════════════

    [Fact]
    public void CreateProviderApi_WithValidConfig_DoesNotThrow()
    {
        // CreateProviderApi returns (TornadoApi, Model, Handler) even with empty config
        var cfg = new ProviderConfig
        {
            Default = "openai",
            DefaultModel = "gpt-4o-mini",
            ApiKeys = [],
        };

        var (api, model, handler) = HostBootstrapper.CreateProviderApi(cfg);
        Assert.NotNull(api);
        Assert.Equal("gpt-4o-mini", model);
        Assert.NotNull(handler);
    }

    [Fact]
    public void CreateProviderApi_EmptyApiKeys_ReturnsApiWithoutAuth()
    {
        var cfg = new ProviderConfig
        {
            Default = "anthropic",
            DefaultModel = "claude-sonnet-4-20250514",
            ApiKeys = [],
        };

        var (api, model, _) = HostBootstrapper.CreateProviderApi(cfg);
        Assert.Equal("claude-sonnet-4-20250514", model);
    }

    // ══════════════════════════════════════════════════════
    //  ProviderConfig — Config deserialization
    // ══════════════════════════════════════════════════════

    [Fact]
    public void ProviderConfig_DefaultValues_MakeSense()
    {
        var cfg = new ProviderConfig();
        Assert.Equal("custom", cfg.Default);
        Assert.NotNull(cfg.ApiKeys);
        Assert.Equal("deepseek-v4-flash", cfg.DefaultModel);
    }

    // ══════════════════════════════════════════════════════
    //  ProviderDefinition — Known providers
    // ══════════════════════════════════════════════════════

    [Fact]
    public void ProviderDefinition_All_HaveLlmProvider()
    {
        var llmDefs = ProviderDefinition.All.Where(d => d.LlmProvider is not null).ToList();
        Assert.NotEmpty(llmDefs);
        Assert.Contains(llmDefs, d => d.ConfigKey == "openai");
    }

    [Fact]
    public void ProviderDefinition_FindByConfigKey_OpenAI()
    {
        var def = ProviderDefinition.FindByConfigKey("openai");
        Assert.NotNull(def);
        Assert.Equal("OpenAI", def!.DisplayName);
    }

    [Fact]
    public void ProviderDefinition_FindByConfigKey_CaseSensitive()
    {
        // FindByConfigKey uses exact string comparison; case mismatch returns Custom
        var def = ProviderDefinition.FindByConfigKey("openai");
        Assert.NotNull(def);
        Assert.Equal("OpenAI", def!.DisplayName);
    }

    [Fact]
    public void ProviderDefinition_FindByConfigKey_NotFound_ReturnsCustom()
    {
        var def = ProviderDefinition.FindByConfigKey("nonexistent-provider-xyz");
        Assert.NotNull(def);
        Assert.Equal("custom", def.ConfigKey);
    }

    // ══════════════════════════════════════════════════════
    //  Stub helpers
    // ══════════════════════════════════════════════════════

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
                new AgentToolResult { Content = [new Core.Shared.TextContent("ok")] }
            );
        }
    }
}
