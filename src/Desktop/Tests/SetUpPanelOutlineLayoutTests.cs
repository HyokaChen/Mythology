using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Core.Data;
using Core.Novels;
using Core.Tools;
using Desktop.ViewModels;
using Desktop.Views;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 大纲树不得横向溢出：长章节标题必须省略号收尾，而不是把面板撑出横向滚动条
/// （用户报障：13 寸屏三级展开后出现横向滚动条、右侧内容被截）。
/// 断言用真实布局的 Extent / Viewport，不解析源码文本。
/// </summary>
public sealed class SetUpPanelOutlineLayoutTests : IDisposable
{
    private const string NovelId = "n1";
    private readonly string _root;
    private readonly string _myth;
    private readonly FileNovelStore _store;

    public SetUpPanelOutlineLayoutTests()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"desktop-outline-layout-{Guid.NewGuid():N}");
        _root = Path.Combine(tmp, "novels");
        _myth = Path.Combine(tmp, "mythology");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_myth);
        _store = new FileNovelStore(_root, _myth);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_root)!, true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    private const string LongTitle =
        "第一卷 眷恋的幽灵——谁在眷恋着世间？以及更长更长更长的一个章节标题用来逼出横向滚动条";

    private async Task SeedAsync()
    {
        await _store.SaveNovel(new Novel { Id = NovelId, Title = "恋爱吧！折花君" });
        await _store.SaveVolume(
            new Volume
            {
                Id = "vol1",
                NovelId = NovelId,
                Name = "第一卷 眷恋的幽灵",
                Order = 1,
            }
        );
        await _store.SaveChapter(
            new Chapter
            {
                Id = "ch1",
                NovelId = NovelId,
                Title = LongTitle,
                Order = 1,
                VolumeId = "vol1",
                Status = ChapterStatus.Draft,
                Content = new string('文', 1000),
            }
        );
    }

    [Fact]
    public async Task LongChapterTitle_Does_Not_Overflow_Horizontally()
    {
        await SeedAsync();
        var vm = new SetUpPanelViewModel(
            _store,
            null,
            new ToolRegistry(),
            null,
            () => NovelId,
            null,
            a => a()
        );
        await vm.RefreshAsync();

        HeadlessUiHost.Instance.Run(() =>
        {
            var view = new SetUpPanelView { DataContext = vm };
            // 13 寸笔记本的侧栏实际宽度约 330–360
            var window = new Window
            {
                Content = view,
                Width = 360,
                Height = 640,
            };
            window.Show();

            for (var i = 0; i < 40; i++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
                if (view.GetVisualDescendants().OfType<TreeView>().Any())
                    break;
            }

            // 展开卷，让章行（长标题）物化
            var volume = vm.OutlineNodes[0]
                .Children.FirstOrDefault(n => n.Kind == OutlineRowKind.VolumeHeader);
            Assert.NotNull(volume);
            volume!.IsExpanded = true;

            for (var i = 0; i < 40; i++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            var tree = view.GetVisualDescendants().OfType<TreeView>().FirstOrDefault();
            Assert.NotNull(tree);

            var scroll = tree!.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            Assert.NotNull(scroll);

            Assert.True(
                scroll!.Extent.Width <= scroll.Viewport.Width + 1,
                $"大纲树横向溢出：Extent={scroll.Extent.Width:F1} > Viewport={scroll.Viewport.Width:F1}"
            );
        });
    }
}
