using Core.Agent;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Core.Vector;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using AgentType = Core.Agent.Agent;

namespace Plugins.ContextContinuation.Tests;

/// <summary>
/// 插件门禁：只有 <c>context_management.mode = continuation</c> 才提供工具并投递运行期选项；
/// compact 模式（默认）必须零注册、零注入（与引入接续前的行为完全一致）。
/// </summary>
public sealed class ContextContinuationPluginGateTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly FileSessionStore _sessions;
    private readonly ConfigStore _configStore;
    private readonly AgentType _agent;
    private readonly RetrievalKernel _kernel;

    public ContextContinuationPluginGateTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"mythology-cc-plugin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _sessions = new FileSessionStore(
            novelsRoot: Path.Combine(_tempRoot, "novels"),
            mythologyDir: _tempRoot
        )
        {
            SkipConflictCheck = true,
        };
        _configStore = new ConfigStore(_tempRoot);
        _agent = new AgentType(new ToolRegistry());
        _kernel = new RetrievalKernel(new InMemoryRetrievalStore(), new HashEmbedder(128));
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

    private ServiceProvider BuildProvider(bool withKernel = true)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_configStore);
        services.AddSingleton(_sessions);
        services.AddSingleton(_agent);
        if (withKernel)
            services.AddSingleton(_kernel);
        return services.BuildServiceProvider();
    }

    private async Task SetModeAsync(string mode)
    {
        var config = _configStore.Load();
        config.ContextManagement.Mode = mode;
        await _configStore.SaveAsync(config);
    }

    [Fact]
    public async Task CompactMode_RegistersNoTools_AndLeavesOptionsNull()
    {
        await SetModeAsync("compact");
        var plugin = new ContextContinuationPlugin();

        using var sp = BuildProvider();
        plugin.Initialize(sp);

        Assert.Empty(plugin.GetTools());
        Assert.Null(_agent.ContextManagement);
    }

    [Fact]
    public async Task ContinuationMode_RegistersToolFamily_AndActivatesOptions()
    {
        await SetModeAsync("continuation");
        var plugin = new ContextContinuationPlugin();

        using var sp = BuildProvider();
        plugin.Initialize(sp);

        var tools = plugin.GetTools().ToList();
        Assert.Equal(ContextContinuationPlugin.ToolNames.Length, tools.Count);
        Assert.Equal(5, tools.Count);
        Assert.Equal(
            ContextContinuationPlugin.ToolNames.OrderBy(n => n, StringComparer.Ordinal),
            tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal)
        );

        Assert.NotNull(_agent.ContextManagement);
        Assert.Equal(ContextMode.Continuation, _agent.ContextManagement!.Mode);
    }

    [Fact]
    public async Task RegisteredTools_MatchHiddenSet()
    {
        await SetModeAsync("continuation");
        var plugin = new ContextContinuationPlugin();

        using var sp = BuildProvider();
        plugin.Initialize(sp);

        // 注册名与隐藏名单必须一一对应：任一工具漏进 HiddenTools 就会被渲染给用户
        foreach (var tool in plugin.GetTools())
            Assert.True(HiddenTools.IsHidden(tool.Name), $"{tool.Name} 必须进 HiddenTools");

        Assert.Equal(HiddenTools.Names.Count, ContextContinuationPlugin.ToolNames.Length);
    }

    [Fact]
    public async Task ContinuationMode_WithoutRetrievalKernel_StillYieldsAllTools()
    {
        await SetModeAsync("continuation");
        var plugin = new ContextContinuationPlugin();

        using var sp = BuildProvider(withKernel: false);
        plugin.Initialize(sp);

        // 向量通道是可选增强：kernel 缺失时工具照常可用（semantic=true 退化为字面检索）
        Assert.Equal(5, plugin.GetTools().Count());
    }

    [Fact]
    public async Task ApplyMode_SwitchesToolFamilyAndOptionsAtRuntime()
    {
        // 启动为 compact（0 工具），运行期切到接续 → 工具族 + 选项同时生效；
        // 再切回 compact → 工具族消失、选项归 null（与 /compact 守卫、换页分支的判据一致）。
        await SetModeAsync("compact");
        var plugin = new ContextContinuationPlugin();

        using var sp = BuildProvider();
        plugin.Initialize(sp);
        Assert.Empty(plugin.GetTools());
        Assert.Null(plugin.CurrentOptions);

        var applied = plugin.ApplyMode(
            new ContextManagementOptions(
                ContextMode.Continuation,
                ReminderPercent: 0,
                VectorRetrieval: true
            )
        );

        Assert.Equal(5, applied.Count);
        Assert.Equal(ContextMode.Continuation, _agent.ContextManagement!.Mode);
        Assert.True(_agent.ContextManagement.VectorRetrieval);
        Assert.Equal(5, plugin.GetTools().Count());

        var compacted = plugin.ApplyMode(null);

        Assert.Empty(compacted);
        Assert.Null(_agent.ContextManagement);
        Assert.Empty(plugin.GetTools());
    }
}
