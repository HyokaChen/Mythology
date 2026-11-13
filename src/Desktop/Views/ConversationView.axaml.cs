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
        AttachedToVisualTree += (_, _) => AttachHostWindow();
        DetachedFromVisualTree += (_, _) => DetachHostWindow();
        MessageList.ItemsSource = _windowRows;
        MessageList.FollowChanged += OnFollowChanged;
        MessageScroll.ScrollChanged += OnMessageScrollChanged;
        MessageScroll.PointerMoved += OnMessageScrollPointerMoved;
        MessageScroll.PointerExited += OnMessageScrollPointerExited;
        MessageList.LayoutUpdated += OnMessageListLayoutUpdated;
        // 详情展开后的延迟 reveal 需要知道“点击之后用户是否自己滚动过”；
        // 布局/高度校正不算用户手势。
        MessageScroll.AddHandler(
            InputElement.PointerWheelChangedEvent,
            OnMessageScrollUserInput,
            RoutingStrategies.Tunnel,
            handledEventsToo: true
        );
        MessageScroll.AddHandler(
            InputElement.KeyDownEvent,
            OnMessageScrollUserInput,
            RoutingStrategies.Tunnel,
            handledEventsToo: true
        );
        MessageScroll.AddHandler(
            InputElement.PointerPressedEvent,
            OnMessageScrollUserInput,
            RoutingStrategies.Tunnel,
            handledEventsToo: true
        );
        // Enter 必须在 TextBox 处理 AcceptsReturn 之前拦截（隧道），
        // 否则 TextBox 会先插入换行、把 KeyDown 标记为已处理。
        AddHandler(KeyDownEvent, InputBox_OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, UserEditBox_OnPreviewKeyDown, RoutingStrategies.Tunnel);
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

    private void AttachHostWindow()
    {
        if (TopLevel.GetTopLevel(this) is not Window window)
            return;
        if (ReferenceEquals(_hostWindow, window))
            return;
        DetachHostWindow();
        _hostWindow = window;
        window.Activated += OnHostWindowActivated;
        window.Deactivated += OnHostWindowDeactivated;
    }

    private void DetachHostWindow()
    {
        if (_hostWindow is null)
            return;
        _hostWindow.Activated -= OnHostWindowActivated;
        _hostWindow.Deactivated -= OnHostWindowDeactivated;
        _hostWindow = null;
    }

    /// <summary>
    /// 窗口失焦/回来时保持跟随意图，不用几何位置反推。
    /// 失焦期间布局、spacer 或窗口激活都可能产生 ScrollChanged；只忽略
    /// 用户意图判定，Follow 状态本身继续贴底。Pause 状态捕获锚点，回来
    /// 后恢复阅读位置，绝不 ResetFollow。
    /// </summary>
    private void OnHostWindowDeactivated(object? sender, EventArgs e)
    {
        SetHoveredMessage(null);
        _hostWindowActive = false;
        _restoreAnchor = MessageList.IsFollowing ? null : CaptureAnchor(MessageScroll.Offset.Y);
        InvalidatePendingReveal();
        MessageList.SetWindowActive(false);
    }

    private void OnHostWindowActivated(object? sender, EventArgs e)
    {
        _hostWindowActive = true;
        MessageList.SetWindowActive(true);
        if (MessageList.IsFollowing || _restoreAnchor is not { } anchor)
        {
            _restoreAnchor = null;
            return;
        }

        _restoreAnchor = null;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!MessageList.IsFollowing)
                    RestoreAnchor(anchor);
            },
            DispatcherPriority.Loaded
        );
    }

    /// <summary>记住程序化高度变化前的阅读位置；用户下一次手势会清掉。</summary>
    private void RememberReadingAnchor()
    {
        if (!MessageList.IsFollowing)
            _readingAnchor ??= CaptureAnchor(MessageScroll.Offset.Y);
    }

    private void OnMessageScrollUserInput(object? sender, RoutedEventArgs e)
    {
        // 真正的用户手势：程序化展开/高度回写的临时锚点到此结束。
        _readingAnchor = null;
        InvalidatePendingReveal();
    }

    /// <summary>
    /// 消息级 hover：指针在同一消息的不同行之间移动时不闪断，只有真正换消息才切换。
    /// 动作条通过 ChatMessage.IsPointerOverMessage 显示，避免无正文回合没有可 hover 的块。
    /// </summary>
    private void OnMessageScrollPointerMoved(object? sender, PointerEventArgs e)
    {
        // 只在新进入某条消息时切换；行间 4px 空隙/滚动条不主动清空，
        // 否则从「中断行」移向动作行时按钮会在中途消失。
        if (FindConversationRow(e.Source as Visual) is { } row)
            SetHoveredMessage(row.Message);
    }

    private void OnMessageScrollPointerExited(object? sender, PointerEventArgs e) =>
        SetHoveredMessage(null);

    private void SetHoveredMessage(ChatMessage? message)
    {
        if (ReferenceEquals(_hoveredMessage, message))
            return;
        if (_hoveredMessage is not null)
            _hoveredMessage.IsPointerOverMessage = false;
        _hoveredMessage = message;
        if (message is not null)
            message.IsPointerOverMessage = true;
    }

    private static ConversationRow? FindConversationRow(Visual? visual)
    {
        while (visual is not null)
        {
            if (visual is Control { DataContext: ConversationRow row })
                return row;
            visual = visual.GetVisualParent();
        }
        return null;
    }

    /// <summary>让所有已投递但尚未执行的详情 reveal 失效。</summary>
    private void InvalidatePendingReveal() => _revealVersion++;

    private void OnFollowChanged(object? sender, EventArgs e)
    {
        if (_syncingWindow || !MessageList.IsFollowing)
            return;
        SyncWindowToTail();
    }

    /// <summary>窗口关闭/切走时停掉后台折叠定时器，并让未执行的 reveal 失效。</summary>
    private void StopBackgroundWork()
    {
        CancelProcessFold();
        InvalidatePendingReveal();
        SetHoveredMessage(null);
    }

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
    private bool _forceWindowSync;
    private bool _syncingWindow;

    /// <summary>
    /// 阅读锚点：<see cref="Row"/> 是视口中心所在行，<see cref="Delta"/> 是中心点相对该行顶部的
    /// 内容偏移，<see cref="ViewportHeight"/> 是捕获时的真实视口高度（窗口失焦后可能改变）。
    /// 恢复时优先用该行已实现的真实顶部，而不是 HeightIndex 对未实测行的估算；
    /// 否则窗口换页时估算误差会把视口反向拽回。
    /// </summary>
    private readonly record struct ScrollAnchor(
        ConversationRow Row,
        double Delta,
        double ViewportHeight
    );

    private readonly record struct RealizedRow(ConversationRow Row, double Top, double Height);

    private Window? _hostWindow;
    private bool _hostWindowActive = true;
    private ScrollAnchor? _restoreAnchor;
    private ScrollAnchor? _pendingAnchor;
    private ScrollAnchor? _readingAnchor;
    private bool _anchorRestoreScheduled;
    private readonly List<RealizedRow> _realizedRows = [];
    private readonly Dictionary<ConversationRow, ContentPresenter> _rowPresenters = [];
    private double _contentOriginY;
    private int _revealVersion;
    private bool _captureScheduled;
    private bool _restoringOffset;
    private double _lastObservedOffset = double.NaN;
    private bool _bulkRestore;
    private bool _refreshScheduled;
    private bool _rowRefreshScheduled;
    private System.Threading.Timer? _processFoldTimer;
    private ChatMessage? _processFoldCandidate;
    private ChatMessage? _hoveredMessage;
    private bool _editWasFollowing;

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
                    // 用户当前视口不动。失焦期间 ScrollViewer 可能在 extent 增长时
                    // 自动贴底，必须把离开前的阅读锚点再压回去。
                    if (MessageList.IsFollowing)
                        SyncWindowToTail();
                    else
                    {
                        UpdateWindowSpacers();
                        if (!_hostWindowActive && _restoreAnchor is { } readingAnchor)
                            RestoreAnchor(readingAnchor);
                    }
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
                _forceWindowSync = true;
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
    private const int MinLiveRows = 16;
    private const int MaxLiveRows = 80;
    private const int LiveBufferRows = 8;
    private readonly HeightIndex<ConversationRow> _heightIndex = new();
    private readonly Dictionary<
        ChatMessage,
        (long Version, double Width, double Height)
    > _rowHeightCache = [];
    private double _estimateWidth = 800;

    private void OnMessageScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_syncingWindow || _restoringOffset || !_hostWindowActive)
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
        // 布局完成、下一帧绘制前先贴底。流式正文/步骤让 extent 增长时，
        // 若等 Background 再追底，最新一帧会先画在旧 offset 上（大用户气泡
        // 会整块闪一下），所以这里必须在布局回调内同步修正 offset。
        if (
            MessageList.IsFollowing
            && MessageScroll.Offset.Y
                < Math.Max(0, MessageScroll.Extent.Height - MessageScroll.Viewport.Height) - 0.5
        )
            MessageList.ScrollToEndImmediate();

        // 视觉树扫描仍放到 Background 合并执行；它只回写实测高度/spacer，
        // 不负责当帧贴底。
        if (_captureScheduled)
            return;
        _captureScheduled = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _captureScheduled = false;
                UpdateEstimateWidth();
                CaptureMeasuredRows();
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
            DispatcherPriority.Normal
        );
    }

    private void SyncWindowForCurrentState()
    {
        // 失焦只冻结“阅读态”的窗口换页/锚点校正；跟随态必须继续处理
        // 流式行替换（Running 中新增 step/正文不一定走 Messages.Add）。
        if (_syncingWindow || (!_hostWindowActive && !MessageList.IsFollowing))
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

        var offset = MessageScroll.Offset.Y;
        // 优先用已实现行定位视口中心。高度索引只用于完全拿不到实测行时的兜底；
        // 若拿估算行当 focus，窗口可能围着一条用户根本看不见的行换页。
        // 程序化展开会先改变行高；原生 ScrollViewer 可能在布局时保持“贴底”。
        // 在它把 offset 拉走之前记住的锚点，必须优先于当前 offset 的重新采样。
        var anchor = !MessageList.IsFollowing ? _readingAnchor ?? CaptureAnchor(offset) : null;
        var focus = anchor is { } current
            ? _heightIndex.IndexOf(current.Row)
            : HeightIndexAtContentOffset(offset + AnchorViewportHeight() * 0.5);
        if (focus < 0)
            focus = HeightIndexAtContentOffset(offset + AnchorViewportHeight() * 0.5);

        // 阅读历史时保留当前窗口，只有焦点接近窗口边缘才换页。
        // 每滚一个像素就 Remove/Insert + RestoreAnchor，会让高度估算误差
        // 变成持续的“往下滚反而被拉回上方”。
        var currentStart = _windowRows.Count > 0 ? _heightIndex.IndexOf(_windowRows[0]) : -1;
        var currentEnd = currentStart < 0 ? -1 : currentStart + _windowRows.Count;
        if (!_forceWindowSync && currentStart >= 0 && currentEnd <= _rows.Count)
        {
            var safeStart = currentStart + LiveBufferRows;
            var safeEnd = currentEnd - LiveBufferRows;
            if (focus >= safeStart && focus <= safeEnd)
                return;
        }
        _forceWindowSync = false;

        var maxLive = GetMaxLiveRows();
        var start = Math.Clamp(focus - LiveBufferRows, 0, Math.Max(0, _rows.Count - maxLive));
        var end = Math.Min(_rows.Count, start + maxLive);
        SetWindow(start, end);
        if (!MessageList.IsFollowing && anchor is { } value)
            RestoreAnchor(value);
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
        MessageList.BeginLayoutUpdate();
        try
        {
            ReconcileWindowRows(desired);
            MessageList.TopSpacerHeight = top;
            MessageList.BottomSpacerHeight = bottom;
        }
        finally
        {
            MessageList.EndLayoutUpdate();
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

        // 差异段必须插回 prefix 与 suffix 之间；用 oldCount 追加会把新增行
        // 扔到窗口末尾，展开过程后步骤要等到下一次滚动换窗才出现。
        var insertAt = prefix;
        for (var i = prefix; i < desired.Count - suffix; i++)
            old.Insert(insertAt++, desired[i]);
    }

    /// <summary>全量行模型变化但窗口对象不需要变化时，重算上下 spacer。</summary>
    private void UpdateWindowSpacers()
    {
        MessageList.BeginLayoutUpdate();
        try
        {
            if (_windowRows.Count == 0)
            {
                MessageList.TopSpacerHeight = 0;
                MessageList.BottomSpacerHeight = _rows.Count == 0 ? 0 : TotalHeight();
                return;
            }

            var start = _heightIndex.IndexOf(_windowRows[0]);
            if (start < 0)
                return;
            var end = Math.Min(_rows.Count, start + _windowRows.Count);
            MessageList.TopSpacerHeight = TopOf(start);
            MessageList.BottomSpacerHeight = Math.Max(0, TotalHeight() - TopOf(end));
        }
        finally
        {
            MessageList.EndLayoutUpdate();
        }
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

    // 固定行高必须与 ConversationView.axaml 的模板/样式同步；动作条是 24px
    // 按钮层，且在布局里真实占位（hover 只改 Opacity，不改高度）。
    private const double ActionBarHeight = 24;
    private const double AgentBubbleChrome = 26; // padding 12*2 + border 2
    private const double StepBubbleChrome = 34; // padding 12*2 + border 2 + margin 4*2
    private const double UserBubbleChrome = 20; // padding 10*2
    private const double LabelLineHeight = 16; // TextBlock.Eyebrow LineHeight
    private const double NoticeLineHeight = 20; // SystemNoticeText LineHeight
    private const double RowSpacing = 2; // RowContent/RowStep StackPanel Spacing
    private const double UserRowSpacing = 3; // RowUser StackPanel Spacing
    private const double StepHeaderLineHeight = 16; // 单行步骤头 TextBlock.LineHeight
    private const double NoticeChrome = LabelLineHeight + UserRowSpacing; // eyebrow + gap

    private double ViewportHeight() => Math.Max(FallbackViewport, MessageScroll.Viewport.Height);

    /// <summary>
    /// 阅读锚点的中心点必须用 ScrollViewer 的真实视口高度；<see cref="ViewportHeight"/>
    /// 的 600 下限只用于窗口行数估算，不能拿来算“视口中心”，否则在矮窗口里
    /// 每次恢复都会把视口多推半个 fallback 差值。
    /// </summary>
    private double AnchorViewportHeight()
    {
        var height = MessageScroll.Viewport.Height;
        return double.IsFinite(height) && height > 0 ? height : FallbackViewport;
    }

    private double RowHeight(ConversationRow row)
    {
        if (_measuredHeights.TryGetValue(row, out var measured) && measured > 0.5)
            return measured;
        return EstimateRowHeight(row);
    }

    internal double EstimateRowHeight(ConversationRow row)
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

        if (row.Kind == ConversationRowKind.Step && row.Step is { } step)
            return EstimateStepHeight(step, row);

        return row.Kind switch
        {
            ConversationRowKind.Process => 20 + row.Margin.Bottom,
            ConversationRowKind.Waiting => LabelLineHeight + row.Margin.Bottom,
            ConversationRowKind.Interrupted => NoticeChrome + NoticeLineHeight + row.Margin.Bottom,
            ConversationRowKind.Sources => row.Message.Sources.Count * LabelLineHeight
                + row.Margin.Bottom,
            ConversationRowKind.System => EstimateSystemRowHeight(row),
            ConversationRowKind.Actions => ActionBarHeight + row.Margin.Bottom,
            _ => 32 + row.Margin.Bottom,
        };
    }

    private double EstimateSystemRowHeight(ConversationRow row)
    {
        if (row.Message.Content is not { } content)
            return NoticeChrome + NoticeLineHeight + row.Margin.Bottom;

        // RebuildHeightIndex 会在每次消息/步骤变化时重跑；系统通知不允许
        // 每次重建都 new TextBlock 做文本测量。
        if (
            _rowHeightCache.TryGetValue(row.Message, out var cached)
            && cached.Version == content.Version
            && Math.Abs(cached.Width - _estimateWidth) <= 1
        )
            return cached.Height;

        var cardChrome = row.Message.IsError ? 22 : 0; // padding 10*2 + border 2
        var textWidth = Math.Max(160, _estimateWidth - (row.Message.IsError ? 30 : 0));
        var textHeight = MarkdownHeightEstimator.EstimatePlainText(
            content.ToString(),
            textWidth,
            13,
            NoticeLineHeight
        );
        var height = NoticeChrome + textHeight + cardChrome + row.Margin.Bottom;
        _rowHeightCache[row.Message] = (content.Version, _estimateWidth, height);
        return height;
    }

    private double EstimateAgentContentHeight(string content, ConversationRow row)
    {
        var bodyWidth = Math.Max(160, _estimateWidth - 34); // 16*2 padding + 1*2 border
        var body = MarkdownHeightEstimator.EstimateDocument(
            content,
            bodyWidth,
            MarkdownHeightEstimator.Conversation
        );
        return body + AgentBubbleChrome + row.Margin.Bottom;
    }

    private double EstimateUserContentHeight(string content, ConversationRow row)
    {
        var textWidth = Math.Max(160, _estimateWidth - 32); // Bubble padding 16*2
        return MarkdownHeightEstimator.EstimatePlainText(content, textWidth, 15, 24)
            + LabelLineHeight
            + UserRowSpacing
            + UserBubbleChrome
            + UserRowSpacing
            + ActionBarHeight
            + row.Margin.Bottom;
    }

    private double EstimateStepHeight(AgentStep step, ConversationRow row)
    {
        var width = Math.Max(160, _estimateWidth);
        var bodyWidth = Math.Max(160, width - 18);
        var height = 0.0;
        var headerHeight = StepHeaderLineHeight;

        if (step.IsContentStep)
        {
            // AgentBubble: 16,12 padding + 1px border + 0,4 外边距；步骤行不再挂动作条。
            height +=
                MarkdownHeightEstimator.EstimateDocument(
                    step.ContentText,
                    Math.Max(160, width - 34),
                    MarkdownHeightEstimator.Step
                ) + StepBubbleChrome;
        }
        else if (step.IsNoticeStep)
        {
            height +=
                MarkdownHeightEstimator.EstimatePlainText(
                    step.ContentText,
                    bodyWidth,
                    13,
                    NoticeLineHeight
                ) + 2; // AgentStepNoticeText Margin 1*2
        }
        else if (step.IsSystemEvent)
        {
            var cardChrome = step.IsError ? 22 : 0; // padding 10*2 + border 2
            var textWidth = Math.Max(160, bodyWidth - (step.IsError ? 30 : 0));
            height +=
                NoticeChrome
                + MarkdownHeightEstimator.EstimatePlainText(
                    step.ContentText,
                    textWidth,
                    13,
                    NoticeLineHeight
                )
                + cardChrome
                + 4; // margin 2*2
        }
        else if (step.IsStepHeader)
        {
            height += headerHeight;
            // 结果摘要与参数摘要都是单行省略：固定 16px 行高，
            // 不再按宽度估算换行，避免窗口换页时高度反复修正。
            if (step.HasResult)
                height += RowSpacing + 16;
            if (step.ShowStreamingPreview)
                height += RowSpacing + 40; // StreamingReasoningPreview 固定 40
            if (step.IsDetailsExpanded)
                height += EstimateStepDetailsHeight(step, bodyWidth);
        }

        return Math.Max(headerHeight, height) + row.Margin.Bottom;
    }

    private static double EstimateStepDetailsHeight(AgentStep step, double bodyWidth)
    {
        var height = RowSpacing + 2.0; // StackPanel spacing + StepDetails.Margin top
        var items = 0;

        void Add(double value)
        {
            if (items++ > 0)
                height += 4; // StepDetails.Spacing
            height += value;
        }

        if (step.HasThinking)
            Add(
                Math.Min(
                    320,
                    MarkdownHeightEstimator.EstimatePlainText(step.ThinkingText, bodyWidth, 12, 16)
                )
            );
        if (step.HasDetailArguments)
        {
            Add(16); // 「参数」Eyebrow
            Add(
                Math.Min(
                    240,
                    MarkdownHeightEstimator.EstimatePlainText(
                        step.DetailArguments ?? "",
                        bodyWidth,
                        11,
                        16
                    )
                )
            );
        }
        if (step.HasResultDetail && step.ShowRawResultDetail)
        {
            Add(16); // 「结果」Eyebrow
            Add(
                Math.Min(
                    320,
                    MarkdownHeightEstimator.EstimatePlainText(
                        step.DetailResult ?? "",
                        bodyWidth,
                        11,
                        16
                    )
                )
            );
        }
        if (step.HasDiff)
        {
            Add(16); // DiffSummary
            Add(Math.Min(320, step.DiffLines.Count * 18 + 8));
        }
        return height;
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
        // 窗口保持约两屏 + 缓冲，而不是三屏：过程行控件重，缩小窗口能显著
        // 降低每次换窗的测量成本；缓冲仍保证滚动时不会看到空白。
        var target = (int)Math.Ceiling(ViewportHeight() * 2 / average);
        return Math.Clamp(target, MinLiveRows, MaxLiveRows);
    }

    private void RebuildHeightIndex() => _heightIndex.Rebuild(_rows, RowHeight);

    /// <summary>
    /// 从已安排的行容器读回真实高度。GetVisualDescendants 父先子后，同一行
    /// 第一次出现的 ContentPresenter 就是行容器；同一次遍历顺手缓存行在
    /// 内容坐标中的顶部，供锚点使用。
    /// </summary>
    private void CaptureMeasuredRows()
    {
        if (_windowRows.Count == 0)
        {
            _realizedRows.Clear();
            return;
        }

        UpdateRealizedRows();
        if (_realizedRows.Count == 0)
            return;

        var following = MessageList.IsFollowing;
        var anchor =
            following || !_hostWindowActive
                ? null
                : _readingAnchor ?? CaptureAnchor(MessageScroll.Offset.Y);
        var changed = false;
        foreach (var candidate in _realizedRows)
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
            RestoreAnchor(value);
    }

    /// <summary>
    /// 缓存窗口内已实现行的真实顶部/高度。只扫描一次视觉树；父先子后，
    /// 同一行首次出现的 ContentPresenter 就是行容器。
    /// </summary>
    private void UpdateRealizedRows()
    {
        // ScrollViewer.Padding 会把 MessageList 推离 content 原点；Offset 却是
        // content 坐标。先把 MessageList 原点换算到 content 坐标，再把行顶部
        // 从 MessageList 坐标平移过去，避免固定 padding 误差污染锚点。
        var listOriginContent = ContentOriginY();
        _rowPresenters.Clear();
        foreach (var presenter in MessageList.GetVisualDescendants().OfType<ContentPresenter>())
        {
            if (presenter.DataContext is not ConversationRow row || presenter.Bounds.Height <= 0.5)
                continue;
            // GetVisualDescendants 父先子后；同一行第一次出现的容器就是行容器。
            _rowPresenters.TryAdd(row, presenter);
        }

        _realizedRows.Clear();
        foreach (var row in _windowRows)
        {
            if (!_rowPresenters.TryGetValue(row, out var presenter))
                continue;
            if (
                presenter.TranslatePoint(new Point(0, 0), MessageList) is not { } top
                || !double.IsFinite(top.Y)
            )
                continue;
            _realizedRows.Add(
                new RealizedRow(row, listOriginContent + top.Y, presenter.Bounds.Height)
            );
        }
    }

    /// <summary>
    /// MessageList 顶部在 ScrollViewer content 坐标中的位置。ScrollViewer.Padding
    /// 会把 MessageList 推离原点；缓存 content 坐标时只加这一次。
    /// </summary>
    private double ContentOriginY()
    {
        if (
            MessageList.TranslatePoint(new Point(0, 0), MessageScroll) is { } origin
            && double.IsFinite(origin.Y)
        )
            _contentOriginY = origin.Y + MessageScroll.Offset.Y;
        return _contentOriginY;
    }

    private int HeightIndexAtContentOffset(double contentOffset) =>
        _heightIndex.IndexAtOffset(contentOffset - ContentOriginY());

    /// <summary>
    /// 以视口中心为锚点。窗口内已实现行的顶部是真实坐标；只有完全没有实测行时
    /// 才退回 HeightIndex 的估算，保证锚点不会比视口本身更“会算”。
    /// </summary>
    private ScrollAnchor? CaptureAnchor(double offset)
    {
        if (_rows.Count == 0)
            return null;

        var viewportHeight = AnchorViewportHeight();
        var anchorPoint = offset + viewportHeight * 0.5;
        // 滚动条可直接跳到窗口外；此时缓存的行顶部属于旧窗口，不能拿它
        // 当焦点。视口中心不在已实现范围内就退回高度索引，触发换页。
        var cacheCoversViewport =
            _realizedRows.Count > 0
            && anchorPoint >= _realizedRows[0].Top - 0.5
            && anchorPoint <= _realizedRows[^1].Top + _realizedRows[^1].Height + 0.5;
        if (cacheCoversViewport)
        {
            var best = _realizedRows[0];
            var bestDistance = double.PositiveInfinity;
            foreach (var candidate in _realizedRows)
            {
                var bottom = candidate.Top + candidate.Height;
                if (anchorPoint >= candidate.Top && anchorPoint < bottom)
                    return new ScrollAnchor(
                        candidate.Row,
                        anchorPoint - candidate.Top,
                        viewportHeight
                    );

                var distance =
                    anchorPoint < candidate.Top
                        ? candidate.Top - anchorPoint
                        : anchorPoint - bottom;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }
            return new ScrollAnchor(best.Row, anchorPoint - best.Top, viewportHeight);
        }

        var index = HeightIndexAtContentOffset(anchorPoint);
        var row = _rows[index];
        var top = ContentOriginY() + _heightIndex.OffsetOf(index);
        return new ScrollAnchor(row, anchorPoint - top, viewportHeight);
    }

    /// <summary>
    /// 恢复阅读锚点。窗口换页/高度回写后，行的真实顶部要等下一次布局才可靠，
    /// 因此把落点投递到布局之后；同一帧的多次请求只保留最新锚点。
    /// </summary>
    private void RestoreAnchor(ScrollAnchor anchor)
    {
        _pendingAnchor = anchor;
        if (_anchorRestoreScheduled)
            return;
        _anchorRestoreScheduled = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _anchorRestoreScheduled = false;
                var pending = _pendingAnchor;
                _pendingAnchor = null;
                if (pending is { } value && !MessageList.IsFollowing)
                    ApplyAnchor(value);
            },
            DispatcherPriority.Loaded
        );
    }

    private void ApplyAnchor(ScrollAnchor anchor)
    {
        var index = _heightIndex.IndexOf(anchor.Row);
        if (index < 0)
            return;
        _readingAnchor = null;

        // 锚点行已经不在当前 live window：这是尾部追加导致的 offset 变化，
        // 不是换页后的恢复目标。绝不拿 HeightIndex 的 TopOf 把视口拖到未实现行。
        if (!_windowRows.Contains(anchor.Row))
            return;

        // 换页后的真实行顶部必须从当前视觉树重新取；HeightIndex 里窗口内
        // 未实测行的值仍是估算，不能拿去算滚动落点。
        UpdateRealizedRows();
        var rowTop = ContentOriginY() + TopOf(index);
        foreach (var candidate in _realizedRows)
        {
            if (!ReferenceEquals(candidate.Row, anchor.Row))
                continue;
            rowTop = candidate.Top;
            break;
        }

        var target = rowTop + anchor.Delta - anchor.ViewportHeight * 0.5;
        // ScrollViewer extent 包含上下 padding；从它取上限，避免底部差一个
        // padding 高度时锚点永远差几像素、随后又被布局校正。
        var max = Math.Max(0, MessageScroll.Extent.Height - MessageScroll.Viewport.Height);
        target = Math.Clamp(target, 0, max);
        if (Math.Abs(MessageScroll.Offset.Y - target) <= 0.5)
            return;

        _restoringOffset = true;
        MessageList.BeginLayoutUpdate();
        try
        {
            MessageScroll.Offset = MessageScroll.Offset.WithY(target);
        }
        finally
        {
            MessageList.EndLayoutUpdate();
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
        RememberReadingAnchor();
        _realizedRows.Clear();
        _rowPresenters.Clear();
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
        SetHoveredMessage(null);
        foreach (var message in _subscribedMessages.ToArray())
            UnsubscribeMessage(message);
        _rows.Clear();
        _windowRows.Clear();
        _measuredHeights.Clear();
        _realizedRows.Clear();
        _rowPresenters.Clear();
        _pendingAnchor = null;
        _readingAnchor = null;
        _contentOriginY = 0;
        _heightIndex.Rebuild([], RowHeight);
        _rowsByMessage.Clear();
        _dirtyRowMessages.Clear();
        _stepOwners.Clear();
        MessageList.TopSpacerHeight = 0;
        MessageList.BottomSpacerHeight = 0;
        _lastObservedOffset = double.NaN;
        _restoreAnchor = null;
        CancelProcessFold();
        InvalidatePendingReveal();
    }

    /// <summary>
    /// 增量同步行拓扑：只插入/移除变化的行，保留 Content 行与其中的 MarkdownRenderer 实例。
    /// 步骤追加、状态变化、过程折叠都不再重建整条消息的行树（对齐 TKCode.Arc 的原地 step view）。
    /// </summary>
    private void ReplaceRowsForMessage(ChatMessage message)
    {
        RememberReadingAnchor();
        _realizedRows.Clear();
        _rowPresenters.Clear();
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
        var available =
            new Dictionary<(ConversationRowKind, AgentStep?, bool, bool), Queue<ConversationRow>>();
        foreach (var row in oldRows)
        {
            var key = RowTemplateKey(row);
            if (!available.TryGetValue(key, out var queue))
                available[key] = queue = new Queue<ConversationRow>();
            queue.Enqueue(row);
        }
        foreach (var template in desired)
        {
            var key = RowTemplateKey(template);
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
        _forceWindowSync = true;
        RequestWindowSync();
    }

    /// <summary>
    /// 行模板由步骤状态决定（运行中/完成/展开/收起）。复用旧行对象时容器不会
    /// 重新选模板，因此状态变化必须换新行对象，让 ItemsControl 重建该行。
    /// </summary>
    private static (ConversationRowKind, AgentStep?, bool, bool) RowTemplateKey(
        ConversationRow row
    ) => (row.Kind, row.Step, row.Step?.IsProcessing == true, row.Step?.IsDetailsExpanded == true);

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
                    or nameof(ChatMessage.IsCompleted)
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
        if (
            e.PropertyName
            is not (
                nameof(AgentStep.Status)
                or nameof(AgentStep.IsProcessing)
                or nameof(AgentStep.IsDetailsExpanded)
            )
        )
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
        // 自动折叠会让步骤详情一起消失；不能留下一个已无目标的延迟 reveal。
        InvalidatePendingReveal();
        if (!MessageList.IsFollowing)
            RememberReadingAnchor();
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
            DispatcherPriority.Normal
        );
    }

    // ── 消息动作：复制 / 编辑 / 重试 ─────────────────────────────────────

    /// <summary>用户消息「编辑」：原气泡换成内联输入框，并聚焦到草稿末尾。</summary>
    private void UserMessageEdit_OnRequested(object? sender, EventArgs e)
    {
        if (
            sender is not MessageActionBar { DataContext: ConversationRow { Message: var message } }
            || Vm is not { } vm
        )
            return;

        InvalidatePendingReveal();
        _editWasFollowing = MessageList.IsFollowing;
        MessageList.PauseFollow();
        vm.BeginEditUserMessage(message);
        Dispatcher.UIThread.Post(() => FocusUserEditor(message), DispatcherPriority.Loaded);
    }

    /// <summary>助手消息「重试」：回到目标回合并重新生成（编辑态按钮共用同一入口）。</summary>
    private void MessageRetry_On_Requested(object? sender, EventArgs e)
    {
        if (
            sender is not MessageActionBar { DataContext: ConversationRow { Message: var message } }
            || Vm is not { } vm
        )
            return;

        InvalidatePendingReveal();
        MessageList.ResetFollow();
        _editWasFollowing = false;
        _ = RetryMessageAsync(vm, message);
    }

    private async Task RetryMessageAsync(ConversationViewModel vm, ChatMessage message)
    {
        try
        {
            await vm.RetryMessageAsync(message);
        }
        catch (Exception ex)
        {
            vm.AddSystemNotice($"重试失败：{ex.Message}");
        }
    }

    private void UserEditBox_OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is not TextBox { Name: "UserEditBox" } box || Vm is not { } vm)
            return;
        if (box.DataContext is not ConversationRow { Message: var message })
            return;

        // 中文输入法组合中（拼音未上屏）Enter 属于候选词确认，不能当成发送。
        if (
            box.GetVisualDescendants()
                .OfType<TextPresenter>()
                .Any(p => !string.IsNullOrEmpty(p.PreeditText))
        )
            return;

        if (e.Key == Key.Escape)
        {
            vm.CancelUserEdit(message);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None)
            return;

        message.EditDraft = box.Text ?? "";
        e.Handled = true;
        MessageList.ResetFollow();
        _editWasFollowing = false;
        _ = SubmitUserEditAsync(vm, message);
    }

    private void CancelUserEdit_OnClick(object? sender, RoutedEventArgs e)
    {
        if (
            sender is Control { DataContext: ConversationRow { Message: var message } }
            && Vm is { } vm
        )
        {
            vm.CancelUserEdit(message);
            if (_editWasFollowing)
                MessageList.ResetFollow();
            _editWasFollowing = false;
        }
        e.Handled = true;
    }

    private void SubmitUserEdit_OnClick(object? sender, RoutedEventArgs e)
    {
        if (
            sender is Control { DataContext: ConversationRow { Message: var message } }
            && Vm is { } vm
        )
        {
            var editor = FindUserEditor(message);
            if (editor is not null)
                message.EditDraft = editor.Text ?? "";
            MessageList.ResetFollow();
            _editWasFollowing = false;
            _ = SubmitUserEditAsync(vm, message);
        }
        e.Handled = true;
    }

    private async Task SubmitUserEditAsync(ConversationViewModel vm, ChatMessage message)
    {
        try
        {
            await vm.SubmitUserEditAsync(message);
        }
        catch (Exception ex)
        {
            vm.AddSystemNotice($"编辑发送失败：{ex.Message}");
        }
    }

    private TextBox? FindUserEditor(ChatMessage message) =>
        MessageList
            .GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(box =>
                box.Name == "UserEditBox"
                && box.DataContext is ConversationRow row
                && ReferenceEquals(row.Message, message)
            );

    private void FocusUserEditor(ChatMessage message)
    {
        var editor = FindUserEditor(message);
        if (editor is null)
            return;
        editor.Focus();
        editor.CaretIndex = editor.Text?.Length ?? 0;
        editor.BringIntoView();
    }

    /// <summary>展开历史过程的折叠行。</summary>
    private void ProcessRow_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ConversationRow { Message: var message } })
        {
            // 展开历史过程会改变行高；先退出尾部跟随，避免把视口拉回最后一条。
            InvalidatePendingReveal();
            MessageList.PauseFollow();
            RememberReadingAnchor();
            message.ProcessTouchedByUser = true;
            CancelProcessFold();
            message.IsProcessExpanded = true;
        }
        e.Handled = true;
    }

    private void StepDetails_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ConversationRow { Step: { } step } row })
        {
            if (!step.HasDetails)
            {
                e.Handled = true;
                return;
            }

            // 展开步骤详情会让当前行长高。若仍在尾部跟随，AutoScroll 会把视口
            // 追到新的底部，刚展开的内容反而跑到视口上方；先退出跟随，再在布局后
            // 把详情滚进可见区，保证点击后立即看到内容。收起不需要打断跟随。
            var expand = !step.IsDetailsExpanded;
            InvalidatePendingReveal();
            if (expand)
            {
                MessageList.PauseFollow();
                RememberReadingAnchor();
            }
            step.IsDetailsExpanded = expand;
            if (expand)
            {
                var revealVersion = _revealVersion;
                // 模板切换会替换行对象，窗口同步/新容器布局都在 Background 阶段；
                // reveal 放到 ContextIdle，确保详情控件已经实例化且锚点恢复已结束。
                Dispatcher.UIThread.Post(
                    () => BringStepDetailsIntoView(row, revealVersion),
                    DispatcherPriority.ContextIdle
                );
            }
        }
        e.Handled = true;
    }

    private void BringStepDetailsIntoView(ConversationRow row, int revealVersion)
    {
        if (row.Step is not { IsDetailsExpanded: true })
            return;
        if (_revealVersion != revealVersion || MessageList.IsFollowing)
            return;
        // 模板切换会替换 ConversationRow 对象；详情面板的 DataContext 是
        // AgentStep，按步骤匹配，不能拿点击时捕获的旧行对象比较。
        var details = this.GetVisualDescendants()
            .OfType<StackPanel>()
            .FirstOrDefault(panel =>
                panel.Classes.Contains("StepDetails")
                && ReferenceEquals(panel.DataContext, row.Step)
            );
        if (details is not { IsVisible: true })
            return;
        if (details.TranslatePoint(new Point(0, 0), MessageScroll) is not { } top)
            return;

        // 展开动作发生在尾部时，ScrollViewer 可能仍保持贴底：详情顶部虽然
        // 落在视口内，但下半截被截掉，阅读位置也还停在底部。这里始终把详情
        // 顶部对齐到视口上方留白，保证点击后从第一行开始读；用户随后的滚动
        // 会通过 revealVersion 让这次延迟定位失效。
        if (top.Y >= 8 && top.Y <= 8.5)
            return;

        // 把详情顶部放到视口上方留白处。BringIntoView 对“比视口还高”的控件只保证
        // 部分可见，长思考正文仍可能第一行在视口外；这里显式对齐，保证点击后能读到内容。
        var target = MessageScroll.Offset.Y + top.Y - 8;
        var max = Math.Max(0, MessageScroll.Extent.Height - MessageScroll.Viewport.Height);
        target = Math.Clamp(target, 0, max);
        if (Math.Abs(target - MessageScroll.Offset.Y) <= 0.5)
            return;

        _restoringOffset = true;
        MessageList.BeginLayoutUpdate();
        try
        {
            MessageScroll.Offset = MessageScroll.Offset.WithY(target);
        }
        finally
        {
            MessageList.EndLayoutUpdate();
            _restoringOffset = false;
        }
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

    // ── 输入区按键：Enter 发送 / Shift+Enter 换行 / Esc 中断 / 补全弹窗导航 ──

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
        // 并让本轮流式输出继续跟随到底部；旧的详情 reveal 不应再反向拽回。
        InvalidatePendingReveal();
        MessageList.ResetFollow();
        vm.SendMessageCommand.Execute().Subscribe();
        e.Handled = true;
    }

    /// <summary>
    /// 弹窗打开时消费 Enter / Up / Down / Tab / Esc。
    /// Up / Down 在候选列表首尾循环。
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
                        vm.SelectedCommandIndex = WrapSelectionIndex(
                            vm.SelectedCommandIndex,
                            1,
                            vm.CommandSuggestions.Count
                        );
                    return true;
                case Key.Up:
                    if (vm.CommandSuggestions.Count > 0)
                        vm.SelectedCommandIndex = WrapSelectionIndex(
                            vm.SelectedCommandIndex,
                            -1,
                            vm.CommandSuggestions.Count
                        );
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
                        vm.SelectedSuggestionIndex = WrapSelectionIndex(
                            vm.SelectedSuggestionIndex,
                            1,
                            vm.Suggestions.Count
                        );
                    return true;
                case Key.Up:
                    if (vm.Suggestions.Count > 0)
                        vm.SelectedSuggestionIndex = WrapSelectionIndex(
                            vm.SelectedSuggestionIndex,
                            -1,
                            vm.Suggestions.Count
                        );
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

    /// <summary>
    /// 补全列表循环导航：首项再向上跳到末项，末项再向下回到首项。
    /// 列表重建期间索引可能短暂为 -1：此时向下选首项、向上选末项。
    /// </summary>
    private static int WrapSelectionIndex(int current, int delta, int count)
    {
        if (count <= 0)
            return 0;
        if (current < 0 || current >= count)
            return delta > 0 ? 0 : count - 1;
        var next = (current + delta) % count;
        return next < 0 ? next + count : next;
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
    private void SendButton_OnClick(object? sender, RoutedEventArgs e)
    {
        InvalidatePendingReveal();
        MessageList.ResetFollow();
    }

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
