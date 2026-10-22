namespace Core.Stability;

/// <summary>
/// GuardRail tripwire 触发异常。
/// 当 IGuardRail.CheckOutputAsync 返回 Tripwire=true 时，
/// ConversationLoop 抛出此异常中断循环。
/// 不继承自 ContextOverflowException——这是语义约束违反，不是上下文溢出。
/// </summary>
public class GuardRailTriggeredException : Exception
{
    public GuardRailTriggeredException(string message)
        : base(message) { }

    public GuardRailTriggeredException(string message, Exception inner)
        : base(message, inner) { }
}
