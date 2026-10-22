namespace Core.Tools;

/// <summary>
/// 工具钩子上下文。
/// 在工具执行前后传递给钩子，包含工具调用的相关信息。
/// </summary>
public sealed record ToolHookContext
{
    /// <summary>工具调用 ID。</summary>
    public string ToolCallId { get; init; } = "";

    /// <summary>工具名称。</summary>
    public string ToolName { get; init; } = "";

    /// <summary>工具调用参数（JSON 格式）。</summary>
    public string Arguments { get; init; } = "";

    /// <summary>工具执行结果（仅在 AfterToolCallAsync 中有效）。</summary>
    public AgentToolResult? Result { get; init; }

    /// <summary>当前 Agent 实例（可选）。</summary>
    public object? Agent { get; init; }
}

/// <summary>
/// 工具执行前钩子的返回结果。
/// 支持阻止工具执行，用于权限控制和前置校验。
/// </summary>
public record ToolCallBeforeResult
{
    /// <summary>是否阻止工具执行。如果为 true，工具不会被执行。</summary>
    public bool Block { get; init; }

    /// <summary>阻止原因（当 Block=true 时有效）。</summary>
    public string? Reason { get; init; }

    /// <summary>允许执行的默认实例。</summary>
    public static readonly ToolCallBeforeResult Allow = new() { Block = false };

    /// <summary>
    /// 创建阻止执行的实例。
    /// </summary>
    /// <param name="reason">阻止原因。</param>
    /// <returns>阻止执行的结果实例。</returns>
    public static ToolCallBeforeResult BlockExecution(string reason) =>
        new() { Block = true, Reason = reason };
}

/// <summary>
/// 工具执行钩子接口。
/// 允许在工具执行前后插入自定义逻辑，如日志记录、权限校验等。
/// </summary>
public interface IToolHook
{
    /// <summary>
    /// 工具执行前的钩子。
    /// 返回 ToolCallBeforeResult 决定是否允许执行。
    /// </summary>
    /// <param name="context">工具钩子上下文。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>是否允许执行的判定结果。</returns>
    Task<ToolCallBeforeResult> BeforeToolCallAsync(ToolHookContext context, CancellationToken ct);

    /// <summary>
    /// 工具执行后的钩子。
    /// 可用于日志记录、后处理等。
    /// </summary>
    /// <param name="context">工具钩子上下文（包含执行结果）。</param>
    /// <param name="ct">取消令牌。</param>
    Task AfterToolCallAsync(ToolHookContext context, CancellationToken ct);
}
