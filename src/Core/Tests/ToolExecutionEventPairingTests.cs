using System.Text.Json;
using Core.Agent;
using Core.Shared;
using Core.Tools;
using LlmTornado;
using LlmTornado.ChatFunctions;
using LlmTornado.Code;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 工具调用事件配对契约：每次工具调用有且只有一个 <see cref="ToolExecutionStart"/>，
/// 且它与 <see cref="ToolExecutionEnd"/> 成对出现。
/// </summary>
/// <remarks>
/// 三个前端（Cli/Tui/Desktop）都靠 Start 建卡并取得工具名，End 只按 ToolCallId 更新既有卡：
/// 少一个 Start，未注册工具的失败会被静默丢弃（界面上「什么都没发生」）；
/// 多一个 Start，同一个 ToolCallId 会建出两张卡，先建的那张永远停在「运行中」。
/// </remarks>
public sealed class ToolExecutionEventPairingTests
{
    private static ConversationLoop MakeLoop(ToolRegistry registry) =>
        new(
            new TornadoApi(LLmProviders.OpenAi, "test-key"),
            "test-model",
            "",
            "medium",
            registry,
            ToolExecutionMode.Sequential
        );

    private static async Task<List<AgentEvent>> RecordAsync(
        ConversationLoop loop,
        FunctionCall call
    )
    {
        var events = new List<AgentEvent>();
        await loop.ExecuteFunctionCallAsync(
            call,
            (evt, _) =>
            {
                events.Add(evt);
                return Task.CompletedTask;
            },
            CancellationToken.None
        );
        return events;
    }

    [Fact]
    public async Task UnknownTool_EmitsStartThenFailedEnd()
    {
        await using var loop = MakeLoop(new ToolRegistry());

        var events = await RecordAsync(
            loop,
            new FunctionCall { Name = "ghost_tool", Arguments = """{"path":"a.md"}""" }
        );

        Assert.Equal(2, events.Count);

        // Start 必须带工具名——End 事件没有名字字段，前端只能从这里拿到。
        var start = Assert.IsType<ToolExecutionStart>(events[0]);
        Assert.Equal("ghost_tool", start.ToolName);

        var end = Assert.IsType<ToolExecutionEnd>(events[1]);
        Assert.Equal(start.ToolCallId, end.ToolCallId);
        Assert.True(end.IsError);
        Assert.Contains("ghost_tool", end.Result);
    }

    [Fact]
    public async Task RegisteredTool_EmitsExactlyOneStart()
    {
        var registry = new ToolRegistry();
        registry.Register(new EchoTool(), "Test");
        await using var loop = MakeLoop(registry);

        var events = await RecordAsync(
            loop,
            new FunctionCall { Name = EchoTool.ToolName, Arguments = "{}" }
        );

        var start = Assert.Single(events.OfType<ToolExecutionStart>());
        Assert.Equal(EchoTool.ToolName, start.ToolName);

        var end = Assert.Single(events.OfType<ToolExecutionEnd>());
        Assert.Equal(start.ToolCallId, end.ToolCallId);
        Assert.False(end.IsError);
    }

    /// <summary>最小可执行工具：只回一段文本，不触碰任何外部状态。</summary>
    private sealed class EchoTool : AgentTool
    {
        public const string ToolName = "test_echo";

        public override string Name => ToolName;

        public override string Label => "回声";

        public override string Description => "回显测试工具";

        public override JsonElement? Parameters { get; } =
            JsonSchemaHelper.GenerateSchema<object>();

        public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential;

        public override Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            JsonElement args,
            CancellationToken ct,
            IProgress<AgentToolResult>? onUpdate = null
        ) => Task.FromResult(new AgentToolResult { Content = [new TextContent("ok")] });
    }
}
