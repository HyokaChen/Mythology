namespace Core.Stability;

/// <summary>
/// Token 预算耗尽异常。
/// 当 StabilityMonitor 累计 token 消耗超过配置的 TokenBudget 时抛出。
/// 定义在 Core 层，使 ConversationLoop 可以 catch 并映射为 ContextOverflowException，
/// 触发 auto-compact + retry 恢复路径。
/// </summary>
public class BudgetExhaustedException : Exception
{
    public long TokensUsed { get; }
    public long BudgetCeiling { get; }

    public BudgetExhaustedException(string message, long tokensUsed, long budgetCeiling)
        : base(message)
    {
        TokensUsed = tokensUsed;
        BudgetCeiling = budgetCeiling;
    }

    public BudgetExhaustedException(
        string message,
        Exception inner,
        long tokensUsed,
        long budgetCeiling
    )
        : base(message, inner)
    {
        TokensUsed = tokensUsed;
        BudgetCeiling = budgetCeiling;
    }
}
