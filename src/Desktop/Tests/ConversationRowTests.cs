using Desktop.Models;
using Xunit;

namespace Desktop.Tests;

public sealed class ConversationRowTests
{
    [Fact]
    public void HistoryProcess_AlwaysEmitsStepRows()
    {
        var message = new ChatMessage(MessageSender.Agent);
        message.AgentSteps.Add(
            new AgentStep
            {
                Kind = StepKind.ToolCall,
                Title = "搜索",
                ToolName = "search",
            }
        );
        message.AgentSteps.Add(
            new AgentStep
            {
                Kind = StepKind.ToolCall,
                Title = "编辑",
                ToolName = "edit",
            }
        );

        var rows = ConversationRow.FromMessage(message);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(ConversationRowKind.Step, row.Kind));
    }

    /// <summary>
    /// 阶段之间的等待行必须挂在时间线尾部，而不是消息顶部。
    /// 「思考结束 → 工具开始」的间隙里，用户应看到最后一个步骤下方的执行中行。
    /// </summary>
    [Fact]
    public void WaitingRow_StaysAfterCompletedSteps()
    {
        var message = new ChatMessage(MessageSender.Agent) { IsWaiting = true };
        message.AgentSteps.Add(
            new AgentStep { Kind = StepKind.Thinking, Status = AgentStepStatus.Completed }
        );

        var rows = ConversationRow.FromMessage(message);

        Assert.Equal(2, rows.Count);
        Assert.Equal(ConversationRowKind.Step, rows[0].Kind);
        Assert.Equal(ConversationRowKind.Waiting, rows[1].Kind);
        Assert.True(rows[1].IsLastInMessage);
    }
}
