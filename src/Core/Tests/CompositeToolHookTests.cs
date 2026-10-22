using Core.Tools;
using Xunit;

namespace Core.Tests;

/// <summary>
/// CompositeToolHook 组合钩子测试。
/// 验证链式执行、短路阻止和 After 全执行。
/// </summary>
public sealed class CompositeToolHookTests
{
    private static ToolHookContext MakeContext() =>
        new()
        {
            ToolCallId = "c1",
            ToolName = "t",
            Arguments = "{}",
        };

    [Fact]
    public async Task Before_AllAllow_ReturnsAllow()
    {
        var hook = new CompositeToolHook(new AllowHook(), new AllowHook());
        var result = await hook.BeforeToolCallAsync(MakeContext(), CancellationToken.None);
        Assert.False(result.Block);
    }

    [Fact]
    public async Task Before_EmptyHooks_ReturnsAllow()
    {
        var hook = new CompositeToolHook();
        var result = await hook.BeforeToolCallAsync(MakeContext(), CancellationToken.None);
        Assert.False(result.Block);
    }

    [Fact]
    public async Task Before_FirstBlocks_ShortCircuits()
    {
        var allowHook = new AllowHook();
        var hook = new CompositeToolHook(new BlockHook("denied"), allowHook);
        var result = await hook.BeforeToolCallAsync(MakeContext(), CancellationToken.None);
        Assert.True(result.Block);
        Assert.Equal("denied", result.Reason);
    }

    [Fact]
    public async Task Before_SecondBlocks_FirstAllowed()
    {
        var hook = new CompositeToolHook(new AllowHook(), new BlockHook("second block"));
        var result = await hook.BeforeToolCallAsync(MakeContext(), CancellationToken.None);
        Assert.True(result.Block);
        Assert.Equal("second block", result.Reason);
    }

    [Fact]
    public async Task After_AllHooksExecuted()
    {
        var h1 = new TrackingHook();
        var h2 = new TrackingHook();
        var hook = new CompositeToolHook(h1, h2);
        await hook.AfterToolCallAsync(MakeContext(), CancellationToken.None);
        Assert.True(h1.AfterCalled);
        Assert.True(h2.AfterCalled);
    }

    [Fact]
    public async Task After_ExceptionPropagates()
    {
        var hook = new CompositeToolHook(new ThrowingAfterHook());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            hook.AfterToolCallAsync(MakeContext(), CancellationToken.None)
        );
    }

    // ── Stub hooks ──

    private sealed class AllowHook : IToolHook
    {
        public Task<ToolCallBeforeResult> BeforeToolCallAsync(
            ToolHookContext context,
            CancellationToken ct
        ) => Task.FromResult(ToolCallBeforeResult.Allow);

        public Task AfterToolCallAsync(ToolHookContext context, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class BlockHook(string reason) : IToolHook
    {
        public Task<ToolCallBeforeResult> BeforeToolCallAsync(
            ToolHookContext context,
            CancellationToken ct
        ) => Task.FromResult(ToolCallBeforeResult.BlockExecution(reason));

        public Task AfterToolCallAsync(ToolHookContext context, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class TrackingHook : IToolHook
    {
        public bool AfterCalled { get; private set; }

        public Task<ToolCallBeforeResult> BeforeToolCallAsync(
            ToolHookContext context,
            CancellationToken ct
        ) => Task.FromResult(ToolCallBeforeResult.Allow);

        public Task AfterToolCallAsync(ToolHookContext context, CancellationToken ct)
        {
            AfterCalled = true;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingAfterHook : IToolHook
    {
        public Task<ToolCallBeforeResult> BeforeToolCallAsync(
            ToolHookContext context,
            CancellationToken ct
        ) => Task.FromResult(ToolCallBeforeResult.Allow);

        public Task AfterToolCallAsync(ToolHookContext context, CancellationToken ct) =>
            throw new InvalidOperationException("after boom");
    }
}
