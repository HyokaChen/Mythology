using System.Text.Json;
using Core.Data;
using Core.Tools;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>
/// 章节正文读取入口：按 ID 或 novel-id + 章节号返回正文，避免模型用通用 read
/// 按文件路径猜章节、扫描工作区。
/// </summary>
public sealed class ReadChapterToolTests
{
    private static FakeNovelStore CreateStore()
    {
        var store = new FakeNovelStore();
        store.SaveNovel(new Novel { Id = "n1", Title = "测试小说" });
        store.SaveChapter(
            new Chapter
            {
                Id = "c1",
                NovelId = "n1",
                Title = "序章",
                Order = 1,
                Content = "序章内容。",
            }
        );
        store.SaveChapter(
            new Chapter
            {
                Id = "c2",
                NovelId = "n1",
                Title = "第一章",
                Order = 2,
                Content = "第一章内容。",
            }
        );
        return store;
    }

    [Fact]
    public async Task ExecuteAsync_ById_ReturnsChapterContent()
    {
        var tool = new ReadChapterTool(CreateStore());
        var args = JsonDocument.Parse("""{"chapter-id":"c1"}""").RootElement;

        var result = await tool.ExecuteAsync("call1", args, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("序章内容。", ToolDispatcher.ExtractText(result.Content));
    }

    [Fact]
    public async Task ExecuteAsync_ByOrder_ReturnsChapterContent()
    {
        var tool = new ReadChapterTool(CreateStore());
        var args = JsonDocument.Parse("""{"novel-id":"n1","chapter-id":"2"}""").RootElement;

        var result = await tool.ExecuteAsync("call2", args, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("第一章内容。", ToolDispatcher.ExtractText(result.Content));
    }

    [Fact]
    public async Task ExecuteAsync_ByOrderWithoutNovelId_ReturnsActionableError()
    {
        var tool = new ReadChapterTool(CreateStore());
        var args = JsonDocument.Parse("""{"chapter-id":"2"}""").RootElement;

        var result = await tool.ExecuteAsync("call3", args, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("novel-id", ToolDispatcher.ExtractText(result.Content));
    }

    [Fact]
    public void Metadata_UsesReadableNameAndAliases()
    {
        var tool = new ReadChapterTool(CreateStore());

        Assert.Equal("read_chapter", tool.Name);
        Assert.Equal("读取章节", tool.Label);
        Assert.Contains("manage_chapter", tool.Aliases);
        Assert.Contains("读取指定章节正文", tool.Description);
    }
}
