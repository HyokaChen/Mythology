using System.Collections.Concurrent;

namespace Core.Agent;

/// <summary>
/// 工具使用统计：记录每个工具的调用次数、batch 工具占比、断路器触发次数。
/// 用于监控断路器修复后是否仍有误触发，以及 batch 工具的采用情况。
/// 线程安全，可在 ConversationLoop 中并发更新。
///
/// 统计维度：
/// - 每个工具的累计调用次数
/// - batch 工具（名称以 batch_ 开头）的调用占比
/// - 断路器触发次数（用于评估稳定性）
/// </summary>
public sealed class ToolUsageStats
{
    /// <summary>每个工具的累计调用次数（线程安全字典）。</summary>
    private readonly ConcurrentDictionary<string, int> _toolCallCounts = new();

    /// <summary>batch 工具累计调用次数。</summary>
    private int _batchToolCalls;

    /// <summary>断路器触发次数。</summary>
    private int _breakerTriggers;

    /// <summary>总工具调用次数。</summary>
    private int _totalCalls;

    /// <summary>每个工具的累计调用次数（只读视图）。</summary>
    public IReadOnlyDictionary<string, int> ToolCallCounts => _toolCallCounts;

    /// <summary>batch 工具（名称以 batch_ 开头）的累计调用次数。</summary>
    public int BatchToolCalls => _batchToolCalls;

    /// <summary>断路器触发次数。</summary>
    public int BreakerTriggers => _breakerTriggers;

    /// <summary>总工具调用次数。</summary>
    public int TotalCalls => _totalCalls;

    /// <summary>
    /// 记录一次工具调用。
    /// 线程安全，使用 ConcurrentDictionary 和 Interlocked 操作。
    /// </summary>
    /// <param name="toolName">工具名称。</param>
    public void RecordCall(string toolName)
    {
        _toolCallCounts.AddOrUpdate(toolName, 1, (_, c) => c + 1);
        Interlocked.Increment(ref _totalCalls);
        if (toolName.StartsWith("batch_", StringComparison.Ordinal))
            Interlocked.Increment(ref _batchToolCalls);
    }

    /// <summary>
    /// 记录一次断路器触发。
    /// 线程安全，使用 Interlocked 操作。
    /// </summary>
    public void RecordBreakerTrigger() => Interlocked.Increment(ref _breakerTriggers);

    /// <summary>重置所有统计到初始状态。</summary>
    public void Reset()
    {
        _toolCallCounts.Clear();
        _batchToolCalls = 0;
        _breakerTriggers = 0;
        _totalCalls = 0;
    }

    /// <summary>生成可读的统计摘要文本。</summary>
    /// <returns>格式化后的统计摘要。</returns>
    public string GetSummary()
    {
        var top = _toolCallCounts
            // 上下文接续机制对用户隐藏：new_context / history_* 不出现在统计里
            .Where(kvp => !Core.Tools.HiddenTools.IsHidden(kvp.Key))
            .OrderByDescending(kvp => kvp.Value)
            .Take(10)
            .Select(kvp => $"  {kvp.Key}: {kvp.Value}");

        var batchRatio = _totalCalls > 0 ? (double)_batchToolCalls / _totalCalls * 100 : 0;

        return $"工具调用统计:\n"
            + $"  总调用: {_totalCalls}\n"
            + $"  batch 工具调用: {_batchToolCalls} ({batchRatio:F1}%)\n"
            + $"  断路器触发: {_breakerTriggers}\n"
            + $"  Top 10 工具:\n{string.Join("\n", top)}";
    }
}
