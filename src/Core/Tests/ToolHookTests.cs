using Core.Shared;
using Core.Tools;
using Xunit;

namespace Core.Tests;

public class ToolHookTests
{
    [Fact]
    public void ToolCallBeforeResult_Allow_HasBlockFalse()
    {
        var result = ToolCallBeforeResult.Allow;
        Assert.False(result.Block);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void BlockExecution_SetsBlockTrueWithReason()
    {
        var result = ToolCallBeforeResult.BlockExecution("permission denied");
        Assert.True(result.Block);
        Assert.Equal("permission denied", result.Reason);
    }

    [Fact]
    public void ToolHookContext_DefaultValues()
    {
        var ctx = new ToolHookContext();
        Assert.Equal("", ctx.ToolCallId);
        Assert.Equal("", ctx.ToolName);
        Assert.Equal("", ctx.Arguments);
        Assert.Null(ctx.Result);
        Assert.Null(ctx.Agent);
    }

    [Fact]
    public void ToolHookContext_WithValues_ReturnsCorrectly()
    {
        var result = new AgentToolResult { Content = [new TextContent("ok")] };
        var agent = new object();
        var ctx = new ToolHookContext
        {
            ToolCallId = "call_123",
            ToolName = "test_tool",
            Arguments = "{}",
            Result = result,
            Agent = agent,
        };

        Assert.Equal("call_123", ctx.ToolCallId);
        Assert.Equal("test_tool", ctx.ToolName);
        Assert.Equal("{}", ctx.Arguments);
        Assert.Same(result, ctx.Result);
        Assert.Same(agent, ctx.Agent);
    }
}
