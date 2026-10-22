using Core.Agent;
using Core.Data;
using Core.Plugins;
using Core.Shared;
using Core.Tools;
using Xunit;
using AgentType = Core.Agent.Agent;

namespace Core.Tests;

/// <summary>
/// 上下文管理模式热切换（compact ↔ continuation）：
/// 工具族的注册/卸载、Agent 选项、工具搜索索引重建、config.yaml 落盘四件事必须同时发生，
/// 否则 UI 开关会出现「配置改了但模型看不到工具」这类半生效状态。
/// </summary>
public sealed class ContextModeSwitcherTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly ConfigStore _configStore;

    public ContextModeSwitcherTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"mythology-switch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _configStore = new ConfigStore(_tempRoot);
    }

    public void Dispose()
    {
        AtomicFile.ForgetPath(_tempRoot);
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试
        }
    }

    private (
        ContextModeSwitcher Switcher,
        ToolRegistry Registry,
        AgentType Agent,
        FakeContextModePlugin Plugin
    ) Build(bool withPlugin = true)
    {
        var registry = new ToolRegistry();
        var agent = new AgentType(registry);
        var plugin = new FakeContextModePlugin(agent);
        var switcher = new ContextModeSwitcher(
            registry,
            agent,
            _configStore,
            new ToolSearchEngine(embeddingService: null),
            withPlugin ? new ContextModePluginRef("Context Continuation", plugin) : null
        );
        return (switcher, registry, agent, plugin);
    }

    [Fact]
    public async Task SwitchToContinuation_RegistersTools_AppliesOptions_AndPersistsMode()
    {
        var (switcher, registry, agent, plugin) = Build();
        registry.Register(new FakeTool("keep_me"), "Other Plugin");

        var (ok, message) = await switcher.SwitchAsync(
            new ContextManagementConfig { Mode = "continuation", VectorRetrieval = true }
        );

        Assert.True(ok);
        Assert.Contains("接续", message);
        // 工具族已注册（组名 = 插件 manifest.Name），且不影响其它插件的工具
        Assert.Equal(
            ["fake_new_context"],
            registry.GetToolsByPlugin("Context Continuation").Select(t => t.Name)
        );
        Assert.NotNull(registry.TryGet("keep_me"));
        // Agent 选项生效（换页分支/提示骨架/片段注入的判据）
        Assert.Equal(ContextMode.Continuation, agent.ContextManagement!.Mode);
        // 工具表 + 搜索索引都跟着刷新（否则 tool_search 发现不了新工具）
        Assert.Contains(agent.State.Tools, t => t.Name == "fake_new_context");
        Assert.True(switcher.CurrentMode == ContextMode.Continuation);
        Assert.Equal("continuation", new ConfigStore(_tempRoot).Load().ContextManagement.Mode);
        Assert.True(new ConfigStore(_tempRoot).Load().ContextManagement.VectorRetrieval);
        Assert.Equal(ContextMode.Continuation, plugin.LastOptions!.Mode);
    }

    [Fact]
    public async Task SwitchToCompact_UnregistersTools_AndClearsOptions()
    {
        var (switcher, registry, agent, plugin) = Build();
        await switcher.SwitchAsync(new ContextManagementConfig { Mode = "continuation" });

        var (ok, message) = await switcher.SwitchAsync(
            new ContextManagementConfig { Mode = "compact" }
        );

        Assert.True(ok);
        Assert.Contains("压缩", message);
        Assert.Empty(registry.GetToolsByPlugin("Context Continuation"));
        Assert.Null(agent.ContextManagement);
        Assert.DoesNotContain(agent.State.Tools, t => t.Name == "fake_new_context");
        Assert.Equal("compact", new ConfigStore(_tempRoot).Load().ContextManagement.Mode);
        Assert.Null(plugin.LastOptions);
    }

    [Fact]
    public async Task SwitchToContinuation_WithoutPlugin_Fails_AndKeepsConfig()
    {
        var (switcher, registry, agent, _) = Build(withPlugin: false);

        var (ok, message) = await switcher.SwitchAsync(
            new ContextManagementConfig { Mode = "continuation" }
        );

        Assert.False(ok);
        Assert.Contains("插件未加载", message);
        Assert.Equal("compact", new ConfigStore(_tempRoot).Load().ContextManagement.Mode);
        Assert.Null(agent.ContextManagement);
        Assert.Empty(registry.GetAll());
    }

    [Fact]
    public async Task MisspelledMode_FallsBackToCompact_AndIsNotPersisted()
    {
        // 拼错不得静默切到接续；落盘的也必须是归一化后的值
        var (switcher, _, agent, _) = Build();

        var (ok, _) = await switcher.SwitchAsync(
            new ContextManagementConfig { Mode = "continuatoin" }
        );

        Assert.True(ok);
        Assert.Null(agent.ContextManagement);
        Assert.Equal("compact", new ConfigStore(_tempRoot).Load().ContextManagement.Mode);
    }

    [Fact]
    public void Find_ReturnsNull_WhenNoLoaderOrNoModePlugin()
    {
        Assert.Null(ContextModeSwitcher.Find(null));
    }

    /// <summary>
    /// 最小插件替身：按契约投递选项给 Agent + 给出一个可辨识的工具名。
    /// （真实插件 ContextContinuationPlugin 用同一契约实现，见其 ApplyMode。）
    /// </summary>
    private sealed class FakeContextModePlugin(AgentType agent) : IContextModePlugin
    {
        public ContextManagementOptions? LastOptions { get; private set; }

        public ContextManagementOptions? CurrentOptions => LastOptions;

        public IReadOnlyList<AgentTool> ApplyMode(ContextManagementOptions? options)
        {
            LastOptions = options;
            agent.ApplyContextManagement(options);
            return options is null ? [] : [new FakeTool("fake_new_context")];
        }
    }

    private sealed class FakeTool(string name) : AgentTool
    {
        public override string Name => name;
        public override string Label => name;
        public override string Description => "测试替身";
        public override System.Text.Json.JsonElement? Parameters => null;

        public override Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            System.Text.Json.JsonElement args,
            CancellationToken ct,
            IProgress<AgentToolResult>? onUpdate = null
        ) => Task.FromResult(new AgentToolResult { Content = [new TextContent("stub")] });
    }
}
