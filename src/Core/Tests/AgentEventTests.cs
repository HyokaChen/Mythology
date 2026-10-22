using Core.Agent;
using Xunit;

namespace Core.Tests;

/// <summary>
/// AgentEvent 事件类型测试。
/// 验证各种事件子类的字段存储和行为。
/// </summary>
public class AgentEventTests
{
    [Fact]
    /// <summary>AgentStart 事件应正确存储时间戳。</summary>
    public void AgentStart_HasTimestamp()
    {
        var evt = new AgentStart { Timestamp = 12345 };
        Assert.Equal(12345, evt.Timestamp);
    }

    [Fact]
    /// <summary>StreamChunk 事件应正确存储所有字段。</summary>
    public void StreamChunk_StoresFields()
    {
        var chunk = new StreamChunk("delta", "accumulated", StreamContentType.Text);
        Assert.Equal("delta", chunk.Delta);
        Assert.Equal("accumulated", chunk.AccumulatedText);
        Assert.Equal(StreamContentType.Text, chunk.ContentType);
    }

    [Fact]
    /// <summary>ToolExecutionStart 事件应正确存储工具调用信息。</summary>
    public void ToolExecutionStart_StoresFields()
    {
        var evt = new ToolExecutionStart("call_123", "write_chapter", """{"title":"Ch1"}""");
        Assert.Equal("call_123", evt.ToolCallId);
        Assert.Equal("write_chapter", evt.ToolName);
        Assert.Equal("""{"title":"Ch1"}""", evt.Arguments);
    }

    [Fact]
    /// <summary>ToolExecutionEnd 事件应正确存储执行结果。</summary>
    public void ToolExecutionEnd_StoresFields()
    {
        var evt = new ToolExecutionEnd("call_123", false, "ok");
        Assert.Equal("call_123", evt.ToolCallId);
        Assert.False(evt.IsError);
        Assert.Equal("ok", evt.Result);
    }

    [Fact]
    /// <summary>CircuitBreakerTriggered 事件应正确存储断路器触发信息。</summary>
    public void CircuitBreakerTriggered_StoresFields()
    {
        var evt = new CircuitBreakerTriggered("stuck_tool", "{}", 3, "重复调用");
        Assert.Equal("stuck_tool", evt.ToolName);
        Assert.Equal(3, evt.RepeatCount);
        Assert.Equal("重复调用", evt.Reason);
    }
}
