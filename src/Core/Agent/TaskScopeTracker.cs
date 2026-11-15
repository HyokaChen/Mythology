using Core.Data;

namespace Core.Agent;

/// <summary>
/// 作者消息 → 任务边界。只在作者明确给出“任务级”范围时替换长期 Scope；
/// 单章/单段修改只记本轮 TurnTarget，避免一句“改一下第54章”把卷二任务弄丢。
/// </summary>
public static class TaskScopeTracker
{
    /// <summary>会话切换/恢复时从 sidecar 载入 Scope。</summary>
    public static async Task LoadAsync(
        Agent agent,
        FileSessionStore sessionStore,
        string? sessionId,
        CancellationToken ct = default
    )
    {
        agent.TurnTargetChapterId = null;
        agent.ActiveTaskScope = string.IsNullOrWhiteSpace(sessionId)
            ? null
            : await sessionStore.GetTaskScopeAsync(sessionId, ct);
    }

    /// <summary>新会话：清空内存态；sidecar 由新会话目录自然隔离。</summary>
    public static void Clear(Agent agent)
    {
        agent.ActiveTaskScope = null;
        agent.TurnTargetChapterId = null;
    }

    /// <summary>
    /// 观察一条作者消息。调用方必须只传真正的作者输入（不要传 harness/云端诊断/远期推演）。
    /// </summary>
    public static async Task ObserveAuthorMessageAsync(
        Agent agent,
        FileSessionStore sessionStore,
        INovelStore novelStore,
        string? text,
        CancellationToken ct = default
    )
    {
        agent.TurnTargetChapterId = null;
        if (string.IsNullOrWhiteSpace(text) || SessionInternalMessages.IsInjectedUserMessage(text))
            return;

        var sessionId = agent.SessionId;
        var novelId = agent.CurrentNovelId;
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(novelId))
            return;

        if (CompactionTaskScope.IsCancellationInstruction(text))
        {
            agent.ActiveTaskScope = null;
            await sessionStore.SaveTaskScopeAsync(sessionId, novelId, null, ct);
            return;
        }

        if (!CompactionTaskScope.MayContainScopeReference(text))
            return;

        var structure = await novelStore.GetStructure(novelId, ct);
        if (structure is null || structure.Chapters.Count == 0)
            return;

        var explicitScope = CompactionTaskScope.TryCreate(text, structure);
        if (explicitScope is null)
            return;

        if (!CompactionTaskScope.IsTaskLevelInstruction(text) && agent.ActiveTaskScope is not null)
        {
            // 已有长期任务时，单章/单段修改只记本轮 TurnTarget，不覆盖长期范围。
            agent.TurnTargetChapterId = explicitScope.Chapters.FirstOrDefault()?.Id;
            return;
        }

        // 没有长期任务时，作者明确点名的章节就是一个新任务（单章也持久化）。
        var state = explicitScope.ToState();
        agent.ActiveTaskScope = state;
        await sessionStore.SaveTaskScopeAsync(sessionId, novelId, state, ct);
    }
}
