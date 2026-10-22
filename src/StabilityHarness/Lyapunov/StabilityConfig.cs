using Core.Stability;

namespace StabilityHarness.Lyapunov;

/// <summary>
/// Lyapunov 稳定性体系的核心配置。
/// 所有阈值通过 ModelContextProfile 比例化，不再硬编码绝对值。
/// 上下文窗口从 8K 到 1M 的 128× 差异通过比例值自然消化。
///
/// 覆盖语义：与 VSAConfig 一致，手动设置优先于 ApplyProfile 计算值。
/// 如果用户手动设置了某个属性，模型切换时不会覆盖它（??= 语义）。
/// 未手动设置的属性在 ApplyProfile 时从 ModelContextProfile 计算。
/// </summary>
public sealed record StabilityConfig
{
    /// <summary>
    /// Token 预算上限（上下文窗口大小）。
    /// 默认 null（未设置），ApplyProfile 后为实际窗口大小。
    /// GrowthRatioGuard/LyapunovMonitor 中 TokenBudget > 0 才检查预算。
    /// null 视为 0（无预算限制）。
    /// </summary>
    public long? TokenBudget { get; set; }

    /// <summary>
    /// 增长比率阈值。默认 null（未设置），ApplyProfile 后按 thinking 级别动态调整：
    /// 无 thinking → 2.0，有 manual thinking → 2.0-5.0，adaptive → 3.5。
    /// null 视为 2.0（无 thinking 场景的合理默认值）。
    /// </summary>
    public double? RatioThreshold { get; set; }

    /// <summary>
    /// 连续 ΔV≥0 多少轮触发断路。默认 null（未设置）。
    /// ApplyProfile 后按上下文窗口动态调整：8K→3, 128K→4, 1M→5。
    /// null 视为 3（最小合理值）。
    /// </summary>
    public int? Window { get; set; }

    /// <summary>
    /// Baseline 计算所需的 warmup 轮数。默认 null（未设置）。
    /// ApplyProfile 后按 thinking 级别调整：无 thinking→3, 有 thinking→5。
    /// null 视为 3（无 thinking 场景的最小值）。
    /// </summary>
    public int? WarmupTurns { get; set; }

    /// <summary>
    /// BudgetGate: GrowthRatioGuard 的"门控"阈值，累计 token 低于此值时不检查 ratio。
    /// 默认 null（未设置），ApplyProfile 后按上下文窗口动态计算。
    /// null 视为 0（跳过门控，所有 step 都检查 ratio）。
    /// </summary>
    public long? BudgetGate { get; set; }

    /// <summary>
    /// 能量函数 V = sK + λ·θK 中的耦合常数。
    /// 默认 null（未设置），ApplyProfile 后按 TypicalTurnTokens/100 计算。
    /// null 视为 1.0（1 error = 1 token，仅对小模型合理）。
    /// </summary>
    public double? Lambda { get; set; }

    /// <summary>
    /// 是否启用自适应阈值调整（基于 IQR 自动调整 RatioThreshold）。
    /// 默认 true（之前为 false + dead code，现在已接通）。
    /// </summary>
    public bool Adaptive { get; init; } = true;

    /// <summary>
    /// 自适应调整的缩放系数 K。
    /// adaptiveTau = 1.0 + K × (IQR / baseline)，钳制在 [1.5, 5.0]。
    /// 默认 null（未设置），ApplyProfile 后按上下文窗口动态计算。
    /// 大模型 baseline 更稳定（IQR 小），K 应更小避免阈值过度放大；
    /// 小模型 baseline 波动大，K 应更大以适应正常波动。
    /// 1M → 1.5, 128K → 2.0, 8K → 2.5。
    /// null 时使用 EffectiveAdaptiveK 的计算值。
    /// </summary>
    public double? AdaptiveK { get; init; }

    /// <summary>获取有效的自适应缩放系数 K。手动设置优先，否则 ApplyProfile 计算值，最后 2.0。</summary>
    public double EffectiveAdaptiveK => AdaptiveK ?? _computedAdaptiveK ?? 2.0;

    /// <summary>ApplyProfile 计算的 AdaptiveK 兜底值。</summary>
    private double? _computedAdaptiveK;

    /// <summary>
    /// DeltaVTolerance: LyapunovMonitor 的 ΔV 相对增长容忍度。
    /// 默认 null（未设置），ApplyProfile 后按上下文窗口动态调整。
    /// null 视为 0.10（10%，小模型的合理值）。
    /// </summary>
    public double? DeltaVTolerance { get; set; }

    // ── 有效值访问器（null 时返回安全默认值） ──

    /// <summary>获取有效的 TokenBudget。null 视为 0（无预算限制）。</summary>
    public long EffectiveTokenBudget => TokenBudget ?? 0;

    /// <summary>获取有效的 RatioThreshold。null 视为 2.0。</summary>
    public double EffectiveRatioThreshold => RatioThreshold ?? 2.0;

    /// <summary>获取有效的 Window。null 视为 3。</summary>
    public int EffectiveWindow => Window ?? 3;

    /// <summary>获取有效的 WarmupTurns。null 视为 3。</summary>
    public int EffectiveWarmupTurns => WarmupTurns ?? 3;

    /// <summary>获取有效的 BudgetGate。null 视为 0（跳过门控）。</summary>
    public long EffectiveBudgetGate => BudgetGate ?? 0;

    /// <summary>获取有效的 Lambda。null 视为 1.0。</summary>
    public double EffectiveLambda => Lambda ?? 1.0;

    /// <summary>获取有效的 DeltaVTolerance。null 视为 0.10。</summary>
    public double EffectiveDeltaVTolerance => DeltaVTolerance ?? 0.10;

    /// <summary>
    /// 从 ModelContextProfile 应用所有动态阈值。
    /// 在模型切换时调用，使所有参数随上下文窗口和 thinking 级别自适应。
    /// 使用 ??= 语义：手动设置的值不会被覆盖。
    /// </summary>
    /// <param name="profile">模型上下文能力运行时 profile。</param>
    public void ApplyProfile(ModelContextProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        TokenBudget ??= profile.ContextWindowTokens;
        BudgetGate ??= profile.ScaledBudgetGate;
        RatioThreshold ??= profile.ScaledRatioThreshold;
        Window ??= profile.ScaledWindow;
        WarmupTurns ??= profile.ScaledWarmupTurns;
        Lambda ??= profile.ScaledLambda;
        DeltaVTolerance ??= profile.ScaledDeltaVTolerance;
        _computedAdaptiveK ??= profile.ScaledAdaptiveK;
    }
}
