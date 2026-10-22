using Core.Vector;
using Microsoft.Extensions.Logging;

namespace Core.Tools;

/// <summary>
/// Agent 记忆自动钩子 —— 在工具执行后自动标注记忆有用性。
///
/// 当前行为：
/// - memory_recall / memory_find_conflicts 成功返回后 → 标记返回的记忆为有用
///   （LLM 主动调用了召回，说明这些记忆与当前任务相关，usefulness 应上升）
///
/// 设计：<see cref="IToolHook.AfterToolCallAsync"/> 中读取工具结果详情
/// <see cref="AgentMemoryDetails.MemoryIds"/>，对每条记忆调用
/// <see cref="AgentMemoryStore.MarkUsefulAsync"/>。
/// 需在 Agent 上设置 <see cref="Agent.MemoryStore"/> 引用。
/// </summary>
public sealed class AgentMemoryHook(ILogger? logger = null) : IToolHook
{
    public Task<ToolCallBeforeResult> BeforeToolCallAsync(
        ToolHookContext context,
        CancellationToken ct
    ) => Task.FromResult(ToolCallBeforeResult.Allow);

    public async Task AfterToolCallAsync(ToolHookContext context, CancellationToken ct)
    {
        // 只处理成功执行的记忆工具（Result 为 null 时无 Details 可用，同样跳过）
        if (context.Result is null or { IsError: true })
            return;

        if (context.ToolName is not ("memory_recall" or "memory_find_conflicts"))
            return;

        if (context.Result.Details is not AgentMemoryDetails details)
            return;

        if (details.MemoryIds is not { Count: > 0 })
            return;

        // 需要 Agent 引用和 MemoryStore
        if (context.Agent is not Core.Agent.Agent agent || agent.MemoryStore is null)
            return;

        const double autoWeight = 0.3; // 自动标注的权重（低于人工显式标注的 1.0）

        foreach (var memoryId in details.MemoryIds)
        {
            try
            {
                await agent.MemoryStore.MarkUsefulAsync(memoryId, autoWeight, ct);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "AgentMemoryHook 自动标注有用性失败: {MemoryId}", memoryId);
            }
        }

        logger?.LogDebug(
            "AgentMemoryHook 自动标注 {Count} 条记忆为有用（来自 {ToolName}）",
            details.MemoryIds.Count,
            context.ToolName
        );
    }
}
