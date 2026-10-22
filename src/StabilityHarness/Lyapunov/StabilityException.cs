namespace StabilityHarness.Lyapunov;

/// <summary>
/// 稳定性异常基类。Lyapunov 监控器内部异常的公共基类。
/// </summary>
public abstract class StabilityException : Exception
{
    protected StabilityException(string message)
        : base(message) { }

    protected StabilityException(string message, Exception inner)
        : base(message, inner) { }
}

public sealed class StabilityViolationException(
    string message,
    int consecutiveSteps,
    double peakEnergy
) : StabilityException(message)
{
    public int ConsecutiveSteps { get; } = consecutiveSteps;
    public double PeakEnergy { get; } = peakEnergy;
}

/// <summary>
/// Token 预算耗尽。继承 Core.Stability.BudgetExhaustedException，
/// 使 ConversationLoop 可以通过基类 catch 并映射为 ContextOverflowException。
/// </summary>
public sealed class BudgetExhaustedException(string message, long tokensUsed, long budgetCeiling)
    : Core.Stability.BudgetExhaustedException(message, tokensUsed, budgetCeiling);

public sealed class PermanentFailureException(
    string message,
    string reason,
    TelemetrySnapshot? report = null
) : StabilityException(message)
{
    public string FailureReason { get; } = reason;
    public TelemetrySnapshot? Report { get; } = report;
}
