using Core.Agent;
using LlmTornado.Chat;
using LlmTornado.ChatFunctions;
using Xunit;

namespace Core.Tests;

/// <summary>
/// Tool-call 参数流式跟踪：同一调用只发一次 Started，alias 形状变化不重复建卡，
/// 完整 FunctionCall 能解析回 pending 卡使用的同一个 key。
/// </summary>
public sealed class ToolCallStreamTrackerTests
{
    private static FunctionCallStreamUpdate Delta(
        string? delta = "{",
        string? name = "edit_entities",
        string? callId = "call_1",
        int? index = 0
    ) =>
        new()
        {
            ArgumentsDelta = delta,
            Name = name,
            CallId = callId,
            Index = index,
        };

    private static FunctionCall Complete(
        string name = "edit_entities",
        string? callId = "call_1",
        int? index = 0
    ) =>
        new()
        {
            Name = name,
            ToolCall = new ToolCall { Id = callId, Index = index },
        };

    [Fact]
    public void Observe_EmitsOncePerCall()
    {
        var tracker = new ToolCallStreamTracker();

        var first = tracker.Observe(Delta());
        Assert.NotNull(first);
        Assert.Equal("call_1", first.ToolCallKey);
        Assert.Equal("edit_entities", first.ToolName);
        Assert.Equal(0, first.Index ?? -1);

        Assert.Null(tracker.Observe(Delta(delta: "{\"name\"")));
    }

    [Fact]
    public void Observe_WaitsForToolName()
    {
        var tracker = new ToolCallStreamTracker();

        Assert.Null(tracker.Observe(Delta(name: null)));
        var started = tracker.Observe(Delta(name: "edit_entities"));

        Assert.NotNull(started);
        Assert.Equal("edit_entities", started.ToolName);
    }

    [Fact]
    public void Observe_HandlesAliasShapeChange_AndExecutionUsesSameKey()
    {
        var tracker = new ToolCallStreamTracker();

        var started = tracker.Observe(Delta(callId: null, index: 0, name: "edit_entities"));
        Assert.NotNull(started);
        Assert.Equal("idx:0", started.ToolCallKey);

        // 后续 delta 带来 CallId：仍应视为同一调用，不重复发 Started。
        Assert.Null(tracker.Observe(Delta(callId: "call_1", index: 0, name: "edit_entities")));

        var executionKey = tracker.ResolveExecutionKey(Complete(callId: "call_1", index: 0));
        Assert.Equal("idx:0", executionKey);
    }

    [Fact]
    public void ResolveExecutionKey_UsesCallIdWhenItWasThePrimaryKey()
    {
        var tracker = new ToolCallStreamTracker();
        var started = tracker.Observe(
            Delta(callId: "call_1", index: null, name: "edit_entities")
        );
        Assert.NotNull(started);
        Assert.Equal("call_1", started.ToolCallKey);

        Assert.Equal("call_1", tracker.ResolveExecutionKey(Complete(callId: "call_1", index: 2)));
    }

    [Fact]
    public void ParallelCalls_GetDistinctKeys()
    {
        var tracker = new ToolCallStreamTracker();

        var first = tracker.Observe(Delta(callId: "call_a", index: 0, name: "edit_entities"));
        var second = tracker.Observe(Delta(callId: "call_b", index: 1, name: "write_chapter"));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.ToolCallKey, second.ToolCallKey);
        Assert.Equal("call_a", first.ToolCallKey);
        Assert.Equal("call_b", second.ToolCallKey);
    }

    [Fact]
    public void EmptyDelta_IsIgnored()
    {
        var tracker = new ToolCallStreamTracker();
        Assert.Null(tracker.Observe(Delta(delta: "")));
    }

    [Fact]
    public void NameOnlyFallback_ResolvesToSameKey()
    {
        var tracker = new ToolCallStreamTracker();
        var started = tracker.Observe(
            Delta(callId: null, index: null, name: "edit_entities")
        );
        Assert.NotNull(started);
        Assert.Equal("name:edit_entities", started.ToolCallKey);

        var complete = new FunctionCall { Name = "edit_entities" };
        Assert.Equal("name:edit_entities", tracker.ResolveExecutionKey(complete));
    }

    [Fact]
    public void Observe_RegistersIdentityFromEmptyFirstDelta()
    {
        var tracker = new ToolCallStreamTracker();

        // 首帧只带身份、参数增量为空：先登记 alias，但不急着建卡。
        Assert.Null(
            tracker.Observe(
                Delta(delta: "", callId: "call_1", index: 0, name: "edit_entities")
            )
        );

        // 后续帧只带 Index 和参数：仍应解析回同一调用并发 Started。
        var started = tracker.Observe(Delta(delta: "{", callId: null, index: 0, name: null));
        Assert.NotNull(started);
        Assert.Equal("call_1", started.ToolCallKey);
        Assert.Equal("edit_entities", started.ToolName);
    }

    [Fact]
    public void ParallelCalls_SameName_DistinctIndex_GetDistinctKeys()
    {
        var tracker = new ToolCallStreamTracker();

        var first = tracker.Observe(
            Delta(callId: null, index: 0, name: "edit_entities")
        );
        var second = tracker.Observe(
            Delta(callId: null, index: 1, name: "edit_entities")
        );

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal("idx:0", first.ToolCallKey);
        Assert.Equal("idx:1", second.ToolCallKey);
        Assert.NotEqual(first.ToolCallKey, second.ToolCallKey);
    }

    [Fact]
    public void ParallelCalls_SameName_DistinctCallIds_GetDistinctKeys()
    {
        var tracker = new ToolCallStreamTracker();

        var first = tracker.Observe(
            Delta(callId: "call_a", index: null, name: "edit_entities")
        );
        var second = tracker.Observe(
            Delta(callId: "call_b", index: null, name: "edit_entities")
        );

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal("call_a", first.ToolCallKey);
        Assert.Equal("call_b", second.ToolCallKey);
        Assert.NotEqual(first.ToolCallKey, second.ToolCallKey);
    }

    [Fact]
    public void Observe_IgnoresAnonymousArgumentsWithoutIdentity()
    {
        var tracker = new ToolCallStreamTracker();
        Assert.Null(tracker.Observe(Delta(delta: "{", callId: null, index: null, name: null)));
    }

    [Fact]
    public void ResolveExecutionKey_NoStream_UsesNameKeyConsistently()
    {
        var tracker = new ToolCallStreamTracker();
        var complete = new FunctionCall { Name = "edit_entities" };

        Assert.Equal("name:edit_entities", tracker.ResolveExecutionKey(complete));
    }
}
