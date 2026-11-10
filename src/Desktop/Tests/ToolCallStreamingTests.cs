using Core.Agent;
using Desktop.Models;
using Desktop.Services;
using LlmTornado.Code;
using Xunit;
using ChatMessage = Desktop.Models.ChatMessage;

namespace Desktop.Tests;

/// <summary>
/// 工具调用参数流式阶段：Thinking 结束后立刻出现 pending 工具卡，
/// ToolExecutionStart 原地复用，ToolExecutionEnd/LlmStreamFinished 后不再有 processing 步骤。
/// </summary>
public sealed class ToolCallStreamingTests
{
    private static StreamChunk Text(string delta) => new(delta, "", StreamContentType.Text);

    private static StreamChunk Thinking(string delta) =>
        new(delta, "", StreamContentType.Thinking);

    [Fact]
    public void WhitespaceText_DoesNotCloseThinking()
    {
        var target = new ChatMessage(MessageSender.Agent);
        var handler = new StreamEventHandler(target);

        handler.Handle(Thinking("分析"));
        handler.Handle(Text("\n\n"));

        var thinking = Assert.Single(target.AgentSteps);
        Assert.True(thinking.IsProcessing);
        Assert.Equal("思考中…", thinking.Title);
        Assert.False(target.HasVisibleContent);
    }

    [Fact]
    public void ToolCallStreamStarted_CreatesPendingCardAndClosesThinking()
    {
        var target = new ChatMessage(MessageSender.Agent);
        var handler = new StreamEventHandler(target);

        handler.Handle(Thinking("分析"));
        handler.Handle(new ToolCallStreamStarted("call_1", "edit_entities", 0));

        Assert.Equal(2, target.AgentSteps.Count);
        Assert.Equal(AgentStepStatus.Completed, target.AgentSteps[0].Status);

        var pending = target.AgentSteps[1];
        Assert.Equal(StepKind.ToolCall, pending.Kind);
        Assert.Equal("edit_entities", pending.ToolName);
        Assert.True(pending.IsProcessing);
        Assert.Equal("正在生成参数…", pending.ArgumentsSummary);
    }

    [Fact]
    public void ToolExecutionStart_ReusesPendingCard()
    {
        var target = new ChatMessage(MessageSender.Agent);
        var handler = new StreamEventHandler(target);

        handler.Handle(new ToolCallStreamStarted("call_1", "edit_entities", 0));
        handler.Handle(
            new ToolExecutionStart(
                "provider-id",
                "edit_entities",
                """{"name":"林月如"}"""
            )
            {
                ToolCallKey = "call_1",
            }
        );

        var tool = Assert.Single(target.AgentSteps);
        Assert.True(tool.IsProcessing);
        Assert.Equal("林月如", tool.ArgumentsSummary);
    }

    [Fact]
    public void ToolExecutionEnd_ThenLlmStreamFinished_LeavesNoProcessingStep()
    {
        var target = new ChatMessage(MessageSender.Agent);
        var handler = new StreamEventHandler(target);

        handler.Handle(new ToolCallStreamStarted("call_1", "edit_entities", 0));
        handler.Handle(
            new ToolExecutionStart("provider-id", "edit_entities", "{}")
            {
                ToolCallKey = "call_1",
            }
        );
        handler.Handle(
            new ToolExecutionEnd("provider-id", false, "ok") { ToolCallKey = "call_1" }
        );
        handler.Handle(
            new LlmStreamFinished(ChatMessageFinishReasons.EndTurn, true, false, true)
        );

        var tool = Assert.Single(target.AgentSteps);
        Assert.Equal(AgentStepStatus.Completed, tool.Status);
        Assert.DoesNotContain(target.AgentSteps, step => step.IsProcessing);
    }

    [Fact]
    public void FinalizeProcessingSteps_MarksPendingAndExecutingCardsFailed()
    {
        var target = new ChatMessage(MessageSender.Agent);
        var handler = new StreamEventHandler(target);

        handler.Handle(new ToolCallStreamStarted("pending", "edit_entities", 0));
        handler.Handle(new ToolCallStreamStarted("running", "write_chapter", 1));
        handler.Handle(
            new ToolExecutionStart("provider-id", "write_chapter", "{}")
            {
                ToolCallKey = "running",
            }
        );

        handler.FinalizeProcessingSteps(interrupted: true);

        Assert.All(
            target.AgentSteps,
            step =>
            {
                Assert.Equal(AgentStepStatus.Failed, step.Status);
                Assert.True(step.IsError);
                Assert.Equal("已中断", step.ResultSummary);
            }
        );
    }

    [Fact]
    public void HiddenTool_DoesNotCreatePendingCard()
    {
        var target = new ChatMessage(MessageSender.Agent);
        var handler = new StreamEventHandler(target);

        handler.Handle(new ToolCallStreamStarted("hidden", "new_context", 0));

        Assert.Empty(target.AgentSteps);
    }
}
