using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// ToolTip 真实打开回归：用生产主题（App.axaml 的 MythologyStyles）打开悬浮提示，
/// 断言 UI 线程不抛异常、提示确实进入 open 态，且后续滚轮输入仍然有效。
///
/// 背景：ToolTip 的 Style.Animations 曾在 KeyFrame 里 Set RenderTransform。
/// Avalonia 没有该属性的 animator，ToolTip.Open 时抛
/// <c>InvalidOperationException: No animator registered for the property RenderTransform</c>；
/// 实机异常从 macOS 原生 RawMouseEvent 的 Dispatcher.Send 路径冒出，打断输入帧。
/// 结构锁在 MotionWiringTests，这里锁运行期行为。
/// </summary>
public sealed class ToolTipStyleRuntimeTests
{
    private static readonly HeadlessUiHost Ui = HeadlessUiHost.Instance;

    private static void Pump(int rounds = 20)
    {
        for (var i = 0; i < rounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void OpenToolTip_DoesNotThrow_AndWheelStillWorks()
    {
        Ui.Run(() =>
        {
            var stack = new StackPanel();
            var button = new Button
            {
                Content = "hover me",
                Width = 160,
                Height = 40,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            };
            ToolTip.SetTip(button, "tip text");
            ToolTip.SetShowDelay(button, 0);
            stack.Children.Add(button);
            for (var i = 0; i < 40; i++)
                stack.Children.Add(new TextBlock { Text = $"line {i}" });

            var scroller = new ScrollViewer
            {
                Content = stack,
                Width = 300,
                Height = 300,
            };
            var window = new Window
            {
                Width = 320,
                Height = 320,
                Content = scroller,
            };
            window.Show();
            try
            {
                Pump();
                var buttonPoint = new Point(80, 20);

                // 悬浮打开 ToolTip：修复前这里同步抛 InvalidOperationException。
                window.MouseMove(buttonPoint);
                Pump();
                Assert.True(ToolTip.GetIsOpen(button), "ToolTip 没有打开（样式未生效/被异常中断）");

                // 离开后应正常关闭；异常中断会把它留在 open 态。
                window.MouseMove(new Point(300, 300));
                Pump();
                Assert.False(ToolTip.GetIsOpen(button), "ToolTip 移开后仍未关闭");

                // 输入管线仍活着：滚轮能滚动。
                scroller.Offset = new Vector(0, 100);
                Pump(2);
                window.MouseWheel(new Point(250, 250), new Vector(0, -5));
                Pump(4);
                Assert.True(scroller.Offset.Y > 100, $"ToolTip 之后滚轮失效：{scroller.Offset.Y}");
            }
            finally
            {
                window.Close();
            }
        });
    }
}
