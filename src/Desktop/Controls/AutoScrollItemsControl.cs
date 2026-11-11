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
    private ScrollViewer? _scrollViewer;
    private bool _autoFollow = true;
    private bool _resumeOnBottom = true;
    private bool _followPostPending;
    private double _lastMaxOffset = double.NaN;
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

    /// <summary>当前是否处于「跟随尾部」状态。</summary>
    public bool IsFollowing => _autoFollow;

    /// <summary>跟随状态变化；上层据此切换尾部窗口/阅读窗口。</summary>
    public event EventHandler? FollowChanged;

    /// <summary>
    /// 宿主窗口激活状态。失焦期间忽略滚动状态变化，避免窗口切换产生的
    /// ScrollChanged 被误判成用户上滚；重新激活后如果仍在跟随则立即贴底。
    /// </summary>
    public void SetWindowActive(bool active)
    {
        if (_windowActive == active)
            return;
        _windowActive = active;
        if (active && _autoFollow)
            Dispatcher.UIThread.Post(ScrollToEndImmediate, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 停止尾部跟随但保留当前视口。用于用户点击历史过程行/步骤详情展开：
    /// 内容高度变化不应把用户拉回底部，更不能把刚展开的内容追到视口外。
    /// </summary>
    public void PauseFollow()
    {
        SetFollow(false);
        // 这次暂停来自内容操作（展开历史过程），不能让布局校正自动恢复跟随；
        // 用户主动向下滚/拖回底部后才恢复。
        _resumeOnBottom = false;
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

            if (_autoFollow)
                ScheduleFollowTail();
        };
    }

    /// <summary>会话/数据源切换：恢复跟随并滚到底部（新会话不允许继承上一会话的暂停跟随）。</summary>
    public void ResetFollow()
    {
        SetFollow(true);
        _resumeOnBottom = true;
        Dispatcher.UIThread.Post(ScrollToEndImmediate, DispatcherPriority.Loaded);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // ScrollViewer 是 ItemsControl 的祖先（不是模板子级）。
        AttachScrollViewer(this.FindAncestorOfType<ScrollViewer>());
        if (_autoFollow)
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
        _scrollViewer = null;
    }

    /// <summary>
    /// 滚轮只负责唤醒“可能回到底部”的意图；上/下方向交给 offset 增量判定。
    /// 不依赖滚轮符号，避免 macOS 自然滚动/系统反转导致上滚被当成下滚。
    /// </summary>
    private void OnScrollViewerPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!_windowActive || e.Delta.Y == 0)
            return;

        _resumeOnBottom = true;
        // 上滚符号可信时立即停；但 macOS 自然滚动可能反转符号，所以不能
        // 依赖它做“下滚就吸附”。下滚是否真的靠近底部，在这一次输入
        // 造成 offset 变化之后再判断。
        if (e.Delta.Y > 0)
            DisableFollow();

        var offsetBefore = _scrollViewer?.Offset.Y ?? 0;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!_windowActive || _scrollViewer is null)
                    return;
                // 用户这次输入让 offset 上移了，说明是在向上滚：绝不吸附，
                // 并在离开底部回弹区后停止跟随。这里用 offset 实际变化，
                // 不依赖 macOS 自然滚动可能反转的 Delta 符号。
                if (_scrollViewer.Offset.Y < offsetBefore - 1)
                {
                    var max = Math.Max(
                        0,
                        _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height
                    );
                    var wasNearBottom = _scrollViewer.Offset.Y >= max - 64;
                    // 先停掉自动贴底，避免用户上滚时 OnLayoutUpdated 又把
                    // 视口拉回去；如果是底部 overscroll 回弹，80ms 后
                    // offset 回到 max 再恢复 follow。
                    DisableFollow();
                    if (wasNearBottom)
                    {
                        var timer = new DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(80),
                        };
                        timer.Tick += (_, _) =>
                        {
                            timer.Stop();
                            if (
                                _windowActive
                                && _scrollViewer is not null
                                && _scrollViewer.Offset.Y
                                    >= Math.Max(
                                        0,
                                        _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height
                                    ) - 2
                            )
                            {
                                SetFollow(true);
                                ScheduleFollowTail();
                            }
                        };
                        timer.Start();
                    }
                    return;
                }
                if (IsNearBottom())
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
            _resumeOnBottom = true;
            if (IsAtBottom() || IsNearBottom())
            {
                SetFollow(true);
                ScheduleFollowTail();
            }
        }
    }

    /// <summary>滚动条按下后允许拖回底部时恢复跟随；方向上移仍由 ScrollChanged 判定。</summary>
    private void OnScrollViewerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && source.GetVisualAncestors().OfType<ScrollBar>().Any())
            _resumeOnBottom = true;
    }

    /// <summary>立即滚到底部（无动画，初始加载无闪烁）。</summary>
    public void ScrollToEndImmediate()
    {
        _scrollViewer?.ScrollToEnd();
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scrollViewer is null || !_windowActive)
            return;

        var maxOffset = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
        var previousMaxOffset = _lastMaxOffset;
        _lastMaxOffset = maxOffset;

        // 用户已经贴住旧的 max，随后虚拟化实测让 extent 变高：这时 offset
        // 仍停在旧 max，属于“还在底部”而不是新内容的中间位置。先记下
        // bottom seek，即使当前 gap 暂时大于吸附阈值也要继续跟。
        if (
            !_autoFollow
            && _resumeOnBottom
            && e.ExtentDelta.Y > 0
            && double.IsFinite(previousMaxOffset)
            && _scrollViewer.Offset.Y >= previousMaxOffset - 2
        )
        {
            SetFollow(true);
            ScheduleFollowTail();
        }

        // 跟随状态下任何 extent 增长（流式正文长高 / 新行被测量）都重新贴底。
        if (_autoFollow && e.ExtentDelta.Y > 0)
            ScheduleFollowTail();

        // 用户上滚 → 停止跟随。
        // 只认“offset 负增量 + extent 稳定”；布局校正/窗口换页会同时改变 extent，
        // 不能当成用户上滚，否则恢复会话后可能永远停在中间。
        // 不依赖 PointerPressed 的 ScrollBar 命中，滚动条、键盘、触控板都统一。
        if (
            _layoutUpdateDepth == 0
            && e.OffsetDelta.Y < -0.5
            && Math.Abs(e.ExtentDelta.Y) < 0.5
            // 底部 64px 是 overscroll/rubber-band 回弹区；滚轮上滚和
            // 键盘上滚已经显式停跟随，不能让回弹把刚到底的跟随关掉。
            && _scrollViewer.Offset.Y < maxOffset - 64
        )
            DisableFollow();

        // 用户滚到底部 → 恢复跟随。
        // 快速滚动时先到的是“当前估算 extent 的底部”；随后 block/行实测
        // 会让 extent 继续增长。不能在接近底部时因为 extent 同时变化就拒绝
        // 恢复跟随，否则会出现“回弹一下、差一点到底，要再滚一次”。
        // 仍然排除程序的窗口/锚点更新（_layoutUpdateDepth）和向上回弹。
        if (
            !_autoFollow
            && _resumeOnBottom
            && _layoutUpdateDepth == 0
            && e.OffsetDelta.Y > 0.5
            && _scrollViewer.Offset.Y >= maxOffset - BottomSnapDistance
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
            DispatcherPriority.Background
        );
    }

    /// <summary>投递执行时再次确认仍在跟随（用户可能在 Post 与执行之间上滚）。</summary>
    private void FollowTailIfEnabled()
    {
        if (!_autoFollow || !_windowActive || _scrollViewer is null)
            return;

        var maxOffset = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
        if (_scrollViewer.Offset.Y >= maxOffset - 0.5)
            return;

        // StackPanel 的 Extent 是真实高度：ScrollToEnd 精确贴底，不做 ScrollIntoView。
        _scrollViewer.ScrollToEnd();
    }

    private void DisableFollow()
    {
        SetFollow(false);
        // 上滚后必须重新由用户下滚/拖到底部才恢复，不能让布局校正自动恢复。
        _resumeOnBottom = false;
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
        if (_autoFollow == value)
            return;
        _autoFollow = value;
        FollowChanged?.Invoke(this, EventArgs.Empty);
    }
}
