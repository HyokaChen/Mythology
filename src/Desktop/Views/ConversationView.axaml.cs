using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Desktop.Controls;
using Desktop.Models;
using Desktop.ViewModels;
using ReactiveUI.Primitives;

namespace Desktop.Views;

public partial class ConversationView : UserControl
{
    public ConversationView()
    {
        InitializeComponent();
        Loaded += (_, _) => MessageList.ResetFollow();
        Unloaded += (_, _) => StopBackgroundWork();
        MessageList.ItemsSource = _windowRows;
        MessageScroll.ScrollChanged += OnMessageScrollChanged;
        MessageList.LayoutUpdated += OnMessageListLayoutUpdated;
        // Enter 必须在 TextBox 处理 AcceptsReturn 之前拦截（隧道），
        // 否则 TextBox 会先插入换行、把 KeyDown 标记为已处理。
        AddHandler(KeyDownEvent, InputBox_OnPreviewKeyDown, RoutingStrategies.Tunnel);
        // 光标移动也会改变当前 @ / # token；只监听 TextChanged 会让
        // “先设置文本、后设置 CaretIndex”的路径停在旧建议上。
        InputBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.CaretIndexProperty)
                RefreshSuggestions();
        };
        // Avalonia 12：拖放事件通过附加事件路由注册
        AddHandler(DragDrop.DragOverEvent, InputBox_OnDragOver);
        AddHandler(DragDrop.DropEvent, InputBox_OnDrop);

        // 补全弹窗：点击条目直接插入（对齐 TUI 鼠标点击语义；纯键盘导航仍可用）
        SuggestionList.PointerReleased += SuggestionList_OnPointerReleased;
        CommandList.PointerReleased += CommandList_OnPointerReleased;
        // 键盘导航时让选中项保持可见（长列表不丢失视野）
        SuggestionList.SelectionChanged += (_, _) => ScrollSelectionIntoView(SuggestionList);
        CommandList.SelectionChanged += (_, _) => ScrollSelectionIntoView(CommandList);
        // 弹窗打开时列表可能尚未生成容器，SelectedIndex 绑定会停在 -1。
        // 打开后显式选中首项，保证 / 与 # 弹窗默认高亮第一行。
        SuggestionPopup.Opened += (_, _) => SelectFirstSuggestion();
        CommandPopup.Opened += (_, _) => SelectFirstCommand();
        // 会话切换 / /new 后数据上下文换新 VM：先重建行，再整页滑入。
        DataContextChanged += (_, _) =>
        {
            AttachMessageSource();
            if (!IsLoaded || DataContext is not ConversationViewModel)
                return;
            Dispatcher.UIThread.Post(
                () =>
                    _ = Desktop.Styles.Motion.PlayPageEnter(this, Desktop.Styles.Motion.PageShift),
                DispatcherPriority.Loaded
            );
        };
    }

    private ConversationViewModel? Vm => DataContext as ConversationViewModel;

    /// <summary>窗口关闭/切走时停掉后台折叠定时器。</summary>
    private void StopBackgroundWork() => CancelProcessFold();

    // ── 全量行模型 + 可见窗口 ────────────────────────────────────────────
    // _rows 保存所有消息的行模型（不挂控件）；_windowRows 只保存视口附近的行，
    // 由上下 spacer 撑起完整滚动高度。窗口外永远没有控件，更没有纯文本降级。

    private readonly ObservableCollection<ConversationRow> _rows = [];
    private readonly ObservableCollection<ConversationRow> _windowRows = [];
    private readonly Dictionary<ConversationRow, double> _measuredHeights = [];
    private readonly Dictionary<ChatMessage, List<ConversationRow>> _rowsByMessage = [];
    private readonly HashSet<ChatMessage> _subscribedMessages = [];
    private readonly HashSet<ChatMessage> _dirtyRowMessages = [];
    private readonly Dictionary<AgentStep, ChatMessage> _stepOwners = [];
    private readonly Dictionary<object, ChatMessage> _collectionOwners = [];
    private ConversationViewModel? _messagesVm;
    private bool _windowSyncScheduled;
    private bool _syncingWindow;
    private bool _captureScheduled;
    private bool _restoringOffset;
    private double _lastObservedOffset = double.NaN;
    private bool _bulkRestore;
    private bool _refreshScheduled;
    private bool _rowRefreshScheduled;
    private System.Threading.Timer? _processFoldTimer;
    private ChatMessage? _processFoldCandidate;

    /// <summary>DataContext 换 VM：重挂 Messages.CollectionChanged 并重建全部行。</summary>
    private void AttachMessageSource()
    {
        var vm = DataContext as ConversationViewModel;
        if (ReferenceEquals(vm, _messagesVm))
            return;

        if (_messagesVm is not null)
        {
            _messagesVm.Messages.CollectionChanged -= OnMessagesChanged;
            _messagesVm.PropertyChanged -= OnMessagesVmPropertyChanged;
        }

        _messagesVm = vm;
        _bulkRestore = false;
        _dirtyRowMessages.Clear();
        ClearRows();
        CancelProcessFold();
        if (_messagesVm is not null)
        {
            _messagesVm.Messages.CollectionChanged += OnMessagesChanged;
            _messagesVm.PropertyChanged += OnMessagesVmPropertyChanged;
        }

        RebuildRows();
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_messagesVm is null)
            return;

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                if (_bulkRestore)
                    return;
                // 仅尾部追加可以直接插行；中间插入（例如实体提示）走一次完整重建。
                if (e.NewStartingIndex >= _messagesVm.Messages.Count - e.NewItems.Count)
                {
                    foreach (var item in e.NewItems)
                        if (item is ChatMessage message)
                            AppendRowsForMessage(message);
                    RebuildHeightIndex();

                    // 跟随尾部时窗口滚到最新；阅读历史时只把新行计入底部 spacer，
                    // 用户当前视口不动。
                    if (MessageList.IsFollowing)
                        SyncWindowToTail();
                    else
                        UpdateWindowSpacers();
                }
                else
                {
                    ScheduleRowsRefresh();
                }
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                foreach (var item in e.OldItems)
                    if (item is ChatMessage message)
                        RemoveRowsForMessage(message);
                RebuildHeightIndex();
                RequestWindowSync();
                break;
            case NotifyCollectionChangedAction.Reset:
                _bulkRestore = true;
                ScheduleRowsRefresh();
                break;
            default:
                ScheduleRowsRefresh();
                break;
        }
    }

    private void OnMessagesVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConversationViewModel.IsProcessing) || _messagesVm is null)
            return;

        if (_messagesVm.IsProcessing)
        {
            CancelProcessFold();
            return;
        }

        for (var i = _rows.Count - 1; i >= 0; i--)
        {
            var message = _rows[i].Message;
            if (message.Sender == MessageSender.Agent && message.AgentSteps.Count > 0)
            {
                TryScheduleProcessFold(message);
                break;
            }
        }
    }

    private void ScheduleRowsRefresh()
    {
        if (_refreshScheduled)
            return;
        _refreshScheduled = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _refreshScheduled = false;
                _bulkRestore = false;
                RebuildRows();
            },
            DispatcherPriority.Background
        );
    }

    // ── 可见窗口：全量行模型 + 上下 spacer 虚拟化 ─────────────────────────

    private const double FallbackViewport = 600;
    private const int MinLiveRows = 48;
    private const int MaxLiveRows = 200;
    private const int LiveBufferRows = 20;
    private readonly HeightIndex<ConversationRow> _heightIndex = new();
    private readonly Dictionary<
        ChatMessage,
        (long Version, double Width, double Height)
    > _rowHeightCache = [];
    private double _estimateWidth = 800;

    private void OnMessageScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_syncingWindow || _restoringOffset)
            return;

        // 流式正文/spacer 只改变 extent 时 offset 不变；不要因此重算窗口并
        // 触发 anchor 修正，否则阅读历史时新增消息也会被拉走。
        var offset = MessageScroll.Offset.Y;
        if (double.IsFinite(_lastObservedOffset) && Math.Abs(offset - _lastObservedOffset) < 0.5)
            return;
        _lastObservedOffset = offset;
        RequestWindowSync();
    }

    private void OnMessageListLayoutUpdated(object? sender, EventArgs e)
    {
        if (_captureScheduled)
            return;
        _captureScheduled = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _captureScheduled = false;
                UpdateEstimateWidth();
                CaptureMeasuredRows();
                if (
                    MessageList.IsFollowing
                    && MessageScroll.Offset.Y
                        < Math.Max(0, MessageScroll.Extent.Height - MessageScroll.Viewport.Height)
                            - 0.5
                )
                    MessageList.ScrollToEndImmediate();
            },
            DispatcherPriority.Background
        );
    }

    private void RequestWindowSync()
    {
        if (_windowSyncScheduled || _syncingWindow)
            return;
        _windowSyncScheduled = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _windowSyncScheduled = false;
                SyncWindowForCurrentState();
            },
            DispatcherPriority.Background
        );
    }

    private void SyncWindowForCurrentState()
    {
        if (_syncingWindow)
            return;
        if (_rows.Count == 0)
        {
            SetWindow(0, 0);
            return;
        }
        if (MessageList.IsFollowing)
            SyncWindowToTail();
        else
            SyncWindowToViewport();
    }

    /// <summary>尾部窗口：保证最后一屏和预取缓冲全部可见，全部 LiveMarkdown。</summary>
    private void SyncWindowToTail()
    {
        if (_rows.Count == 0)
        {
            SetWindow(0, 0);
            return;
        }

        var maxLive = GetMaxLiveRows();
        var start = Math.Max(0, _rows.Count - maxLive);
        SetWindow(start, _rows.Count);
    }

    /// <summary>用户阅读历史：只实例化视口前后各一屏的行。</summary>
    private void SyncWindowToViewport()
    {
        if (_rows.Count == 0)
        {
            SetWindow(0, 0);
            return;
        }

        var viewport = ViewportHeight();
        var offset = MessageScroll.Offset.Y;
        var anchor = CaptureAnchor(offset);
        var focus = _heightIndex.IndexAtOffset(offset + viewport * 0.5);
        var maxLive = GetMaxLiveRows();
        var start = Math.Clamp(focus - LiveBufferRows, 0, Math.Max(0, _rows.Count - maxLive));
        var end = Math.Min(_rows.Count, start + maxLive);
        SetWindow(start, end);
        if (!MessageList.IsFollowing && anchor is { } value)
            RestoreAnchor(value.Row, value.Delta);
    }

    /// <summary>设置可见窗口并同步上下 spacer；窗口外没有控件，也没有纯文本。</summary>
    private void SetWindow(int start, int end)
    {
        start = Math.Clamp(start, 0, _rows.Count);
        end = Math.Clamp(end, start, _rows.Count);
        var desired = new List<ConversationRow>(end - start);
        for (var i = start; i < end; i++)
            desired.Add(_rows[i]);

        var top = TopOf(start);
        var bottom = Math.Max(0, TotalHeight() - TopOf(end));

        _syncingWindow = true;
        try
        {
            ReconcileWindowRows(desired);
            MessageList.TopSpacerHeight = top;
            MessageList.BottomSpacerHeight = bottom;
        }
        finally
        {
            _syncingWindow = false;
        }
    }

    /// <summary>
    /// 保留相同行对象的增量替换；只移除/插入差异，避免重建 Content 行里的
    /// MarkdownRenderer。窗口换页不调用 Clear()，否则 AutoScroll 会把 Reset
    /// 当成新会话并强制贴底。
    /// </summary>
    private void ReconcileWindowRows(List<ConversationRow> desired)
    {
        var old = _windowRows;
        var prefix = 0;
        while (
            prefix < old.Count
            && prefix < desired.Count
            && ReferenceEquals(old[prefix], desired[prefix])
        )
            prefix++;

        var suffix = 0;
        while (
            suffix < old.Count - prefix
            && suffix < desired.Count - prefix
            && ReferenceEquals(old[old.Count - 1 - suffix], desired[desired.Count - 1 - suffix])
        )
            suffix++;

        for (var i = old.Count - suffix - 1; i >= prefix; i--)
            old.RemoveAt(i);

        var oldCount = old.Count;
        for (var i = prefix; i < desired.Count - suffix; i++)
            old.Insert(oldCount + (i - prefix), desired[i]);
    }

    /// <summary>全量行模型变化但窗口对象不需要变化时，重算上下 spacer。</summary>
    private void UpdateWindowSpacers()
    {
        if (_windowRows.Count == 0)
        {
            MessageList.TopSpacerHeight = 0;
            MessageList.BottomSpacerHeight = _rows.Count == 0 ? 0 : TotalHeight();
            return;
        }

        var start = _rows.IndexOf(_windowRows[0]);
        if (start < 0)
            return;
        var end = Math.Min(_rows.Count, start + _windowRows.Count);
        MessageList.TopSpacerHeight = TopOf(start);
        MessageList.BottomSpacerHeight = Math.Max(0, TotalHeight() - TopOf(end));
    }

    /// <summary>重建全量行模型；行对象不挂控件，控件只由可见窗口创建。</summary>
    private void RebuildRows()
    {
        if (_messagesVm is null)
        {
            ClearRows();
            return;
        }

        ClearRows();
        var total = _messagesVm.Messages.Count;
        for (var i = 0; i < total; i++)
        {
            var message = _messagesVm.Messages[i];
            // 历史过程默认折叠；最后 4 条消息保留过程展开，当前回合随流式展开。
            message.IsProcessExpanded = i >= total - 4;
            AppendRowsForMessage(message);
        }
        RebuildHeightIndex();
        SyncWindowToTail();
        MessageList.ResetFollow();
    }

    // ── 高度索引：估算 + 实测缓存 + 锚点 ────────────────────────────────

    private double ViewportHeight() => Math.Max(FallbackViewport, MessageScroll.Viewport.Height);

    private double RowHeight(ConversationRow row)
    {
        if (_measuredHeights.TryGetValue(row, out var measured) && measured > 0.5)
            return measured;
        return EstimateRowHeight(row);
    }

    private double EstimateRowHeight(ConversationRow row)
    {
        if (
            row.Kind is ConversationRowKind.Content or ConversationRowKind.User
            && row.Message.Content is { } content
        )
        {
            var version = content.Version;
            if (
                _rowHeightCache.TryGetValue(row.Message, out var cached)
                && cached.Version == version
                && Math.Abs(cached.Width - _estimateWidth) <= 1
            )
                return cached.Height;

            var height =
                row.Kind == ConversationRowKind.Content
                    ? EstimateAgentContentHeight(content.ToString(), row)
                    : EstimateUserContentHeight(content.ToString(), row);
            _rowHeightCache[row.Message] = (version, _estimateWidth, height);
            return height;
        }

        return row.Kind switch
        {
            ConversationRowKind.Step => 54,
            ConversationRowKind.Process => 30,
            ConversationRowKind.Waiting => 30,
            ConversationRowKind.Interrupted => 28,
            ConversationRowKind.Sources => 20 + row.Message.Sources.Count * 16,
            ConversationRowKind.System => 28,
            _ => 32,
        };
    }

    private double EstimateAgentContentHeight(string content, ConversationRow row)
    {
        var bodyWidth = Math.Max(160, _estimateWidth - 34); // 16*2 padding + 1*2 border
        var body = MarkdownHeightEstimator.EstimateDocument(
            content,
            bodyWidth,
            MarkdownHeightEstimator.Conversation
        );
        return body + 26 + row.Margin.Bottom; // Bubble padding 12*2 + border 2
    }

    private double EstimateUserContentHeight(string content, ConversationRow row)
    {
        var textWidth = Math.Max(160, _estimateWidth - 32); // Bubble padding 16*2
        return MarkdownHeightEstimator.EstimatePlainText(content, textWidth, 15, 24)
            + 20 // Bubble padding 10*2
            + row.Margin.Bottom;
    }

    private void UpdateEstimateWidth()
    {
        var width = Math.Max(320, MessageScroll.Viewport.Width - 48);
        if (Math.Abs(width - _estimateWidth) <= 1)
            return;

        _estimateWidth = width;
        _rowHeightCache.Clear();
        _measuredHeights.Clear();
        RebuildHeightIndex();
    }

    private double TopOf(int index) => _heightIndex.OffsetOf(index);

    private double TotalHeight() => _heightIndex.Total;

    private int GetMaxLiveRows()
    {
        if (_heightIndex.Count == 0)
            return MinLiveRows;
        var average = Math.Max(24, _heightIndex.Total / _heightIndex.Count);
        var target = (int)Math.Ceiling(ViewportHeight() * 3 / average);
        return Math.Clamp(target, MinLiveRows, MaxLiveRows);
    }

    private void RebuildHeightIndex() => _heightIndex.Rebuild(_rows, RowHeight);

    /// <summary>
    /// 从已安排的行容器读回真实高度。只取 DataContext 为行、视觉层级最浅的
    /// ContentPresenter，避免把 Markdown 内部同 DataContext 的控件误当成整行。
    /// </summary>
    private void CaptureMeasuredRows()
    {
        if (_windowRows.Count == 0)
            return;

        var candidates = MessageList
            .GetVisualDescendants()
            .OfType<ContentPresenter>()
            .Where(p => p.DataContext is ConversationRow && p.Bounds.Height > 0.5)
            .Select(p => new
            {
                Row = (ConversationRow)p.DataContext!,
                Height = p.Bounds.Height,
                Depth = p.GetVisualAncestors().Count(),
            })
            .GroupBy(x => x.Row)
            .Select(g => g.OrderBy(x => x.Depth).First())
            .ToList();
        if (candidates.Count == 0)
            return;

        var following = MessageList.IsFollowing;
        var anchor = following ? null : CaptureAnchor(MessageScroll.Offset.Y);
        var changed = false;
        foreach (var candidate in candidates)
        {
            if (
                _measuredHeights.TryGetValue(candidate.Row, out var old)
                && Math.Abs(old - candidate.Height) <= 0.5
            )
                continue;
            _measuredHeights[candidate.Row] = candidate.Height;
            _heightIndex.Update(candidate.Row, candidate.Height);
            changed = true;
        }

        if (!changed)
            return;

        // 高度修正会改变 spacer 总高度；阅读状态下用锚点抵消。
        UpdateWindowSpacers();
        if (following)
            MessageList.ScrollToEndImmediate();
        else if (anchor is { } value)
            RestoreAnchor(value.Row, value.Delta);
    }

    private (ConversationRow Row, double Delta)? CaptureAnchor(double offset)
    {
        if (_rows.Count == 0)
            return null;

        var index = _heightIndex.IndexAtOffset(offset);
        var row = _rows[index];
        var top = _heightIndex.OffsetOf(index);
        return (row, Math.Max(0, offset - top));
    }

    private void RestoreAnchor(ConversationRow row, double delta)
    {
        var index = _rows.IndexOf(row);
        if (index < 0)
            return;

        var target = TopOf(index) + delta;
        var max = Math.Max(0, TotalHeight() - ViewportHeight());
        target = Math.Clamp(target, 0, max);
        if (Math.Abs(MessageScroll.Offset.Y - target) <= 0.5)
            return;

        _restoringOffset = true;
        try
        {
            MessageScroll.Offset = MessageScroll.Offset.WithY(target);
        }
        finally
        {
            _restoringOffset = false;
        }
    }

    // ── 扁平化行：一条消息拆成用户行/等待行/步骤行/正文行/中断行/来源行 ──

    private static List<ConversationRow> BuildRows(ChatMessage message) =>
        ConversationRow.FromMessage(message);

    private void AppendRowsForMessage(ChatMessage message)
    {
        var rows = BuildRows(message);
        _rowsByMessage[message] = rows;
        foreach (var row in rows)
            _rows.Add(row);
        SubscribeMessage(message);
    }

    private void RemoveRowsForMessage(ChatMessage message)
    {
        if (_rowsByMessage.Remove(message, out var rows))
        {
            foreach (var row in rows)
                _rows.Remove(row);
        }
        UnsubscribeMessage(message);
        _dirtyRowMessages.Remove(message);
    }

    private void ClearRows()
    {
        foreach (var message in _subscribedMessages.ToArray())
            UnsubscribeMessage(message);
        _rows.Clear();
        _windowRows.Clear();
        _measuredHeights.Clear();
        _heightIndex.Rebuild([], RowHeight);
        _rowsByMessage.Clear();
        _dirtyRowMessages.Clear();
        _stepOwners.Clear();
        MessageList.TopSpacerHeight = 0;
        MessageList.BottomSpacerHeight = 0;
        _lastObservedOffset = double.NaN;
        CancelProcessFold();
    }

    /// <summary>
    /// 增量同步行拓扑：只插入/移除变化的行，保留 Content 行与其中的 MarkdownRenderer 实例。
    /// 步骤追加、状态变化、过程折叠都不再重建整条消息的行树（对齐 TKCode.Arc 的原地 step view）。
    /// </summary>
    private void ReplaceRowsForMessage(ChatMessage message)
    {
        var messages = _messagesVm?.Messages;
        var messageIndex = messages?.IndexOf(message) ?? -1;
        if (messages is null || messageIndex < 0)
            return;

        var start = 0;
        for (var i = 0; i < messageIndex; i++)
        {
            if (_rowsByMessage.TryGetValue(messages[i], out var previous))
                start += previous.Count;
        }

        var oldRows = _rowsByMessage.TryGetValue(message, out var existing) ? existing : [];
        var desired = BuildRows(message);
        var selected = new List<ConversationRow>(desired.Count);
        var available = new Dictionary<(ConversationRowKind, AgentStep?), Queue<ConversationRow>>();
        foreach (var row in oldRows)
        {
            var key = (row.Kind, row.Step);
            if (!available.TryGetValue(key, out var queue))
                available[key] = queue = new Queue<ConversationRow>();
            queue.Enqueue(row);
        }
        foreach (var template in desired)
        {
            var key = (template.Kind, template.Step);
            selected.Add(
                available.TryGetValue(key, out var queue) && queue.Count > 0
                    ? queue.Dequeue()
                    : template
            );
        }

        var keep = new HashSet<ConversationRow>(selected);
        for (var i = oldRows.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(oldRows[i]))
                _rows.Remove(oldRows[i]);
        }

        for (var i = 0; i < selected.Count; i++)
        {
            var row = selected[i];
            var current = _rows.IndexOf(row);
            if (current >= 0)
            {
                if (current != start + i)
                    _rows.Move(current, start + i);
            }
            else
            {
                _rows.Insert(start + i, row);
            }
        }

        // 最后一行留白标记只影响 Margin，重建失败时也不影响内容行稳定性。
        for (var i = 0; i < selected.Count; i++)
            selected[i].IsLastInMessage = i == selected.Count - 1;
        _rowsByMessage[message] = selected;
        RebuildHeightIndex();
        RequestWindowSync();
    }

    private void SubscribeMessage(ChatMessage message)
    {
        if (!_subscribedMessages.Add(message))
            return;
        message.PropertyChanged += OnMessagePropertyChanged;
        message.AgentSteps.CollectionChanged += OnMessageStepsChanged;
        message.Sources.CollectionChanged += OnMessageSourcesChanged;
        _collectionOwners[message.AgentSteps] = message;
        _collectionOwners[message.Sources] = message;
        foreach (var step in message.AgentSteps)
            SubscribeStep(step, message);
    }

    private void UnsubscribeMessage(ChatMessage message)
    {
        if (!_subscribedMessages.Remove(message))
            return;
        message.PropertyChanged -= OnMessagePropertyChanged;
        message.AgentSteps.CollectionChanged -= OnMessageStepsChanged;
        message.Sources.CollectionChanged -= OnMessageSourcesChanged;
        _collectionOwners.Remove(message.AgentSteps);
        _collectionOwners.Remove(message.Sources);
        foreach (var pair in _stepOwners.Where(pair => pair.Value == message).ToArray())
            UnsubscribeStep(pair.Key);
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (
            sender is ChatMessage message
            && e.PropertyName
                is nameof(ChatMessage.IsWaiting)
                    or nameof(ChatMessage.HasVisibleContent)
                    or nameof(ChatMessage.IsInterrupted)
                    or nameof(ChatMessage.IsProcessExpanded)
        )
        {
            MarkRowsDirty(message);
            if (e.PropertyName == nameof(ChatMessage.IsWaiting))
                TryScheduleProcessFold(message);
        }
    }

    private void OnMessageStepsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (sender is null || !_collectionOwners.TryGetValue(sender, out var message))
            return;

        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems)
                if (item is AgentStep removed)
                    UnsubscribeStep(removed);
        }
        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems)
                if (item is AgentStep added)
                    SubscribeStep(added, message);
        }

        // 新一轮/新工具步骤出现：重新展开过程；用户手动操作过的保持用户意图。
        if (e.Action == NotifyCollectionChangedAction.Add && !message.ProcessTouchedByUser)
        {
            message.IsProcessExpanded = true;
            CancelProcessFold();
        }

        MarkRowsDirty(message);
        TryScheduleProcessFold(message);
    }

    private void SubscribeStep(AgentStep step, ChatMessage message)
    {
        if (!_stepOwners.TryAdd(step, message))
            return;
        step.PropertyChanged += OnStepPropertyChanged;
    }

    private void UnsubscribeStep(AgentStep step)
    {
        if (!_stepOwners.Remove(step))
            return;
        step.PropertyChanged -= OnStepPropertyChanged;
    }

    private void OnStepPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not AgentStep step || !_stepOwners.TryGetValue(step, out var message))
            return;
        if (e.PropertyName is not (nameof(AgentStep.Status) or nameof(AgentStep.IsProcessing)))
            return;

        // 失败/审批需要决定：自动展开过程，不再自动收。
        if (step.Status == AgentStepStatus.Failed)
        {
            message.IsProcessExpanded = true;
            CancelProcessFold();
            MarkRowsDirty(message);
            return;
        }

        TryScheduleProcessFold(message);
    }

    /// <summary>ToolCallGroup 语义：全部步骤结束后 450ms 自动折叠过程，除非用户手动展开过。</summary>
    private void TryScheduleProcessFold(ChatMessage message)
    {
        if (
            _messagesVm?.IsProcessing == true
            || !_subscribedMessages.Contains(message)
            || message.ProcessTouchedByUser
            || !message.IsProcessExpanded
            || message.IsWaiting
            || message.AgentSteps.Count == 0
            || message.AgentSteps.Any(step =>
                step.IsProcessing || step.Status == AgentStepStatus.Failed
            )
        )
            return;

        CancelProcessFold();
        _processFoldCandidate = message;
        _processFoldTimer = new System.Threading.Timer(
            _ => Dispatcher.UIThread.Post(OnProcessFoldTick, DispatcherPriority.Background),
            null,
            TimeSpan.FromMilliseconds(450),
            Timeout.InfiniteTimeSpan
        );
    }

    private void OnProcessFoldTick()
    {
        var message = _processFoldCandidate;
        CancelProcessFold();
        if (
            message is null
            || message.ProcessTouchedByUser
            || !message.IsProcessExpanded
            || message.IsWaiting
            || message.AgentSteps.Any(step =>
                step.IsProcessing || step.Status == AgentStepStatus.Failed
            )
        )
            return;
        message.IsProcessExpanded = false;
    }

    private void CancelProcessFold()
    {
        _processFoldTimer?.Dispose();
        _processFoldTimer = null;
        _processFoldCandidate = null;
    }

    private void OnMessageSourcesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (sender is not null && _collectionOwners.TryGetValue(sender, out var message))
            MarkRowsDirty(message);
    }

    private void MarkRowsDirty(ChatMessage message)
    {
        if (!_rowsByMessage.ContainsKey(message))
            return;
        if (!_dirtyRowMessages.Add(message) || _rowRefreshScheduled)
            return;

        _rowRefreshScheduled = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _rowRefreshScheduled = false;
                var dirty = _dirtyRowMessages.ToArray();
                _dirtyRowMessages.Clear();
                foreach (var item in dirty)
                    ReplaceRowsForMessage(item);
            },
            DispatcherPriority.Background
        );
    }

    /// <summary>展开历史过程的折叠行。</summary>
    private void ProcessRow_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ConversationRow { Message: var message } })
        {
            // 展开历史过程会改变行高；先退出尾部跟随，避免把视口拉回最后一条。
            MessageList.PauseFollow();
            message.ProcessTouchedByUser = true;
            CancelProcessFold();
            message.IsProcessExpanded = true;
        }
        e.Handled = true;
    }

    private void StepDetails_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ConversationRow { Step: { } step } })
            step.IsDetailsExpanded = !step.IsDetailsExpanded;
        e.Handled = true;
    }

    /// <summary>补全弹窗条目被点击：插入该项（仅当点击落在条目上,滚动条等不触发）。</summary>
    private void SuggestionList_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (Vm is not { } vm || e.Source is not Visual source)
            return;
        if (source.FindAncestorOfType<ListBoxItem>(true) is null)
            return;
        InsertSelectedSuggestion(vm);
    }

    /// <summary>命令弹窗条目被点击：插入该命令（仅当点击落在条目上）。</summary>
    private void CommandList_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (Vm is not { } vm || e.Source is not Visual source)
            return;
        if (source.FindAncestorOfType<ListBoxItem>(true) is null)
            return;
        InsertSelectedCommand(vm);
    }

    /// <summary>键盘导航时滚动到选中项（长列表保持可见）。</summary>
    private static void ScrollSelectionIntoView(ListBox list)
    {
        var index = list.SelectedIndex;
        if (index >= 0)
            list.ScrollIntoView(index);
    }

    /// <summary>弹窗打开后默认高亮首项（列表容器此时才完成生成）。</summary>
    private void SelectFirstSuggestion()
    {
        var vm = Vm;
        if (vm is null || vm.Suggestions.Count == 0)
            return;
        if (vm.SelectedSuggestionIndex != 0)
            vm.SelectedSuggestionIndex = 0;
        SuggestionList.SelectedIndex = 0;
        SuggestionList.ScrollIntoView(0);
    }

    /// <summary>命令弹窗打开后默认高亮首个命令。</summary>
    private void SelectFirstCommand()
    {
        var vm = Vm;
        if (vm is null || vm.CommandSuggestions.Count == 0)
            return;
        if (vm.SelectedCommandIndex != 0)
            vm.SelectedCommandIndex = 0;
        CommandList.SelectedIndex = 0;
        CommandList.ScrollIntoView(0);
    }

    /// <summary>空状态快捷操作：预填输入框（发送由用户确认）。</summary>
    private void QuickAction_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string action } && Vm is { } vm)
        {
            var prompt = action switch
            {
                "写一章" => "请根据当前设定写一章新内容，注意承接上一章情节，保持文风一致。",
                "建角色" => "请创建一个新角色，包含名字、性别、性格、外貌、背景与动机。",
                "埋伏笔" => "请为当前主线埋一个可回收的伏笔，并记录到情节线中。",
                "查设定" => "请检索并整理当前小说的主要设定（角色、地点、情节线）。",
                "续写" => "请续写当前章节，保持文风与设定一致，自然推进情节。",
                _ => action,
            };
            vm.AppendInput(prompt); // 仅预填，发送由用户按 Enter 确认
            InputBox.Focus();
        }
    }

    // ── 输入区按键：Enter 发送 / Shift+Enter 换行 / Esc 中断 / @ 弹窗导航 ──

    /// <summary>
    /// 隧道阶段拦截 Enter：TextBox.AcceptsReturn=true 时，类处理器会先插入换行，
    /// 冒泡的 KeyDown 再也收不到。这里先于 TextBox 处理，保证 Enter 发送、Shift+Enter 换行。
    /// </summary>
    private void InputBox_OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var vm = Vm;
        if (vm is null)
            return;

        // 中文输入法组合中（拼音未上屏）方向键属于候选词导航、Enter 属于确认，不能抢
        if (
            InputBox
                .GetVisualDescendants()
                .OfType<TextPresenter>()
                .Any(p => !string.IsNullOrEmpty(p.PreeditText))
        )
            return;

        // 弹窗导航必须在 TextBox 自己的 KeyDown 之前消费；否则多行输入会把
        // Up/Down 当成光标移动，弹窗永远收不到。
        if (TryHandlePopupKey(vm, e))
        {
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter || !InputBox.IsFocused)
            return;

        // 仅无修饰键发送；Shift/Ctrl/Alt+Enter 留给 TextBox 插入换行
        if (e.KeyModifiers != KeyModifiers.None)
            return;

        // 处理中（含提交准备阶段）且没有待发变体：不发送，也不让 TextBox 插入空换行
        if (vm.IsBusy && !vm.HasPendingRefinement)
        {
            e.Handled = true;
            return;
        }

        // 发送后回到最新内容：用户可能刚在历史里翻看，回车发送应看到自己刚发的消息，
        // 并让本轮流式输出继续跟随到底部。
        MessageList.ResetFollow();
        vm.SendMessageCommand.Execute().Subscribe();
        e.Handled = true;
    }

    /// <summary>
    /// 弹窗打开时消费 Enter / Up / Down / Tab / Esc。
    /// 返回 true 表示按键已归弹窗所有。
    /// </summary>
    private bool TryHandlePopupKey(ConversationViewModel vm, KeyEventArgs e)
    {
        if (vm.IsCommandPopupVisible)
        {
            switch (e.Key)
            {
                case Key.Down:
                    if (vm.CommandSuggestions.Count > 0)
                        vm.SelectedCommandIndex = Math.Min(
                            vm.SelectedCommandIndex + 1,
                            vm.CommandSuggestions.Count - 1
                        );
                    return true;
                case Key.Up:
                    if (vm.CommandSuggestions.Count > 0)
                        vm.SelectedCommandIndex = Math.Max(vm.SelectedCommandIndex - 1, 0);
                    return true;
                case Key.Tab:
                case Key.Enter:
                    InsertSelectedCommand(vm);
                    return true;
                case Key.Escape:
                    vm.IsCommandPopupVisible = false;
                    return true;
            }
        }

        if (vm.IsSuggestionPopupVisible)
        {
            switch (e.Key)
            {
                case Key.Down:
                    if (vm.Suggestions.Count > 0)
                        vm.SelectedSuggestionIndex = Math.Min(
                            vm.SelectedSuggestionIndex + 1,
                            vm.Suggestions.Count - 1
                        );
                    return true;
                case Key.Up:
                    if (vm.Suggestions.Count > 0)
                        vm.SelectedSuggestionIndex = Math.Max(vm.SelectedSuggestionIndex - 1, 0);
                    return true;
                case Key.Tab:
                case Key.Enter:
                    InsertSelectedSuggestion(vm);
                    return true;
                case Key.Escape:
                    vm.IsSuggestionPopupVisible = false;
                    return true;
            }
        }

        return false;
    }

    private void InputBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        var vm = Vm;
        if (vm is null)
            return;

        // 弹窗内的导航键已在隧道处理器中消费；这里只处理无弹窗时的历史/中断。

        // 输入历史导航：无弹窗 + 无多行 + 仅方向键（对齐 TUI 上下键历史）
        if (e.Key is Key.Up or Key.Down && InputBox.Text?.Contains('\n') != true)
        {
            if (InputBox.CaretIndex == 0 || e.Key == Key.Up)
            {
                var text = e.Key == Key.Up ? vm.NavigateHistoryUp() : vm.NavigateHistoryDown();
                if (text is not null)
                {
                    vm.SetHistoryDraft(InputBox.Text ?? "");
                    InputBox.Text = text;
                    InputBox.CaretIndex = text.Length;
                }
                e.Handled = true;
                return;
            }
        }

        switch (e.Key)
        {
            case Key.Escape:
                if (vm.IsBusy)
                    vm.StopProcessingCommand.Execute().Subscribe();
                e.Handled = true;
                break;
        }
    }

    private void InputBox_OnTextChanged(object? sender, TextChangedEventArgs e) =>
        RefreshSuggestions();

    /// <summary>按当前文本与光标重算补全弹窗（TextChanged 与程序化插入后共用）。</summary>
    private void RefreshSuggestions()
    {
        var vm = Vm;
        if (vm is null)
            return;

        var text = InputBox.Text ?? "";
        var caret = InputBox.CaretIndex;

        // 当前光标 token 优先：# / @ / slash。斜杠命令不能吞掉正在输入的 @path；
        // 例如 "/new @src/Desktop/" 应展示文件补全，而不是静默执行 /new 丢弃参数。
        var entityRef = EntityRefParser.TryParse(text, caret);
        if (entityRef.Stage != EntityRefParser.Stage.None)
        {
            vm.IsCommandPopupVisible = false;
            _ = vm.UpdateEntityRefSuggestionsAsync(entityRef);
            return;
        }

        var mention = MentionParser.TryGetMention(text, caret);
        if (mention.Found)
        {
            vm.IsCommandPopupVisible = false;
            _ = vm.UpdateMentionSuggestionsAsync(mention.Query);
            return;
        }

        var trimmed = text.TrimStart();
        var isCommand =
            (trimmed.StartsWith('/') || trimmed.StartsWith(':')) && !text.Contains('\n');
        if (isCommand)
        {
            vm.IsSuggestionPopupVisible = false;
            vm.UpdateCommandSuggestions(text);
            return;
        }

        // 非命令、非 @/#：关闭两个补全弹窗。
        vm.IsCommandPopupVisible = false;
        vm.IsSuggestionPopupVisible = false;
    }

    /// <summary>插入选中命令到输入框（按 VM 给出的替换区间；命令级补空格、/tools 插件级补冒号）。</summary>
    private void InsertSelectedCommand(ConversationViewModel vm)
    {
        var text = InputBox.Text ?? "";
        if (vm.CommandSuggestions.Count == 0)
        {
            vm.IsCommandPopupVisible = false;
            return;
        }

        var index = Math.Clamp(vm.SelectedCommandIndex, 0, vm.CommandSuggestions.Count - 1);
        var command = vm.CommandSuggestions[index];
        var start = Math.Clamp(vm.CommandReplaceStart, 0, text.Length);
        var length = Math.Clamp(vm.CommandReplaceLength, 0, text.Length - start);
        var insert = command + vm.CommandSuffix;
        InputBox.Text = text[..start] + insert + text[(start + length)..];
        InputBox.CaretIndex = start + insert.Length;
        vm.IsCommandPopupVisible = false;
        InputBox.Focus();
        // 级联补全：/tools → 插件、/tools 插件: → 工具；其余命令选中后自然关闭。
        RefreshSuggestions();
    }

    /// <summary>插入选中建议到输入框（# 一级 → "#类型:"；# 二级 → "#类型:名称 "；@ → "@路径"，目录尾带 / 继续导航）。</summary>
    private void InsertSelectedSuggestion(ConversationViewModel vm)
    {
        var text = InputBox.Text ?? "";
        var caret = InputBox.CaretIndex;
        var index = Math.Clamp(
            vm.SelectedSuggestionIndex,
            0,
            Math.Max(vm.Suggestions.Count - 1, 0)
        );

        var entityRef = EntityRefParser.TryParse(text, caret);
        if (entityRef.Stage != EntityRefParser.Stage.None)
        {
            if (vm.Suggestions.Count == 0)
            {
                vm.IsSuggestionPopupVisible = false;
                return;
            }

            var picked = vm.Suggestions[index].Name;
            var applied =
                entityRef.Stage == EntityRefParser.Stage.Type
                    ? EntityRefParser.InsertType(text, caret, entityRef, picked) // 一级 → 随后进入二级
                    : EntityRefParser.InsertName(text, caret, entityRef, picked); // 二级 → 随后关闭
            InputBox.Text = applied.Text;
            InputBox.CaretIndex = applied.Caret;
            InputBox.Focus();
            RefreshSuggestions();
            return;
        }

        var mention = MentionParser.TryGetMention(text, caret);
        if (!mention.Found || vm.Suggestions.Count == 0)
        {
            vm.IsSuggestionPopupVisible = false;
            return;
        }

        var suggestion = vm.Suggestions[index];
        var (newText, newCaret) = MentionParser.InsertSuggestion(
            text,
            caret,
            mention.Start,
            suggestion.Name
        );
        InputBox.Text = newText;
        InputBox.CaretIndex = newCaret;
        // 目录候选尾带 / → 继续展开下一级；文件候选补空格关闭弹窗
        vm.IsSuggestionPopupVisible = false;
        InputBox.Focus();
        if (suggestion.Name.EndsWith('/'))
            RefreshSuggestions(); // 目录 → 下一级导航
    }

    // ── 拖放：把文件/目录路径插入输入框（@ 路径语义，与补全插入格式一致） ──

    /// <summary>点击发送按钮不经过 Enter 路径，这里补一次跟随重置（命令执行后由 Loaded 追底）。</summary>
    private void SendButton_OnClick(object? sender, RoutedEventArgs e) => MessageList.ResetFollow();

    private void InputBox_OnDragOver(object? sender, DragEventArgs e)
    {
        var hasFiles = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void InputBox_OnDrop(object? sender, DragEventArgs e)
    {
        var paths = e
            .DataTransfer.GetItems(DataFormat.File)
            .Select(item => item.TryGetRaw(DataFormat.File))
            .OfType<Avalonia.Platform.Storage.IStorageItem>()
            .Select(item => item.Path.LocalPath)
            .ToArray();
        if (paths.Length == 0)
            return;

        var text = InputBox.Text ?? "";
        var insert = string.Join("\n", paths.Select(p => "@" + p));
        var caret = InputBox.CaretIndex;
        InputBox.Text = text.Insert(caret, insert + "\n");
        InputBox.CaretIndex = caret + insert.Length + 1;
        InputBox.Focus();
    }
}
