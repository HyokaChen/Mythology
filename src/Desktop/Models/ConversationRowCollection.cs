using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Desktop.Models;

/// <summary>
/// 测试/预览用的实时扁平行集合：消息状态、步骤或来源变化时重建行。
/// 生产视图有自己的窗口化增量行管理，不直接使用本类。
/// </summary>
public sealed class ConversationRowCollection : ObservableCollection<ConversationRow>
{
    private readonly List<ChatMessage> _messages = [];

    public ConversationRowCollection(params ChatMessage[] messages)
    {
        foreach (var message in messages)
        {
            _messages.Add(message);
            Subscribe(message);
        }
        Rebuild();
    }

    private void Subscribe(ChatMessage message)
    {
        message.PropertyChanged += OnMessagePropertyChanged;
        message.AgentSteps.CollectionChanged += OnCollectionChanged;
        message.Sources.CollectionChanged += OnCollectionChanged;
    }

    private void Unsubscribe(ChatMessage message)
    {
        message.PropertyChanged -= OnMessagePropertyChanged;
        message.AgentSteps.CollectionChanged -= OnCollectionChanged;
        message.Sources.CollectionChanged -= OnCollectionChanged;
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (
            e.PropertyName
            is nameof(ChatMessage.IsWaiting)
                or nameof(ChatMessage.HasVisibleContent)
                or nameof(ChatMessage.IsInterrupted)
        )
            Rebuild();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Rebuild();

    private void Rebuild()
    {
        var rows = _messages.SelectMany(ConversationRow.FromMessage).ToList();
        Clear();
        foreach (var row in rows)
            Add(row);
    }
}
