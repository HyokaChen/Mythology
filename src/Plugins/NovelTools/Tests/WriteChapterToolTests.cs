using System.Text.Json;
using Core.Data;
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
}
