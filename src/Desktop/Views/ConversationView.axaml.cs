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
        Loaded += (_, _) => ScheduleIdlePrefetch();
        Unloaded += (_, _) => StopBackgroundWork();
        MessageList.ItemsSource = _visibleRows;
        MessageList.ScrolledToTop += MessageList_OnScrolledToTop;
        // 距顶部 2 屏就预取下一页：用户撞到顶之前内容已插入，滚动不会在顶部停顿。
        MessageScroll.ScrollChanged += MessageScroll_OnScrollChanged;
        // Enter 必须在 TextBox 处理 AcceptsReturn 之前拦截（隧道），
        // 否则 TextBox 会先插入换行、把 KeyDown 标记为已处理。
        AddHandler(KeyDownEvent, InputBox_OnPreviewKeyDown, RoutingStrategies.Tunnel);
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
        // 会话切换 / /new 后数据上下文换新 VM：先重建窗口，再整页滑入。
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

    /// <summary>窗口关闭/切走时停掉预取与富文本升级定时器，避免后台继续跑布局。</summary>
    private void StopBackgroundWork()
    {
        _drainTimer?.Stop();
        _richUpgradeTimer?.Stop();
        CancelProcessFold();
        _earlierQueue.Clear();
        _loadingEarlier = false;
        _idleDrain = false;
        _idlePrefetchScheduled = false;
    }

    // ── 消息窗口：尾部约 3 屏 + 滚到顶部增量加载 ─────────────────────────

    /// <summary>
    /// 不使用 VirtualizingStackPanel：stock VSP 的 extent 只按当前 realized 窗口估算，
    /// 对话行高度差异极大时（10k 字用户气泡、长表格/工具结果），ScrollToEnd 触发的 realize
    /// 会让估算在 30k↔67k px 间来回，offset 被 anchor/coerce 反复拉回，滚动条无限上下跳。
    /// 这里改回精确测量的 StackPanel + 像素预算窗口，窗口内 Extent 是真实高度。
    /// </summary>
    private const int MaxWindowMessages = 40;

    private const int MinWindowMessages = 4;
    private const int MaxPageMessages = 20;
    private const double WindowViewports = 3.0;
    private const double PageViewports = 2.0;
    private const double PrefetchViewports = 2.0;
    private const int PrefetchPages = 3;
    private const int MaxPrefetchedWindowMessages = 80;
    private const double FallbackViewport = 600;

    private readonly ObservableCollection<ChatMessage> _visibleMessages = [];
    private readonly ObservableCollection<ConversationRow> _visibleRows = [];
    private readonly Dictionary<ChatMessage, List<ConversationRow>> _rowsByMessage = [];
    private readonly HashSet<ChatMessage> _subscribedMessages = [];
    private readonly HashSet<ChatMessage> _dirtyRowMessages = [];
    private readonly Dictionary<AgentStep, ChatMessage> _stepOwners = [];
    private readonly Dictionary<object, ChatMessage> _collectionOwners = [];
    private System.Threading.Timer? _processFoldTimer;
    private ChatMessage? _processFoldCandidate;
    private ConversationViewModel? _messagesVm;
    private int _windowStart;
    private bool _windowExpanded;
    private bool _bulkRestore;
    private bool _refreshScheduled;
    private bool _loadingEarlier;
    private readonly Queue<ConversationRow> _earlierQueue = new();
    private bool _rowRefreshScheduled;
    private int _windowGeneration;
    private int _prefetchedPages;
    private bool _idlePrefetchScheduled;
    private bool _idleDrain;
    private DispatcherTimer? _drainTimer;
    private DispatcherTimer? _richUpgradeTimer;
    private long _lastScrollTicks;

    /// <summary>DataContext 换 VM：重挂 Messages.CollectionChanged，窗口先落到最新一页。</summary>
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
        _loadingEarlier = false;
        _earlierQueue.Clear();
        _dirtyRowMessages.Clear();
        _idleDrain = false;
        _windowGeneration++;
        ClearRows();
        _prefetchedPages = 0;
        _idlePrefetchScheduled = false;
        _drainTimer?.Stop();
        _richUpgradeTimer?.Stop();
        if (_messagesVm is not null)
        {
            _messagesVm.Messages.CollectionChanged += OnMessagesChanged;
            _messagesVm.PropertyChanged += OnMessagesVmPropertyChanged;
        }

        RebuildWindowToTail();
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_messagesVm is null)
            return;

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                // Clear + 批量 Add 的会话恢复：等整批落完再一次性取尾部窗口。
                if (_bulkRestore)
                    return;
                AppendWindowMessages(e);
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                RemoveWindowMessages(e);
                break;
            case NotifyCollectionChangedAction.Reset:
                _bulkRestore = true;
                ScheduleWindowRefresh();
                break;
            default:
                ScheduleWindowRefresh();
                break;
        }
    }

    private void OnMessagesVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConversationViewModel.IsProcessing) || _messagesVm is null)
            return;

        if (_messagesVm.IsProcessing)
        {
            // 新回合开始：取消任何尚未触发的完成折叠，过程保持可见。
            CancelProcessFold();
            return;
        }

        // 整个 Turn 结束才允许自动折叠过程；中途步骤结束不折。
        for (var i = _visibleMessages.Count - 1; i >= 0; i--)
        {
            var message = _visibleMessages[i];
            if (message.Sender == MessageSender.Agent && message.AgentSteps.Count > 0)
            {
                TryScheduleProcessFold(message);
                break;
            }
        }
    }

    private void AppendWindowMessages(NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is null || _messagesVm is null)
            return;

        var windowEnd = _windowStart + _visibleMessages.Count;
        if (e.NewStartingIndex < windowEnd)
        {
            // 插到已渲染窗口之前/中间：整体重建，保证窗口始终是源集合的连续后缀。
            ScheduleWindowRefresh();
            return;
        }

        foreach (var item in e.NewItems)
        {
            if (item is ChatMessage message)
            {
                _visibleMessages.Add(message);
                AppendRowsForMessage(message);
            }
        }

        // 用户回到底部后，下次追加恢复固定窗口，避免向上翻过的历史永久驻留。
        if (_windowExpanded && MessageList.IsFollowing)
            _windowExpanded = false;

        if (_windowExpanded || !MessageList.IsFollowing)
            return;

        TrimWindowToBudget();
    }

    private void RemoveWindowMessages(NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is null)
            return;

        for (var i = 0; i < e.OldItems.Count; i++)
        {
            if (e.OldItems[i] is not ChatMessage message)
                continue;

            var visibleIndex = _visibleMessages.IndexOf(message);
            if (visibleIndex >= 0)
            {
                RemoveRowsForMessage(message);
                _visibleMessages.RemoveAt(visibleIndex);
            }
            else if (e.OldStartingIndex >= 0 && e.OldStartingIndex + i < _windowStart)
                _windowStart--;
        }
    }

    /// <summary>合并批量刷新：Reset / 非尾部变更时只重建一次窗口。</summary>
    private void ScheduleWindowRefresh()
    {
        if (_refreshScheduled)
            return;

        _refreshScheduled = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _refreshScheduled = false;
                _bulkRestore = false;
                RebuildWindowToTail();
            },
            DispatcherPriority.Background
        );
    }

    /// <summary>窗口重置为最新一页（会话恢复 / /new / 分支切换）。</summary>
    private void RebuildWindowToTail()
    {
        MessageList.ResetFollow();
        _windowExpanded = false;

        if (_messagesVm is null)
        {
            _windowStart = 0;
            _visibleMessages.Clear();
            return;
        }

        var total = _messagesVm.Messages.Count;
        var budget = ViewportHeight() * WindowViewports;
        var start = total;
        var height = 0d;
        var count = 0;
        while (
            start > 0 && count < MaxWindowMessages && (height < budget || count < MinWindowMessages)
        )
        {
            start--;
            height += EstimateMessageHeight(_messagesVm.Messages[start]);
            count++;
        }

        ClearRows();
        _windowStart = start;
        _visibleMessages.Clear();
        for (var i = _windowStart; i < total; i++)
            _visibleMessages.Add(_messagesVm.Messages[i]);

        ApplyInitialRichState();
        foreach (var message in _visibleMessages)
            AppendRowsForMessage(message);
        if (IsLoaded)
            ScheduleIdlePrefetch();
    }

    // ── 扁平化行：一条消息拆成用户行/等待行/步骤行/正文行/中断行/来源行 ──

    private static List<ConversationRow> BuildRows(ChatMessage message) =>
        ConversationRow.FromMessage(message);

    private void AppendRowsForMessage(ChatMessage message)
    {
        var rows = BuildRows(message);
        _rowsByMessage[message] = rows;
        foreach (var row in rows)
            _visibleRows.Add(row);
        SubscribeMessage(message);
    }

    private void RemoveRowsForMessage(ChatMessage message)
    {
        if (_rowsByMessage.Remove(message, out var rows))
        {
            foreach (var row in rows)
                _visibleRows.Remove(row);
        }
        UnsubscribeMessage(message);
        _dirtyRowMessages.Remove(message);
    }

    private void ClearRows()
    {
        foreach (var message in _subscribedMessages.ToArray())
            UnsubscribeMessage(message);
        _visibleRows.Clear();
        _rowsByMessage.Clear();
        _dirtyRowMessages.Clear();
        _stepOwners.Clear();
        CancelProcessFold();
    }

    /// <summary>
    /// 增量同步行拓扑：只插入/移除变化的行，保留 Content 行与其中的 MarkdownRenderer 实例。
    /// 步骤追加、状态变化、过程折叠都不再重建整条消息的行树（对齐 TKCode.Arc 的原地 step view）。
    /// </summary>
    private void ReplaceRowsForMessage(ChatMessage message)
    {
        var messageIndex = _visibleMessages.IndexOf(message);
        if (messageIndex < 0)
            return;

        var start = 0;
        for (var i = 0; i < messageIndex; i++)
        {
            if (_rowsByMessage.TryGetValue(_visibleMessages[i], out var previous))
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
                _visibleRows.Remove(oldRows[i]);
        }

        for (var i = 0; i < selected.Count; i++)
        {
            var row = selected[i];
            var current = _visibleRows.IndexOf(row);
            if (current >= 0)
            {
                if (current != start + i)
                    _visibleRows.Move(current, start + i);
            }
            else
            {
                _visibleRows.Insert(start + i, row);
            }
        }

        // 最后一行留白标记只影响 Margin，重建失败时也不影响内容行稳定性。
        for (var i = 0; i < selected.Count; i++)
            selected[i].IsLastInMessage = i == selected.Count - 1;
        _rowsByMessage[message] = selected;
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
        if (!_visibleMessages.Contains(message))
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

    /// <summary>跟随尾部时维持像素预算：从头部丢弃多余行（底部视口不动）。</summary>
    private void TrimWindowToBudget()
    {
        // 向前补页正在逐行插入时不要同时裁头，避免队列中的行指向已移除的消息。
        if (_loadingEarlier)
            return;
        var budget = ViewportHeight() * WindowViewports + ViewportHeight();
        var height = _visibleMessages.Sum(EstimateMessageHeight);
        while (
            _visibleMessages.Count > MinWindowMessages
            && (height > budget || _visibleMessages.Count > MaxWindowMessages)
        )
        {
            var oldest = _visibleMessages[0];
            height -= EstimateMessageHeight(oldest);
            RemoveRowsForMessage(oldest);
            _visibleMessages.RemoveAt(0);
            _windowStart++;
        }
    }

    /// <summary>用户滚到顶部（精确 0）：向前补页。</summary>
    private void MessageList_OnScrolledToTop() => PrependEarlierPage(expandWindow: true);

    /// <summary>距顶部 2 屏时提前补页，避免滚到顶后才加载造成停顿。</summary>
    private void MessageScroll_OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        _lastScrollTicks = Environment.TickCount64;
        ScheduleRichUpgradeAfterScroll();

        if (_loadingEarlier || _messagesVm is null || _windowStart <= 0)
            return;
        if (MessageList.IsFollowing)
            return;
        if (MessageScroll.Offset.Y > ViewportHeight() * PrefetchViewports)
            return;

        PrependEarlierPage(expandWindow: true);
    }

    /// <summary>初始窗口只在尾部约 2 屏内使用 Markdown；更早的行先纯文本。</summary>
    private void ApplyInitialRichState()
    {
        var budget = ViewportHeight() * 2;
        var height = 0d;
        for (var i = _visibleMessages.Count - 1; i >= 0; i--)
        {
            var message = _visibleMessages[i];
            var messageHeight = EstimateMessageHeight(message);
            var keepRich = height < budget || i == _visibleMessages.Count - 1;
            message.IsRichMarkdown = keepRich;
            message.IsProcessExpanded = keepRich;
            height += messageHeight;
        }
    }

    /// <summary>滚动停止 180ms 后升级当前可见的历史消息；滚动中只走纯文本。</summary>
    private void ScheduleRichUpgradeAfterScroll()
    {
        if (_richUpgradeTimer is null)
        {
            _richUpgradeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _richUpgradeTimer.Tick += (_, _) => OnRichUpgradeTick();
        }
        _richUpgradeTimer.Stop();
        _richUpgradeTimer.Start();
    }

    private void OnRichUpgradeTick()
    {
        if (_richUpgradeTimer is null)
            return;
        if (_loadingEarlier || _messagesVm is null || MessageList.IsFollowing)
        {
            _richUpgradeTimer.Stop();
            return;
        }
        if (Environment.TickCount64 - _lastScrollTicks < 120)
            return;

        _richUpgradeTimer.Stop();
        UpgradeOneVisibleRichMessage();
    }

    /// <summary>一次只升级一条，避免把一条大 Markdown 的布局塞进同一帧。</summary>
    private void UpgradeOneVisibleRichMessage()
    {
        var offset = MessageScroll.Offset.Y;
        var viewport = ViewportHeight();
        var y = 0d;
        var target = -1;
        var targetTop = 0d;
        for (var i = 0; i < _visibleMessages.Count; i++)
        {
            var message = _visibleMessages[i];
            var height = EstimateMessageHeight(message);
            if (y + height >= offset - viewport && y <= offset + viewport)
            {
                if (!message.IsRichMarkdown)
                {
                    target = i;
                    targetTop = y;
                    break;
                }
            }
            y += height;
        }

        if (target < 0)
            return;

        var scroll = MessageScroll;
        var beforeExtent = scroll.Extent.Height;
        var messageToUpgrade = _visibleMessages[target];
        var aboveViewport = targetTop + EstimateMessageHeight(messageToUpgrade) < offset;
        messageToUpgrade.IsRichMarkdown = true;

        Dispatcher.UIThread.Post(
            () =>
            {
                if (aboveViewport)
                {
                    var added = scroll.Extent.Height - beforeExtent;
                    if (added > 0.5)
                        scroll.Offset = scroll.Offset.WithY(scroll.Offset.Y + added);
                }
                ScheduleRichUpgradeAfterScroll();
            },
            DispatcherPriority.Loaded
        );
    }

    /// <summary>
    /// 空闲时预取更早的一页：大消息的模板实例化/布局（实测单条 ~187ms）发生在用户滚动之前，
    /// 不占用滚轮帧。ContextIdle 会在用户输入/滚动时让路。
    /// </summary>
    private void ScheduleIdlePrefetch()
    {
        if (_idlePrefetchScheduled || _messagesVm is null || _windowStart <= 0)
            return;
        if (_prefetchedPages >= PrefetchPages)
            return;
        // 用户已经上滚时不再静默预取，避免把大消息布局插入滚轮帧。
        if (!MessageList.IsFollowing)
            return;

        _idlePrefetchScheduled = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _idlePrefetchScheduled = false;
                if (_messagesVm is null || _windowStart <= 0 || _loadingEarlier)
                    return;
                if (_prefetchedPages >= PrefetchPages || !MessageList.IsFollowing)
                    return;
                _prefetchedPages++;
                PrependEarlierPage(expandWindow: false);
            },
            DispatcherPriority.ContextIdle
        );
    }

    /// <summary>向前补一页；按新增内容高度下移 offset，视口锚点不跳。</summary>
    private void PrependEarlierPage(bool expandWindow)
    {
        if (_loadingEarlier || _messagesVm is null || _windowStart <= 0)
            return;
        if (!expandWindow && _visibleMessages.Count >= MaxPrefetchedWindowMessages)
            return;

        var pageBudget = ViewportHeight() * PageViewports;
        var take = 0;
        var addedHeight = 0d;
        while (_windowStart - take > 0 && take < MaxPageMessages && addedHeight < pageBudget)
        {
            take++;
            addedHeight += EstimateMessageHeight(_messagesVm.Messages[_windowStart - take]);
        }

        if (!expandWindow)
            take = Math.Min(
                take,
                Math.Max(0, MaxPrefetchedWindowMessages - _visibleMessages.Count)
            );
        if (take == 0)
            return;

        _loadingEarlier = true;
        _idleDrain = !expandWindow;
        if (expandWindow)
            _windowExpanded = true;
        _windowStart -= take;

        // 消息先按窗口顺序插到 _visibleMessages 头部；行按逆序入队，
        // 每 16ms Insert(0) 一行，最终顺序与源集合一致。
        // 预取页先走轻量纯文本，等滚动停止后再升级 Markdown。
        for (var i = take - 1; i >= 0; i--)
        {
            var message = _messagesVm.Messages[_windowStart + i];
            message.IsRichMarkdown = false;
            message.IsProcessExpanded = false;
            _visibleMessages.Insert(0, message);
            SubscribeMessage(message);
            var rows = BuildRows(message);
            _rowsByMessage[message] = rows;
            for (var r = rows.Count - 1; r >= 0; r--)
                _earlierQueue.Enqueue(rows[r]);
        }

        StartEarlierDrain();
    }

    /// <summary>
    /// 每 16ms 只插入一行：步骤多的消息也不会把整条消息的控件树压进一个滚轮帧。
    /// 每插一行按 extent 增量下移 offset，视口锚点保持在原内容。
    /// </summary>
    private void StartEarlierDrain()
    {
        if (_drainTimer is null)
        {
            _drainTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _drainTimer.Tick += (_, _) => DrainOneEarlierRow();
        }
        _drainTimer.Start();
    }

    private void DrainOneEarlierRow()
    {
        if (_drainTimer is null)
            return;
        if (_messagesVm is null || _idleDrain && !MessageList.IsFollowing)
        {
            _earlierQueue.Clear();
            _loadingEarlier = false;
            _idleDrain = false;
            _drainTimer.Stop();
            return;
        }

        if (_earlierQueue.Count == 0)
        {
            _loadingEarlier = false;
            _idleDrain = false;
            _drainTimer.Stop();
            ScheduleIdlePrefetch();
            return;
        }

        var row = _earlierQueue.Dequeue();
        var generation = _windowGeneration;
        var scroll = MessageScroll;
        var beforeExtent = scroll.Extent.Height;
        _visibleRows.Insert(0, row);

        Dispatcher.UIThread.Post(
            () =>
            {
                if (generation != _windowGeneration)
                    return;
                var added = scroll.Extent.Height - beforeExtent;
                if (added > 0.5)
                    scroll.Offset = scroll.Offset.WithY(scroll.Offset.Y + added);
            },
            DispatcherPriority.Loaded
        );
    }

    /// <summary>
    /// 粗略高度预算（只用于决定窗口边界，不参与布局）：
    /// 按 16px/24 行高、约 70 字符/行估算正文，再给步骤/卡片留固定开销。
    /// </summary>
    private static double EstimateMessageHeight(ChatMessage message)
    {
        var chars = message.Content.ToString().Length;
        var height = 80 + Math.Max(0, chars) / 70.0 * 24;
        foreach (var step in message.AgentSteps)
        {
            height += 44;
            var thinking = step.ThinkingBuilder.ToString().Length;
            var result = step.FullResult?.Length ?? 0;
            var details = step.IsDetailsExpanded ? thinking + result : 0;
            height += details / 70.0 * 24;
        }
        return Math.Max(64, Math.Min(height, 20_000));
    }

    private double ViewportHeight()
    {
        var height = MessageScroll.Viewport.Height;
        if (height > 1)
            return height;
        height = MessageScroll.Bounds.Height;
        return height > 1 ? height : FallbackViewport;
    }

    /// <summary>展开/收起步骤详情（思考全文、完整参数与结果）。</summary>
    /// <summary>展开历史过程的折叠行。</summary>
    private void ProcessRow_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ConversationRow { Message: var message } })
        {
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

        // 处理中且没有待发变体：不发送，也不让 TextBox 插入空换行
        if (vm.IsProcessing && !vm.HasPendingRefinement)
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
                if (vm.IsProcessing)
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
