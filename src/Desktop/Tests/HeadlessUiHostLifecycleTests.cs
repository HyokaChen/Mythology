using Avalonia.Controls;
using Desktop.Styles;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// headless 宿主生命周期回归：窗口退场动效默认保留；Run 结束必须兜底关闭
/// 本次新开且仍可见的窗口。后者一旦退化，窗口会永久留在共享 Application 里，
/// 污染后续测试并推高 headless 渲染的 LOH（每帧每个窗口一块全尺寸 framebuffer）。
/// </summary>
public sealed class HeadlessUiHostLifecycleTests
{
    private static readonly HeadlessUiHost Ui = HeadlessUiHost.Instance;

    [Fact]
    public void Run_KeepsWindowExitMotionEnabledByDefault()
    {
        Ui.Run(() =>
        {
            var window = new Window { Width = 200, Height = 100 };
            window.Show();

            var canceled = false;
            window.Closing += (_, e) => canceled |= e.Cancel;
            Motion.AttachWindowMotion(window);
            window.Close();

            // 默认必须走真实退场：Closing 被取消一次；不 Pump，交给 Run 的兜底清理。
            Assert.True(canceled, "headless 默认关闭了窗口退场动效，WindowExit 测试会失去意义");
        });

        Ui.Run(() => Assert.Equal(0, Ui.VisibleWindowCount));
    }

    [Fact]
    public void Run_ClosesWindowsLeftOpenByAction()
    {
        Window? window = null;
        Ui.Run(() =>
        {
            window = new Window { Width = 200, Height = 100 };
            window.Show();
            Assert.True(window.IsVisible);
        });

        Assert.NotNull(window);
        Ui.Run(() =>
        {
            Assert.False(window!.IsVisible);
            Assert.Equal(0, Ui.VisibleWindowCount);
        });
    }
}
