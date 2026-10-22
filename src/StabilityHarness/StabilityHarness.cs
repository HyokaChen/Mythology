using Core.Stability;
using StabilityHarness.Diagnostics;
using StabilityHarness.Lyapunov;
using StabilityHarness.RGDecimator;
using StabilityHarness.VSA;

namespace StabilityHarness;

public static class StabilityHarness
{
    public static StabilityConfig DefaultConfig { get; set; } = new();
    public static RGConfig DefaultRGConfig { get; set; } = new();
    public static VSAConfig DefaultVSAConfig { get; set; } = new();

    /// <summary>创建复合稳定性监控器（GrowthRatioGuard + LyapunovMonitor 聚合）。</summary>
    /// <remarks>
    /// 两者检测不同维度的不稳定：
    /// - GrowthRatioGuard：单轮 token 消耗相对基线的增长比率
    /// - LyapunovMonitor：能量轨迹的持续增长趋势
    /// 任一子监控器触发断路/冻结，复合监控器即触发。
    /// </remarks>
    public static IGrowthRatioGuard CreateGuard(StabilityConfig? config = null) =>
        new CompositeStabilityMonitor(config ?? DefaultConfig);

    /// <summary>
    /// 创建仅 GrowthRatioGuard（不含 LyapunovMonitor）。
    /// 用于需要轻量级监控或与旧版行为兼容的场景。
    /// </summary>
    public static IGrowthRatioGuard CreateGuardOnly(StabilityConfig? config = null) =>
        new GrowthRatioGuard(config ?? DefaultConfig);

    /// <summary>
    /// 创建 RGDecimator（消息冗余裁剪器）。
    ///  返回 IRGDecimator，同时也可当作 Core.Stability.IMessageScorer 使用
    /// （RGDecimator 实现了两个接口）。
    /// </summary>
    public static IRGDecimator CreateDecimator(RGConfig? config = null) =>
        new RGDecimator.RGDecimator(config ?? DefaultRGConfig);

    /// <summary>
    /// 创建 HolographicEngine（VSA 全息引擎）。
    /// 返回 VSA.IHolographicEngine（继承自 Core.Stability.IHolographicEngine），
    /// 可直接传给 Agent.HolographicEngine 使用。
    /// </summary>
    public static VSA.IHolographicEngine CreateHolographicEngine(VSAConfig? config = null) =>
        new HolographicEngine(config ?? DefaultVSAConfig);

    /// <summary>
    /// 生成失败诊断报告（含模式分类、置信度、成本估算、修复建议）。
    /// </summary>
    public static FailureReport Analyze(
        IGrowthRatioGuard guard,
        double[]? driftHistory = null,
        string? modelName = null
    ) => FailureReport.FromGuard(guard, driftHistory, modelName);
}
