using Core.Stability;

namespace StabilityHarness.VSA;

/// <summary>
/// VSA 全息引擎配置。
/// 漂移检测阈值通过 ModelContextProfile 动态化。
/// </summary>
public sealed record VSAConfig
{
    public int Dimensionality { get; init; } = 10_000;
    public int MaxInvariants { get; init; } = 20;
    public int WarnInvariants { get; init; } = 5;

    /// <summary>
    /// 漂移检测阈值（cosine similarity）。
    /// 默认 null 表示使用 ModelContextProfile.ScaledDriftThreshold 计算。
    /// 如果手动设置，则覆盖自动计算值。
    ///
    /// 10K 维 VSA 空间中随机 bipolar 向量的 cosine similarity 期望≈0，标准差≈1/√D。
    /// 3σ 置信度：3 / √10000 = 0.03。低于此值表示当前上下文与不变量正交（漂移）。
    /// 之前硬编码的 0.3 几乎不可达（≈30σ），漂移检测从未真正生效。
    /// </summary>
    public double? DriftThreshold { get; set; }

    /// <summary>
    /// 漂移检查频率：每 N 轮检查一次上下文漂移。
    /// 默认 null 表示使用 ModelContextProfile.ScaledDriftCheckInterval 计算。
    /// </summary>
    public int? DriftCheckInterval { get; set; }

    /// <summary>
    /// 上下文采样窗口：取最近 N 条 assistant 消息作为当前上下文。
    /// 默认 null 表示使用 ModelContextProfile.ScaledDriftContextWindow 计算。
    /// </summary>
    public int? DriftContextWindow { get; set; }

    /// <summary>
    /// 从 ModelContextProfile 应用动态阈值。
    /// 只有手动未设置的参数才会从 profile 计算。
    /// </summary>
    public void ApplyProfile(ModelContextProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        DriftThreshold ??= profile.ScaledDriftThreshold;
        DriftCheckInterval ??= profile.ScaledDriftCheckInterval;
        DriftContextWindow ??= profile.ScaledDriftContextWindow;
    }

    /// <summary>获取有效的漂移阈值（手动设置优先，否则默认 0.03）。</summary>
    public double EffectiveDriftThreshold => DriftThreshold ?? 0.03;

    /// <summary>获取有效的检查频率（手动设置优先，否则默认 5）。</summary>
    public int EffectiveDriftCheckInterval => DriftCheckInterval ?? 5;

    /// <summary>获取有效的上下文采样窗口（手动设置优先，否则默认 3）。</summary>
    public int EffectiveDriftContextWindow => DriftContextWindow ?? 3;
}
