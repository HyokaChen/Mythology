using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Desktop.Controls;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// Ctrl+K 命令面板的表格交互契约：搜索过滤、↑↓ 游标、Enter / 单击执行。
/// 旧实现是 ListBox + DoubleTapped；这里锁住“不是可选列表、不需要二次点击”的语义。
/// </summary>
public sealed class CommandPaletteWindowTests
{
    private static PaletteCommand[] Commands(bool[] executed) =>
        [
            new()
            {
                Title = "新建对话",
                Shortcut = "Ctrl+N",
                Execute = () => executed[0] = true,
            },
            new()
            {
                Title = "/read",
                Description = "打开章节",
                Execute = () => executed[1] = true,
            },
            new()
            {
                Title = "/tree",
                Description = "对话分支树导航",
                Execute = () => executed[2] = true,
            },
        ];

    [Fact]
    public void UsesTableRows_NotSelectableListBox()
    {
        HeadlessUiHost.Instance.Run(() =>
        {
            var window = new CommandPaletteWindow(Commands(new bool[3]));
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(window.GetVisualDescendants().OfType<ListBox>());
            Assert.NotNull(window.FindControl<ItemsControl>("CommandTable"));

            var rows = window
                .GetVisualDescendants()
                .OfType<Button>()
                .Where(b => b.Classes.Contains("PaletteRow"))
                .ToList();
            Assert.Equal("新建对话", AutomationProperties.GetName(rows[0]));

            var headers = window
                .GetVisualDescendants()
                .OfType<TextBlock>()
                .Select(t => t.Text)
                .ToList();
            Assert.Contains("命令", headers);
            Assert.Contains("说明", headers);
            Assert.Contains("快捷键", headers);
            window.Close();
        });
    }

    [Fact]
    public void HeaderAndShortcut_StayAligned_WhenWindowNarrowed()
    {
        HeadlessUiHost.Instance.Run(() =>
        {
            var window = new CommandPaletteWindow(Commands(new bool[3]));
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var header = window
                .GetVisualDescendants()
                .OfType<TextBlock>()
                .First(t => t.Text == "快捷键");
            var shortcut = window
                .GetVisualDescendants()
                .OfType<TextBlock>()
                .First(t => t.Text == "Ctrl+N");

            void AssertAligned()
            {
                var headerRight = header
                    .TranslatePoint(new Point(header.Bounds.Width, 0), window)!
                    .Value.X;
                var shortcutRight = shortcut
                    .TranslatePoint(new Point(shortcut.Bounds.Width, 0), window)!
                    .Value.X;
                Assert.InRange(shortcutRight, headerRight - 1, headerRight + 1);
            }

            AssertAligned();

            window.Width = 520;
            for (var i = 0; i < 5; i++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }
            AssertAligned();
            window.Close();
        });
    }

    [Fact]
    public void SearchFiltersRows_AndEnterExecutesOnlyMatch()
    {
        HeadlessUiHost.Instance.Run(() =>
        {
            var executed = new bool[3];
            var window = new CommandPaletteWindow(Commands(executed));
            window.Show();
            Dispatcher.UIThread.RunJobs();

            window.SearchInput.Text = "/read";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, window.CommandTable.Items.Count);
            Assert.False(window.EmptyNote.IsVisible);

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();

            Assert.False(executed[0]);
            Assert.True(executed[1]);
            Assert.False(executed[2]);
            window.Close();
        });
    }

    [Fact]
    public void NoMatch_ShowsEmptyNote()
    {
        HeadlessUiHost.Instance.Run(() =>
        {
            var window = new CommandPaletteWindow(Commands(new bool[3]));
            window.Show();
            Dispatcher.UIThread.RunJobs();

            window.SearchInput.Text = "no-such-command";
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, window.CommandTable.Items.Count);
            Assert.True(window.EmptyNote.IsVisible);
            window.Close();
        });
    }

    [Fact]
    public void Down_MovesCurrentRow()
    {
        HeadlessUiHost.Instance.Run(() =>
        {
            var window = new CommandPaletteWindow(Commands(new bool[3]));
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var rows = window
                .GetVisualDescendants()
                .OfType<Button>()
                .Where(b => b.Classes.Contains("PaletteRow"))
                .ToList();
            Assert.True(rows[0].Classes.Contains("current"));

            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Dispatcher.UIThread.RunJobs();

            Assert.False(rows[0].Classes.Contains("current"));
            Assert.True(rows[1].Classes.Contains("current"));
            window.Close();
        });
    }

    [Fact]
    public void Click_ExecutesRowCommand()
    {
        HeadlessUiHost.Instance.Run(() =>
        {
            var executed = new bool[3];
            var window = new CommandPaletteWindow(Commands(executed));
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var row = window
                .GetVisualDescendants()
                .OfType<Button>()
                .First(b => b.Classes.Contains("PaletteRow"));
            row.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.True(executed[0]);
            Assert.False(executed[1]);
            Assert.False(executed[2]);
            window.Close();
        });
    }
}
