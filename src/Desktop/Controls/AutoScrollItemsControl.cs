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
/// - 对话列表请用 StackPanel + 像素窗口；不要用 VSP：可变高度下 extent 估算会振荡
///   （Avalonia issue #11989），表现为滚动条无限上下跳
///
/// 「用户上滚」只认输入意图，不认 offset 负增量：布局校正（内容变矮、窗口裁剪）
/// 也会让 offset 变小，把它当用户滚动会永久关闭跟随，表现为恢复会话/发消息后停在中间。
/// </summary>
public class AutoScrollItemsControl : ItemsControl
{
    private ScrollViewer? _scrollViewer;
    private bool _autoFollow = true;
    private bool _scrollBarDragging;
    private bool _followPostPending;

    /// <summary>用户滚到顶部（历史加载钩子）。只在用户输入意图下触发。</summary>
    public event Action? ScrolledToTop;

    /// <summary>当前是否处于「跟随尾部」状态。</summary>
    public bool IsFollowing => _autoFollow;

    static AutoScrollItemsControl()
    {
        ItemsSourceProperty.Changed.AddClassHandler<AutoScrollItemsControl>(
            static (control, e) => control.OnItemsSourceChanged(e)
        );
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
                _autoFollow = true;
                Dispatcher.UIThread.Post(ScrollToBottomOnce, DispatcherPriority.Loaded);
                return;
            }

            if (_autoFollow)
                ScheduleFollowTail();
        };
    }

    /// <summary>会话/数据源切换：恢复跟随并滚到底部（新会话不允许继承上一会话的暂停跟随）。</summary>
    public void ResetFollow()
    {
        _autoFollow = true;
        Dispatcher.UIThread.Post(ScrollToBottomOnce, DispatcherPriority.Loaded);
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
        _scrollViewer?.ScrollToEnd();
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scrollViewer is null)
            return;

        var maxOffset = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);

        // 跟随状态下任何 extent 增长（流式正文长高 / 新行被测量 / 虚拟化校正）都重新贴底。
        // 不能再拿「offset + delta >= max - 8」判断：ScrollViewer 的锚点会在插入新行时
        // 把 offset 往回收（保持原内容），此时 offset 已不在旧底部，条件永远不成立。
        // TEMP: single-shot
        // if (_autoFollow && e.ExtentDelta.Y > 0)
        //     ScheduleFollowTail();

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

        // 用户滚到顶部 → 通知视图加载更早的一页（程序化归零不算）。
        if (!_autoFollow && e.OffsetDelta.Y < 0 && _scrollViewer.Offset.Y <= 0)
            ScrolledToTop?.Invoke();

        // 用户滚到底部 → 恢复跟随
        if (!_autoFollow && _scrollViewer.Offset.Y >= maxOffset - 8)
            _autoFollow = true;
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
    private void ScrollToBottomOnce()
    {
        if (!_autoFollow || _scrollViewer is null)
            return;
        if (Items.Count > 0)
            ScrollIntoView(Items.Count - 1);
        _scrollViewer.ScrollToEnd();
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_autoFollow)
                    _scrollViewer?.ScrollToEnd();
            },
            DispatcherPriority.Loaded
        );
    }

    private void FollowTailIfEnabled()
    {
        if (!_autoFollow || _scrollViewer is null)
            return;

        var maxOffset = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
        if (_scrollViewer.Offset.Y >= maxOffset - 0.5)
            return;

        // 只按 offset 追底，不调用 ScrollIntoView：VSP 的 ScrollIntoView 会把最后一个
        // 容器设为锚点，高消息/估算校正时又把 offset 拉回旧锚点，形成 29.5k↔65k 的
        // 无限循环（真实会话 82 条即可复现）。ScrollToEnd 让 VSP 自己按 offset 实现
        // 底部项，extent 增长时会再次进入本方法，offset 单调收敛到真实 max。
        _scrollViewer.ScrollToEnd();
    }

    private void DisableFollow()
    {
        _autoFollow = false;
    }

    private void OnItemsSourceChanged(AvaloniaPropertyChangedEventArgs e)
    {
        _autoFollow = true;
        Dispatcher.UIThread.Post(ScrollToBottomOnce, DispatcherPriority.Loaded);
    }
}
