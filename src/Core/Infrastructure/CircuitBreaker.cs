using System.Runtime.CompilerServices;
using Core.Agent;
using Core.Tools;

namespace Core.Infrastructure;

/// <summary>
/// 断路器 — 检测重复工具调用并触发断路器事件。
/// 从 AgentLoop 中提取，将稳定性逻辑与编排逻辑分离。
///
/// 检测策略：
/// - 精确重复：连续 N 次调用同一工具且参数完全相同 → 立即触发
/// - 高频检测已移除：同一工具在窗口内的高频出现属于正常批量操作，
///   由 GrowthRatioGuard（token 消耗增长率）兜底检测真正的失控。
///
/// 阈值通过 ModelContextProfile.ScaledCircuitBreakerThreshold 动态化：
/// 大上下文模型合理批量操作更多，需更大阈值避免误触发。
/// </summary>
public static class CircuitBreaker
{
    /// <summary>默认重复调用阈值，用于未指定 threshold 的调用。</summary>
    private const int DefaultThreshold = 3;

    // ── 核心检测逻辑 ──

    /// <summary>
    /// 检查工具调用历史，检测精确重复调用模式。
    /// 使用默认阈值（3）。
    /// </summary>
    /// <param name="history">工具调用历史记录。</param>
    /// <returns>如果检测到精确重复模式，返回断路器触发事件；否则返回 null。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static CircuitBreakerTriggered? Check(
        ToolDispatcher.CircularBuffer<ToolDispatcher.ToolCallRecord> history
    ) => Check(history, DefaultThreshold);

    /// <summary>
    /// 检查工具调用历史，检测精确重复调用模式。
    /// </summary>
    /// <param name="history">工具调用历史记录。</param>
    /// <param name="threshold">重复调用阈值：连续相同调用多少次触发断路器。</param>
    /// <returns>如果检测到精确重复模式，返回断路器触发事件；否则返回 null。</returns>
    public static CircuitBreakerTriggered? Check(
        ToolDispatcher.CircularBuffer<ToolDispatcher.ToolCallRecord> history,
        int threshold
    )
    {
        // 历史记录不足阈值，跳过检测
        if (history.Count < threshold)
            return null;

        // 检查最近的 N 次调用是否完全相同（工具名 + 参数均相同）
        var recent = history.GetRecent(threshold);

        // 检查所有 recent 记录是否与第一条相同
        for (var i = 1; i < recent.Count; i++)
        {
            if (recent[i] != recent[0])
                return null;
        }

        return new CircuitBreakerTriggered(
            recent[0].ToolName,
            recent[0].Arguments,
            threshold,
            $"工具「{recent[0].ToolName}」连续调用 {threshold} 次参数完全相同，已触发断路器。"
        );
    }
}
