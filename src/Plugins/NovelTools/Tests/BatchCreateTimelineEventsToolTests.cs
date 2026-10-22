using Core.Tools;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>
/// BatchCreateTimelineEventsTool 批量创建时间线事件工具测试。
/// </summary>
public class BatchCreateTimelineEventsToolTests
{
    private readonly FakeNovelStore _store = new();

    /// <summary>创建被测工具实例。</summary>
    private BatchCreateTimelineEventsTool CreateTool() => new(_store);

    [Fact]
    /// <summary>空列表应返回错误。</summary>
    public async Task ExecuteAsync_EmptyList_ReturnsError()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync(
            "call-1",
            new BatchCreateTimelineEventsParams { Events = [] },
            CancellationToken.None
        );

        Assert.True(result.IsError);
        Assert.Contains("至少一个时间线事件", ToolDispatcher.ExtractText(result.Content));
    }

    [Fact]
    /// <summary>null 列表应返回错误。</summary>
    public async Task ExecuteAsync_NullList_ReturnsError()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync(
            "call-1",
            new BatchCreateTimelineEventsParams { Events = null! },
            CancellationToken.None
        );

        Assert.True(result.IsError);
    }

    [Fact]
    /// <summary>有效输入应创建所有事件。</summary>
    public async Task ExecuteAsync_AllValid_CreatesAllEvents()
    {
        var tool = CreateTool();
        var args = new BatchCreateTimelineEventsParams
        {
            NovelId = "novel-1",
            Events =
            [
                new() { Title = "开篇", ChapterId = "ch1" },
                new() { Title = "冲突", ChapterId = "ch5" },
            ],
        };

        var result = await tool.ExecuteAsync("call-1", args, CancellationToken.None);

        Assert.False(result.IsError);
        var details = (BatchCreateTimelineEventsDetails)result.Details!;
        Assert.Equal(2, details.CreatedCount);
        Assert.Equal(2, details.CreatedIds.Length);
        Assert.Equal(0, details.FailedCount);
    }
}
