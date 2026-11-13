using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Desktop.Controls;

/// <summary>
/// 自动滚动列表（端口自 LKK AutoScrollItemsControl，去 SignalR 依赖）：
/// - 新消息自动滚动到底部（初始加载无闪烁）
/// - 用户手动上滚（滚轮 / 键盘 / 滚动条）时停止自动跟随，滚回底部时恢复
/// - ScrollViewer 必须是 ItemsControl 的祖先（不要求 PART_ 模板）
/// - 对话列表请用 StackPanel + 上下 spacer 窗口；不要用 VSP：可变高度下 extent
///   估算会振荡（Avalonia issue #11989），表现为滚动条无限上下跳
///
/// 「用户上滚」优先认滚轮/键盘输入；滚动条/触控板用「offset 负增量 + extent 稳定」
/// 判定。布局校正（内容变矮、窗口裁剪）同时会改变 extent，不能当成用户滚动。
/// </summary>
public class AutoScrollItemsControl : ItemsControl
{
    /// <summary>
    /// 跟随意图只有两态：跟尾 / 阅读。窗口焦点、布局校正和底部几何都不能
    /// 直接改写它；只有用户实际产生方向性 offset 的手势才触发迁移。
    /// </summary>
    private enum FollowMode
    {
        Following,
        Reading,
    }

    private ScrollViewer? _scrollViewer;
    private FollowMode _followMode = FollowMode.Following;
    private bool _followPostPending;
    private bool _scrollbarDragging;
    private bool _programmaticScroll;
    private int _wheelInputVersion;
    private bool _wheelInputPending;
    private int _layoutUpdateDepth;
    private bool _windowActive = true;

    // 用户在底部附近下滚时，ScrollViewer 的 overscroll/最后一步，以及
    // 一帧内实测高度增长，可能造成 max-128 以内的 gap；这个窗口内直接
    // 吸附到底，避免“差一点到底、还要再滚一次”。
    private const double BottomSnapDistance = 128;

    /// <summary>窗口之前所有未实例化行的总高度；由上层布局模型计算。</summary>
    public static readonly StyledProperty<double> TopSpacerHeightProperty =
        AvaloniaProperty.Register<AutoScrollItemsControl, double>(nameof(TopSpacerHeight));

    /// <summary>窗口之后所有未实例化行的总高度；由上层布局模型计算。</summary>
    public static readonly StyledProperty<double> BottomSpacerHeightProperty =
        AvaloniaProperty.Register<AutoScrollItemsControl, double>(nameof(BottomSpacerHeight));

    public double TopSpacerHeight
    {
        get => GetValue(TopSpacerHeightProperty);
        set => SetValue(TopSpacerHeightProperty, value);
    }

    public double BottomSpacerHeight
    {
        get => GetValue(BottomSpacerHeightProperty);
        set => SetValue(BottomSpacerHeightProperty, value);
    }

    /// <summary>
    /// 当前是否处于「跟随尾部」状态。滚轮手势落地前暂时视为未跟随，
    /// 防止新消息/布局在方向尚未判定时先把视口拉回底部。
    /// </summary>
    public bool IsFollowing => _followMode == FollowMode.Following && !_wheelInputPending;

    /// <summary>跟随状态变化；上层据此切换尾部窗口/阅读窗口。</summary>
    public event EventHandler? FollowChanged;

    /// <summary>
    /// 宿主窗口激活状态。失焦期间只忽略“用户上滚/回底”的意图判定，避免窗口
    /// 切换产生的 ScrollChanged 被误判；跟随尾部的自动滚动不因失焦暂停。
    /// 重新激活后如果仍在跟随则立即贴底。
    /// </summary>
    public void SetWindowActive(bool active)
    {
        if (_windowActive == active)
            return;
        _windowActive = active;
        if (!active)
        {
            _wheelInputVersion++;
            _wheelInputPending = false;
            _scrollbarDragging = false;
        }
        else if (IsFollowing)
            Dispatcher.UIThread.Post(ScrollToEndImmediate, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 恢复尾部跟随（通常用于用户收起刚展开的详情）。不会重置阅读锚点；
    /// 下一次布局/内容增长时按正常跟随逻辑贴底。
    /// </summary>
    public void ResumeFollowing()
    {
        SetFollow(true);
        ScheduleFollowTail();
    }

    /// <summary>
    /// 停止尾部跟随但保留当前视口。用于用户点击历史过程行/步骤详情展开：
    /// 内容高度变化不应把用户拉回底部，更不能把刚展开的内容追到视口外。
    /// </summary>
    public void PauseFollow()
    {
        // 这次暂停来自内容操作（展开历史过程），不能让布局校正自动恢复跟随；
        // 用户主动向下滚/拖回底部后才恢复。
        SetFollow(false);
    }

    /// <summary>
    /// 虚拟化窗口/锚点修正期间暂停“用户已回到底部”的判定。
    /// 这些程序化改动也会产生 offset/extent 变化，不能当成用户手势。
    /// </summary>
    public void BeginLayoutUpdate() => _layoutUpdateDepth++;

    public void EndLayoutUpdate()
    {
        if (_layoutUpdateDepth > 0)
            _layoutUpdateDepth--;
    }

    /// <summary>测试/预览兼容：行集合与 ItemsSource 相同。</summary>
    public object? Rows
    {
        get => ItemsSource;
        set => ItemsSource = value as System.Collections.IEnumerable;
    }

    /// <summary>测试兼容：当前窗口内已实例化的行数。</summary>
    public int RealizedCount => Items.Count;

    static AutoScrollItemsControl()
    {
        ItemsSourceProperty.Changed.AddClassHandler<AutoScrollItemsControl>(
            static (control, e) => control.OnItemsSourceChanged(e)
        );
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (
            change.Property == TopSpacerHeightProperty
            || change.Property == BottomSpacerHeightProperty
        )
            InvalidateMeasure();
    }

    public AutoScrollItemsControl()
    {
        // 首次入树时祖先 ScrollViewer 可能尚未完成模板，Loaded 再兜底一次。
        Loaded += (_, _) => AttachScrollViewer(this.FindAncestorOfType<ScrollViewer>());

        // 内容变化 → 自动滚动（流式追加）。投递执行时可能用户已上滚：回调内再查一次意图。
        Items.CollectionChanged += (_, e) =>
        {
            // Reset = Clear / 换数据源：新一轮内容必须从底部开始，不能继承上一轮的暂停跟随。
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                SetFollow(true);
                Dispatcher.UIThread.Post(ScrollToEndImmediate, DispatcherPriority.Loaded);
                return;
            }

            if (IsFollowing)
                ScheduleFollowTail();
        };
    }

    /// <summary>会话/数据源切换：恢复跟随并滚到底部（新会话不允许继承上一会话的暂停跟随）。</summary>
    public void ResetFollow()
    {
        SetFollow(true);
        Dispatcher.UIThread.Post(ScrollToEndImmediate, DispatcherPriority.Loaded);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // ScrollViewer 是 ItemsControl 的祖先（不是模板子级）。
        AttachScrollViewer(this.FindAncestorOfType<ScrollViewer>());
        if (IsFollowing)
            Dispatcher.UIThread.Post(ScrollToEndImmediate, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        DetachScrollViewer();
    }

    /// <summary>绑定祖先 ScrollViewer（模板重挂/首次入树时可能尚未就绪，Loaded 再兜底一次）。</summary>
    private void AttachScrollViewer(ScrollViewer? scrollViewer)
    {
        if (ReferenceEquals(scrollViewer, _scrollViewer))
            return;

        DetachScrollViewer();
        if (scrollViewer is null)
            return;

        _scrollViewer = scrollViewer;
        _scrollViewer.ScrollChanged += OnScrollChanged;
        // handledEventsToo：ScrollViewer 自己会处理滚轮/键盘，仍要观察用户意图。
        _scrollViewer.AddHandler(
            InputElement.PointerWheelChangedEvent,
            OnScrollViewerPointerWheel,
            RoutingStrategies.Tunnel,
            handledEventsToo: true
        );
        _scrollViewer.AddHandler(
            InputElement.KeyDownEvent,
            OnScrollViewerKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true
        );
        _scrollViewer.AddHandler(
            InputElement.PointerPressedEvent,
            OnScrollViewerPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true
        );
        _scrollViewer.AddHandler(
            InputElement.PointerReleasedEvent,
            OnScrollViewerPointerReleased,
            RoutingStrategies.Tunnel,
            handledEventsToo: true
        );
    }

    private void DetachScrollViewer()
    {
        if (_scrollViewer is null)
            return;
        _scrollViewer.ScrollChanged -= OnScrollChanged;
        _scrollViewer.RemoveHandler(
            InputElement.PointerWheelChangedEvent,
            OnScrollViewerPointerWheel
        );
        _scrollViewer.RemoveHandler(InputElement.KeyDownEvent, OnScrollViewerKeyDown);
        _scrollViewer.RemoveHandler(InputElement.PointerPressedEvent, OnScrollViewerPointerPressed);
        _scrollViewer.RemoveHandler(
            InputElement.PointerReleasedEvent,
            OnScrollViewerPointerReleased
        );
        _scrollViewer = null;
        _wheelInputVersion++;
        _wheelInputPending = false;
        _scrollbarDragging = false;
    }

    /// <summary>
    /// 滚轮只负责唤醒“可能回到底部”的意图；上/下方向交给 offset 增量判定。
    /// 不依赖滚轮符号，避免 macOS 自然滚动/系统反转导致上滚被当成下滚。
    /// </summary>
    private void OnScrollViewerPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!_windowActive || e.Delta.Y == 0)
            return;

        // 不依赖滚轮符号：macOS 自然滚动可能反转。真正的方向只看这次
        // 输入落地后的 offset 变化。滚轮手势未落地前禁止追底，避免
        // 慢速上滚期间 LayoutUpdated 先把视口拉回底部。
        var version = ++_wheelInputVersion;
        _wheelInputPending = true;
        var offsetBefore = _scrollViewer?.Offset.Y ?? 0;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (version != _wheelInputVersion)
                    return;
                _wheelInputPending = false;
                if (!_windowActive || _scrollViewer is null)
                    return;

                var max = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
                var offset = _scrollViewer.Offset.Y;
                var delta = offset - offsetBefore;

                // 任何实际向上位移都停止跟随。阈值必须远小于一像素：
                // 触控板慢滚/惯性滚动每次可能只移动 0.1~0.5px。底部回弹
                // 回到 max 的负增量不是用户上滚，不能关闭跟随。
                if (delta < -0.01 && offset < max - 0.01)
                {
                    DisableFollow();
                    return;
                }

                // 只有确实向下移动且已靠近底部才恢复。不能因为“滚轮发生了”
                // 就按 IsNearBottom 吸附——慢速上滚尚未落地时会误判并闪烁。
                if (delta > 0.01 && !IsFollowing && offset >= max - BottomSnapDistance)
                {
                    SetFollow(true);
                    ScheduleFollowTail();
                }
            },
            DispatcherPriority.Background
        );
    }

    /// <summary>键盘上滚：停止跟随。</summary>
    private void OnScrollViewerKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_windowActive)
            return;
        if (e.Key is Key.Up or Key.PageUp or Key.Home)
        {
            DisableFollow();
        }
        else if (e.Key is Key.Down or Key.PageDown or Key.End)
        {
            if (!IsFollowing && (IsAtBottom() || IsNearBottom()))
            {
                SetFollow(true);
                ScheduleFollowTail();
            }
        }
    }

    /// <summary>滚动条拖动属于明确用户手势；程序化 reveal/锚点恢复不算。</summary>
    private void OnScrollViewerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && source.GetVisualAncestors().OfType<ScrollBar>().Any())
            _scrollbarDragging = true;
    }

    private void OnScrollViewerPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_scrollbarDragging)
            return;
        _scrollbarDragging = false;
        if (!IsFollowing && IsNearBottom())
        {
            SetFollow(true);
            ScheduleFollowTail();
        }
    }

    /// <summary>立即滚到底部（无动画，初始加载无闪烁）。</summary>
    public void ScrollToEndImmediate()
    {
        if (_wheelInputPending)
            return;
        ScrollToEndCore();
    }

    /// <summary>
    /// 程序化贴底会同步触发 ScrollChanged；内容变矮时 offset 的负增量不能被
    /// 当成用户上滚，否则跟随会被静默关闭，视口停在大用户气泡上。
    /// </summary>
    private void ScrollToEndCore()
    {
        if (_scrollViewer is null)
            return;
        _programmaticScroll = true;
        try
        {
            _scrollViewer.ScrollToEnd();
        }
        finally
        {
            _programmaticScroll = false;
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var scrollViewer = _scrollViewer;
        if (scrollViewer is null)
            return;

        var maxOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);

        // 跟随只由状态驱动，与焦点无关；extent 增长时立即贴底。这里必须同步，
        // 不能只投递 Background：流式 block 增长后若先按旧 offset 绘制一帧，
        // 超长用户气泡会整块闪一下。滚轮手势落地前不追底，避免慢速上滚被抢回。
        if (IsFollowing && !_wheelInputPending && e.ExtentDelta.Y > 0)
            FollowTailIfEnabled();

        if (_windowActive)
            ApplyUserScrollIntent(scrollViewer, e, maxOffset);
    }

    /// <summary>
    /// 只解释真实用户手势。窗口切换、布局校正、锚点恢复也会改 offset/extent，
    /// 必须排除，否则会被误判成上滚/回底。
    /// </summary>
    private void ApplyUserScrollIntent(
        ScrollViewer scrollViewer,
        ScrollChangedEventArgs e,
        double maxOffset
    )
    {
        if (_layoutUpdateDepth != 0 || _programmaticScroll)
            return;

        // 任何可观测的向上内容位移都停止跟随，包括底部 64px 内的慢速
        // 触控板微滚，也不要求 extent 同时稳定：流式正文增长时用户仍可能
        // 上滚。布局/锚点产生的程序化 offset 由 BeginLayoutUpdate 标记隔离；
        // offset 仍在 max 上的负增量是底部回弹，不算上滚。
        if (e.OffsetDelta.Y < -0.01 && scrollViewer.Offset.Y < maxOffset - 0.01)
        {
            DisableFollow();
            return;
        }

        // 只有滚动条拖动这种明确向下手势才在这里恢复；wheel 由输入回调处理。
        // 程序化 reveal/锚点恢复也会产生正增量，不能据此重新跟随。
        if (
            _scrollbarDragging
            && !IsFollowing
            && e.OffsetDelta.Y > 0.01
            && scrollViewer.Offset.Y >= maxOffset - BottomSnapDistance
        )
        {
            SetFollow(true);
            ScheduleFollowTail();
        }
    }

    /// <summary>把多次追底请求合并成一帧一次；执行时再确认用户仍在跟随。</summary>
    private void ScheduleFollowTail()
    {
        if (_followPostPending)
            return;
        _followPostPending = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _followPostPending = false;
                FollowTailIfEnabled();
            },
            DispatcherPriority.Normal
        );
    }

    /// <summary>投递执行时再次确认仍在跟随（用户可能在 Post 与执行之间上滚）。</summary>
    private void FollowTailIfEnabled()
    {
        if (!IsFollowing || _wheelInputPending || _scrollViewer is null)
            return;

        var maxOffset = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
        if (_scrollViewer.Offset.Y >= maxOffset - 0.5)
            return;

        // StackPanel 的 Extent 是真实高度：ScrollToEnd 精确贴底，不做 ScrollIntoView。
        ScrollToEndCore();
    }

    private void DisableFollow()
    {
        // 上滚后必须重新由用户下滚/拖到底部才恢复，不能让布局校正自动恢复。
        SetFollow(false);
    }

    private bool IsAtBottom()
    {
        if (_scrollViewer is null)
            return false;
        var maxOffset = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
        return _scrollViewer.Offset.Y >= maxOffset - 0.5;
    }

    private bool IsNearBottom()
    {
        if (_scrollViewer is null)
            return false;
        var maxOffset = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
        return _scrollViewer.Offset.Y >= maxOffset - BottomSnapDistance;
    }

    private void OnItemsSourceChanged(AvaloniaPropertyChangedEventArgs e)
    {
        SetFollow(true);
        Dispatcher.UIThread.Post(ScrollToEndImmediate, DispatcherPriority.Loaded);
    }

    private void SetFollow(bool value)
    {
        var next = value ? FollowMode.Following : FollowMode.Reading;
        if (_followMode == next)
            return;
        _followMode = next;
        FollowChanged?.Invoke(this, EventArgs.Empty);
    }
}
