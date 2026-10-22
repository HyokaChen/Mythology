namespace Core.Tools;

/// <summary>
/// 组合工具钩子 — 链式执行多个 IToolHook。
/// Before: 顺序执行，任一返回 Block 则短路。
/// After: 顺序执行所有（不短路）。
/// </summary>
public sealed class CompositeToolHook(params IToolHook[] hooks) : IToolHook
{
    public async Task<ToolCallBeforeResult> BeforeToolCallAsync(
        ToolHookContext context,
        CancellationToken ct
    )
    {
        foreach (var hook in hooks)
        {
            var result = await hook.BeforeToolCallAsync(context, ct);
            if (result.Block)
                return result;
        }
        return ToolCallBeforeResult.Allow;
    }

    public async Task AfterToolCallAsync(ToolHookContext context, CancellationToken ct)
    {
        foreach (var hook in hooks)
        {
            await hook.AfterToolCallAsync(context, ct);
        }
    }
}
