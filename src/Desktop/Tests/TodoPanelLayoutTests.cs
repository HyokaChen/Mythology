using Avalonia.Controls;
using Avalonia.VisualTree;
using Core.Shared;
using Desktop.ViewModels;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 右侧栏 Todo 面板高度契约：todo 再多也只能占约 3 行，其余滚动。
/// 面板在 Grid 的 Auto 行里不得按内容无限长高——那会把上方设定面板挤没。
/// </summary>
public sealed class TodoPanelLayoutTests
{
    [Fact]
    public void ManyTodos_CapViewportAndLeaveRoomForSetupPanel()
    {
        HeadlessUiHost.Instance.Run(() =>
        {
            var store = new TodoStore();
            for (var i = 0; i < 9; i++)
                store.Create($"P{i % 4}: 第 {i + 1} 项任务");

            var vm = new TodoPanelViewModel(store);
            var panel = new Desktop.Views.TodoPanelView { DataContext = vm };

            // 复刻 MainWindow 右栏：设定面板 *, Auto —— Todo 只能拿 Auto 行的内容高度。
            var host = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
            var setup = new Border();
            host.Children.Add(setup);
            host.Children.Add(panel);
            Grid.SetRow(setup, 0);
            Grid.SetRow(panel, 1);

            var window = new Window
            {
                Width = 320,
                Height = 700,
                Content = host,
            };
            window.Show();
            try
            {
                Pump();

                Assert.Equal(9, vm.Items.Count);
                var scroll = Assert.Single(panel.GetVisualDescendants().OfType<ScrollViewer>());
                Assert.True(scroll.Bounds.Height <= 118.5, $"可视区超限：{scroll.Bounds.Height}");
                Assert.True(
                    scroll.Extent.Height > scroll.Viewport.Height,
                    "任务数超过 3 项时必须进入滚动区"
                );
                Assert.True(
                    panel.Bounds.Height < 170,
                    $"Todo 面板挤压设定面板：{panel.Bounds.Height}"
                );
                Assert.True(setup.Bounds.Height > 500, $"设定面板只剩 {setup.Bounds.Height}");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void Pump()
    {
        for (var i = 0; i < 10; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(5);
        }
    }
}
