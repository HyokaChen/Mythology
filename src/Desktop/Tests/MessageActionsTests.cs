using Desktop.Controls;
using Desktop.Models;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 消息动作的纯逻辑契约：复制文本口径 + 无正文助手回合的重试落点。
/// UI 点击/hover 由 ConversationViewStructureTests 与进程内探针覆盖。
/// </summary>
public sealed class MessageActionsTests
{
    [Fact]
    public void Copy_UserMessage_KeepsRawText()
    {
        var message = new ChatMessage(MessageSender.User);
        message.Content.Append("假设我只有一份 docx，怎么构建？");

        var row = new ConversationRow { Kind = ConversationRowKind.User, Message = message };

        Assert.Equal("假设我只有一份 docx，怎么构建？", MessageCopy.Build(row));
    }

    [Fact]
    public void Copy_AssistantContent_StripsMarkdownMarkers()
    {
        var message = new ChatMessage(MessageSender.Agent) { IsCompleted = true };
        message.AppendContent("## 标题\n\n**加粗**和 `code`");
        var row = new ConversationRow { Kind = ConversationRowKind.Content, Message = message };

        var text = MessageCopy.Build(row);

        Assert.Contains("标题", text);
        Assert.Contains("加粗", text);
        Assert.Contains("code", text);
        Assert.DoesNotContain("**", text);
        Assert.DoesNotContain("##", text);
    }

    [Fact]
    public void Copy_ThinkingStep_CopiesReasoningText()
    {
        var step = new AgentStep { Kind = StepKind.Thinking };
        step.ThinkingBuilder.Append("先分析人物动机");
        step.NotifyThinkingChanged();
        var message = new ChatMessage(MessageSender.Agent) { IsCompleted = true };
        message.AgentSteps.Add(step);
        var row = new ConversationRow
        {
            Kind = ConversationRowKind.Step,
            Message = message,
            Step = step,
        };

        Assert.Equal("先分析人物动机", MessageCopy.Build(row));
    }

    [Fact]
    public void Copy_ToolStep_IncludesArgumentsAndResult()
    {
        var step = new AgentStep
        {
            Kind = StepKind.ToolCall,
            Title = "编辑",
            FullArguments = """{"path":"chapter.md"}""",
            FullResult = "已写入 1200 字",
        };
        var message = new ChatMessage(MessageSender.Agent) { IsCompleted = true };
        message.AgentSteps.Add(step);
        var row = new ConversationRow
        {
            Kind = ConversationRowKind.Step,
            Message = message,
            Step = step,
        };

        var text = MessageCopy.Build(row);

        Assert.Contains("编辑", text);
        Assert.Contains("参数", text);
        Assert.Contains("chapter.md", text);
        Assert.Contains("结果", text);
        Assert.Contains("已写入 1200 字", text);
    }

    [Fact]
    public void CompletedContentlessAgent_GetsActionsRow_ForRetry()
    {
        var message = new ChatMessage(MessageSender.Agent)
        {
            IsCompleted = true,
            IsProcessExpanded = false,
        };
        message.AgentSteps.Add(
            new AgentStep
            {
                Kind = StepKind.ToolCall,
                Title = "搜索",
                Status = AgentStepStatus.Completed,
            }
        );

        var rows = ConversationRow.FromMessage(message);

        Assert.Contains(rows, row => row.Kind == ConversationRowKind.Process);
        Assert.Equal(ConversationRowKind.Actions, rows[^1].Kind);
        Assert.True(rows[^1].IsLastInMessage);
    }

    [Fact]
    public void CompletedAgentWithContent_HasNoSeparateActionsRow()
    {
        var message = new ChatMessage(MessageSender.Agent) { IsCompleted = true };
        message.AppendContent("最终答复");

        var rows = ConversationRow.FromMessage(message);

        Assert.Contains(rows, row => row.Kind == ConversationRowKind.Content);
        Assert.DoesNotContain(rows, row => row.Kind == ConversationRowKind.Actions);
    }

    [Fact]
    public void Copy_ActionsRow_FallsBackToStepSummary()
    {
        var message = new ChatMessage(MessageSender.Agent) { IsCompleted = true };
        message.AgentSteps.Add(
            new AgentStep
            {
                Kind = StepKind.ToolCall,
                Title = "搜索",
                ResultSummary = "找到 3 章",
                Status = AgentStepStatus.Completed,
            }
        );
        var row = new ConversationRow { Kind = ConversationRowKind.Actions, Message = message };

        var text = MessageCopy.Build(row);

        Assert.Contains("搜索", text);
        Assert.Contains("找到 3 章", text);
    }
}
