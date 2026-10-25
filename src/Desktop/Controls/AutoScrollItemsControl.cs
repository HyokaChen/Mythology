using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;

namespace Desktop.Controls;

/// <summary>
/// 自动滚动列表（端口自 LKK AutoScrollItemsControl，去 SignalR 依赖）：
/// - 新消息自动滚动到底部（初始加载无闪烁）
/// - 用户手动上滚时停止自动跟随，滚回底部时恢复
/// - 模板要求包含 x:Name="PART_ScrollViewer" 的 ScrollViewer
/// </summary>
public class AutoScrollItemsControl : ItemsControl
{
    private const int ScrollAnimMs = 160;
    private const int ScrollTickMs = 16;
    private ScrollViewer? _scrollViewer;
    private bool _autoFollow = true;
    private bool _suppressAutoScroll;
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
        // 内容变化 → 自动滚动（流式追加）
        Items.CollectionChanged += (_, _) =>
        {
            if (_autoFollow && !_suppressAutoScroll)
                Dispatcher.UIThread.Post(ScrollToEndSmooth, DispatcherPriority.Background);
        };
    }

    /// <summary>用户滚到顶部（历史加载钩子）。</summary>
    public event Action? ScrolledToTop;

    /// <summary>用户上滚（停止跟随）。</summary>
    public event Action? AutoFollowChanged;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _scrollViewer = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
        if (_scrollViewer is not null)
            _scrollViewer.ScrollChanged += OnScrollChanged;
    }

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

        var maxOffset = _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height;

        // 流式输出让内容长高（没有新 item）：原本贴底时继续跟随，否则保持用户位置
        if (
            _autoFollow
            && e.ExtentDelta.Y > 0
            && _scrollViewer.Offset.Y + e.ExtentDelta.Y >= maxOffset - 8
        )
            Dispatcher.UIThread.Post(ScrollToEndSmooth, DispatcherPriority.Background);

        // 用户上滚（内容顶部露出）→ 停止跟随
        if (e.OffsetDelta.Y < 0 && _autoFollow && _scrollViewer.Offset.Y < maxOffset - 8)
        {
            StopScrollAnimation();
            _autoFollow = false;
            AutoFollowChanged?.Invoke();
        }

        // 用户滚到底部 → 恢复跟随
        if (!_autoFollow && _scrollViewer.Offset.Y >= maxOffset - 8)
            _autoFollow = true;

        // 滚动到顶部 → 触发历史加载
        if (e.OffsetDelta.Y < 0 && _scrollViewer.Offset.Y <= 0)
            ScrolledToTop?.Invoke();
    }

    private void OnItemsSourceChanged(AvaloniaPropertyChangedEventArgs e)
    {
        _autoFollow = true;
        StopScrollAnimation();
        Dispatcher.UIThread.Post(ScrollToEndImmediate, DispatcherPriority.Loaded);
    }
}
