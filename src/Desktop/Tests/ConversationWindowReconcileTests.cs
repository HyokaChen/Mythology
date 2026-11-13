using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Desktop.Models;
using Desktop.Views;
using Xunit;

namespace Desktop.Tests;

public sealed class ConversationWindowReconcileTests
{
    [Fact]
    public void SlidingTailWindow_ReusesOverlappingRowObjects()
    {
        var message = new ChatMessage(MessageSender.Agent);
        var rows = Enumerable
            .Range(0, 6)
            .Select(i => new ConversationRow
            {
                Kind = ConversationRowKind.Step,
                Message = message,
                Step = new AgentStep { Kind = StepKind.ToolCall, Title = $"step {i}" },
            })
            .ToArray();
        var window = new ObservableCollection<ConversationRow>(rows.Take(4));
        var changes = new List<NotifyCollectionChangedAction>();
        window.CollectionChanged += (_, e) => changes.Add(e.Action);

        ConversationView.ReconcileWindowRows(window, rows.Skip(1).Take(4).ToList());

        Assert.Equal([rows[1], rows[2], rows[3], rows[4]], window);
        Assert.Equal(
            [NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Add],
            changes
        );
    }

    [Fact]
    public void DisjointWindow_ReplacesOnlyNonIntersectingRows()
    {
        var message = new ChatMessage(MessageSender.Agent);
        var rows = Enumerable
            .Range(0, 8)
            .Select(i => new ConversationRow
            {
                Kind = ConversationRowKind.Step,
                Message = message,
                Step = new AgentStep { Kind = StepKind.ToolCall, Title = $"step {i}" },
            })
            .ToArray();
        var window = new ObservableCollection<ConversationRow>(rows.Take(3));

        ConversationView.ReconcileWindowRows(window, rows.Skip(5).Take(3).ToList());

        Assert.Equal([rows[5], rows[6], rows[7]], window);
    }
}
