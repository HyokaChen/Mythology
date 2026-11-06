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
            var list = new ListBox { Width = 480 };
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
}
