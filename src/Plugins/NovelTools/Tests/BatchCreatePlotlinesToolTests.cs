using Core.Tools;
using Plugins.NovelTools.Tools;
using Xunit;

namespace NovelTools.Tests;

/// <summary>
/// BatchCreatePlotlinesTool 批量创建情节线工具测试。
/// </summary>
public class BatchCreatePlotlinesToolTests
{
    private readonly FakeNovelStore _store = new();

    /// <summary>创建被测工具实例。</summary>
    private BatchCreatePlotlinesTool CreateTool() => new(_store);

    [Fact]
    /// <summary>空列表应返回错误。</summary>
    public async Task ExecuteAsync_EmptyList_ReturnsError()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync(
            "call-1",
            new BatchCreatePlotlinesParams { Plotlines = [] },
            CancellationToken.None
        );

        Assert.True(result.IsError);
        Assert.Contains("至少一个情节线", ToolDispatcher.ExtractText(result.Content));
    }

    [Fact]
    /// <summary>null 列表应返回错误。</summary>
    public async Task ExecuteAsync_NullList_ReturnsError()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync(
            "call-1",
            new BatchCreatePlotlinesParams { Plotlines = null! },
            CancellationToken.None
        );

        Assert.True(result.IsError);
    }

    [Fact]
    /// <summary>有效输入应创建所有情节线。</summary>
    public async Task ExecuteAsync_AllValid_CreatesAllPlotlines()
    {
        var tool = CreateTool();
        var args = new BatchCreatePlotlinesParams
        {
            NovelId = "novel-1",
            Plotlines =
            [
                new()
                {
                    Name = "主线",
                    Type = "main",
                    PlannedRevealChapterId = "ch10",
                },
                new()
                {
                    Name = "伏笔",
                    Type = "foreshadowing",
                    PlannedRevealChapterId = "ch20",
                },
            ],
        };

        var result = await tool.ExecuteAsync("call-1", args, CancellationToken.None);

        Assert.False(result.IsError);
        var details = (BatchCreatePlotlinesDetails)result.Details!;
        Assert.Equal(2, details.CreatedCount);
        Assert.Equal(2, details.CreatedIds.Length);
        Assert.Equal(0, details.FailedCount);
    }

    [Fact]
    /// <summary>空名称应跳过并报告失败。</summary>
    public async Task ExecuteAsync_EmptyName_SkipsAndReportsFailed()
    {
        var tool = CreateTool();
        var args = new BatchCreatePlotlinesParams
        {
            NovelId = "novel-1",
            Plotlines = [new() { Name = "主线" }, new() { Name = "" }, new() { Name = "支线" }],
        };

        var result = await tool.ExecuteAsync("call-1", args, CancellationToken.None);

        Assert.False(result.IsError);
        var details = (BatchCreatePlotlinesDetails)result.Details!;
        Assert.Equal(2, details.CreatedCount);
        Assert.Equal(1, details.FailedCount);
    }
}
