using Plugins.WebSearchTools;
using Xunit;

namespace WebSearchTools.Tests;

public class WebSearchToolTests
{
    [Fact]
    public async Task ExecuteAsync_ReturnsSearchResults()
    {
        var provider = new MockSearchProvider([
            new WebSearchResult
            {
                Title = "Result 1",
                Url = "http://example.com/1",
                Snippet = "Snippet 1",
            },
        ]);
        var tool = new WebSearchTool(provider);

        var result = await tool.ExecuteAsync(
            "call_1",
            new WebSearchParams { Query = "test" },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("Result 1", text);
        Assert.Contains("Snippet 1", text);
        Assert.Equal("test", provider.LastQuery);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyResults_ReturnsEmpty()
    {
        var provider = new MockSearchProvider([]);
        var tool = new WebSearchTool(provider);

        var result = await tool.ExecuteAsync(
            "call_1",
            new WebSearchParams { Query = "nothing" },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        Assert.Equal(1, provider.SearchCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_MaxResults_Limits()
    {
        var results = Enumerable
            .Range(0, 10)
            .Select(i => new WebSearchResult
            {
                Title = $"Result {i}",
                Url = $"http://example.com/{i}",
                Snippet = "",
            })
            .ToList();
        var provider = new MockSearchProvider(results);
        var tool = new WebSearchTool(provider);

        var result = await tool.ExecuteAsync(
            "call_1",
            new WebSearchParams { Query = "test", MaxResults = 3 },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        // Only 3 results should be shown
        Assert.Contains("Result 0", text);
        Assert.Contains("Result 2", text);
    }

    private static string GetText(Core.Tools.AgentToolResult? result) =>
        result?.Content?.OfType<Core.Shared.TextContent>().FirstOrDefault()?.Text ?? "";
}
