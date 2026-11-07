using System.Text.Json;
using Core.Data;
using Core.Shared;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>
/// create_outline 参数校验：空 chapter-titles 必须是可行动的 tool error，
/// 不能返回「已创建 0 个」造成调用方误以为成功（历史 bug：arguments="" 时静默成功）。
/// </summary>
public sealed class CreateOutlineToolTests
{
    [Fact]
    public async Task EmptyChapterTitles_ReturnsError()
    {
        var store = new FakeNovelStore();
        var tool = new CreateOutlineTool(store);

        var args = JsonDocument.Parse("""{"chapter-titles":[]}""").RootElement;
        var result = await tool.ExecuteAsync("call1", args, CancellationToken.None);

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContent>(Assert.Single(result.Content));
        Assert.Contains("chapter-titles", text.Text);
    }

    [Fact]
    public async Task BlankChapterTitles_ReturnsError()
    {
        var store = new FakeNovelStore();
        var tool = new CreateOutlineTool(store);

        var args = JsonDocument.Parse("""{"chapter-titles":["  ",""]}""").RootElement;
        var result = await tool.ExecuteAsync("call1", args, CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ValidChapterTitles_CreatesChapters()
    {
        var store = new FakeNovelStore();
        await store.SaveNovel(new Novel { Id = "n1", Title = "书" });
        var tool = new CreateOutlineTool(store);

        var args = JsonDocument
            .Parse("""{"novel-id":"n1","chapter-titles":["第一章","第二章"]}""")
            .RootElement;
        var result = await tool.ExecuteAsync("call1", args, CancellationToken.None);

        Assert.False(result.IsError);
        var chapters = await store.GetChapters("n1", TestContext.Current.CancellationToken);
        Assert.Equal(2, chapters.Count);
        Assert.Equal(["第一章", "第二章"], chapters.OrderBy(c => c.Order).Select(c => c.Title));
    }
}
