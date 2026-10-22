using Core.Vector;
using Plugins.SemanticRetrievalTool;
using Xunit;

namespace SemanticRetrievalTool.Tests;

public class RecallPlotToolTests : SemanticRetrievalToolTestBase
{
    private readonly RecallPlotTool _tool;

    public RecallPlotToolTests()
    {
        _tool = new RecallPlotTool(Kernel);
    }

    [Fact]
    public async Task ExecuteAsync_NoResults_ReturnsNotFound()
    {
        var result = await _tool.ExecuteAsync(
            "call_1",
            new RecallPlotParams { NovelId = "novel1", Description = "unknown plot" },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("未找到", text);
    }

    [Fact]
    public async Task ExecuteAsync_WithResults_ReturnsFormatted()
    {
        // 索引情节线文档到检索存储
        await IndexDocumentAsync(
            "p1",
            DocumentKind.PlotlineProfile,
            "a mysterious event",
            "novel1"
        );

        var result = await _tool.ExecuteAsync(
            "call_1",
            new RecallPlotParams { NovelId = "novel1", Description = "a mysterious event" },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("a mysterious event", text);
    }

    private static string GetText(Core.Tools.AgentToolResult result) =>
        result.Content?.OfType<Core.Shared.TextContent>().FirstOrDefault()?.Text ?? "";
}
