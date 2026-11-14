using Core.Data;
using Core.Shared;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>
/// rewrite_selection 也必须把独立分隔符挡在正文之外；
/// 否则模型可以绕过 write_chapter 的整章守卫，逐段把分隔线塞回来。
/// </summary>
public sealed class RewriteSelectionToolTests
{
    [Fact]
    public async Task Rewrite_WithStandaloneSeparator_StripsItBeforeSave()
    {
        var store = new FakeNovelStore();
        await store.SaveNovel(new Novel { Id = "n1", Title = "测试小说" });
        await store.SaveChapter(
            new Chapter
            {
                Id = "ch1",
                NovelId = "n1",
                Title = "第一章",
                Order = 1,
                Content = "旧段落",
            }
        );
        var tool = new RewriteSelectionTool(store);

        var result = await tool.ExecuteAsync(
            "call",
            new RewriteSelectionParams
            {
                ChapterId = "ch1",
                NovelId = "n1",
                OldText = "旧段落",
                NewText = "新段落\n\n---\n\n下一段",
            },
            CancellationToken.None
        );

        Assert.False(result.IsError);
        Assert.Equal("新段落\n\n下一段", (await store.GetChapter("ch1"))!.Content);
        var text = string.Join("", result.Content.OfType<TextContent>().Select(c => c.Text));
        Assert.Contains("已清理 1 处独立分隔符", text);
    }
}
