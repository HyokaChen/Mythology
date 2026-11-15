using Core.Agent;
using Core.Data;
using Core.Novels;
using Microsoft.Extensions.Logging;
using AgentModel = Core.Agent.Agent;

namespace Core.Tools;

/// <summary>
/// 工具执行前的最后一道任务边界闸门。只拦写入类工具，读/诊断不受影响。
/// 即使 compact 摘要再次写错，Scope 外的 write_chapter/edit_text/rewrite_selection 也执行不了。
/// </summary>
public sealed class TaskScopeGuardHook(
    AgentModel agent,
    INovelStore novelStore,
    FileSessionStore? sessionStore = null,
    ILogger<TaskScopeGuardHook>? logger = null
) : IToolHook
{
    public async Task<ToolCallBeforeResult> BeforeToolCallAsync(
        ToolHookContext context,
        CancellationToken ct
    )
    {
        if (!TaskScopeGuard.IsGuarded(context.ToolName))
            return ToolCallBeforeResult.Allow;

        var scope = agent.ActiveTaskScope;
        if (
            scope is null
            && sessionStore is not null
            && !string.IsNullOrWhiteSpace(agent.SessionId)
        )
        {
            scope = await sessionStore.GetTaskScopeAsync(agent.SessionId, ct);
            agent.ActiveTaskScope = scope;
        }

        if (scope is null || !scope.IsActive)
            return ToolCallBeforeResult.Allow;

        var novelId = agent.CurrentNovelId;
        if (string.IsNullOrWhiteSpace(novelId))
            return ToolCallBeforeResult.BlockExecution(
                "当前任务边界已生效，但无法确定小说 ID，已阻止写入。"
            );

        NovelStructure? structure;
        try
        {
            structure = await novelStore.GetStructure(novelId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "task scope guard failed to read novel structure");
            return ToolCallBeforeResult.BlockExecution(
                "当前任务边界已生效，但无法读取小说结构，已阻止写入。"
            );
        }

        if (structure is null)
            return ToolCallBeforeResult.BlockExecution(
                "当前任务边界已生效，但无法读取小说结构，已阻止写入。"
            );

        var reason = TaskScopeGuard.Evaluate(
            scope,
            context.ToolName,
            context.Arguments,
            structure.Chapters,
            structure.Volumes,
            agent.TurnTargetChapterId
        );

        if (reason is null)
            return ToolCallBeforeResult.Allow;

        logger?.LogWarning(
            "task scope blocked tool={Tool} scope={Scope} targetArgs={Args}",
            context.ToolName,
            scope.Label,
            context.Arguments
        );
        return ToolCallBeforeResult.BlockExecution(reason);
    }

    public Task AfterToolCallAsync(ToolHookContext context, CancellationToken ct) =>
        Task.CompletedTask;
}
