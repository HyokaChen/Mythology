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
    private ScrollViewer? _scrollViewer;
    private bool _autoFollow = true;
    private bool _suppressAutoScroll;

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
                Dispatcher.UIThread.Post(ScrollToEndImmediate, DispatcherPriority.Background);
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

    /// <summary>平滑滚到底部。</summary>
    public void ScrollToEndSmooth()
    {
        _scrollViewer?.ScrollToEnd();
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scrollViewer is null)
            return;

        var maxOffset = _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height;

        // 用户上滚（内容顶部露出）→ 停止跟随
        if (e.OffsetDelta.Y < 0 && _autoFollow && _scrollViewer.Offset.Y < maxOffset - 8)
        {
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
        Dispatcher.UIThread.Post(ScrollToEndImmediate, DispatcherPriority.Loaded);
    }
}
