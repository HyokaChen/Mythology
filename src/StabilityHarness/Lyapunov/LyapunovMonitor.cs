using Core.Stability;

namespace StabilityHarness.Lyapunov;

/// <summary>
/// 李雅普诺夫稳定性监控器：能量函数 V = sK + λ·θK，连续 ΔV 超过容忍度触发断路。
/// 所有阈值通过 StabilityConfig.ApplyProfile(ModelContextProfile) 动态化。
/// Thinking-aware：能量计算使用排除 thinking 的 token 数。
/// 改进：ΔV≥0 不再无条件触发，引入相对增长容忍度（默认 10%），
/// 小幅递增属于正常工作流模式（如"分析→构思→写作"的递增 completion）。
/// </summary>
public sealed class LyapunovMonitor : IStabilityMonitor
{
    private readonly List<double> _energyHistory = [];
    private int _consecutiveNonNegative;

    /// <summary>
    /// 当前上下文占用估算（seed + 每轮增量，≈ 最近一次调用的 PromptTokens）。
    /// RecordStep 的 tokensUsed 应为本轮增量（上下文增长 + 输出），
    /// 而非含全部历史的 PromptTokens —— 全量重复累计会使预算检查虚高。
    /// </summary>
    public long TotalTokens { get; private set; }

    /// <summary>累计 thinking tokens（含在 TotalTokens 中）。</summary>
    public long TotalThinkingTokens { get; private set; }

    public long TotalErrors { get; private set; }

    public int TotalSteps { get; private set; }

    public bool IsTripped { get; private set; }

    public bool IsFrozen { get; private set; }

    public bool IsStable => CheckStability() is StabilityStatus.Stable or StabilityStatus.Warning;
    public IReadOnlyList<double> EnergyHistory => _energyHistory;
    public StabilityConfig Config { get; }

    /// <summary>
    /// ΔV 相对增长容忍度。当 ΔV/V_prev 在此比例以内时，不视为"能量递增"，
    /// 因为小幅递增属于正常工作流（后续轮次因更多上下文自然产生更长 completion）。
    /// 通过 StabilityConfig.ApplyProfile(ModelContextProfile) 动态化：
    /// 1M → 0.20, 128K → 0.15, 8K → 0.10（adaptive thinking +0.05）。
    /// </summary>
    /// <summary>
    /// ΔV 相对增长容忍度。当 ΔV/V_prev 在此比例以内时，不视为"能量递增"，
    /// 因为小幅递增属于正常工作流（后续轮次因更多上下文自然产生更长 completion）。
    /// 委托到 Config.EffectiveDeltaVTolerance，消除双存储不一致风险。
    /// 通过 StabilityConfig.ApplyProfile(ModelContextProfile) 动态化：
    /// 1M → 0.20, 128K → 0.15, 8K → 0.10（adaptive thinking +0.05）。
    /// </summary>
    public double DeltaVTolerance => Config.EffectiveDeltaVTolerance;

    public LyapunovMonitor(StabilityConfig config)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        if (config.EffectiveLambda <= 0.0)
            throw new ArgumentException("Coupling constant λ must be positive", nameof(config));
        if (config.EffectiveWindow == 0)
            throw new ArgumentException("Stability window must be ≥ 1", nameof(config));
    }

    public LyapunovMonitor(double lambda = 1.0, int window = 3, long? budgetCeiling = null)
    {
        Config = new StabilityConfig
        {
            Lambda = lambda,
            Window = window,
            TokenBudget = budgetCeiling ?? long.MaxValue,
        };
        if (lambda <= 0.0)
            throw new ArgumentException("Coupling constant λ must be positive", nameof(lambda));
        if (window == 0)
            throw new ArgumentException("Stability window must be ≥ 1", nameof(window));
    }

    public StabilityStatus RecordStep(int tokensUsed, int errors = 0) =>
        RecordStep(tokensUsed, thinkingTokens: 0, errors: errors);

    /// <summary>
    /// 记录一次执行步骤（thinking-aware）。
    /// 能量计算使用排除 thinking 的 token 数，因为 thinking 深度由模型自主决定，
    /// 不应被判定为"能量递增=不稳定"。TokenBudget 检查仍使用总量。
    /// </summary>
    // errors 不给默认值：与两参重载 (tokensUsed, errors = 0) 叠加会让
    // RecordStep(x, y) 这类位置实参调用产生 CS0121 二义性
    public StabilityStatus RecordStep(int tokensUsed, int thinkingTokens, int errors)
    {
        if (IsFrozen)
            return StabilityStatus.Frozen;
        if (IsTripped)
            return StabilityStatus.Tripped;

        // 能量计算使用排除 thinking 的 token 数
        var nonThinkingTokens =
            thinkingTokens > 0 ? Math.Max(0, tokensUsed - thinkingTokens) : tokensUsed;
        var sK = (double)nonThinkingTokens;
        var thetaK = (double)errors;
        var vK = sK + Config.EffectiveLambda * thetaK;

        // 预算检查使用总量（含 thinking），因为 thinking 也消耗上下文窗口
        TotalTokens += tokensUsed;
        TotalThinkingTokens += thinkingTokens;
        TotalErrors += errors;
        TotalSteps++;

        var budget = Config.EffectiveTokenBudget;
        if (budget > 0 && TotalTokens >= budget)
        {
            IsFrozen = true;
            throw new BudgetExhaustedException(
                $"Token budget exhausted: current context {TotalTokens} tokens (ceiling: {budget}). State frozen for recovery. Steps: {TotalSteps}, errors: {TotalErrors}, energy: [{RecentEnergy(5)}]",
                TotalTokens,
                budget
            );
        }

        if (_energyHistory.Count > 0)
        {
            var vPrev = _energyHistory[^1];
            var deltaV = vK - vPrev;

            if (deltaV >= 0.0)
            {
                // ★ 改进：引入相对增长容忍度
                // 小幅递增（ΔV/V_prev ≤ tolerance）属于正常工作流模式，
                // 如"分析→构思→写作"的递增 completion。只有显著递增才计数。
                var relativeDelta = vPrev > 0 ? deltaV / vPrev : deltaV;
                if (relativeDelta > DeltaVTolerance)
                    _consecutiveNonNegative++;
                else
                    _consecutiveNonNegative = 0; // 容忍范围内的递增，重置计数
            }
            else
            {
                _consecutiveNonNegative = 0;
            }
        }

        _energyHistory.Add(vK);

        var window = Config.EffectiveWindow;
        if (_consecutiveNonNegative >= window)
        {
            IsTripped = true;
            throw new StabilityViolationException(
                $"Lyapunov stability violation: ΔV > {DeltaVTolerance:P0} for {_consecutiveNonNegative} consecutive steps (window: {window}). Energy trajectory: [{RecentEnergy(window + 2)}]. Total tokens: {TotalTokens} (thinking: {TotalThinkingTokens}), total errors: {TotalErrors}.",
                _consecutiveNonNegative,
                _energyHistory.Count > 0 ? _energyHistory.Max() : vK
            );
        }

        return _consecutiveNonNegative > 0 ? StabilityStatus.Warning : StabilityStatus.Stable;
    }

    public StabilityStatus ReportTransientFailure() => RecordStep(0, 1);

    public void ReportPermanentFailure(string reason)
    {
        var snapshot = CompileSnapshot(StabilityStatus.Tripped);
        IsTripped = true;
        throw new PermanentFailureException(
            $"Permanent failure: {reason}. Post-mortem: {snapshot}",
            reason,
            snapshot
        );
    }

    public StabilityStatus CheckStability()
    {
        if (IsFrozen)
            return StabilityStatus.Frozen;
        if (IsTripped || _consecutiveNonNegative >= Config.EffectiveWindow)
            return StabilityStatus.Unstable;
        return _consecutiveNonNegative > 0 ? StabilityStatus.Warning : StabilityStatus.Stable;
    }

    public TelemetrySnapshot Snapshot() => CompileSnapshot(CheckStability());

    public IReadOnlyList<double> GetEnergyHistory() => _energyHistory;

    public double? CurrentEnergy() => _energyHistory.Count > 0 ? _energyHistory[^1] : null;

    public int ConsecutiveWarnings() => _consecutiveNonNegative;

    public void Reset()
    {
        _energyHistory.Clear();
        TotalTokens = 0;
        TotalThinkingTokens = 0;
        TotalErrors = 0;
        _consecutiveNonNegative = 0;
        IsTripped = false;
        IsFrozen = false;
        TotalSteps = 0;
    }

    /// <summary>
    /// 解冻监控器（用于 auto-compact 后恢复）。
    /// 重置 Frozen 状态，同时清除能量历史使监控器从零开始重新建立基线。
    /// compact 后能量函数的绝对值不再有参考意义（上下文已变化）。
    /// </summary>
    /// <param name="currentTokens">compact 后的当前 token 总量。≤0 表示不更新 TotalTokens。</param>
    public void Unfreeze(long currentTokens = -1)
    {
        if (!IsFrozen)
            return;
        IsFrozen = false;
        if (currentTokens >= 0)
            TotalTokens = currentTokens;

        // ★ compact 后能量历史过时，重置使监控器重新建立基线
        _energyHistory.Clear();
        _consecutiveNonNegative = 0;
    }

    /// <summary>
    /// 重置 baseline，使监控器重新 warmup。
    /// LyapunovMonitor 的 "baseline" 是能量历史序列，清除后重新积累。
    /// </summary>
    public void ResetBaseline()
    {
        _energyHistory.Clear();
        _consecutiveNonNegative = 0;
    }

    /// <summary>重置升级计数与断路状态（新任务边界调用；保留 TotalTokens 与能量历史）。</summary>
    public void ResetEscalation()
    {
        _consecutiveNonNegative = 0;
        IsTripped = false;
    }

    public void UpdateTokenBudget(long budgetCeiling) => Config.TokenBudget = budgetCeiling;

    public long GetTokenBudget() => Config.EffectiveTokenBudget;

    public void ApplyProfile(ModelContextProfile profile)
    {
        Config.ApplyProfile(profile);
        // DeltaVTolerance 不再需要同步——直接委托到 Config.EffectiveDeltaVTolerance
    }

    public void SeedTokenCount(long tokens)
    {
        if (tokens > 0 && TotalTokens == 0)
            TotalTokens = tokens;
    }

    public void Dispose() { }

    private TelemetrySnapshot CompileSnapshot(StabilityStatus status) =>
        new()
        {
            StepCount = TotalSteps,
            CumulativeTokens = TotalTokens,
            CumulativeErrors = TotalErrors,
            EnergyTrajectory = _energyHistory.ToArray(),
            FinalStatus = status,
            ConsecutiveNonNegative = _consecutiveNonNegative,
        };

    private string RecentEnergy(int n)
    {
        var start = Math.Max(0, _energyHistory.Count - n);
        var count = Math.Min(n, _energyHistory.Count - start);
        return count == 0
            ? ""
            : string.Join(
                ", ",
                _energyHistory.Skip(start).Take(count).Select(e => e.ToString("F1"))
            );
    }
}
