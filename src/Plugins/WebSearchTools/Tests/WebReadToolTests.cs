using Plugins.WebSearchTools;
using Xunit;

namespace WebSearchTools.Tests;

public class WebReadToolTests
{
    [Fact]
    public async Task ExecuteAsync_ReturnsFetchedContent()
    {
        var provider = new MockSearchProvider(fetchResult: "Mock article content here");
        var tool = new WebReadTool(provider);

        var result = await tool.ExecuteAsync(
            "call_1",
            new WebReadParams { Url = "http://example.com/article" },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("Mock article content here", text);
        Assert.Equal("http://example.com/article", provider.LastUrl);
    }

    [Fact]
    public async Task ExecuteAsync_ErrorFetching_ReturnsErrorMessage()
    {
        var provider = new MockSearchProvider(fetchResult: "获取失败: timeout");
        var tool = new WebReadTool(provider);

        var result = await tool.ExecuteAsync(
            "call_1",
            new WebReadParams { Url = "http://example.com/bad" },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("获取失败", text);
    }

    private static string GetText(Core.Tools.AgentToolResult? result) =>
        result?.Content?.OfType<Core.Shared.TextContent>().FirstOrDefault()?.Text ?? "";
}
