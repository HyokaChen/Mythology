using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// ScrollViewer 布局回归：滚动条必须占用自己的布局槽位，不能压在内容上。
///
/// 历史 bug：App 级主题（SmoothScroll）把 ScrollContentPresenter 的
/// Grid.ColumnSpan/RowSpan 设成 2，内容横跨到滚动条列下面——新建项目表单的
/// 输入框右缘被滚动条直接盖住（右侧内容「被遮挡」）。
/// </summary>
public sealed class ScrollViewerLayoutTests
{
    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public void VerticalScrollBar_ReservesItsOwnLane_InsteadOfOverlayingContent()
    {
        HeadlessUiHost.Instance.Run(() =>
        {
            var stack = new StackPanel();
            for (var i = 0; i < 20; i++)
                stack.Children.Add(
                    new Border
                    {
                        Height = 40,
                        Child = new TextBlock { Text = $"row {i}" },
                    }
                );

            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = stack,
            };
            var window = new Window
            {
                Width = 400,
                Height = 300,
                Content = scrollViewer,
            };
            window.Show();
            Pump();

            var presenter = scrollViewer
                .GetVisualDescendants()
                .OfType<ScrollContentPresenter>()
                .Single();
            var bar = scrollViewer
                .GetVisualDescendants()
                .OfType<ScrollBar>()
                .Single(b => b.Orientation == Orientation.Vertical);

            Assert.True(bar.IsEffectivelyVisible, "自动滚动条未显示：本用例的滚动内容不足");
            Assert.True(
                presenter.Bounds.Right <= bar.Bounds.X + 0.5,
                $"内容右缘 {presenter.Bounds.Right} 越过滚动条左缘 {bar.Bounds.X}：滚动条压住了内容"
            );

            window.Close();
        });
    }
}
