using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
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
        await _store.SaveNovel(
            new Novel
            {
                Id = NovelId,
                Title = "恋爱吧！折花君",
                Author = "觞沉·测试测试测试测试",
                Genre = "都市生活",
                TargetWords = 500000,
            }
        );
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

    /// <summary>
    /// 小说行副文本（作者 · 题材 · 目标字数）很长时：标题必须仍可见、行尾省略，
    /// 且右侧 ＋/⋯ 与文字行垂直居中（用户报障：Meta 把标题挤成 0 宽并覆盖整行、
    /// 动作图标比文字低半行）。
    /// </summary>
    [Fact]
    public async Task LongNovelMeta_DoesNotHideTitle_AndActionsCenterWithText()
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
            // 13 寸侧栏实际宽度约 330；旧布局在这个宽度下标题宽度正好被 Meta 挤成 0
            var window = new Window
            {
                Content = view,
                Width = 330,
                Height = 640,
            };
            window.Show();
            for (var i = 0; i < 50; i++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            var tree = view.GetVisualDescendants().OfType<TreeView>().First();

            // 标题/Meta 合成一条 TextBlock（Inlines）；旧布局下标题是单独的 TextBlock。
            // 两种结构都用「文本是否以书名开头」识别，断言的是可见宽度而不是源码结构。
            var headers = tree.GetVisualDescendants()
                .OfType<TextBlock>()
                .Select(t =>
                    (
                        Block: t,
                        Text: t.Text
                            ?? string.Concat(t.Inlines.OfType<Run>().Select(run => run.Text))
                    )
                )
                .Where(x => x.Text.StartsWith("恋爱吧！折花君", StringComparison.Ordinal))
                .ToList();
            var header = Assert.Single(headers);
            Assert.True(
                header.Block.Bounds.Width > 0,
                $"标题被 Meta 挤成 0 宽（Bounds={header.Block.Bounds}）"
            );
            Assert.Equal(TextTrimming.CharacterEllipsis, header.Block.TextTrimming);
            Assert.Equal(TextWrapping.NoWrap, header.Block.TextWrapping);

            var addButton = tree.GetVisualDescendants()
                .OfType<Button>()
                .Where(b => (b.GetValue(ToolTip.TipProperty) as string)?.Contains("新增") == true)
                .OrderBy(b => (b.TranslatePoint(new Point(0, 0), view) ?? default).Y)
                .First();

            var headerOrigin = header.Block.TranslatePoint(new Point(0, 0), view) ?? default;
            var buttonOrigin = addButton.TranslatePoint(new Point(0, 0), view) ?? default;
            var headerCenterY = headerOrigin.Y + header.Block.Bounds.Height / 2;
            var buttonCenterY = buttonOrigin.Y + addButton.Bounds.Height / 2;
            Assert.True(
                Math.Abs(headerCenterY - buttonCenterY) <= 1.0,
                $"文字行中心 Y={headerCenterY:F1}，动作按钮中心 Y={buttonCenterY:F1}，未垂直居中"
            );

            // 文字不得越过右侧动作区（旧布局 Meta 会压到按钮边缘）
            Assert.True(
                headerOrigin.X + header.Block.Bounds.Width <= buttonOrigin.X + 1.0,
                $"文字右缘 {headerOrigin.X + header.Block.Bounds.Width:F1} 越过按钮左缘 {buttonOrigin.X:F1}"
            );
        });
    }
}
