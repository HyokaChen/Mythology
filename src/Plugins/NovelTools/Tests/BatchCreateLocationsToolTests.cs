using Core.Tools;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>
/// BatchCreateLocationsTool 批量创建地点工具测试。
/// </summary>
public class BatchCreateLocationsToolTests
{
    private readonly FakeNovelStore _store = new();

    /// <summary>创建被测工具实例。</summary>
    private BatchCreateLocationsTool CreateTool() => new(_store);

    [Fact]
    /// <summary>空列表应返回错误。</summary>
    public async Task ExecuteAsync_EmptyList_ReturnsError()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync(
            "call-1",
            new BatchCreateLocationsParams { Locations = [] },
            CancellationToken.None
        );

        Assert.True(result.IsError);
        Assert.Contains("至少一个地点", ToolDispatcher.ExtractText(result.Content));
    }

    [Fact]
    /// <summary>null 列表应返回错误。</summary>
    public async Task ExecuteAsync_NullList_ReturnsError()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync(
            "call-1",
            new BatchCreateLocationsParams { Locations = null! },
            CancellationToken.None
        );

        Assert.True(result.IsError);
    }

    [Fact]
    /// <summary>有效输入应创建所有地点。</summary>
    public async Task ExecuteAsync_AllValid_CreatesAllLocations()
    {
        var tool = CreateTool();
        var args = new BatchCreateLocationsParams
        {
            NovelId = "novel-1",
            Locations =
            [
                new() { Name = "京城", Description = "都城" },
                new() { Name = "边疆", Description = "边境要塞" },
            ],
        };

        var result = await tool.ExecuteAsync("call-1", args, CancellationToken.None);

        Assert.False(result.IsError);
        var details = (BatchCreateLocationsDetails)result.Details!;
        Assert.Equal(2, details.CreatedCount);
        Assert.Equal(2, details.CreatedIds.Length);
        Assert.Equal(0, details.FailedCount);
    }
}
