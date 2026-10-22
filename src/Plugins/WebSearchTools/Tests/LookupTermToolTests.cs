using Plugins.WebSearchTools;
using Xunit;

namespace WebSearchTools.Tests;

public class LookupTermToolTests
{
    [Fact]
    public async Task ExecuteAsync_SearchesWithTerm()
    {
        var provider = new MockSearchProvider([
            new WebSearchResult
            {
                Title = "Definition",
                Url = "",
                Snippet = "The term means...",
            },
        ]);
        var tool = new LookupTermTool(provider);

        var result = await tool.ExecuteAsync(
            "call_1",
            new LookupTermParams { Term = "serendipity" },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        Assert.Contains("serendipity", provider.LastQuery);
        Assert.Contains("释义", provider.LastQuery);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyResults_ReturnsEmpty()
    {
        var provider = new MockSearchProvider([]);
        var tool = new LookupTermTool(provider);

        var result = await tool.ExecuteAsync(
            "call_1",
            new LookupTermParams { Term = "xyzzy" },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
    }

    private static string GetText(Core.Tools.AgentToolResult? result) =>
        result?.Content?.OfType<Core.Shared.TextContent>().FirstOrDefault()?.Text ?? "";
}
