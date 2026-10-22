using Core.Agent;
using Tui.Services;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// AgentEventAdapter Token 事件测试。
/// 回归保护：TokenUsageEvent 必须转发当次 PromptTokens（当前上下文大小），
/// 不得跨会话/跨调用累积——累积是"切换 session 后 token 统计不切换"的根因。
/// </summary>
public class AgentEventAdapterTokenTests
{
    /// <summary>
    /// 连续两次 LLM 调用：每次事件都应携带当次 PromptTokens，而非累积值。
    /// 旧实现（_cumulativeTokens += TotalTokens）会返回 150、350 之类的累积值；
    /// 新实现应原样转发 100、200。
    /// </summary>
    [Fact]
    public void TokenUsageEvent_ForwardsPromptTokens_WithoutAccumulation()
    {
        var adapter = new AgentEventAdapter();
        var received = new List<int>();
        adapter.OnContextTokens += t => received.Add(t);

        adapter.OnAgentEvent(new TokenUsageEvent(100, 50));
        adapter.OnAgentEvent(new TokenUsageEvent(200, 100));
        adapter.DispatchAll();

        Assert.Equal([100, 200], received);
    }

    /// <summary>
    /// PromptTokens 只统计输入（上下文），CompletionTokens 不应进入上下文显示。
    /// 同一调用 100 prompt + 1000 completion，显示值只能是 100。
    /// </summary>
    [Fact]
    public void TokenUsageEvent_IgnoresCompletionTokens()
    {
        var adapter = new AgentEventAdapter();
        var received = new List<int>();
        adapter.OnContextTokens += t => received.Add(t);

        adapter.OnAgentEvent(new TokenUsageEvent(300, 5_000));
        adapter.DispatchAll();

        Assert.Equal([300], received);
    }

    /// <summary>
    /// 事件分发按入队顺序执行，保证 UI 侧看到的是最后一次调用的真实上下文。
    /// </summary>
    [Fact]
    public void TokenUsageEvent_EventsAreDispatchedInOrder()
    {
        var adapter = new AgentEventAdapter();
        var received = new List<int>();
        adapter.OnContextTokens += t => received.Add(t);

        adapter.OnAgentEvent(new TokenUsageEvent(10, 1));
        adapter.OnAgentEvent(new TokenUsageEvent(20, 2));
        adapter.OnAgentEvent(new TokenUsageEvent(30, 3));
        adapter.DispatchAll();

        Assert.Equal([10, 20, 30], received);
    }
}
