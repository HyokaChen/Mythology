using Plugins.WebSearchTools;
using Xunit;

namespace WebSearchTools.Tests;

public class BatchWebSearchToolTests
{
    [Fact]
    public async Task ExecuteAsync_EmptyQueries_ReturnsError()
    {
        var provider = new MockSearchProvider();
        var tool = new BatchWebSearchTool(provider);

        var result = await tool.ExecuteAsync(
            "call_1",
            new BatchWebSearchParams(),
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ExecuteAsync_MultipleQueries_ReturnsAll()
    {
        var provider = new MockSearchProvider([
            new WebSearchResult
            {
                Title = "Result",
                Url = "http://example.com",
                Snippet = "...",
            },
        ]);
        var tool = new BatchWebSearchTool(provider);

        var result = await tool.ExecuteAsync(
            "call_1",
            new BatchWebSearchParams { Queries = ["query1", "query2"] },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("查询 1", text);
        Assert.Contains("查询 2", text);
        Assert.Equal(2, provider.SearchCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WhitespaceOnly_Skipped()
    {
        var provider = new MockSearchProvider();
        var tool = new BatchWebSearchTool(provider);

        var result = await tool.ExecuteAsync(
            "call_1",
            new BatchWebSearchParams { Queries = ["", "   "] },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        Assert.Contains("所有查询均为空", GetText(result));
    }

    private static string GetText(Core.Tools.AgentToolResult? result) =>
        result?.Content?.OfType<Core.Shared.TextContent>().FirstOrDefault()?.Text ?? "";
}
