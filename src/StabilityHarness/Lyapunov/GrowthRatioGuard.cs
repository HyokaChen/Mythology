using Core.Stability;

namespace StabilityHarness.Lyapunov;

/// <summary>
/// 增长比率监控器：warmup → baseline → ratio 检测 → 断路。
/// 所有阈值通过 StabilityConfig.ApplyProfile(ModelContextProfile) 动态化。
/// Thinking-aware：在 ratio/baseline 计算中排除 thinking tokens，
/// 避免 extended thinking 模型的 baseline 被 thinking 深度波动污染。
/// </summary>
public sealed class GrowthRatioGuard(StabilityConfig config) : IGrowthRatioGuard
{
    private readonly List<int> _turnTokens = [];
    private readonly List<int> _turnThinkingTokens = [];
    private readonly List<double> _energyHistory = [];
    private bool _tripped;
    private double? _baseline;

    /// <summary>
    /// 自适应阈值：由 IQR 计算得出，优先于 Config.RatioThreshold。
    /// null 表示尚未计算（warmup 期间或 Adaptive 关闭）。
    /// </summary>
    private double? _adaptiveThreshold;

    /// <summary>累计 thinking tokens（含在 TotalTokens 中）。</summary>
    public long TotalThinkingTokens { get; private set; }

    /// <summary>
    /// 当前上下文占用估算（seed + 每轮增量，≈ 最近一次调用的 PromptTokens）。
    /// RecordStep 的 tokensUsed 应为本轮增量（上下文增长 + 输出），
    /// 而非含全部历史的 PromptTokens —— 全量重复累计会使预算检查虚高。
    /// </summary>
    public long TotalTokens { get; private set; }

    public long TotalErrors { get; private set; }

    public int TotalSteps { get; private set; }

    public bool IsTripped => _tripped || ConsecutiveEscalating >= Config.EffectiveWindow;
    public bool IsFrozen { get; private set; }

    public bool IsStable => CheckStability() is StabilityStatus.Stable or StabilityStatus.Warning;
    public bool WarmupComplete => _baseline.HasValue;
    public IReadOnlyList<double> EnergyHistory => _energyHistory;
    public double? Baseline => _baseline;
    public double? CurrentRatio
    {
        get
        {
            if (_baseline is > 0 && _turnTokens.Count > 0)
                return _turnTokens[^1] / _baseline.Value;
            return null;
        }
    }
    public int ConsecutiveEscalating { get; private set; }

    /// <summary>实际使用的 RatioThreshold：自适应阈值优先，否则用配置值。</summary>
    private double EffectiveThreshold => _adaptiveThreshold ?? Config.EffectiveRatioThreshold;

    public StabilityConfig Config { get; } =
        config ?? throw new ArgumentNullException(nameof(config));

    public StabilityStatus RecordStep(int tokensUsed, int errors = 0) =>
        RecordStep(tokensUsed, thinkingTokens: 0, errors: errors);

    /// <summary>
    /// 记录一次执行步骤（thinking-aware）。
    /// 在 ratio/baseline 计算中使用排除 thinking 的 token 数，
    /// 因为 thinking 深度由模型自主决定，不应计入"失控增长"的判定。
    /// TokenBudget 检查仍使用总量（含 thinking），因为 thinking tokens 同样消耗上下文窗口。
    /// </summary>
    // errors 不给默认值：与两参重载 (tokensUsed, errors = 0) 叠加会让
    // RecordStep(x, y) 这类位置实参调用产生 CS0121 二义性
    public StabilityStatus RecordStep(int tokensUsed, int thinkingTokens, int errors)
    {
        if (IsFrozen)
            return StabilityStatus.Frozen;
        if (_tripped)
            return StabilityStatus.Tripped;

        // ratio/baseline 计算使用排除 thinking 的 token 数
        var nonThinkingTokens =
            thinkingTokens > 0 ? Math.Max(0, tokensUsed - thinkingTokens) : tokensUsed;
        _turnTokens.Add(nonThinkingTokens);
        _turnThinkingTokens.Add(thinkingTokens);

        // 但预算检查使用总量（含 thinking），因为 thinking 也消耗上下文窗口
        TotalTokens += tokensUsed;
        TotalThinkingTokens += thinkingTokens;
        TotalErrors += errors;
        TotalSteps++;

        var budget = Config.EffectiveTokenBudget;
        if (budget > 0 && TotalTokens >= budget)
        {
            IsFrozen = true;
            throw new BudgetExhaustedException(
                $"Token budget exhausted: current context {TotalTokens} tokens (ceiling: {budget})",
                TotalTokens,
                budget
            );
        }

        // Compute baseline after warmup（基于 non-thinking tokens）
        if (!_baseline.HasValue)
        {
            if (_turnTokens.Count == Config.EffectiveWarmupTurns)
            {
                var sorted = _turnTokens.OrderBy(x => x).ToList();
                _baseline = sorted[sorted.Count / 2];

                // ★ Adaptive 阈值调整（之前是 dead code，现在接通）
                if (Config.Adaptive && _turnTokens.Count >= 4)
                {
                    var q1 = sorted[sorted.Count / 4];
                    var q3 = sorted[sorted.Count * 3 / 4];
                    var iqr = q3 - q1;
                    if (_baseline > 0 && iqr > 0)
                    {
                        var adaptiveTau = 1.0 + Config.EffectiveAdaptiveK * (iqr / _baseline.Value);
                        adaptiveTau = Math.Max(1.5, Math.Min(5.0, adaptiveTau));
                        _adaptiveThreshold = adaptiveTau;
                    }
                }
                // Warmup 不足 4 轮时无法计算 IQR，_adaptiveThreshold 保持 null，
                // EffectiveThreshold 回退到 Config.EffectiveRatioThreshold（已由 ApplyProfile 设置）
            }

            _energyHistory.Add(_baseline is > 0 ? nonThinkingTokens / _baseline.Value : 1.0);
            return StabilityStatus.Stable;
        }

        if (_turnTokens.Count <= Config.EffectiveWarmupTurns)
        {
            _energyHistory.Add(_baseline > 0 ? nonThinkingTokens / _baseline.Value : 1.0);
            return StabilityStatus.Stable;
        }

        // Compute growth ratio（基于 non-thinking tokens）
        var ratio = _baseline > 0 ? nonThinkingTokens / _baseline.Value : 1.0;
        _energyHistory.Add(ratio);

        // ★ 使用 EffectiveThreshold（自适应优先，配置兜底）
        if (ratio > EffectiveThreshold && TotalTokens >= Config.EffectiveBudgetGate)
            ConsecutiveEscalating++;
        else
            ConsecutiveEscalating = 0;

        if (ConsecutiveEscalating >= Config.EffectiveWindow)
        {
            _tripped = true;
            throw new StabilityViolationException(
                $"Growth ratio violation: {ratio:F1}× baseline for {ConsecutiveEscalating} consecutive turns (threshold: {EffectiveThreshold:F1}×, window: {Config.EffectiveWindow}). Baseline: {_baseline:F0} tokens/turn. Cumulative: {TotalTokens} tokens (thinking: {TotalThinkingTokens}).",
                ConsecutiveEscalating,
                ratio
            );
        }

        return ConsecutiveEscalating > 0 ? StabilityStatus.Warning : StabilityStatus.Stable;
    }

    public StabilityStatus CheckStability()
    {
        if (IsFrozen)
            return StabilityStatus.Frozen;
        if (_tripped || ConsecutiveEscalating >= Config.EffectiveWindow)
            return StabilityStatus.Tripped;
        return ConsecutiveEscalating > 0 ? StabilityStatus.Warning : StabilityStatus.Stable;
    }

    /// <summary>
    /// 重置升级计数与断路状态（新任务边界调用；保留 TotalTokens、baseline 与能量历史）。
    /// </summary>
    public void ResetEscalation()
    {
        ConsecutiveEscalating = 0;
        _tripped = false;
    }

    /// <summary>
    /// 重置 baseline 和自适应阈值，使监控器重新进入 warmup 阶段。
    /// 用于 auto-compact 后上下文显著变化、单轮 token 消耗模式改变的场景。
    /// compact 后旧的 baseline 不再代表当前消耗水平，ratio 计算会持续偏低
    /// （compact 后 prompt 更短，单轮消耗更少），导致监控器变"盲"。
    /// 重置后监控器会重新收集 warmup 轮次的数据建立新 baseline。
    /// 保留 TotalTokens/TotalErrors/TotalSteps（累计统计不受 compact 影响）。
    /// </summary>
    public void ResetBaseline()
    {
        _turnTokens.Clear();
        _turnThinkingTokens.Clear();
        // 保留 _energyHistory（趋势仍有参考价值，且外部可能正在读取）
        _baseline = null;
        _adaptiveThreshold = null;
        ConsecutiveEscalating = 0;
    }

    public void Reset()
    {
        _turnTokens.Clear();
        _turnThinkingTokens.Clear();
        _energyHistory.Clear();
        TotalTokens = 0;
        TotalThinkingTokens = 0;
        TotalErrors = 0;
        ConsecutiveEscalating = 0;
        _tripped = false;
        IsFrozen = false;
        TotalSteps = 0;
        _baseline = null;
        _adaptiveThreshold = null;
    }

    /// <summary>
    /// 解冻监控器（用于 auto-compact 后恢复）。
    /// 重置 Frozen 状态，同时重置 baseline 使监控器重新 warmup。
    /// compact 后单轮 token 消耗模式改变，旧 baseline 不再适用——
    /// ratio = compact后消耗 / compact前baseline 持续 < 1.0，监控器变"盲"。
    /// </summary>
    public void Unfreeze(long currentTokens = -1)
    {
        if (!IsFrozen)
            return;
        IsFrozen = false;
        if (currentTokens >= 0)
            TotalTokens = currentTokens;

        // ★ compact 后 baseline 过时，重置使监控器重新 warmup
        ResetBaseline();
    }

    public void UpdateTokenBudget(long budgetCeiling) => Config.TokenBudget = budgetCeiling;

    public long GetTokenBudget() => Config.EffectiveTokenBudget;

    public void ApplyProfile(ModelContextProfile profile) => Config.ApplyProfile(profile);

    public void SeedTokenCount(long tokens)
    {
        if (tokens > 0 && TotalTokens == 0)
            TotalTokens = tokens;
    }

    public void Dispose() { }
}
