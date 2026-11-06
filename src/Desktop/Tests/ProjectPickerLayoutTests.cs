using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Core.Data;
using Desktop.Controls;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 书房（小说项目）弹窗：新建入口要在列表里显眼处——列表首行「新建小说」，
/// 空列表时也必须保留（那时最需要它）。与会话弹窗同一模式。
/// </summary>
public sealed class ProjectPickerLayoutTests : IDisposable
{
    private readonly string _root;

    public ProjectPickerLayoutTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"desktop-picker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "novels"));
        Directory.CreateDirectory(Path.Combine(_root, "mythology"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    private ProjectPickerWindow CreateWindow()
    {
        var novels = Path.Combine(_root, "novels");
        var store = new FileNovelStore(novels, Path.Combine(_root, "mythology"));
        return new ProjectPickerWindow(
            store,
            _ => { },
            NovelWorkspace.Single(novels),
            showLlmHint: false
        );
    }

    [Fact]
    public void NewNovel_Row_Is_First_Row_Above_List()
    {
        HeadlessUiHost.Instance.Run(() =>
        {
            var window = CreateWindow();
            window.Show();
            Pump();

            var newRow = window.FindControl<Button>("NewButton");
            var list = window.GetVisualDescendants().OfType<ListBox>().FirstOrDefault();
            Assert.NotNull(newRow);
            Assert.NotNull(list);
            Assert.True(newRow!.IsEffectivelyVisible, "「新建小说」行不可见");

            var rowPos = newRow.TranslatePoint(new Point(0, 0), window);
            var listPos = list!.TranslatePoint(new Point(0, 0), window);
            Assert.NotNull(rowPos);
            Assert.NotNull(listPos);
            Assert.True(
                rowPos!.Value.Y < listPos!.Value.Y,
                $"「新建小说」应在列表之上（rowY={rowPos.Value.Y:F0}, listY={listPos.Value.Y:F0}）"
            );
        });
    }

    [Fact]
    public void Empty_Library_Still_Shows_NewNovel_Row()
    {
        HeadlessUiHost.Instance.Run(() =>
        {
            var window = CreateWindow();
            window.Show();
            Pump();

            var newRow = window.FindControl<Button>("NewButton");
            var empty = window.FindControl<Border>("EmptyNote");
            Assert.NotNull(newRow);
            Assert.NotNull(empty);
            Assert.True(newRow!.IsEffectivelyVisible, "空书房时「新建小说」必须仍可见");
            Assert.True(empty!.IsVisible, "空书房应显示空态提示");
            // 空态与新建行不重叠：空态在卡片内、位于新建行下方
            var rowPos = newRow.TranslatePoint(new Point(0, 0), window);
            var emptyPos = empty.TranslatePoint(new Point(0, 0), window);
            Assert.NotNull(rowPos);
            Assert.NotNull(emptyPos);
            Assert.True(
                emptyPos!.Value.Y >= rowPos!.Value.Y + newRow.Bounds.Height - 1,
                $"空态盖住了新建行（rowY={rowPos.Value.Y:F0}+{newRow.Bounds.Height:F0}, emptyY={emptyPos.Value.Y:F0}）"
            );
        });
    }

    private static void Pump()
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }
}
