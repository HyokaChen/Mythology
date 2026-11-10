using Desktop.Models;
using Xunit;

namespace Desktop.Tests;

public sealed class ConversationRowTests
{
    [Fact]
    public void HistoryProcess_FoldsToProcessRow_AndExpandsToSteps()
    {
        var message = new ChatMessage(MessageSender.Agent) { IsProcessExpanded = false };
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

        var folded = ConversationRow.FromMessage(message);
        var process = Assert.Single(folded);
        Assert.Equal(ConversationRowKind.Process, process.Kind);
        Assert.Contains("2 步", process.Message.ProcessSummary);
        Assert.Contains("搜索", process.Message.ProcessSummary);

        message.IsProcessExpanded = true;
        var expanded = ConversationRow.FromMessage(message);
        Assert.Equal(2, expanded.Count(row => row.Kind == ConversationRowKind.Step));
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
