using Plugins.WebSearchTools;
using Xunit;

namespace WebSearchTools.Tests;

public class RecommendSimilarStyleToolTests
{
    [Fact]
    public async Task ExecuteAsync_SearchesWithStyleDescription()
    {
        var provider = new MockSearchProvider([
            new WebSearchResult
            {
                Title = "Recommendation",
                Url = "",
                Snippet = "You might like...",
            },
        ]);
        var tool = new RecommendSimilarStyleTool(provider);

        var result = await tool.ExecuteAsync(
            "call_1",
            new RecommendSimilarStyleParams { StyleDescription = "cyberpunk" },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        Assert.Contains("cyberpunk", provider.LastQuery);
        Assert.Contains("类似风格", provider.LastQuery);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyResults_ReturnsEmpty()
    {
        var provider = new MockSearchProvider([]);
        var tool = new RecommendSimilarStyleTool(provider);

        var result = await tool.ExecuteAsync(
            "call_1",
            new RecommendSimilarStyleParams { StyleDescription = "nonexistent style" },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
    }

    private static string GetText(Core.Tools.AgentToolResult? result) =>
        result?.Content?.OfType<Core.Shared.TextContent>().FirstOrDefault()?.Text ?? "";
}
