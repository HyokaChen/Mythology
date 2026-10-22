using Core.Tools;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>
/// BatchCreateFactionsTool 批量创建组织工具测试。
/// </summary>
public class BatchCreateFactionsToolTests
{
    private readonly FakeNovelStore _store = new();

    /// <summary>创建被测工具实例。</summary>
    private BatchCreateFactionsTool CreateTool() => new(_store);

    [Fact]
    /// <summary>空列表应返回错误。</summary>
    public async Task ExecuteAsync_EmptyList_ReturnsError()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync(
            "call-1",
            new BatchCreateFactionsParams { Factions = [] },
            CancellationToken.None
        );

        Assert.True(result.IsError);
        Assert.Contains("至少一个组织", ToolDispatcher.ExtractText(result.Content));
    }

    [Fact]
    /// <summary>null 列表应返回错误。</summary>
    public async Task ExecuteAsync_NullList_ReturnsError()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync(
            "call-1",
            new BatchCreateFactionsParams { Factions = null! },
            CancellationToken.None
        );

        Assert.True(result.IsError);
    }

    [Fact]
    /// <summary>有效输入应创建所有组织。</summary>
    public async Task ExecuteAsync_AllValid_CreatesAllFactions()
    {
        var tool = CreateTool();
        var args = new BatchCreateFactionsParams
        {
            NovelId = "novel-1",
            Factions =
            [
                new() { Name = "宗门A", Leader = "张三" },
                new() { Name = "宗门B", Leader = "李四" },
            ],
        };

        var result = await tool.ExecuteAsync("call-1", args, CancellationToken.None);

        Assert.False(result.IsError);
        var details = (BatchCreateFactionsDetails)result.Details!;
        Assert.Equal(2, details.CreatedCount);
        Assert.Equal(2, details.CreatedIds.Length);
        Assert.Equal(0, details.FailedCount);
    }
}
