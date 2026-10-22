using Core.Agent;
using Xunit;

namespace Core.Tests;

/// <summary>
/// AgentState 状态类测试。
/// 验证默认状态和 with 表达式复制行为。
/// </summary>
public class AgentStateTests
{
    [Fact]
    /// <summary>默认状态应包含空字段。</summary>
    public void DefaultState_HasEmptyFields()
    {
        var state = new AgentState();
        Assert.Equal("", state.SystemPrompt);
        Assert.Equal("", state.ModelName);
        Assert.Equal("medium", state.ThinkingLevel);
        Assert.Empty(state.Tools);
        Assert.Empty(state.Messages);
        Assert.False(state.IsStreaming);
        Assert.Null(state.StreamMessage);
        Assert.Empty(state.PendingToolCalls);
        Assert.Null(state.Error);
    }

    [Fact]
    /// <summary>with 表达式应创建独立副本，不影响原始对象。</summary>
    public void AgentState_WithExpression_CreatesCopy()
    {
        var original = new AgentState { ModelName = "gpt-4o", Error = null };
        var modified = original with { ModelName = "claude-sonnet", Error = "timeout" };

        Assert.Equal("gpt-4o", original.ModelName);
        Assert.Null(original.Error);
        Assert.Equal("claude-sonnet", modified.ModelName);
        Assert.Equal("timeout", modified.Error);
    }
}
