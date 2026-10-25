using Core.Agent;
using Desktop.Models;
using Desktop.Services;
using Xunit;
using ChatMessage = Desktop.Models.ChatMessage;

namespace Desktop.Tests;

/// <summary>
/// 思考步骤标签必须跟状态走：流式中「思考中…」，结束后「思考过程」；
/// 模型只调用工具、Core 不发 ThinkingEnd 时，回合收尾也要兜底关闭。
/// </summary>
public sealed class StreamEventHandlerThinkingTests
{
    [Fact]
    public void ThinkingEnd_ChangesLabelToCompletedProcess()
    {
        var target = new ChatMessage(MessageSender.Agent);
        var handler = new StreamEventHandler(target);

        handler.Handle(new StreamChunk("先分析", "", StreamContentType.Thinking));

        var step = Assert.Single(target.AgentSteps);
        Assert.Equal("思考中…", step.Title);
        Assert.True(step.IsProcessing);
        Assert.True(step.HasDetails);
        Assert.True(step.IsDetailsExpanded);
        Assert.Equal("收起", step.DetailsToggleLabel);

        var changed = new List<string?>();
        step.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        handler.Handle(new StreamChunk("", "", StreamContentType.ThinkingEnd));

        Assert.Equal("思考过程", step.Title);
        Assert.False(step.IsProcessing);
        Assert.Equal(AgentStepStatus.Completed, step.Status);
        Assert.Contains(nameof(AgentStep.Title), changed);
    }

    [Fact]
    public void CompleteThinking_ClosesStepWhenNoThinkingEndArrives()
    {
        var target = new ChatMessage(MessageSender.Agent);
        var handler = new StreamEventHandler(target);

        handler.Handle(new StreamChunk("只有工具调用", "", StreamContentType.Thinking));
        handler.CompleteThinking();

        var step = Assert.Single(target.AgentSteps);
        Assert.Equal("思考过程", step.Title);
        Assert.False(step.IsProcessing);
    }

    [Fact]
    public void ThinkingAfterCompletedPhase_StartsNewIndependentStep()
    {
        var target = new ChatMessage(MessageSender.Agent);
        var handler = new StreamEventHandler(target);

        handler.Handle(new StreamChunk("第一轮", "", StreamContentType.Thinking));
        handler.Handle(new StreamChunk("", "", StreamContentType.ThinkingEnd));
        handler.Handle(new StreamChunk("第二轮", "", StreamContentType.Thinking));

        Assert.Equal(2, target.AgentSteps.Count);
        var first = target.AgentSteps[0];
        var second = target.AgentSteps[1];
        Assert.NotSame(first, second);
        Assert.Equal(AgentStepStatus.Completed, first.Status);
        Assert.Equal("思考过程", first.Title);
        Assert.Equal("第一轮", first.ThinkingText);
        Assert.Equal(AgentStepStatus.Processing, second.Status);
        Assert.Equal("思考中…", second.Title);
        Assert.Equal("第二轮", second.ThinkingText);

        var changed = new List<string?>();
        second.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        handler.Handle(new StreamChunk("", "", StreamContentType.ThinkingEnd));
        Assert.Equal("思考过程", second.Title);
        Assert.Equal("第一轮", first.ThinkingText);
    }

    [Fact]
    public void ToolCallBetweenThinkingPhases_ProducesChronologicalSteps()
    {
        var target = new ChatMessage(MessageSender.Agent);
        var handler = new StreamEventHandler(target);

        handler.Handle(new StreamChunk("先搜索项目摘要", "", StreamContentType.Thinking));
        handler.Handle(new ToolExecutionStart("c1", "tool_search", """{"query":"项目摘要"}"""));
        handler.Handle(new ToolExecutionEnd("c1", false, "找到 5 个相关工具"));
        handler.Handle(new StreamChunk("再根据结果总结", "", StreamContentType.Thinking));

        Assert.Collection(
            target.AgentSteps,
            first =>
            {
                Assert.Equal(StepKind.Thinking, first.Kind);
                Assert.Equal(AgentStepStatus.Completed, first.Status);
                Assert.Equal("先搜索项目摘要", first.ThinkingText);
            },
            tool =>
            {
                Assert.Equal(StepKind.ToolCall, tool.Kind);
                Assert.Equal("tool_search", tool.ToolName);
                Assert.Equal(AgentStepStatus.Completed, tool.Status);
            },
            second =>
            {
                Assert.Equal(StepKind.Thinking, second.Kind);
                Assert.Equal(AgentStepStatus.Processing, second.Status);
                Assert.Equal("再根据结果总结", second.ThinkingText);
            }
        );
    }
}
