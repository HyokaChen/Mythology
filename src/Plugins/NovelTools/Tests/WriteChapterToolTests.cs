using System.Text.Json;
using Core.Data;
using Core.Prose;
using Core.Shared;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>
/// write_chapter 的新建章节必须落到明确分卷；更新已有章节时保留原分卷，
/// 显式传 volume-id 才移动。避免新建章节静默进入「未分卷」。
/// </summary>
public sealed class WriteChapterToolTests
{
    private const string NovelId = "n1";

    private static async Task<FakeNovelStore> SeedAsync()
    {
        var store = new FakeNovelStore();
        await store.SaveNovel(new Novel { Id = NovelId, Title = "测试小说" });
        await store.SaveVolume(
            new Volume
            {
                Id = "v1",
                NovelId = NovelId,
                Name = "第一卷",
                Order = 1,
            }
        );
        await store.SaveVolume(
            new Volume
            {
                Id = "v2",
                NovelId = NovelId,
                Name = "第二卷",
                Order = 2,
            }
        );
        return store;
    }

    private static WriteChapterParams NewChapter(string volumeId = "") =>
        new()
        {
            NovelId = NovelId,
            Title = "第一章",
            Order = 1,
            Content = "正文",
            VolumeId = volumeId,
        };

    [Fact]
    public void Params_Schema_RequiresVolumeId()
    {
        var tool = new WriteChapterTool(new FakeNovelStore());
        using var schema = JsonDocument.Parse(tool.Parameters!.Value.GetRawText());
        var required = schema
            .RootElement.GetProperty("required")
            .EnumerateArray()
            .Select(x => x.GetString())
            .ToArray();

        Assert.Contains("volume-id", required);
    }

    [Fact]
    public async Task Create_WithoutVolume_IsRejectedWithChoices()
    {
        var store = await SeedAsync();
        var tool = new WriteChapterTool(store);

        var result = await tool.ExecuteAsync("call", NewChapter(), CancellationToken.None);

        Assert.True(result.IsError);
        var text = string.Join("", result.Content.OfType<TextContent>().Select(c => c.Text));
        Assert.Contains("volume-id", text);
        Assert.Contains("[ID:v1]", text);
        Assert.Empty(await store.GetChapters(NovelId));
    }

    [Fact]
    public async Task Create_WithVolume_AssignsVolume()
    {
        var store = await SeedAsync();
        var tool = new WriteChapterTool(store);

        var result = await tool.ExecuteAsync("call", NewChapter("v1"), CancellationToken.None);

        Assert.False(result.IsError);
        var chapter = await store.GetChapterByOrder(NovelId, 1);
        Assert.NotNull(chapter);
        Assert.Equal("v1", chapter.VolumeId);
    }

    [Fact]
    public async Task Update_WithoutVolume_PreservesExistingVolume()
    {
        var store = await SeedAsync();
        await store.SaveChapter(
            new Chapter
            {
                Id = "ch1",
                NovelId = NovelId,
                Title = "第一章",
                Order = 1,
                VolumeId = "v1",
                Content = "旧正文",
            }
        );
        var tool = new WriteChapterTool(store);

        var result = await tool.ExecuteAsync(
            "call",
            new WriteChapterParams
            {
                NovelId = NovelId,
                ChapterId = "ch1",
                Title = "第一章",
                Order = 1,
                Content = "新正文",
            },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        var chapter = await store.GetChapter("ch1");
        Assert.Equal("v1", chapter!.VolumeId);
        Assert.Equal("新正文", chapter.Content);
    }

    [Fact]
    public async Task Update_WithVolume_MovesChapter()
    {
        var store = await SeedAsync();
        await store.SaveChapter(
            new Chapter
            {
                Id = "ch1",
                NovelId = NovelId,
                Title = "第一章",
                Order = 1,
                VolumeId = "v1",
                Content = "旧正文",
            }
        );
        var tool = new WriteChapterTool(store);

        var result = await tool.ExecuteAsync(
            "call",
            new WriteChapterParams
            {
                NovelId = NovelId,
                ChapterId = "ch1",
                Title = "第一章",
                Order = 1,
                Content = "新正文",
                VolumeId = "v2",
            },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        Assert.Equal("v2", (await store.GetChapter("ch1"))!.VolumeId);
    }

    [Fact]
    public async Task Create_WithAiHarnessKeepingManySeparators_DoesNotApplyDensityCap()
    {
        var store = await SeedAsync();
        var content = ProseTestContent.WithSeparators(20);
        var revised = content.Replace("---", "***", StringComparison.Ordinal);
        var harness = FakeProseHarness.Returning(revised);
        var tool = new WriteChapterTool(store, proseHarness: harness);

        var result = await tool.ExecuteAsync(
            "call",
            new WriteChapterParams
            {
                NovelId = NovelId,
                Title = "第一章",
                Order = 1,
                VolumeId = "v1",
                Content = content,
            },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        var chapter = await store.GetChapterByOrder(NovelId, 1);
        Assert.NotNull(chapter);
        Assert.Equal(revised, chapter.Content);
        Assert.Equal(20, ProseHarnessValidator.CountSeparatorLines(chapter.Content));
        Assert.Equal(1, harness.Calls);
        var text = string.Join("", result.Content.OfType<TextContent>().Select(c => c.Text));
        Assert.Contains("AI 分隔符 harness", text);
        Assert.Contains("仍保留 20 处", text);
    }

    [Fact]
    public async Task Create_WithAiHarnessRemovingBeatSeparator_SavesHarnessResult()
    {
        var store = await SeedAsync();
        var original = $"{new string('甲', 500)}\n\n---\n\n{new string('乙', 500)}";
        var revised = ProseTestContent.WithoutSeparators(original);
        var harness = FakeProseHarness.Returning(revised);
        var tool = new WriteChapterTool(store, proseHarness: harness);

        var result = await tool.ExecuteAsync(
            "call",
            new WriteChapterParams
            {
                NovelId = NovelId,
                Title = "第一章",
                Order = 1,
                VolumeId = "v1",
                Content = original,
            },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        var chapter = await store.GetChapterByOrder(NovelId, 1);
        Assert.NotNull(chapter);
        Assert.Equal(revised, chapter.Content);
        Assert.Equal(0, ProseHarnessValidator.CountSeparatorLines(chapter.Content));
        var text = string.Join("", result.Content.OfType<TextContent>().Select(c => c.Text));
        Assert.Contains("原 1 处 → 现 0 处", text);
    }

    [Fact]
    public async Task Create_WhenHarnessUnavailable_DoesNotApplyLocalCleanup()
    {
        var store = await SeedAsync();
        var content = ProseTestContent.WithSeparators(20);
        var tool = new WriteChapterTool(store, proseHarness: FakeProseHarness.Unavailable());

        var result = await tool.ExecuteAsync(
            "call",
            new WriteChapterParams
            {
                NovelId = NovelId,
                Title = "第一章",
                Order = 1,
                VolumeId = "v1",
                Content = content,
            },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        var chapter = await store.GetChapterByOrder(NovelId, 1);
        Assert.NotNull(chapter);
        Assert.Equal(content, chapter.Content);
        Assert.Equal(20, ProseHarnessValidator.CountSeparatorLines(chapter.Content));
        var text = string.Join("", result.Content.OfType<TextContent>().Select(c => c.Text));
        Assert.Contains("未运行", text);
    }

    [Fact]
    public async Task Create_WhenHarnessFails_DoesNotApplyLocalCleanup()
    {
        var store = await SeedAsync();
        var content = ProseTestContent.WithSeparators(20);
        var tool = new WriteChapterTool(store, proseHarness: FakeProseHarness.Failed());

        var result = await tool.ExecuteAsync(
            "call",
            new WriteChapterParams
            {
                NovelId = NovelId,
                Title = "第一章",
                Order = 1,
                VolumeId = "v1",
                Content = content,
            },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        var chapter = await store.GetChapterByOrder(NovelId, 1);
        Assert.NotNull(chapter);
        Assert.Equal(content, chapter.Content);
        Assert.Equal(20, ProseHarnessValidator.CountSeparatorLines(chapter.Content));
        var text = string.Join("", result.Content.OfType<TextContent>().Select(c => c.Text));
        Assert.Contains("未完成", text);
    }

    [Fact]
    public async Task Create_PassesChapterContextToHarness()
    {
        var store = await SeedAsync();
        await store.SaveChapter(
            new Chapter
            {
                Id = "prev",
                NovelId = NovelId,
                Title = "序章",
                Order = 1,
                VolumeId = "v1",
                Content = "前章结尾",
                Summary = "前章细纲",
            }
        );
        await store.SaveChapter(
            new Chapter
            {
                Id = "next",
                NovelId = NovelId,
                Title = "第三章",
                Order = 3,
                VolumeId = "v1",
                Content = "后章开头",
                Summary = "后章细纲",
            }
        );
        var harness = FakeProseHarness.Returning("新章正文");
        var tool = new WriteChapterTool(store, proseHarness: harness);

        var result = await tool.ExecuteAsync(
            "call",
            new WriteChapterParams
            {
                NovelId = NovelId,
                Title = "第二章",
                Order = 2,
                VolumeId = "v1",
                Content = "新章正文",
            },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        var request = harness.LastRequest!;
        Assert.Equal("测试小说", request.NovelTitle);
        Assert.Equal("第二章", request.ChapterTitle);
        Assert.Equal(2, request.ChapterOrder);
        Assert.Equal("write", request.Operation);
        Assert.Equal("序章", request.PreviousChapterTitle);
        Assert.Contains("前章结尾", request.PreviousChapterExcerpt);
        Assert.Equal("第三章", request.NextChapterTitle);
        Assert.Contains("后章开头", request.NextChapterExcerpt);
    }
}
