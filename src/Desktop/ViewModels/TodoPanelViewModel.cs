using System.Collections.ObjectModel;
using Avalonia.Threading;
using Core.Shared;
using Lucide.Avalonia;
using ReactiveUI;

namespace Desktop.ViewModels;

/// <summary>
/// Todo 任务面板视图模型（移植 TUI TodoPanel）：
/// 实时展示 todo_write 建立的任务清单（待办 / 进行中 / 完成）。
/// 订阅 TodoStore.Changed 刷新；变更可能来自 Agent 后台线程，调度回 UI 线程。
/// </summary>
public sealed class TodoPanelViewModel : ViewModelBase
{
    private readonly TodoStore _store;

    /// <summary>任务列表。</summary>
    public ObservableCollection<TodoItemRow> Items { get; } = [];

    /// <summary>列表为空（显示空态占位）。</summary>
    public bool IsEmpty
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    /// <summary>列表非空（控制面板可见性）。</summary>
    public bool HasItems
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public int CompletedCount
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public int TotalCount
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>设计时构造。</summary>
    public TodoPanelViewModel()
        : this(new TodoStore()) { }

    public TodoPanelViewModel(TodoStore store)
    {
        _store = store;
        _store.Changed += OnStoreChanged;
        Refresh();
    }

    /// <summary>解除事件订阅（MainWindow 关闭时调用）。</summary>
    public void Dispose()
    {
        _store.Changed -= OnStoreChanged;
    }

    private void OnStoreChanged() => Dispatcher.UIThread.Post(Refresh);

    /// <summary>重新加载任务列表。</summary>
    public void Refresh()
    {
        var items = _store.List();
        Items.Clear();
        foreach (var item in items)
            Items.Add(new TodoItemRow(item));

        var total = items.Count;
        var done = items.Count(i => i.Status == TodoStatus.Completed);
        TotalCount = total;
        CompletedCount = done;
        HasItems = total > 0;
        IsEmpty = total == 0;
    }
}

/// <summary>Todo 行（View 绑定）。</summary>
public sealed class TodoItemRow(TodoItem item) : ReactiveObject
{
    public TodoItem Item { get; } = item;

    public int Id => Item.Id;
    public string Subject => Item.Subject;
    public string Description => Item.Description;

    /// <summary>状态图标（Lucide 单色描边）。</summary>
    public LucideIconKind StatusIcon =>
        Item.Status switch
        {
            TodoStatus.InProgress => LucideIconKind.LoaderCircle,
            TodoStatus.Completed => LucideIconKind.CircleCheck,
            TodoStatus.Deleted => LucideIconKind.Trash,
            _ => LucideIconKind.Circle,
        };

    /// <summary>状态标签（中文）。</summary>
    public string StatusLabel =>
        Item.Status switch
        {
            TodoStatus.InProgress => "进行中",
            TodoStatus.Completed => "完成",
            _ => "待办",
        };

    /// <summary>状态着色（View 侧用 Triggers 或此标记）。</summary>
    public bool IsCompleted => Item.Status == TodoStatus.Completed;
    public bool IsInProgress => Item.Status == TodoStatus.InProgress;
}
