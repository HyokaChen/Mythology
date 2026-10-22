using Core.Data;
using Plugins.SemanticRetrievalTool;
using Xunit;

namespace SemanticRetrievalTool.Tests;

public class FindConsistencyToolTests
{
    private readonly FakeNovelStore _novelStore = new();
    private readonly FindConsistencyTool _tool;

    public FindConsistencyToolTests()
    {
        _tool = new FindConsistencyTool(new PluginStore(_novelStore));
    }

    [Fact]
    public async Task ExecuteAsync_NoMatchingCharacters_ReturnsEmpty()
    {
        _novelStore.AddCharacter(
            new Character
            {
                Id = "ch1",
                NovelId = "novel1",
                Name = "Alice",
                Personality = "brave",
                Appearance = "tall",
            }
        );

        var result = await _tool.ExecuteAsync(
            "call_1",
            new FindConsistencyParams { NovelId = "novel1", Text = "Bob walked into the room." },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("未发现", text);
    }

    [Fact]
    public async Task ExecuteAsync_WithMatchingCharacter_ReturnsDetails()
    {
        _novelStore.AddCharacter(
            new Character
            {
                Id = "ch1",
                NovelId = "novel1",
                Name = "Alice",
                Personality = "brave",
                Appearance = "tall with blue eyes",
            }
        );

        var result = await _tool.ExecuteAsync(
            "call_1",
            new FindConsistencyParams
            {
                NovelId = "novel1",
                Text = "Alice walked into the room. She looked around.",
            },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("Alice", text);
        Assert.Contains("tall", text);
        Assert.Contains("brave", text);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyNovel_ReturnsEmpty()
    {
        var result = await _tool.ExecuteAsync(
            "call_1",
            new FindConsistencyParams { NovelId = "novel1", Text = "some text" },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("未发现", text);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyText_ReturnsEmpty()
    {
        var result = await _tool.ExecuteAsync(
            "call_1",
            new FindConsistencyParams { NovelId = "novel1", Text = "" },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
    }

    private static string GetText(Core.Tools.AgentToolResult result) =>
        result.Content?.OfType<Core.Shared.TextContent>().FirstOrDefault()?.Text ?? "";
}
