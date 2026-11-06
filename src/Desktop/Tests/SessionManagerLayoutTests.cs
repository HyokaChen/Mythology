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
}
