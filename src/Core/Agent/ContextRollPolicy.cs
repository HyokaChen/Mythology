namespace Core.Agent;

/// <summary>
/// 换页后的自动接续策略（Host 用它决定：新页要不要不再等用户、直接把上一任务接着做）。
///
/// 只在**被迫换页**（接近上限 threshold / 溢出恢复 overflow）后自动接续：这类换页是系统切断的，
/// 模型很可能被迫草草收尾，任务并没真做完。模型主动 new_context 属模型自己的决定，不代它继续。
///
/// 注：这不是 Codex 语义（Codex 的 new_context 无参数、无自动交接），是本仓的产品化增强：
/// 让"任务做完没做完"由模型在新页自行判断，而不是甩给用户再说一句「继续」。
/// </summary>
public static class ContextRollPolicy
{
    /// <summary>同一条用户消息下最多自动接续次数（防无界消耗：模型持续接不上也不会刷爆预算）。</summary>
    public const int MaxAutoContinuesPerUserMessage = 2;

    /// <summary>该换页原因是否属于「系统被迫切断」。</summary>
    public static bool IsForcedRoll(string? rollReason) => rollReason is "threshold" or "overflow";

    /// <summary>是否应在 run 边界换页之后自动接续（Host 调 agent.Continue，不追加用户消息）。</summary>
    public static bool ShouldAutoContinue(string? rollReason, int usedCount) =>
        IsForcedRoll(rollReason) && usedCount < MaxAutoContinuesPerUserMessage;
}
