using Core.Agent;
using Core.Tools;
using LlmTornado.ChatFunctions;
using LlmTornado.Code;
using Xunit;
using ChatMessage = LlmTornado.Chat.ChatMessage;
using ChatMessageRoles = LlmTornado.Code.ChatMessageRoles;

namespace Core.Tests;

/// <summary>
/// 异常路径的 attempt 收口：已完成 tool result 必须进入持久化消息；
/// 没有 tool result 的 assistant/tool-call 半成品必须从 Conversation 丢弃。
/// </summary>
public sealed class ConversationLoopRecoveryTests
{
    private static ConversationLoop CreateLoop() =>
        new(
            api: new LlmTornado.TornadoApi([
                new ProviderAuthentication(LLmProviders.OpenAi, "test-key"),
            ]),
            modelName: "gpt-test",
            systemPrompt: "system",
            thinkingLevel: "medium",
            toolRegistry: new ToolRegistry(),
            toolExecution: ToolExecutionMode.Parallel
        );

    [Fact]
    public void CommitCompletedToolResults_CommitsAssistantAndToolResult()
    {
        var loop = CreateLoop();
        loop.RestoreFromMessages([new ChatMessage(ChatMessageRoles.User, "任务")]);
        var conversation = loop.Conversation!;
        var before = conversation.Messages.Count;

        var call = new FunctionCall { Name = "probe", Arguments = "{}" };
        call.ToolCall = new ToolCall { Id = "call-1", FunctionCall = call };
        conversation.AddMessage(
            new ChatMessage(ChatMessageRoles.Assistant, "") { ToolCalls = [call.ToolCall] }
        );
        conversation.AddMessage(
            new ChatMessage(ChatMessageRoles.Tool, "工具结果") { ToolCallId = "call-1" }
        );

        var committed = loop.CommitCompletedToolResultsForRecovery(before, "thinking");

        Assert.True(committed);
        Assert.Equal(3, loop.PersistedMessages.Count);
        Assert.Equal(ChatMessageRoles.Tool, loop.PersistedMessages[^1].Role);
        Assert.Equal("call-1", loop.PersistedMessages[^1].ToolCallId);
        Assert.Equal("thinking", loop.PersistedMessages[1].ReasoningContent);
        Assert.Equal(before + 2, conversation.Messages.Count);
    }

    [Fact]
    public void CommitCompletedToolResults_TrimsPartialAssistantWithoutToolResult()
    {
        var loop = CreateLoop();
        loop.RestoreFromMessages([new ChatMessage(ChatMessageRoles.User, "任务")]);
        var conversation = loop.Conversation!;
        var before = conversation.Messages.Count;

        conversation.AddMessage(new ChatMessage(ChatMessageRoles.Assistant, "半截 thinking"));

        var committed = loop.CommitCompletedToolResultsForRecovery(before, "半截 thinking");

        Assert.False(committed);
        Assert.Single(loop.PersistedMessages);
        Assert.Equal(before, conversation.Messages.Count);
    }

    [Fact]
    public void CommitCompletedToolResults_TrimsTrailingPartialAfterToolResult()
    {
        var loop = CreateLoop();
        loop.RestoreFromMessages([new ChatMessage(ChatMessageRoles.User, "任务")]);
        var conversation = loop.Conversation!;
        var before = conversation.Messages.Count;

        var call = new FunctionCall { Name = "probe", Arguments = "{}" };
        call.ToolCall = new ToolCall { Id = "call-1", FunctionCall = call };
        conversation.AddMessage(
            new ChatMessage(ChatMessageRoles.Assistant, "") { ToolCalls = [call.ToolCall] }
        );
        conversation.AddMessage(
            new ChatMessage(ChatMessageRoles.Tool, "工具结果") { ToolCallId = "call-1" }
        );
        conversation.AddMessage(new ChatMessage(ChatMessageRoles.Assistant, "尾随半截"));

        var committed = loop.CommitCompletedToolResultsForRecovery(before, "");

        Assert.True(committed);
        Assert.Equal(before + 2, conversation.Messages.Count);
        Assert.Equal(ChatMessageRoles.Tool, loop.PersistedMessages[^1].Role);
    }
}
