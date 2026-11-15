using Core.Data;
using Core.Prose;
using Core.Shared;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>
/// edit_text 不能把空操作/未知操作/找不到旧文本当成成功；
/// 失败必须保留原正文，避免模型以为已经改完。
/// </summary>
public sealed class EditTextToolTests
{
    private const string NovelId = "n1";
    private const string ChapterId = "ch1";

    private static async Task<(FakeNovelStore Store, EditTextTool Tool)> SeedAsync()
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
                Content = "旧到的文本",
            }
        );
        return (store, new EditTextTool(store));
    }

    private static EditTextParams Args(
        string operation,
        string? oldText = null,
        string? newText = null,
        int? position = null
    ) =>
        new()
        {
            ChapterId = ChapterId,
            Operation = operation,
            OldText = oldText,
            NewText = newText,
            Position = position,
        };

    private static async Task AssertUnchangedAsync(FakeNovelStore store)
    {
        var chapter = await store.GetChapter(ChapterId);
        Assert.Equal("旧到的文本", chapter!.Content);
    }

    [Fact]
    public async Task UnknownOperation_IsRejected()
    {
        var (store, tool) = await SeedAsync();
        var result = await tool.ExecuteAsync("call", Args("rename"), CancellationToken.None);

        Assert.True(result.IsError);
        await AssertUnchangedAsync(store);
    }

    [Fact]
    public async Task ReplaceWithoutOldText_IsRejected()
    {
        var (store, tool) = await SeedAsync();
        var result = await tool.ExecuteAsync("call", Args("replace"), CancellationToken.None);

        Assert.True(result.IsError);
        await AssertUnchangedAsync(store);
    }

    [Fact]
    public async Task ReplaceMiss_IsRejected()
    {
        var (store, tool) = await SeedAsync();
        var result = await tool.ExecuteAsync(
            "call",
            Args("replace", oldText: "不存在的文本", newText: "新文本"),
            CancellationToken.None
        );

        Assert.True(result.IsError);
        await AssertUnchangedAsync(store);
    }

    [Fact]
    public async Task InsertWithoutNewText_IsRejected()
    {
        var (store, tool) = await SeedAsync();
        var result = await tool.ExecuteAsync("call", Args("insert"), CancellationToken.None);

        Assert.True(result.IsError);
        await AssertUnchangedAsync(store);
    }

    [Fact]
    public async Task ValidReplace_UpdatesChapter()
    {
        var (store, tool) = await SeedAsync();
        var result = await tool.ExecuteAsync(
            "call",
            Args("replace", oldText: "旧到的", newText: "新的"),
            CancellationToken.None
        );

        Assert.False(result.IsError);
        Assert.Equal("新的文本", (await store.GetChapter(ChapterId))!.Content);
    }

    [Fact]
    public async Task ValidInsert_AppendsText()
    {
        var (store, tool) = await SeedAsync();
        var result = await tool.ExecuteAsync(
            "call",
            Args("insert", newText: "！", position: 999),
            CancellationToken.None
        );

        Assert.False(result.IsError);
        Assert.Equal("旧到的文本！", (await store.GetChapter(ChapterId))!.Content);
    }

    [Fact]
    public async Task Replace_WithAiHarnessKeepingSeparator_SavesIt()
    {
        var (store, _) = await SeedAsync();
        var replacement = $"{new string('甲', 500)}\n\n---\n\n{new string('乙', 500)}";
        var expected = replacement + "文本";
        var harness = FakeProseHarness.Returning(expected);
        var tool = new EditTextTool(store, proseHarness: harness);

        var result = await tool.ExecuteAsync(
            "call",
            Args("replace", oldText: "旧到的", newText: replacement),
            CancellationToken.None
        );

        Assert.False(result.IsError);
        var content = (await store.GetChapter(ChapterId))!.Content;
        Assert.Equal(expected, content);
        Assert.Equal(1, ProseHarnessValidator.CountSeparatorLines(content));
        Assert.Equal(1, harness.Calls);
        var text = string.Join("", result.Content.OfType<TextContent>().Select(c => c.Text));
        Assert.Contains("AI 分隔符 harness", text);
    }

    [Fact]
    public async Task Replace_WithAiHarnessRemovingSeparator_SavesWithoutIt()
    {
        var (store, _) = await SeedAsync();
        var replacement = $"{new string('甲', 500)}\n\n---\n\n{new string('乙', 500)}";
        var candidate = replacement + "文本";
        var expected = ProseTestContent.WithoutSeparators(candidate);
        var harness = FakeProseHarness.Returning(expected);
        var tool = new EditTextTool(store, proseHarness: harness);

        var result = await tool.ExecuteAsync(
            "call",
            Args("replace", oldText: "旧到的", newText: replacement),
            CancellationToken.None
        );

        Assert.False(result.IsError);
        var content = (await store.GetChapter(ChapterId))!.Content;
        Assert.Equal(expected, content);
        Assert.Equal(0, ProseHarnessValidator.CountSeparatorLines(content));
        var text = string.Join("", result.Content.OfType<TextContent>().Select(c => c.Text));
        Assert.Contains("原 1 处 → 现 0 处", text);
    }

    [Fact]
    public async Task Replace_WhenHarnessUnavailable_DoesNotApplyLocalCleanup()
    {
        var (store, _) = await SeedAsync();
        var replacement = ProseTestContent.WithSeparators(20);
        var expected = replacement + "文本";
        var tool = new EditTextTool(store, proseHarness: FakeProseHarness.Unavailable());

        var result = await tool.ExecuteAsync(
            "call",
            Args("replace", oldText: "旧到的", newText: replacement),
            CancellationToken.None
        );

        Assert.False(result.IsError);
        var content = (await store.GetChapter(ChapterId))!.Content;
        Assert.Equal(expected, content);
        Assert.Equal(20, ProseHarnessValidator.CountSeparatorLines(content));
        var text = string.Join("", result.Content.OfType<TextContent>().Select(c => c.Text));
        Assert.Contains("未运行", text);
    }
}
