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
    private readonly HashSet<AgentStep> _steps = [];

    public ConversationRowCollection(params ChatMessage[] messages)
    {
        foreach (var message in messages)
        {
            _messages.Add(message);
            Subscribe(message);
        }
        SyncStepSubscriptions();
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

    private void OnStepPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (
            e.PropertyName
            is nameof(AgentStep.IsDetailsExpanded)
                or nameof(AgentStep.IsProcessing)
                or nameof(AgentStep.Status)
        )
            Rebuild();
    }

    /// <summary>
    /// 行模板由步骤状态决定；预览/测试集合没有生产视图的增量订阅，
    /// 这里补齐步骤级订阅，保证展开/状态切换能重建行。
    /// </summary>
    private void SyncStepSubscriptions()
    {
        var current = _messages.SelectMany(m => m.AgentSteps).ToHashSet();
        foreach (var step in _steps.Where(step => !current.Contains(step)).ToArray())
        {
            step.PropertyChanged -= OnStepPropertyChanged;
            _steps.Remove(step);
        }
        foreach (var step in current)
        {
            if (_steps.Add(step))
                step.PropertyChanged += OnStepPropertyChanged;
        }
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (
            e.PropertyName
            is nameof(ChatMessage.IsWaiting)
                or nameof(ChatMessage.HasVisibleContent)
                or nameof(ChatMessage.IsInterrupted)
                or nameof(ChatMessage.IsProcessExpanded)
                or nameof(ChatMessage.IsCompleted)
                or nameof(ChatMessage.IsEditing)
        )
            Rebuild();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncStepSubscriptions();
        Rebuild();
    }

    private void Rebuild()
    {
        var rows = _messages.SelectMany(ConversationRow.FromMessage).ToList();
        // 用逐项替换而不是 Clear：Clear 会触发 ItemsControl Reset，自动滚动
        // 把 Reset 当成“新数据源”强制回底，导致展开详情后跟随意向被重置。
        var common = Math.Min(Count, rows.Count);
        for (var i = 0; i < common; i++)
        {
            if (ReferenceEquals(this[i], rows[i]))
                continue;
            // Remove/Insert 会重建行容器；直接 this[i]= 只改 DataContext，
            // ItemsControl 不会重新按状态选 DataTemplate（展开详情会失效）。
            RemoveAt(i);
            Insert(i, rows[i]);
        }
        while (Count > rows.Count)
            RemoveAt(Count - 1);
        for (var i = common; i < rows.Count; i++)
            Add(rows[i]);
    }
}
