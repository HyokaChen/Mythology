using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// SukiUI 的 ListBox 模板在 ItemsPresenter 之外多声明了一个 ListBoxItem，
/// Content = 数据对象的 ToString()（运行时实测 index = -1、恒可见）。
/// 不收起它，列表顶上就会多出一行「标题 · 题材 0章 · 日期」——书房/会话/命令面板全都中招。
/// 这里锁的是**可见容器数量**：N 条数据只能有 N 个可见行。
/// </summary>
public sealed class ListRowContainerTests
{
    private static readonly HeadlessUiHost Ui = HeadlessUiHost.Instance;

    private sealed record Item(string Title);

    [Fact]
    public void ListBox_Renders_One_Visible_Row_Per_Item()
    {
        Ui.Run(() =>
        {
            var list = new ListBox
            {
                Width = 480,
                ItemContainerTheme = (Avalonia.Styling.ControlTheme)
                    Avalonia.Application.Current!.Resources["AdalineRowContainer"]!,
            };
            Desktop.Styles.Motion.SetStagger(list, true);
            list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Item>(
                (item, _) => new TextBlock { Text = item?.Title }
            );
            list.ItemsSource = new[] { new Item("甲"), new Item("乙") };

            var window = new Window
            {
                Content = list,
                Width = 520,
                Height = 200,
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var items = list.GetVisualDescendants().OfType<ListBoxItem>().ToList();
            Assert.Equal(2, items.Count(i => i.IsVisible));
            Assert.All(items.Where(i => i.IsVisible), i => Assert.IsType<Item>(i.Content));
        });
    }

    /// <summary>
    /// 真实弹窗口径：会话管理器用的是同一套 ListBox + AdalineRowContainer + Stagger。
    /// 这里直接开真窗口，确保「幻影行」不会在弹窗场景里复活（用户报障的正是这个窗口）。
    /// </summary>
    [Fact]
    public void SessionManagerWindow_Shows_No_Phantom_Row()
    {
        Ui.Run(() =>
        {
            var session = new Core.Data.AgentSession
            {
                Id = "s1",
                Title = "再继续结合云端分析检查设定的合理性",
            };
            var window = new Desktop.Controls.SessionManagerWindow(
                loader: () => Task.FromResult(new List<Core.Data.AgentSession> { session }),
                onOpen: _ => { },
                onDelete: _ => Task.CompletedTask,
                currentSessionId: "s1"
            );
            window.Show();

            // LoadAsync 是 fire-and-forget：泵几轮直到行容器出现
            for (var i = 0; i < 20 && !window.GetVisualDescendants().OfType<ListBoxItem>().Any(); i++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            var rows = window.GetVisualDescendants().OfType<ListBoxItem>().ToList();
            Assert.Single(rows, r => r.IsVisible);
            Assert.IsNotType<string>(rows.Single(r => r.IsVisible).Content); // 幻影行的 Content 是 ToString() 字符串
        });
    }
}
