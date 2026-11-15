using Core.Data;
using Core.Prose;
using Core.Shared;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>
/// rewrite_selection 保存前由 AI 分隔符 harness 通读全章并自主决定保留/新增/删除；
/// 工具不做本地数量、密度或位置收敛。
/// </summary>
public sealed class RewriteSelectionToolTests
{
    private const string NovelId = "n1";
    private const string ChapterId = "ch1";

    private static async Task<FakeNovelStore> SeedAsync()
    {
        var store = new FakeNovelStore();
        await store.SaveNovel(new Novel { Id = NovelId, Title = "测试小说" });
        await store.SaveChapter(
            new Chapter
            {
                Id = ChapterId,
                NovelId = NovelId,
                Title = "第一章",
                Order = 1,
                Content = "旧段落",
            }
        );
        return store;
    }

    private static RewriteSelectionParams Args(string newText) =>
        new()
        {
            ChapterId = ChapterId,
            NovelId = NovelId,
            OldText = "旧段落",
            NewText = newText,
        };

    [Fact]
    public async Task Rewrite_WithAiHarnessKeepingManySeparators_DoesNotApplyDensityCap()
    {
        var store = await SeedAsync();
        var replacement = ProseTestContent.WithSeparators(20);
        var revised = replacement.Replace("---", "***", StringComparison.Ordinal);
        var harness = FakeProseHarness.Returning(revised);
        var tool = new RewriteSelectionTool(store, proseHarness: harness);

        var result = await tool.ExecuteAsync("call", Args(replacement), CancellationToken.None);

        Assert.False(result.IsError);
        var content = (await store.GetChapter(ChapterId))!.Content;
        Assert.Equal(revised, content);
        Assert.Equal(20, ProseHarnessValidator.CountSeparatorLines(content));
        var text = string.Join("", result.Content.OfType<TextContent>().Select(c => c.Text));
        Assert.Contains("AI 分隔符 harness", text);
        Assert.Contains("仍保留 20 处", text);
    }

    [Fact]
    public async Task Rewrite_WithAiHarnessRemovingSeparator_SavesWithoutIt()
    {
        var store = await SeedAsync();
        var replacement = $"{new string('甲', 500)}\n\n---\n\n{new string('乙', 500)}";
        var expected = ProseTestContent.WithoutSeparators(replacement);
        var harness = FakeProseHarness.Returning(expected);
        var tool = new RewriteSelectionTool(store, proseHarness: harness);

        var result = await tool.ExecuteAsync("call", Args(replacement), CancellationToken.None);

        Assert.False(result.IsError);
        var content = (await store.GetChapter(ChapterId))!.Content;
        Assert.Equal(expected, content);
        Assert.Equal(0, ProseHarnessValidator.CountSeparatorLines(content));
        var text = string.Join("", result.Content.OfType<TextContent>().Select(c => c.Text));
        Assert.Contains("原 1 处 → 现 0 处", text);
    }

    [Fact]
    public async Task Rewrite_WhenHarnessUnavailable_DoesNotApplyLocalCleanup()
    {
        var store = await SeedAsync();
        var replacement = ProseTestContent.WithSeparators(20);
        var tool = new RewriteSelectionTool(store, proseHarness: FakeProseHarness.Unavailable());

        var result = await tool.ExecuteAsync("call", Args(replacement), CancellationToken.None);

        Assert.False(result.IsError);
        var content = (await store.GetChapter(ChapterId))!.Content;
        Assert.Equal(replacement, content);
        Assert.Equal(20, ProseHarnessValidator.CountSeparatorLines(content));
        var text = string.Join("", result.Content.OfType<TextContent>().Select(c => c.Text));
        Assert.Contains("未运行", text);
    }
}
