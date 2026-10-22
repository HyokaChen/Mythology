using Core.Vector;
using Plugins.SemanticRetrievalTool;
using Xunit;

namespace SemanticRetrievalTool.Tests;

public class BatchSearchMemoryToolTests : SemanticRetrievalToolTestBase
{
    private readonly BatchSearchMemoryTool _tool;

    public BatchSearchMemoryToolTests()
    {
        _tool = new BatchSearchMemoryTool(Kernel);
    }

    [Fact]
    public async Task ExecuteAsync_NoQueries_ReturnsError()
    {
        var result = await _tool.ExecuteAsync(
            "call_1",
            new BatchSearchMemoryParams(),
            TestContext.Current.CancellationToken
        );
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyQueries_ReturnsError()
    {
        var result = await _tool.ExecuteAsync(
            "call_1",
            new BatchSearchMemoryParams { Queries = [] },
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ExecuteAsync_MultipleQueries_ReturnsAllResults()
    {
        // 索引文档到检索存储
        await IndexDocumentAsync("ch1", DocumentKind.CharacterProfile, "hero", "novel1");

        var result = await _tool.ExecuteAsync(
            "call_1",
            new BatchSearchMemoryParams { NovelId = "novel1", Queries = ["hero", "villain"] },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("查询 1", text);
        Assert.Contains("查询 2", text);
    }

    [Fact]
    public async Task ExecuteAsync_WhitespaceQuery_IsSkipped()
    {
        var result = await _tool.ExecuteAsync(
            "call_1",
            new BatchSearchMemoryParams { NovelId = "novel1", Queries = ["", "   "] },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        Assert.Contains("所有查询均为空", GetText(result));
    }

    private static string GetText(Core.Tools.AgentToolResult result) =>
        result.Content?.OfType<Core.Shared.TextContent>().FirstOrDefault()?.Text ?? "";
}
