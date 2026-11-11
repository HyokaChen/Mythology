using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Core.Data;
using Desktop.Controls;
using Lucide.Avalonia;
using Xunit;

namespace Desktop.Tests;

/// <summary>
/// 章节内容窗：默认直接可编辑（无阅读态），保存按钮随改动启用，
/// 「未保存」标记与保存落盘行为由真实 FileNovelStore + 无头窗口验证。
/// </summary>
public sealed class ChapterReaderWindowUiTests : IDisposable
{
    private const string NovelId = "n1";
    private const string ChapterId = "ch1";
    private const string Chapter2Id = "ch2";
    private const string Original = "原始正文第一段。\n原始正文第二段，用于验证默认编辑与保存。";
    private const string SecondChapter = "第二章正文，用于验证翻页。";
    private readonly string _root;
    private readonly string _myth;
    private readonly FileNovelStore _store;

    public ChapterReaderWindowUiTests()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"desktop-reader-ui-{Guid.NewGuid():N}");
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

    private async Task SeedAsync()
    {
        await _store.SaveNovel(new Novel { Id = NovelId, Title = "预览测试" });
        await _store.SaveVolume(
            new Volume
            {
                Id = "v1",
                NovelId = NovelId,
                Name = "第一卷",
                Order = 1,
            }
        );
        await _store.SaveChapter(
            new Chapter
            {
                Id = ChapterId,
                NovelId = NovelId,
                Title = "第一章",
                Order = 1,
                VolumeId = "v1",
                Status = ChapterStatus.Draft,
                Content = Original,
                Summary = "陈折花被皮皮龙叫醒，开始兵荒马乱的一天。",
                PlotlineIds = ["p1"],
                CharacterIds = ["c1"],
            }
        );
        await _store.SaveChapter(
            new Chapter
            {
                Id = Chapter2Id,
                NovelId = NovelId,
                Title = "第二章",
                Order = 2,
                VolumeId = "v1",
                Status = ChapterStatus.Draft,
                Content = SecondChapter,
            }
        );
    }

    private static void PumpUntil(Func<bool> condition, string what)
    {
        for (var i = 0; i < 300; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (condition())
                return;
            Thread.Sleep(10);
        }
        Assert.Fail($"等待超时：{what}");
    }

    [Fact]
    public async Task EditorIsAlwaysEditable_SaveTracksDirtyState_AndPersists()
    {
        await SeedAsync();

        HeadlessUiHost.Instance.Run(() =>
        {
            var window = new ChapterReaderWindow(_store, NovelId, ChapterId);
            window.Show();
            try
            {
                var editor = window.FindControl<TextBox>("ContentEditor")!;
                var save = window.FindControl<Button>("SaveButton")!;
                var close = window.FindControl<Button>("CloseButton")!;
                var dirty = window.FindControl<TextBlock>("DirtyTag")!;

                PumpUntil(() => editor.Text == Original, "章节加载");

                // 默认就是编辑态：正文框可见，无改动时保存禁用
                Assert.True(editor.IsVisible);
                Assert.True(editor.AcceptsReturn);
                Assert.False(save.IsEnabled);
                Assert.False(dirty.IsVisible);
                Assert.Equal("取消", close.Content);

                // 一改就出现未保存标记，保存按钮可用
                editor.Text = "修改后的正文";
                PumpUntil(() => save.IsEnabled, "脏状态");
                Assert.True(save.IsEnabled);
                Assert.True(dirty.IsVisible);

                // 保存后标记消失、按钮禁用，正文留在当前章
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => !save.IsEnabled, "保存完成");
                Assert.False(dirty.IsVisible);
                Assert.Equal("修改后的正文", editor.Text);
                Assert.True(editor.IsVisible);
            }
            finally
            {
                window.Close();
            }
        });

        var saved = await _store.GetChapter(NovelId, ChapterId);
        Assert.NotNull(saved);
        Assert.Equal("修改后的正文", saved.Content);
    }

    [Fact]
    public async Task UnsavedContent_NextChapterPrompt_SaveThenSwitch()
    {
        await SeedAsync();

        HeadlessUiHost.Instance.Run(() =>
        {
            var window = new ChapterReaderWindow(_store, NovelId, ChapterId);
            window.Show();
            try
            {
                var editor = window.FindControl<TextBox>("ContentEditor")!;
                var next = window.FindControl<Button>("NextButton")!;
                var save = window.FindControl<Button>("SaveButton")!;

                PumpUntil(() => editor.Text == Original, "章节加载");
                editor.Text = "第一章修改";
                PumpUntil(() => save.IsEnabled, "脏状态");

                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => window.OwnedWindows.Count > 0, "未保存提示");
                var prompt = Assert.IsType<SaveChangesWindow>(window.OwnedWindows[0]);
                prompt
                    .FindControl<Button>("SaveButton")!
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                PumpUntil(() => editor.Text == SecondChapter, "保存并切换");
                Assert.Contains(
                    "第二章",
                    window.FindControl<TextBlock>("TitleBlock")!.Text,
                    StringComparison.Ordinal
                );
            }
            finally
            {
                window.Close();
            }
        });

        var first = await _store.GetChapter(NovelId, ChapterId);
        Assert.NotNull(first);
        Assert.Equal("第一章修改", first.Content);
    }

    [Fact]
    public async Task UnsavedContent_NextChapterPrompt_CancelStaysOnCurrentChapter()
    {
        await SeedAsync();

        HeadlessUiHost.Instance.Run(() =>
        {
            var window = new ChapterReaderWindow(_store, NovelId, ChapterId);
            window.Show();
            try
            {
                var editor = window.FindControl<TextBox>("ContentEditor")!;
                var next = window.FindControl<Button>("NextButton")!;
                var save = window.FindControl<Button>("SaveButton")!;

                PumpUntil(() => editor.Text == Original, "章节加载");
                editor.Text = "不应保存的修改";
                PumpUntil(() => save.IsEnabled, "脏状态");

                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => window.OwnedWindows.Count > 0, "未保存提示");
                var prompt = Assert.IsType<SaveChangesWindow>(window.OwnedWindows[0]);
                prompt
                    .FindControl<Button>("CancelButton")!
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                PumpUntil(() => window.OwnedWindows.Count == 0, "提示关闭");
                Assert.Equal("不应保存的修改", editor.Text);
                Assert.Contains(
                    "第一章",
                    window.FindControl<TextBlock>("TitleBlock")!.Text,
                    StringComparison.Ordinal
                );

                // 清除脏状态后关闭，避免关闭提示介入测试清理。
                editor.Text = Original;
                PumpUntil(() => !save.IsEnabled, "清除脏状态");
            }
            finally
            {
                window.Close();
            }
        });

        var first = await _store.GetChapter(NovelId, ChapterId);
        Assert.NotNull(first);
        Assert.Equal(Original, first.Content);
    }

    [Fact]
    public async Task UnsavedContent_NextChapterPrompt_DiscardSwitchesWithoutSaving()
    {
        await SeedAsync();

        HeadlessUiHost.Instance.Run(() =>
        {
            var window = new ChapterReaderWindow(_store, NovelId, ChapterId);
            window.Show();
            try
            {
                var editor = window.FindControl<TextBox>("ContentEditor")!;
                var next = window.FindControl<Button>("NextButton")!;
                var save = window.FindControl<Button>("SaveButton")!;

                PumpUntil(() => editor.Text == Original, "章节加载");
                editor.Text = "丢弃的修改";
                PumpUntil(() => save.IsEnabled, "脏状态");

                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => window.OwnedWindows.Count > 0, "未保存提示");
                var prompt = Assert.IsType<SaveChangesWindow>(window.OwnedWindows[0]);
                prompt
                    .FindControl<Button>("DiscardButton")!
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                PumpUntil(() => editor.Text == SecondChapter, "不保存切换");
                Assert.Contains(
                    "第二章",
                    window.FindControl<TextBlock>("TitleBlock")!.Text,
                    StringComparison.Ordinal
                );
            }
            finally
            {
                window.Close();
            }
        });

        var first = await _store.GetChapter(NovelId, ChapterId);
        Assert.NotNull(first);
        Assert.Equal(Original, first.Content);
    }

    [Fact]
    public async Task OutlineCard_IsCollapsedByDefault_AndTogglesOnClick()
    {
        await SeedAsync();

        HeadlessUiHost.Instance.Run(() =>
        {
            var window = new ChapterReaderWindow(_store, NovelId, ChapterId);
            window.Show();
            try
            {
                var panel = window.FindControl<Border>("OutlinePanel")!;
                var toggle = window.FindControl<Button>("OutlineToggle")!;
                var details = window.FindControl<ScrollViewer>("OutlineDetails")!;
                var chevron = window.FindControl<LucideIcon>("OutlineChevron")!;
                var meta = window.FindControl<TextBlock>("OutlineMeta")!;
                var teaser = window.FindControl<TextBlock>("OutlineTeaser")!;

                PumpUntil(() => panel.IsVisible, "细纲卡加载");
                Assert.False(details.IsVisible);
                Assert.True(teaser.IsVisible);
                Assert.Contains("计划 2", meta.Text, StringComparison.Ordinal);
                Assert.Equal(LucideIconKind.ChevronRight, chevron.Kind);

                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(details.IsVisible);
                Assert.False(teaser.IsVisible);
                Assert.Equal(LucideIconKind.ChevronDown, chevron.Kind);

                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(details.IsVisible);
                Assert.True(teaser.IsVisible);
                Assert.Equal(LucideIconKind.ChevronRight, chevron.Kind);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SaveChangesWindow_ThreeButtonsMapToChoices()
    {
        HeadlessUiHost.Instance.Run(() =>
        {
            foreach (
                var (buttonName, expected) in new[]
                {
                    ("SaveButton", SaveChangesChoice.Save),
                    ("DiscardButton", SaveChangesChoice.Discard),
                    ("CancelButton", SaveChangesChoice.Cancel),
                }
            )
            {
                var prompt = new SaveChangesWindow(
                    "「第一章」有未保存的修改",
                    "切换章节前是否保存？",
                    "保存并切换"
                );
                prompt.Show();
                var button = prompt.FindControl<Button>(buttonName)!;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(expected, prompt.Choice);
            }
        });
    }
}
