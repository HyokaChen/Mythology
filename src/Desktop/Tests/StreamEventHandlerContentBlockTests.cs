using Core.Agent;
using Desktop.Models;
using Desktop.Services;
using Xunit;
using ChatMessage = Desktop.Models.ChatMessage;

namespace Desktop.Tests;

/// <summary>
/// 中间正文必须冻结在发生位置：工具/思考之间的 Text 不能统一追加到回合末尾，
/// 时间线应为「思考 → 正文 → 工具 → 思考 → 最终正文」。
/// </summary>
public sealed class StreamEventHandlerContentBlockTests
{
    private static StreamChunk Text(string delta) => new(delta, "", StreamContentType.Text);

    private static StreamChunk Thinking(string delta) => new(delta, "", StreamContentType.Thinking);

    private static void CallTool(
        StreamEventHandler handler,
        string id,
        string name,
        string args,
        string result = "ok"
    )
    {
        handler.Handle(new ToolExecutionStart(id, name, args));
        handler.Handle(new ToolExecutionEnd(id, false, result));
    }

    [Fact]
    public void TextBeforeFirstTool_BecomesContentStepBeforeTool()
    {
        var target = new ChatMessage(MessageSender.Agent);
        var handler = new StreamEventHandler(target);

        handler.Handle(Text("I'll start with the first search now."));
        CallTool(
            handler,
            "c1",
            "tool_search",
            """{"query":"project summary"}""",
            "找到 4 个相关工具"
        );

        Assert.Collection(
            target.AgentSteps,
            content =>
            {
                Assert.Equal(StepKind.Content, content.Kind);
                Assert.Equal("I'll start with the first search now.", content.ContentText);
            },
            tool => Assert.Equal(StepKind.ToolCall, tool.Kind)
        );
        Assert.Equal("", target.Content.ToString());
        Assert.False(target.HasVisibleContent);
    }

    [Fact]
    public void TextWithSurroundingBlankLines_IsTrimmedWhenSealed()
    {
        var target = new ChatMessage(MessageSender.Agent);
        var handler = new StreamEventHandler(target);

        // 模型常把 “\n\n” 一起流出来；尾部空行若原样进 TextBlock，会按 24px 行高
        // 在正文与下一步骤间撑出一大块空白。
        handler.Handle(Text("\n\nI'll start with the first search now.\n\n"));
        CallTool(handler, "c1", "tool_search", """{"query":"project summary"}""");

        var content = target.AgentSteps[0];
        Assert.Equal(StepKind.Content, content.Kind);
        Assert.Equal("I'll start with the first search now.", content.ContentText);
    }

    [Fact]
    public void TextBeforeThinking_BecomesContentStepBeforeThinking()
    {
        var target = new ChatMessage(MessageSender.Agent);
        var handler = new StreamEventHandler(target);

        handler.Handle(Text("先说明下一步。"));
        handler.Handle(Thinking("然后开始分析"));

        Assert.Collection(
            target.AgentSteps,
            content => Assert.Equal(StepKind.Content, content.Kind),
            thinking => Assert.Equal(StepKind.Thinking, thinking.Kind)
        );
        Assert.Equal("先说明下一步。", target.AgentSteps[0].ContentText);
        Assert.Equal("然后开始分析", target.AgentSteps[1].ThinkingText);
        Assert.Equal("", target.Content.ToString());
    }

    [Fact]
    public void MultipleContentPhases_StayChronological_AndFinalTextStaysInBubble()
    {
        var target = new ChatMessage(MessageSender.Agent);
        var handler = new StreamEventHandler(target);

        handler.Handle(Thinking("第一轮思考"));
        handler.Handle(Text("第一段说明。"));
        CallTool(handler, "c1", "tool_search", """{"query":"a"}""");
        handler.Handle(Thinking("第二轮思考"));
        handler.Handle(Text("第二段说明。"));
        CallTool(handler, "c2", "tool_search", """{"query":"b"}""");
        handler.Handle(Text("最终答复。"));

        Assert.Collection(
            target.AgentSteps,
            step => Assert.Equal(StepKind.Thinking, step.Kind),
            step =>
            {
                Assert.Equal(StepKind.Content, step.Kind);
                Assert.Equal("第一段说明。", step.ContentText);
            },
            step => Assert.Equal(StepKind.ToolCall, step.Kind),
            step => Assert.Equal(StepKind.Thinking, step.Kind),
            step =>
            {
                Assert.Equal(StepKind.Content, step.Kind);
                Assert.Equal("第二段说明。", step.ContentText);
            },
            step => Assert.Equal(StepKind.ToolCall, step.Kind)
        );
        Assert.Equal("最终答复。", target.Content.ToString());
        Assert.True(target.HasVisibleContent);
    }

    [Fact]
    public void HiddenContextTool_DoesNotSplitVisibleTextOrThinking()
    {
        var target = new ChatMessage(MessageSender.Agent);
        var handler = new StreamEventHandler(target);

        handler.Handle(Text("前文。"));
        handler.Handle(new ToolExecutionStart("c-hidden", "history_search_contents", "{}"));
        handler.Handle(Text("后文。"));
        handler.Handle(Thinking("一段推理"));
        handler.Handle(new ToolExecutionStart("c-hidden-2", "history_search_contents", "{}"));
        handler.Handle(Thinking("继续推理"));

        Assert.Collection(
            target.AgentSteps,
            content =>
            {
                Assert.Equal(StepKind.Content, content.Kind);
                Assert.Equal("前文。后文。", content.ContentText);
            },
            thinking =>
            {
                Assert.Equal(StepKind.Thinking, thinking.Kind);
                Assert.Equal("一段推理继续推理", thinking.ThinkingText);
            }
        );
        Assert.Equal("", target.Content.ToString());
    }

    [Fact]
    public void WhitespaceBeforeTool_DoesNotCreateBlankContentStep()
    {
        var target = new ChatMessage(MessageSender.Agent);
        var handler = new StreamEventHandler(target);

        handler.Handle(Text("\n\n  "));
        CallTool(handler, "c1", "tool_search", """{"query":"a"}""");

        var step = Assert.Single(target.AgentSteps);
        Assert.Equal(StepKind.ToolCall, step.Kind);
        Assert.Equal("", target.Content.ToString());
        Assert.False(target.HasVisibleContent);
    }

    [Fact]
    public void FinalTextWithoutFollowingBoundary_StaysInBubble()
    {
        var target = new ChatMessage(MessageSender.Agent);
        var handler = new StreamEventHandler(target);

        handler.Handle(Text("最终答复。"));
        handler.CompleteThinking();

        Assert.Empty(target.AgentSteps);
        Assert.Equal("最终答复。", target.Content.ToString());
        Assert.True(target.HasVisibleContent);
    }
}
