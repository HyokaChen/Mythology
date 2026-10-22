using Core.Tools;
using Xunit;

namespace Core.Tests;

/// <summary>
/// AgentMemoryHook 的空值边界测试。
/// 回归点：Result 为 null 时（工具未产生结果 / 上下文只填了工具名）
/// 早期实现用 <c>Result?.IsError == true</c> 守卫，null 时判定为 false 会穿透到
/// <c>Result.Details</c> 解引用抛 NullReferenceException。
/// </summary>
public class AgentMemoryHookTests
{
    [Fact]
    public async Task AfterToolCall_NullResult_DoesNotThrow()
    {
        var hook = new AgentMemoryHook();
        var context = new ToolHookContext { ToolName = "memory_recall", Result = null };

        await hook.AfterToolCallAsync(context, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AfterToolCall_NullResult_NonMemoryTool_DoesNotThrow()
    {
        var hook = new AgentMemoryHook();
        var context = new ToolHookContext { ToolName = "read_file", Result = null };

        await hook.AfterToolCallAsync(context, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BeforeToolCall_AlwaysAllows()
    {
        var hook = new AgentMemoryHook();
        var context = new ToolHookContext { ToolName = "memory_recall" };

        var result = await hook.BeforeToolCallAsync(context, TestContext.Current.CancellationToken);

        Assert.False(result.Block);
    }
}
