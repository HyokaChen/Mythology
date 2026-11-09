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
}
