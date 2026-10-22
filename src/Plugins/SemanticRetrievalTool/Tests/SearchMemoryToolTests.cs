using Core.Vector;
using Plugins.SemanticRetrievalTool;
using Xunit;

namespace SemanticRetrievalTool.Tests;

public class SearchMemoryToolTests : SemanticRetrievalToolTestBase
{
    private readonly SearchMemoryTool _tool;

    public SearchMemoryToolTests()
    {
        _tool = new SearchMemoryTool(Kernel);
    }

    [Fact]
    public async Task ExecuteAsync_NoResults_ReturnsNotFoundMessage()
    {
        var result = await _tool.ExecuteAsync(
            "call_1",
            new SearchMemoryParams { NovelId = "novel1", Query = "test query" },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("未找到", text);
    }

    [Fact]
    public async Task ExecuteAsync_WithResults_ReturnsFormattedResults()
    {
        // 索引角色文档到检索存储
        await IndexDocumentAsync("ch1", DocumentKind.CharacterProfile, "a brave hero", "novel1");

        var result = await _tool.ExecuteAsync(
            "call_1",
            new SearchMemoryParams { NovelId = "novel1", Query = "a brave hero" },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        Assert.Contains("a brave hero", text);
    }

    [Fact]
    public async Task ExecuteAsync_TypeFilter_FiltersByType()
    {
        // 索引相同文本的角色和地点文档
        // 用相同文本确保确定性嵌入匹配，TypeFilter 决定返回哪个
        await IndexDocumentAsync("ch1", DocumentKind.CharacterProfile, "adventure data", "novel1");
        await IndexDocumentAsync("loc1", DocumentKind.LocationProfile, "adventure data", "novel1");

        // 不带 TypeFilter：两个结果都可能出现
        var resultAll = await _tool.ExecuteAsync(
            "call_1",
            new SearchMemoryParams { NovelId = "novel1", Query = "adventure data" },
            TestContext.Current.CancellationToken
        );
        Assert.False(resultAll.IsError);

        // 带 TypeFilter=location：只返回地点文档
        var result = await _tool.ExecuteAsync(
            "call_1",
            new SearchMemoryParams
            {
                NovelId = "novel1",
                Query = "adventure data",
                TypeFilter = "location",
            },
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsError);
        var text = GetText(result);
        // 应包含地点文档的中文标签
        Assert.Contains("地点档案", text);
        // 不应包含角色文档的中文标签
        Assert.DoesNotContain("角色档案", text);
    }

    private static string GetText(Core.Tools.AgentToolResult result) =>
        result.Content?.OfType<Core.Shared.TextContent>().FirstOrDefault()?.Text ?? "";
}
