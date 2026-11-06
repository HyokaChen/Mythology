using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Core.Data;
using Desktop.Controls;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 会话窗口底部动作条：破坏性动作必须远离右下角的主 CTA。
/// 历史问题：`[删除][打开]` 相邻、同高同形、间距 12px，鼠标奔向右下角时经常滑到删除上
/// （用户报障「老是习惯性点到这个删除」）。这里锁的是**几何距离**，不是源码文本。
/// </summary>
public sealed class SessionManagerLayoutTests
{
    private static readonly HeadlessUiHost Ui = HeadlessUiHost.Instance;

    [Fact]
    public void Delete_Is_Far_From_Primary_And_Primary_Owns_The_Corner()
    {
        Ui.Run(() =>
        {
            var window = new SessionManagerWindow(
                loader: () =>
                    Task.FromResult(
                        new List<AgentSession>
                        {
                            new() { Id = "s1", Title = "再继续结合云端分析检查设定的合理性" },
                        }
                    ),
                onOpen: _ => { },
                onDelete: _ => Task.CompletedTask,
                currentSessionId: "s1"
            );
            window.Show();

            // LoadAsync 是 fire-and-forget：泵到行容器出现为止
            for (
                var i = 0;
                i < 20 && !window.GetVisualDescendants().OfType<ListBoxItem>().Any();
                i++
            )
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }
            Dispatcher.UIThread.RunJobs();

            var delete = window.FindControl<Button>("DeleteButton");
            var open = window.FindControl<Button>("OpenButton");
            Assert.NotNull(delete);
            Assert.NotNull(open);

            var deletePos = delete!.TranslatePoint(new Point(0, 0), window);
            var openPos = open!.TranslatePoint(new Point(0, 0), window);
            Assert.NotNull(deletePos);
            Assert.NotNull(openPos);

            var gap = openPos!.Value.X - (deletePos!.Value.X + delete.Bounds.Width);
            Assert.True(
                gap >= 120,
                $"删除与打开只隔 {gap:F0}px（应 ≥120）——主 CTA 旁边紧挨破坏性动作会误点"
            );

            // 删除在左半区，打开独占右下角
            Assert.True(
                deletePos.Value.X < window.Bounds.Width / 2,
                $"删除不在左侧（x={deletePos.Value.X:F0}）"
            );
            Assert.True(
                openPos.Value.X > window.Bounds.Width / 2,
                $"打开不在右半区（x={openPos.Value.X:F0}）"
            );
            Assert.True(
                open.Bounds.Width >= 90,
                $"主 CTA 宽度只有 {open.Bounds.Width:F0}px（应 ≥90，角落目标要够大）"
            );
        });
    }

    /// <summary>
    /// 列表首行 = 「新建对话」：位于 ListBox 之上、在可见区域内，点击即回调并关窗。
    /// 用户诉求：新建入口要在列表里显眼处，不能只留在窗口外的头部工具条。
    /// </summary>
    [Fact]
    public void NewSession_Row_Sits_Above_List_And_Invokes_Callback()
    {
        Ui.Run(() =>
        {
            var calls = 0;
            var window = new SessionManagerWindow(
                loader: () =>
                    Task.FromResult(
                        new List<AgentSession>
                        {
                            new() { Id = "s1", Title = "第一条会话" },
                        }
                    ),
                onOpen: _ => { },
                onDelete: _ => Task.CompletedTask,
                currentSessionId: "s1",
                onNewSession: () => calls++
            );
            window.Show();
            Pump(window);

            var newRow = window.FindControl<Button>("NewSessionButton");
            var list = window.GetVisualDescendants().OfType<ListBox>().FirstOrDefault();
            Assert.NotNull(newRow);
            Assert.NotNull(list);
            Assert.True(newRow!.IsEffectivelyVisible, "「新建对话」行不可见");
            Assert.Contains("新建对话", Texts(newRow), StringComparison.Ordinal);

            var rowPos = newRow.TranslatePoint(new Point(0, 0), window);
            var listPos = list!.TranslatePoint(new Point(0, 0), window);
            Assert.NotNull(rowPos);
            Assert.NotNull(listPos);
            Assert.True(
                rowPos!.Value.Y < listPos!.Value.Y,
                $"「新建对话」应在列表之上（rowY={rowPos.Value.Y:F0}, listY={listPos.Value.Y:F0}）"
            );

            newRow.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, calls);

            // Close() 之后还有退场动画帧：泵到窗口真正不可见
            for (var i = 0; i < 40 && window.IsVisible; i++)
            {
                Dispatcher.UIThread.RunJobs();
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
                Thread.Sleep(10);
            }
            Assert.False(window.IsVisible, "点「新建对话」后应先关闭本窗再开新对话");
        });
    }

    /// <summary>没有会话时列表卡片仍保留「新建对话」入口（空态文案指向它）。</summary>
    [Fact]
    public void Empty_List_Still_Shows_NewSession_Row()
    {
        Ui.Run(() =>
        {
            var window = new SessionManagerWindow(
                loader: () => Task.FromResult(new List<AgentSession>()),
                onOpen: _ => { },
                onDelete: _ => Task.CompletedTask,
                onNewSession: () => { }
            );
            window.Show();
            Pump(window);

            var newRow = window.FindControl<Button>("NewSessionButton");
            var empty = window
                .GetVisualDescendants()
                .OfType<Border>()
                .FirstOrDefault(b => b.Name == "EmptyNote");
            Assert.NotNull(newRow);
            Assert.True(newRow!.IsEffectivelyVisible, "空列表时「新建对话」必须仍可见");
            Assert.NotNull(empty);
            Assert.True(empty!.IsVisible, "空列表应显示空态提示");
        });
    }

    private static void Pump(Window window)
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    private static string Texts(Control root) =>
        string.Join(
            "\n",
            root.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "")
        );
}
