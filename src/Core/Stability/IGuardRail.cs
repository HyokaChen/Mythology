namespace Core.Stability;

/// <summary>
/// GuardRail 检查结果。
/// </summary>
public sealed class GuardRailResult
{
    /// <summary>检查是否通过。</summary>
    public bool Passed { get; init; } = true;

    /// <summary>未通过时的原因描述。</summary>
    public string Reason { get; init; } = "";

    /// <summary>是否应中断 Agent 循环（tripwire）。false = 仅记录/警告，不中断。</summary>
    public bool Tripwire { get; init; }

    /// <summary>检查通过。</summary>
    public static GuardRailResult Ok => new();

    /// <summary>检查未通过，但不中断循环（仅记录）。</summary>
    public static GuardRailResult Warn(string reason) =>
        new()
        {
            Passed = false,
            Reason = reason,
            Tripwire = false,
        };

    /// <summary>检查未通过，中断循环（tripwire）。</summary>
    public static GuardRailResult Block(string reason) =>
        new()
        {
            Passed = false,
            Reason = reason,
            Tripwire = true,
        };
}

/// <summary>
/// GuardRail 语义检查接口 — 在 LLM 调用前后检查输入/输出是否满足约束。
/// Core 层定义此抽象，Host/插件层提供具体实现。
///
/// 与 LlmTornado 的 GuardRailFunction 对比：
/// - LlmTornado: 单一 delegate，仅检查输入，同步返回
/// - Mythology: 接口化，支持输入+输出检查，异步，支持 warn/block 两级
///
/// 典型实现：
/// - 内容安全检查（暴力/色情/政治敏感内容过滤）
/// - 输出格式验证（确保 LLM 输出符合预期结构）
/// - 角色一致性检查（确保 LLM 不脱离角色设定）
/// - 预算保护（单轮 token 消耗超限时阻断）
/// </summary>
public interface IGuardRail : IDisposable
{
    /// <summary>
    /// 检查用户输入是否满足约束。
    /// 在 ConversationLoop 将用户消息加入 Conversation 之前调用。
    /// </summary>
    /// <param name="input">用户输入文本。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>检查结果。</returns>
    ValueTask<GuardRailResult> CheckInputAsync(string? input, CancellationToken ct = default);

    /// <summary>
    /// 检查 LLM 输出是否满足约束。
    /// 在 ConversationLoop 收到完整 LLM 响应后、发射 MessageEnd 之前调用。
    /// </summary>
    /// <param name="output">LLM 输出文本。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>检查结果。</returns>
    ValueTask<GuardRailResult> CheckOutputAsync(string? output, CancellationToken ct = default);
}
