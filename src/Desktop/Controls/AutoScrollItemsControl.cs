using System.Diagnostics;
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
/// - 模板要求包含 x:Name="PART_ScrollViewer" 的 ScrollViewer
///
/// 「用户上滚」只认输入意图，不认 offset 负增量：布局校正（内容变矮、虚拟化估算回收）
/// 也会让 offset 变小，把它当用户滚动会永久关闭跟随，表现为恢复会话/发消息后停在中间。
/// </summary>
public class AutoScrollItemsControl : ItemsControl
{
    private const int ScrollAnimMs = 160;
    private const int ScrollTickMs = 16;
    private ScrollViewer? _scrollViewer;
    private bool _autoFollow = true;
    private bool _suppressAutoScroll;
    private bool _scrollBarDragging;
    private bool _followPostPending;
    private DispatcherTimer? _scrollTimer;
    private double _scrollFrom;
    private double _scrollTo;
    private long _scrollStartTicks;

    static AutoScrollItemsControl()
    {
        ItemsSourceProperty.Changed.AddClassHandler<AutoScrollItemsControl>(
            static (control, e) => control.OnItemsSourceChanged(e)
        );
    }

    public AutoScrollItemsControl()
    {
        // 内容变化 → 自动滚动（流式追加）。投递执行时可能用户已上滚：回调内再查一次意图。
        Items.CollectionChanged += (_, _) =>
        {
            if (_autoFollow && !_suppressAutoScroll)
                ScheduleFollowTail();
        };
    }

    /// <summary>用户滚到顶部（历史加载钩子）。</summary>
    public event Action? ScrolledToTop;

    /// <summary>用户上滚（停止跟随）。</summary>
    public event Action? AutoFollowChanged;

    /// <summary>当前是否处于「跟随尾部」状态（窗口化渲染据它决定是否裁掉最旧的可视消息）。</summary>
    public bool IsFollowing => _autoFollow;

    /// <summary>会话/数据源切换：恢复跟随并滚到底部（新会话不允许继承上一会话的暂停跟随）。</summary>
    public void ResetFollow()
    {
        _autoFollow = true;
        StopScrollAnimation();
        Dispatcher.UIThread.Post(ScrollToEndImmediate, DispatcherPriority.Loaded);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        DetachScrollViewer();
        base.OnApplyTemplate(e);
        _scrollViewer = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
        if (_scrollViewer is null)
            return;

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
        _scrollBarDragging = false;
    }

    /// <summary>用户滚轮上滚：停止跟随（滚回底部由 <see cref="OnScrollChanged"/> 恢复）。</summary>
    private void OnScrollViewerPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_autoFollow && e.Delta.Y > 0)
            DisableFollow();
    }

    /// <summary>键盘上滚：停止跟随。</summary>
    private void OnScrollViewerKeyDown(object? sender, KeyEventArgs e)
    {
        if (_autoFollow && e.Key is Key.Up or Key.PageUp or Key.Home)
            DisableFollow();
    }

    /// <summary>记录滚动条拖动（拖动上移时 <see cref="OnScrollChanged"/> 停止跟随）。</summary>
    private void OnScrollViewerPointerPressed(object? sender, PointerPressedEventArgs e) =>
        _scrollBarDragging =
            e.Source is Visual source && source.GetVisualAncestors().OfType<ScrollBar>().Any();

    private void OnScrollViewerPointerReleased(object? sender, PointerReleasedEventArgs e) =>
        _scrollBarDragging = false;

    /// <summary>立即滚到底部（无动画，初始加载无闪烁）。</summary>
    public void ScrollToEndImmediate()
    {
        if (_scrollViewer is null)
            return;
        StopScrollAnimation();
        _suppressAutoScroll = true;
        try
        {
            _scrollViewer.ScrollToEnd();
        }
        finally
        {
            _suppressAutoScroll = false;
        }
    }

    /// <summary>平滑滚到底部（160ms ease-out）。大跳变 / 首次加载仍走立即滚动。</summary>
    public void ScrollToEndSmooth()
    {
        if (_scrollViewer is null)
            return;
        var target = GetTargetOffset();
        var from = _scrollViewer.Offset.Y;
        var delta = target - from;
        if (delta <= 0.5)
            return;
        if (delta > _scrollViewer.Viewport.Height) // 切会话 / 首屏：动画会拖沓
        {
            ScrollToEndImmediate();
            return;
        }

        if (_scrollTimer is not null)
        {
            // 已在滚动（流式追加会连续触发）：只改目标，不重置 easing，避免每次都被拉回起点
            _scrollTo = target;
            return;
        }

        _scrollFrom = from;
        _scrollTo = target;
        _scrollStartTicks = Stopwatch.GetTimestamp();
        _scrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ScrollTickMs) };
        _scrollTimer.Tick += OnScrollTick;
        _scrollTimer.Start();
    }

    private double GetTargetOffset() =>
        _scrollViewer is null
            ? 0
            : Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);

    private void OnScrollTick(object? sender, EventArgs e)
    {
        if (_scrollViewer is null)
        {
            StopScrollAnimation();
            return;
        }

        // 用真实经过时间而不是「定时器次数 × 16ms」：UI 忙时 tick 会被推迟，
        // 固定步进会让 160ms 的动画实际拖成几百毫秒。
        var elapsedMs = Stopwatch.GetElapsedTime(_scrollStartTicks).TotalMilliseconds;
        var t = Math.Min(1d, elapsedMs / ScrollAnimMs);
        var eased = 1d - Math.Pow(1d - t, 3d); // CubicEaseOut

        _scrollTo = GetTargetOffset(); // 流式正文仍在长高：每帧追当前底部
        _suppressAutoScroll = true;
        try
        {
            _scrollViewer.Offset = _scrollViewer.Offset.WithY(
                _scrollFrom + (_scrollTo - _scrollFrom) * eased
            );
        }
        finally
        {
            _suppressAutoScroll = false;
        }

        if (t >= 1d)
        {
            StopScrollAnimation();
            ScrollToEndImmediate();
        }
    }

    private void StopScrollAnimation()
    {
        if (_scrollTimer is null)
            return;
        _scrollTimer.Tick -= OnScrollTick;
        _scrollTimer.Stop();
        _scrollTimer = null;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopScrollAnimation();
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scrollViewer is null)
            return;

        // 自身滚动动画产生的偏移不算用户滚动
        if (_suppressAutoScroll)
            return;

        var maxOffset = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);

        // 流式输出让内容长高（没有新 item）：原本贴底时继续跟随，否则保持用户位置
        if (
            _autoFollow
            && e.ExtentDelta.Y > 0
            && _scrollViewer.Offset.Y + e.ExtentDelta.Y >= maxOffset - 8
        )
            ScheduleFollowTail();

        // 用户拖动滚动条上移 → 停止跟随。
        // 注意：不能对所有 offset 负增量停跟随——布局校正（内容变矮/虚拟化估算）
        // 也会产生负增量，误判后永远回不到跟随状态（恢复会话/发消息停在中间的直接成因）。
        if (
            _scrollBarDragging
            && _autoFollow
            && e.OffsetDelta.Y < 0
            && _scrollViewer.Offset.Y < maxOffset - 8
        )
            DisableFollow();

        // 用户滚到底部 → 恢复跟随
        if (!_autoFollow && _scrollViewer.Offset.Y >= maxOffset - 8)
            _autoFollow = true;

        // 用户滚到顶部 → 触发历史加载（程序化归零不算）
        if (!_autoFollow && e.OffsetDelta.Y < 0 && _scrollViewer.Offset.Y <= 0)
            ScrolledToTop?.Invoke();
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
        if (_autoFollow && !_suppressAutoScroll)
            ScrollToEndSmooth();
    }

    private void DisableFollow()
    {
        StopScrollAnimation();
        _autoFollow = false;
        AutoFollowChanged?.Invoke();
    }

    private void OnItemsSourceChanged(AvaloniaPropertyChangedEventArgs e)
    {
        _autoFollow = true;
        StopScrollAnimation();
        Dispatcher.UIThread.Post(ScrollToEndImmediate, DispatcherPriority.Loaded);
    }
}
