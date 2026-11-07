using System.Text.Json;
using Core.Agent;
using Core.Shared;
using Core.Tools;
using LlmTornado.ChatFunctions;
using Xunit;
using ChatMessage = LlmTornado.Chat.ChatMessage;
using ChatMessageRoles = LlmTornado.Code.ChatMessageRoles;

namespace Core.Tests;

/// <summary>
/// tool-call arguments 的三道防线：
/// 1. 恢复历史时修复（旧会话不再把空参数带回 API）；
/// 2. 执行入口拒绝空/非法参数（不用默认参数静默执行）；
/// 3. 识别 API 的 arguments must be valid JSON 400（供重试路径使用）。
/// </summary>
public sealed class ToolArgumentGuardTests
{
    [Fact]
    public void RestoreFromMessages_RepairsEmptyArguments()
    {
        var loop = CreateLoop();
        var msg = AssistantMessage(CreateFunctionCall("probe", ""));

        loop.RestoreFromMessages([msg]);

        Assert.Equal("{}", loop.PersistedMessages[0].ToolCalls![0].FunctionCall!.Arguments);
    }

    [Fact]
    public void RestoreFromMessages_KeepsValidArguments()
    {
        var loop = CreateLoop();
        var msg = AssistantMessage(CreateFunctionCall("probe", """{"x":1}"""));

        loop.RestoreFromMessages([msg]);

        Assert.Equal(
            """{"x":1}""",
            loop.PersistedMessages[0].ToolCalls![0].FunctionCall!.Arguments
        );
    }

    [Fact]
    public async Task ExecuteFunctionCall_EmptyArguments_RejectsWithoutExecuting()
    {
        var ct = TestContext.Current.CancellationToken;
        var tool = new ProbeTool();
        var registry = new ToolRegistry();
        registry.Register(tool, "Core");
        var loop = CreateLoop(registry);

        var call = CreateFunctionCall("probe", "");
        var events = new List<AgentEvent>();
        await loop.ExecuteFunctionCallAsync(
            call,
            (evt, _) =>
            {
                events.Add(evt);
                return Task.CompletedTask;
            },
            ct
        );

        Assert.Equal(0, tool.ExecutionCount);
        Assert.Equal("{}", call.Arguments);
        Assert.False(call.Result!.InvocationSucceeded);
        Assert.Contains(
            events.OfType<ToolExecutionEnd>(),
            e => e.IsError && e.Result!.Contains("arguments", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public async Task ExecuteFunctionCall_InvalidArguments_RejectsWithoutExecuting()
    {
        var ct = TestContext.Current.CancellationToken;
        var tool = new ProbeTool();
        var registry = new ToolRegistry();
        registry.Register(tool, "Core");
        var loop = CreateLoop(registry);

        var call = CreateFunctionCall("probe", "not-json");
        await loop.ExecuteFunctionCallAsync(call, (_, _) => Task.CompletedTask, ct);

        Assert.Equal(0, tool.ExecutionCount);
        Assert.Equal("{}", call.Arguments);
        Assert.False(call.Result!.InvocationSucceeded);
    }

    [Fact]
    public async Task ExecuteFunctionCall_ValidArguments_Executes()
    {
        var ct = TestContext.Current.CancellationToken;
        var tool = new ProbeTool();
        var registry = new ToolRegistry();
        registry.Register(tool, "Core");
        var loop = CreateLoop(registry);

        var call = CreateFunctionCall("probe", """{"x":1}""");
        await loop.ExecuteFunctionCallAsync(call, (_, _) => Task.CompletedTask, ct);

        Assert.Equal(1, tool.ExecutionCount);
        Assert.NotEqual(false, call.Result!.InvocationSucceeded);
    }

    [Fact]
    public void IsInvalidToolArgsError_DetectsWrappedProviderError()
    {
        var inner = new Exception("Assistant tool call ***.arguments must be valid JSON.");
        var outer = new Exception("BadRequest", inner);

        Assert.True(ConversationLoop.IsInvalidToolArgsError(outer));
        Assert.False(ConversationLoop.IsInvalidToolArgsError(new Exception("rate limited")));
    }

    // ── helpers ────────────────────────────────────────────────

    private static ConversationLoop CreateLoop(ToolRegistry? registry = null) =>
        new(
            api: new LlmTornado.TornadoApi([
                new LlmTornado.Code.ProviderAuthentication(
                    LlmTornado.Code.LLmProviders.OpenAi,
                    "test-key"
                ),
            ]),
            modelName: "gpt-test",
            systemPrompt: "system",
            thinkingLevel: "medium",
            toolRegistry: registry ?? new ToolRegistry(),
            toolExecution: ToolExecutionMode.Parallel
        );

    private static FunctionCall CreateFunctionCall(string name, string? arguments)
    {
        var functionCall = new FunctionCall { Name = name, Arguments = arguments };
        functionCall.ToolCall = new ToolCall { Id = "call-1", FunctionCall = functionCall };
        return functionCall;
    }

    private static ChatMessage AssistantMessage(FunctionCall functionCall) =>
        new(ChatMessageRoles.Assistant, "") { ToolCalls = [functionCall.ToolCall!] };

    private sealed class ProbeTool : AgentTool
    {
        public int ExecutionCount { get; private set; }

        public override string Name => "probe";
        public override string Label => "Probe";
        public override string Description => "测试工具";
        public override JsonElement? Parameters => null;

        public override Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            JsonElement args,
            CancellationToken ct,
            IProgress<AgentToolResult>? onUpdate = null
        )
        {
            ExecutionCount++;
            return Task.FromResult(new AgentToolResult { Content = [new TextContent("ok")] });
        }
    }
}
