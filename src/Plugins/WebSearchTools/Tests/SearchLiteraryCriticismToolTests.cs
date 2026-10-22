using Plugins.WebSearchTools;
using Xunit;

namespace WebSearchTools.Tests;

public class SearchLiteraryCriticismToolTests
{
    [Fact]
    public async Task ExecuteAsync_SearchesWithWorkName()
    {
        var provider = new MockSearchProvider([
            new WebSearchResult
            {
                Title = "Lit Analysis",
                Url = "",
                Snippet = "Great analysis of the work",
            },
        ]);
        var tool = new SearchLiteraryCriticismTool(provider);

        var result = await tool.ExecuteAsync(
            "call_1",
            new SearchLiteraryCriticismParams { WorkName = "三体" },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        Assert.Contains("三体", provider.LastQuery);
        Assert.Contains("文学评论", provider.LastQuery);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyResults_ReturnsEmpty()
    {
        var provider = new MockSearchProvider([]);
        var tool = new SearchLiteraryCriticismTool(provider);

        var result = await tool.ExecuteAsync(
            "call_1",
            new SearchLiteraryCriticismParams { WorkName = "unknown" },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
    }

    private static string GetText(Core.Tools.AgentToolResult? result) =>
        result?.Content?.OfType<Core.Shared.TextContent>().FirstOrDefault()?.Text ?? "";
}
