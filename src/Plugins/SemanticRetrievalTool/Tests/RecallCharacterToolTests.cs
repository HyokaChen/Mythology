using Core.Data;
using Core.Vector;
using Plugins.SemanticRetrievalTool;
using Xunit;

namespace SemanticRetrievalTool.Tests;

public class RecallCharacterToolTests : SemanticRetrievalToolTestBase
{
    private readonly FakeNovelStore _novelStore = new();
    private readonly RecallCharacterTool _tool;

    public RecallCharacterToolTests()
    {
        _tool = new RecallCharacterTool(Kernel, new PluginStore(_novelStore));
    }

    [Fact]
    public async Task ExecuteAsync_NoResults_ReturnsNotFound()
    {
        var result = await _tool.ExecuteAsync(
            "call_1",
            new RecallCharacterParams { NovelId = "novel1", Query = "nonexistent character" },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("未找到", text);
    }

    [Fact]
    public async Task ExecuteAsync_WithMatchingCharacter_ReturnsDetails()
    {
        var ch = new Character
        {
            Id = "ch1",
            NovelId = "novel1",
            Name = "Alice",
            Personality = "brave",
            Appearance = "tall",
        };
        _novelStore.AddCharacter(ch);

        // 索引角色文档到检索存储（文本不含角色名，确保走语义回退路径）
        await IndexDocumentAsync(
            "ch1",
            DocumentKind.CharacterProfile,
            "a brave tall heroine",
            "novel1",
            "ch1"
        );

        var result = await _tool.ExecuteAsync(
            "call_1",
            new RecallCharacterParams { NovelId = "novel1", Query = "a brave tall heroine" },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("Alice", text);
        Assert.Contains("brave", text);
        Assert.Contains("tall", text);
    }

    [Fact]
    public async Task ExecuteAsync_NameExactMatch_ReturnsCharacterWithoutSemanticSearch()
    {
        var ch = new Character
        {
            Id = "ch1",
            NovelId = "novel1",
            Name = "陈折花",
            Personality = "温柔但优柔寡断",
            Appearance = "清瘦",
        };
        _novelStore.AddCharacter(ch);

        // 不索引任何文档——确定性按名查找不应依赖语义搜索
        var result = await _tool.ExecuteAsync(
            "call_1",
            new RecallCharacterParams { NovelId = "novel1", Name = "陈折花" },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("陈折花", text);
        Assert.Contains("温柔", text);
        Assert.Contains("清瘦", text);
    }

    [Fact]
    public async Task ExecuteAsync_QueryParameter_FindsCharacterBySubstring()
    {
        var ch = new Character
        {
            Id = "ch1",
            NovelId = "novel1",
            Name = "橘夏海",
            Personality = "傲娇",
            Appearance = "高马尾",
        };
        _novelStore.AddCharacter(ch);

        // LLM 实际调用用 query 字段传角色名
        var result = await _tool.ExecuteAsync(
            "call_1",
            new RecallCharacterParams { NovelId = "novel1", Query = "夏海" },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("橘夏海", text);
        Assert.Contains("傲娇", text);
    }

    private static string GetText(Core.Tools.AgentToolResult result) =>
        result.Content?.OfType<Core.Shared.TextContent>().FirstOrDefault()?.Text ?? "";
}
