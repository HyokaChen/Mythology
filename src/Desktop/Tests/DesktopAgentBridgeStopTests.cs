using System.Diagnostics;
using System.Reflection;
using Core.Agent;
using Core.Tools;
using Desktop.Services;
using Xunit;

namespace Desktop.Tests;

public sealed class DesktopAgentBridgeStopTests
{
    [Fact]
    public async Task StaleProcessingButIdleAgent_DoesNotWaitForTimeout()
    {
        var agent = new Agent(new ToolRegistry(), null);
        var bridge = new DesktopAgentBridge(agent);
        SetProcessing(bridge, true);

        var sw = Stopwatch.StartNew();
        await bridge.StopAsync(TimeSpan.FromSeconds(5));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 500, $"waited {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task ConcurrentStops_ShareOneWait()
    {
        var agent = new Agent(new ToolRegistry(), null);
        SetRunning(agent);
        var bridge = new DesktopAgentBridge(agent);
        SetProcessing(bridge, true);
        var timeout = TimeSpan.FromMilliseconds(100);

        var sw = Stopwatch.StartNew();
        await Task.WhenAll(
            bridge.StopAsync(timeout),
            bridge.StopAsync(timeout),
            bridge.StopAsync(timeout)
        );
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 500, $"three stops took {sw.ElapsedMilliseconds}ms");
    }

    private static void SetProcessing(DesktopAgentBridge bridge, bool value) =>
        typeof(DesktopAgentBridge)
            .GetProperty("IsProcessing", BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(bridge, value);

    private static void SetRunning(Agent agent) =>
        typeof(Agent)
            .GetProperty(nameof(Agent.AgentStateEnum))!
            .SetValue(agent, AgentStateEnum.Running);
}
