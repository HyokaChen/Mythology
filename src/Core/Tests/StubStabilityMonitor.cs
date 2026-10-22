using Core.Stability;

namespace Core.Tests;

/// <summary>
/// 测试用稳定性监控器桩（Core.Tests 不引用 StabilityHarness，故此处最小实现）。
/// 只关心 TotalTokens / 预算 / 冻结位——正是接续阈值判定所需的三项。
/// </summary>
internal sealed class StubStabilityMonitor(long budget, long totalTokens = 0) : IStabilityMonitor
{
    public long Budget { get; set; } = budget;

    public bool Frozen { get; set; }

    public long TotalTokens { get; private set; } = totalTokens;

    public long TotalErrors => 0;

    public int TotalSteps => 0;

    public bool IsTripped => false;

    public bool IsFrozen => Frozen;

    public bool IsStable => !Frozen;

    public IReadOnlyList<double> EnergyHistory => [];

    public StabilityStatus RecordStep(int tokensUsed, int errors = 0)
    {
        TotalTokens += tokensUsed;
        return StabilityStatus.Stable;
    }

    public StabilityStatus RecordStep(int tokensUsed, int thinkingTokens, int errors = 0) =>
        RecordStep(tokensUsed, errors);

    public StabilityStatus CheckStability() =>
        Frozen ? StabilityStatus.Frozen : StabilityStatus.Stable;

    public void Reset()
    {
        TotalTokens = 0;
        Frozen = false;
    }

    public void Unfreeze(long currentTokens = -1)
    {
        if (!Frozen)
            return; // 与真实实现一致：未冻结时早退（本项目实测过的陷阱）
        Frozen = false;
        if (currentTokens >= 0)
            SetTokens(currentTokens);
    }

    public void UpdateTokenBudget(long budgetCeiling) => Budget = budgetCeiling;

    public void ApplyProfile(ModelContextProfile profile) { }

    public void SeedTokenCount(long tokens)
    {
        if (tokens > 0 && TotalTokens == 0)
            TotalTokens = tokens;
    }

    public long GetTokenBudget() => Budget;

    /// <summary>测试辅助：直接设置 usage。</summary>
    public void SetTokens(long tokens) => TotalTokens = tokens;

    public void Dispose() { }
}
