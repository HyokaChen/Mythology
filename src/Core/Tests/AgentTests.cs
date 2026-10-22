using Core.Agent;
using Core.Shared;
using Core.Tools;
using LlmTornado.Chat;
using LlmTornado.Code;
using Xunit;
using MythologyAgent = Core.Agent.Agent;

namespace Core.Tests;

/// <summary>
/// Agent 核心类测试：构造、状态管理、生命周期、消息管理、队列集成。
/// 不依赖真实 LLM API。
/// Note: AgentState 使用 init-only 属性，UpdateState 通过 Action 委托注入变更。
/// </summary>
public sealed class AgentTests
{
    private static ToolRegistry CreateRegistry() => new();

    private static MythologyAgent CreateAgent(ToolRegistry? registry = null)
    {
        return new MythologyAgent(registry ?? CreateRegistry());
    }

    // ══════════════════════════════════════════════════════
    //  构造与初始化
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Constructor_WithRegistry_SetsTools()
    {
        var registry = CreateRegistry();
        registry.Register(new StubTool("tool_a", "A", "desc"), "plugin");

        var agent = new MythologyAgent(registry);
        var state = agent.State;

        Assert.Single(state.Tools);
        Assert.Equal("tool_a", state.Tools[0].Name);
        Assert.Equal(AgentStateEnum.Idle, agent.AgentStateEnum);
    }

    [Fact]
    public void Constructor_NullRegistry_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new MythologyAgent(null!));
    }

    [Fact]
    public void Constructor_InitialState_Idle()
    {
        var agent = CreateAgent();
        Assert.Equal(AgentStateEnum.Idle, agent.AgentStateEnum);
        Assert.False(agent.IsRunning);
    }

    // ══════════════════════════════════════════════════════
    //  状态管理 API
    // ══════════════════════════════════════════════════════

    [Fact]
    public void SetSystemPrompt_UpdatesState()
    {
        var agent = CreateAgent();
        agent.SetSystemPrompt("You are a helpful assistant.");

        Assert.Equal("You are a helpful assistant.", agent.State.SystemPrompt);
    }

    [Fact]
    public void SetSystemPrompt_Empty_ClearsPrompt()
    {
        var agent = CreateAgent();
        agent.SetSystemPrompt("test");
        agent.SetSystemPrompt("");

        Assert.Equal("", agent.State.SystemPrompt);
    }

    [Fact]
    public void SetNovelContext_WithUserPrompt_AppendsToSystemPrompt()
    {
        var agent = CreateAgent();
        agent.SetSystemPrompt("User prompt");
        agent.SetNovelContext("Novel context");

        var state = agent.State;
        Assert.Contains("User prompt", state.SystemPrompt);
        Assert.Contains("Novel context", state.SystemPrompt);
    }

    [Fact]
    public void SetNovelContext_WithoutUserPrompt_OnlyContainsContext()
    {
        var agent = CreateAgent();
        agent.SetNovelContext("Novel context only");

        Assert.Equal("Novel context only", agent.State.SystemPrompt);
    }

    [Fact]
    public void SetNovelContext_CanBeUpdated()
    {
        var agent = CreateAgent();
        agent.SetNovelContext("first context");
        agent.SetNovelContext("second context");

        Assert.DoesNotContain("first context", agent.State.SystemPrompt);
        Assert.Contains("second context", agent.State.SystemPrompt);
    }

    [Fact]
    public void SetModelName_UpdatesState()
    {
        var agent = CreateAgent();
        agent.SetModelName("gpt-4o");

        Assert.Equal("gpt-4o", agent.State.ModelName);
    }

    [Fact]
    public void SetThinkingLevel_UpdatesState()
    {
        var agent = CreateAgent();
        agent.SetThinkingLevel("high");

        Assert.Equal("high", agent.State.ThinkingLevel);
    }

    [Fact]
    public void SetTools_ReplacesCurrentTools()
    {
        var agent = CreateAgent();
        var newTools = new[] { new StubTool("a", "A", "desc"), new StubTool("b", "B", "desc") };
        agent.SetTools(newTools);

        Assert.Equal(2, agent.State.Tools.Count);
    }

    [Fact]
    public void RefreshTools_FromRegistry_UpdatesTools()
    {
        var registry = CreateRegistry();
        registry.Register(new StubTool("initial", "Init", "desc"), "p");
        var agent = new MythologyAgent(registry);

        registry.Register(new StubTool("refreshed", "Refresh", "desc"), "p");
        agent.RefreshTools();

        Assert.Equal(2, agent.State.Tools.Count);
    }

    // ══════════════════════════════════════════════════════
    //  State snapshot
    // ══════════════════════════════════════════════════════

    [Fact]
    public void State_ReturnsCurrentState()
    {
        var agent = CreateAgent();
        agent.SetModelName("snapshot-model");

        var snapshot = agent.State;
        Assert.Equal("snapshot-model", snapshot.ModelName);
    }

    [Fact]
    public void State_ModelNameDefault_IsEmpty()
    {
        var agent = CreateAgent();
        Assert.Equal("", agent.State.ModelName);
    }

    [Fact]
    public void State_ThinkingLevelDefault_IsMedium()
    {
        var agent = CreateAgent();
        Assert.Equal("medium", agent.State.ThinkingLevel);
    }

    [Fact]
    public void State_PendingToolCalls_InitiallyEmpty()
    {
        var agent = CreateAgent();
        Assert.Empty(agent.State.PendingToolCalls);
    }

    // ══════════════════════════════════════════════════════
    //  消息管理
    // ══════════════════════════════════════════════════════

    [Fact]
    public void AppendMessage_AddsToHistory()
    {
        var agent = CreateAgent();
        var msg = new ChatMessage(ChatMessageRoles.User, "hello");

        agent.AppendMessage(msg);
        Assert.Single(agent.State.Messages);
        Assert.Equal("hello", agent.State.Messages[0].Content);
    }

    [Fact]
    public void AppendMessage_Multiple_AccumulatesMessages()
    {
        var agent = CreateAgent();
        agent.AppendMessage(new ChatMessage(ChatMessageRoles.User, "first"));
        agent.AppendMessage(new ChatMessage(ChatMessageRoles.Assistant, "second"));

        Assert.Equal(2, agent.State.Messages.Count);
    }

    [Fact]
    public void ReplaceMessages_ReplacesAll()
    {
        var agent = CreateAgent();
        agent.AppendMessage(new ChatMessage(ChatMessageRoles.User, "old"));

        agent.ReplaceMessages([
            new ChatMessage(ChatMessageRoles.User, "new1"),
            new ChatMessage(ChatMessageRoles.Assistant, "new2"),
        ]);

        Assert.Equal(2, agent.State.Messages.Count);
        Assert.Equal("new1", agent.State.Messages[0].Content);
    }

    [Fact]
    public void ClearMessages_RemovesAllMessages()
    {
        var agent = CreateAgent();
        agent.AppendMessage(new ChatMessage(ChatMessageRoles.User, "msg"));
        agent.ClearMessages();

        Assert.Empty(agent.State.Messages);
    }

    // ══════════════════════════════════════════════════════
    //  Reset
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Reset_ClearsMessages()
    {
        var agent = CreateAgent();
        agent.AppendMessage(new ChatMessage(ChatMessageRoles.User, "msg"));
        agent.Reset();

        Assert.Empty(agent.State.Messages);
    }

    [Fact]
    public void Reset_ClearsError()
    {
        var agent = CreateAgent();
        agent.Reset();

        Assert.Null(agent.State.Error);
    }

    [Fact]
    public void Reset_ClearsStreamingState()
    {
        var agent = CreateAgent();
        agent.Reset();

        Assert.False(agent.State.IsStreaming);
        Assert.Null(agent.State.StreamMessage);
        Assert.Empty(agent.State.PendingToolCalls);
    }

    // ══════════════════════════════════════════════════════
    //  Guard clauses
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task Prompt_WithoutTornadoApi_ThrowsInvalidOperation()
    {
        var agent = CreateAgent();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            agent.Prompt("test", TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public void IsRunning_False_Initially()
    {
        var agent = CreateAgent();
        Assert.False(agent.IsRunning);
    }

    // ══════════════════════════════════════════════════════
    //  Continue 方法
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task Continue_WithoutTornadoApi_Throws()
    {
        var agent = CreateAgent();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            agent.Continue(TestContext.Current.CancellationToken)
        );
    }

    // ══════════════════════════════════════════════════════
    //  属性访问
    // ══════════════════════════════════════════════════════

    [Fact]
    public void UsageStats_WithoutConversationLoop_IsNull()
    {
        var agent = CreateAgent();
        Assert.Null(agent.UsageStats);
    }

    [Fact]
    public void ToolExecution_DefaultIsParallel()
    {
        var agent = CreateAgent();
        Assert.Equal(ToolExecutionMode.Parallel, agent.ToolExecution);
    }

    [Fact]
    public void ToolExecution_CanSetSequential()
    {
        var agent = CreateAgent();
        agent.ToolExecution = ToolExecutionMode.Sequential;
        Assert.Equal(ToolExecutionMode.Sequential, agent.ToolExecution);
    }

    // ══════════════════════════════════════════════════════
    //  队列管理 (public API only)
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Steer_EnqueuesMessage()
    {
        var agent = CreateAgent();
        agent.Steer(new ChatMessage(ChatMessageRoles.User, "guidance"));

        Assert.True(agent.SteeringQueue.HasPending);
    }

    [Fact]
    public void ClearSteeringQueue_RemovesAll()
    {
        var agent = CreateAgent();
        agent.Steer(new ChatMessage(ChatMessageRoles.User, "m1"));
        agent.ClearSteeringQueue();

        Assert.False(agent.SteeringQueue.HasPending);
    }

    [Fact]
    public void ClearAllQueues_ClearsSteeringQueue()
    {
        var agent = CreateAgent();
        agent.Steer(new ChatMessage(ChatMessageRoles.User, "s"));
        agent.ClearAllQueues();

        Assert.False(agent.SteeringQueue.HasPending);
    }

    // ══════════════════════════════════════════════════════
    //  P0d: InjectQueue（系统通知队列）
    // ══════════════════════════════════════════════════════

    [Fact]
    public void InjectQueue_IsIndependentOfSteeringQueue()
    {
        var agent = CreateAgent();
        agent.InjectQueue.Enqueue(
            new ChatMessage(ChatMessageRoles.User, "[系统通知] 用户手动修改了角色「X」")
        );

        // 系统通知入队不影响用户 steer 队列
        Assert.True(agent.InjectQueue.HasPending);
        Assert.False(agent.SteeringQueue.HasPending);
    }

    [Fact]
    public void ClearAllQueues_ClearsInjectQueue()
    {
        var agent = CreateAgent();
        agent.InjectQueue.Enqueue(new ChatMessage(ChatMessageRoles.User, "[系统通知]"));
        agent.Steer(new ChatMessage(ChatMessageRoles.User, "user steer"));

        agent.ClearAllQueues();

        Assert.False(agent.InjectQueue.HasPending);
        Assert.False(agent.SteeringQueue.HasPending);
    }

    [Fact]
    public void Reset_ClearsInjectQueue()
    {
        var agent = CreateAgent();
        agent.InjectQueue.Enqueue(new ChatMessage(ChatMessageRoles.User, "[系统通知]"));

        agent.Reset();

        Assert.False(agent.InjectQueue.HasPending);
    }

    [Fact]
    public void SetSteeringMode_ChangesToSingleMode()
    {
        var agent = CreateAgent();
        agent.SetSteeringMode(QueueDeliveryMode.OneAtATime);

        Assert.Equal(QueueDeliveryMode.OneAtATime, agent.SteeringQueue.Mode);
    }

    [Fact]
    public void SetSteeringMode_DefaultIsAll()
    {
        var agent = CreateAgent();
        Assert.Equal(QueueDeliveryMode.All, agent.SteeringQueue.Mode);
    }

    // ══════════════════════════════════════════════════════
    //  钩子属性
    // ══════════════════════════════════════════════════════

    [Fact]
    public void BeforeToolCall_CanBeSetAndRead()
    {
        var agent = CreateAgent();
        var hook = new StubToolHook();
        agent.BeforeToolCall = hook;

        Assert.Same(hook, agent.BeforeToolCall);
    }

    [Fact]
    public void AfterToolCall_CanBeSetAndRead()
    {
        var agent = CreateAgent();
        var hook = new StubToolHook();
        agent.AfterToolCall = hook;

        Assert.Same(hook, agent.AfterToolCall);
    }

    [Fact]
    public void ShouldStopAfterTurn_CanBeSet()
    {
        var agent = CreateAgent();
        agent.ShouldStopAfterTurn = (_, _, _, _) => Task.FromResult(false);

        Assert.NotNull(agent.ShouldStopAfterTurn);
    }

    // ══════════════════════════════════════════════════════
    //  DisposeAsync
    // ══════════════════════════════════════════════════════

    [Fact]
    public async Task DisposeAsync_EmptyAgent_CleansUpWithoutError()
    {
        var agent = CreateAgent();
        await agent.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_WithMessages_CleansUpWithoutError()
    {
        var agent = CreateAgent();
        agent.AppendMessage(new ChatMessage(ChatMessageRoles.User, "test"));
        await agent.DisposeAsync();
    }

    // ══════════════════════════════════════════════════════
    //  InvalidateConversationLoop / SetHttpHandler
    // ══════════════════════════════════════════════════════

    [Fact]
    public void InvalidateConversationLoop_AfterReset_NoError()
    {
        var agent = CreateAgent();
        agent.InvalidateConversationLoop();
        Assert.Null(agent.UsageStats);
    }

    // ══════════════════════════════════════════════════════
    //  StabilityMonitor
    // ══════════════════════════════════════════════════════

    [Fact]
    public void StabilityMonitor_DefaultIsNull()
    {
        var agent = CreateAgent();
        Assert.Null(agent.StabilityMonitor);
    }

    // ══════════════════════════════════════════════════════
    //  Stub helpers
    // ══════════════════════════════════════════════════════

    private sealed class StubTool(string name, string label, string description) : AgentTool
    {
        public override string Name => name;
        public override string Label => label;
        public override string Description => description;
        public override System.Text.Json.JsonElement? Parameters => null;

        public override Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            System.Text.Json.JsonElement args,
            CancellationToken ct,
            IProgress<AgentToolResult>? onUpdate = null
        )
        {
            return Task.FromResult(
                new AgentToolResult { Content = [new TextContent("stub result")] }
            );
        }
    }

    private sealed class StubToolHook : IToolHook
    {
        public Task<ToolCallBeforeResult> BeforeToolCallAsync(
            ToolHookContext context,
            CancellationToken ct
        )
        {
            return Task.FromResult(ToolCallBeforeResult.Allow);
        }

        public Task AfterToolCallAsync(ToolHookContext context, CancellationToken ct)
        {
            return Task.CompletedTask;
        }
    }
}
