using Core.Agent;
using Core.Data;
using Core.Tools;

namespace Desktop.Services;

/// <summary>
/// 小说上下文钩子：当 manage_novel 工具成功时自动设置 agent.CurrentNovelId
/// 并将 WritingProfile 注入 agent 的小说上下文（镜像 TUI 的 Host.LoadNovelHook）。
/// </summary>
public sealed class NovelContextHook(Agent agent, INovelStore store) : IToolHook
{
    /// <summary>工具调用前钩子（直接允许执行）。</summary>
    public Task<ToolCallBeforeResult> BeforeToolCallAsync(
        ToolHookContext context,
        CancellationToken ct
    ) => Task.FromResult(ToolCallBeforeResult.Allow);

    /// <summary>工具调用后钩子：manage_novel 成功时注入小说上下文。</summary>
    public async Task AfterToolCallAsync(ToolHookContext context, CancellationToken ct)
    {
        if (context.Result is null || context.Result.IsError)
            return;

        if (context.ToolName != "manage_novel")
            return;

        if (context.Result.Details is not INovelDetails nd || string.IsNullOrEmpty(nd.Id))
            return;

        agent.CurrentNovelId = nd.Id;

        try
        {
            var novel = await store.GetNovel(nd.Id, ct);
            if (novel is not null)
            {
                var profile = WritingProfile.FromNovel(novel);
                agent.SetNovelContext(profile.ToContextString());
            }
        }
        catch
        {
            // 配置注入失败不应阻塞工具调用流程
        }
    }
}
