using Core.Stability;

namespace StabilityHarness.Lyapunov;

/// <summary>
/// 复合稳定性监控器：聚合 GrowthRatioGuard + LyapunovMonitor。
/// 任一子监控器触发断路/冻结，复合监控器即触发。
/// 两者检测不同维度的不稳定：
/// - GrowthRatioGuard：单轮 token 消耗相对基线的增长比率（"这一轮比基线大多少倍？"）
/// - LyapunovMonitor：能量轨迹的持续增长趋势（"能量是否持续递增？"）
///
/// GRG 可能遗漏的渐进式升级（baseline 适应后 ratio 回落，但能量仍在增长），
/// Lyapunov 可以捕捉；Lyapunov 可能遗漏的突发性跳升（单轮暴增但后续回落），
/// GRG 可以捕捉。两者互补。
/// </summary>
public sealed class CompositeStabilityMonitor : IGrowthRatioGuard
{
    /// <summary>底层 GrowthRatioGuard 实例，供诊断/分析使用。</summary>
    public GrowthRatioGuard Guard { get; }

    /// <summary>底层 LyapunovMonitor 实例，供诊断/分析使用。</summary>
    public LyapunovMonitor Lyapunov { get; }

    public CompositeStabilityMonitor(StabilityConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        Guard = new GrowthRatioGuard(config);
        Lyapunov = new LyapunovMonitor(config);
    }

    // ── 聚合属性 ──

    public long TotalTokens => Guard.TotalTokens;
    public long TotalErrors => Guard.TotalErrors;
    public int TotalSteps => Guard.TotalSteps;

    /// <summary>任一子监控器 Tripped 即 Tripped。</summary>
    public bool IsTripped => Guard.IsTripped || Lyapunov.IsTripped;

    /// <summary>任一子监控器 Frozen 即 Frozen。</summary>
    public bool IsFrozen => Guard.IsFrozen || Lyapunov.IsFrozen;

    /// <summary>两个子监控器都 Stable 才 Stable。</summary>
    public bool IsStable => Guard.IsStable && Lyapunov.IsStable;

    /// <summary>聚合能量历史：GRG 的 ratio 历史 + Lyapunov 的能量历史拼接。</summary>
    public IReadOnlyList<double> EnergyHistory =>
        [.. Guard.EnergyHistory, .. Lyapunov.EnergyHistory];

    // ── IGrowthRatioGuard 透传 ──

    public double? Baseline => Guard.Baseline;
    public double? CurrentRatio => Guard.CurrentRatio;
    public int ConsecutiveEscalating => Guard.ConsecutiveEscalating;

    /// <summary>重置两个子监控器的升级计数与断路状态（新任务边界调用）。</summary>
    public void ResetEscalation()
    {
        Guard.ResetEscalation();
        Lyapunov.ResetEscalation();
    }

    public void ResetBaseline()
    {
        Guard.ResetBaseline();
        Lyapunov.ResetBaseline();
    }

    // ── IStabilityMonitor 实现 ──

    public StabilityStatus RecordStep(int tokensUsed, int errors = 0) =>
        RecordStep(tokensUsed, thinkingTokens: 0, errors: errors);

    /// <summary>
    /// 记录一次执行步骤（thinking-aware），同时推送到两个子监控器。
    /// 返回两者中更严重的状态。
    /// </summary>
    // errors 不给默认值：与两参重载 (tokensUsed, errors = 0) 叠加会让
    // RecordStep(x, y) 这类位置实参调用产生 CS0121 二义性
    public StabilityStatus RecordStep(int tokensUsed, int thinkingTokens, int errors)
    {
        // 先推送到 GRG（可能抛 BudgetExhaustedException / StabilityViolationException）
        var guardStatus = Guard.RecordStep(tokensUsed, thinkingTokens, errors);

        // GRG 触发后仍推送 Lyapunov（保持其能量历史完整），但忽略其异常
        StabilityStatus lyapunovStatus;
        try
        {
            lyapunovStatus = Lyapunov.RecordStep(tokensUsed, thinkingTokens, errors);
        }
        catch (BudgetExhaustedException)
        {
            lyapunovStatus = StabilityStatus.Frozen;
        }
        catch (StabilityViolationException)
        {
            lyapunovStatus = StabilityStatus.Tripped;
        }

        return WorstOf(guardStatus, lyapunovStatus);
    }

    public StabilityStatus CheckStability() =>
        WorstOf(Guard.CheckStability(), Lyapunov.CheckStability());

    public void Reset()
    {
        Guard.Reset();
        Lyapunov.Reset();
    }

    public void Unfreeze(long currentTokens = -1)
    {
        Guard.Unfreeze(currentTokens);
        Lyapunov.Unfreeze(currentTokens);
    }

    public void UpdateTokenBudget(long budgetCeiling)
    {
        Guard.UpdateTokenBudget(budgetCeiling);
        Lyapunov.UpdateTokenBudget(budgetCeiling);
    }

    public void ApplyProfile(ModelContextProfile profile)
    {
        Guard.ApplyProfile(profile);
        Lyapunov.ApplyProfile(profile);
    }

    public void SeedTokenCount(long tokens)
    {
        Guard.SeedTokenCount(tokens);
        Lyapunov.SeedTokenCount(tokens);
    }

    public long GetTokenBudget() => Guard.GetTokenBudget();

    public void Dispose()
    {
        Guard.Dispose();
        Lyapunov.Dispose();
    }

    /// <summary>取两个状态中更严重的一个。严重度：Frozen > Tripped > Unstable > Warning > Stable。</summary>
    private static StabilityStatus WorstOf(StabilityStatus a, StabilityStatus b) =>
        (int)a >= (int)b ? a : b;
}
