using Core.Tools;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>
/// BatchCreateItemsTool 批量创建道具工具测试。
/// </summary>
public class BatchCreateItemsToolTests
{
    private readonly FakeNovelStore _store = new();

    /// <summary>创建被测工具实例。</summary>
    private BatchCreateItemsTool CreateTool() => new(_store);

    [Fact]
    /// <summary>空列表应返回错误。</summary>
    public async Task ExecuteAsync_EmptyList_ReturnsError()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync(
            "call-1",
            new BatchCreateItemsParams { Items = [] },
            CancellationToken.None
        );

        Assert.True(result.IsError);
        Assert.Contains("至少一个道具", ToolDispatcher.ExtractText(result.Content));
    }

    [Fact]
    /// <summary>null 列表应返回错误。</summary>
    public async Task ExecuteAsync_NullList_ReturnsError()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync(
            "call-1",
            new BatchCreateItemsParams { Items = null! },
            CancellationToken.None
        );

        Assert.True(result.IsError);
    }

    [Fact]
    /// <summary>有效输入应创建所有道具。</summary>
    public async Task ExecuteAsync_AllValid_CreatesAllItems()
    {
        var tool = CreateTool();
        var args = new BatchCreateItemsParams
        {
            NovelId = "novel-1",
            Items =
            [
                new() { Name = "宝剑", Description = "锋利无比" },
                new() { Name = "秘笈", Description = "记载武功" },
            ],
        };

        var result = await tool.ExecuteAsync("call-1", args, CancellationToken.None);

        Assert.False(result.IsError);
        var details = (BatchCreateItemsDetails)result.Details!;
        Assert.Equal(2, details.CreatedCount);
        Assert.Equal(2, details.CreatedIds.Length);
        Assert.Equal(0, details.FailedCount);
    }
}
