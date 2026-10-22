using Core.Agent;
using Xunit;

namespace Core.Tests;

/// <summary>
/// ToolUsageStats 工具调用统计测试。
/// 验证调用计数、批量工具检测和统计摘要。
/// </summary>
public class ToolUsageStatsTests
{
    [Fact]
    /// <summary>记录调用应递增工具计数。</summary>
    public void RecordCall_IncrementsToolCount()
    {
        var stats = new ToolUsageStats();
        stats.RecordCall("manage_character");
        stats.RecordCall("manage_character");
        stats.RecordCall("manage_plotline");

        Assert.Equal(2, stats.ToolCallCounts["manage_character"]);
        Assert.Equal(1, stats.ToolCallCounts["manage_plotline"]);
        Assert.Equal(3, stats.TotalCalls);
    }

    [Fact]
    /// <summary>应正确跟踪批量工具调用。</summary>
    public void RecordCall_TracksBatchToolCalls()
    {
        var stats = new ToolUsageStats();
        stats.RecordCall("manage_character");
        stats.RecordCall("batch_create_characters");
        stats.RecordCall("batch_create_plotlines");
        stats.RecordCall("batch_search_memory");

        Assert.Equal(3, stats.BatchToolCalls);
        Assert.Equal(4, stats.TotalCalls);
    }

    [Fact]
    /// <summary>记录断路器触发应递增计数。</summary>
    public void RecordBreakerTrigger_IncrementsCount()
    {
        var stats = new ToolUsageStats();
        stats.RecordBreakerTrigger();
        stats.RecordBreakerTrigger();

        Assert.Equal(2, stats.BreakerTriggers);
    }

    [Fact]
    /// <summary>重置应清空所有统计。</summary>
    public void Reset_ClearsAllStats()
    {
        var stats = new ToolUsageStats();
        stats.RecordCall("batch_create_characters");
        stats.RecordBreakerTrigger();

        stats.Reset();

        Assert.Equal(0, stats.TotalCalls);
        Assert.Equal(0, stats.BatchToolCalls);
        Assert.Equal(0, stats.BreakerTriggers);
        Assert.Empty(stats.ToolCallCounts);
    }

    [Fact]
    /// <summary>摘要应包含批量工具比例。</summary>
    public void GetSummary_IncludesBatchRatio()
    {
        var stats = new ToolUsageStats();
        stats.RecordCall("manage_character");
        stats.RecordCall("batch_create_characters");
        stats.RecordBreakerTrigger();

        var summary = stats.GetSummary();

        Assert.Contains("batch 工具调用: 1", summary);
        Assert.Contains("断路器触发: 1", summary);
        Assert.Contains("batch_create_characters: 1", summary);
    }

    [Fact]
    /// <summary>无调用时应显示零比例。</summary>
    public void GetSummary_NoCalls_ShowsZeroRatio()
    {
        var stats = new ToolUsageStats();
        var summary = stats.GetSummary();

        Assert.Contains("总调用: 0", summary);
        Assert.Contains("batch 工具调用: 0 (0.0%)", summary);
    }
}
