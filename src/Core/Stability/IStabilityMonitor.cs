namespace Core.Stability;

/// <summary>
/// 稳定性监控器接口。Core 层定义此抽象，
/// StabilityHarness 项目提供具体实现（LyapunovMonitor、GrowthRatioGuard）。
/// </summary>
public interface IStabilityMonitor : IDisposable
{
    /// <summary>记录一次执行步骤，返回当前稳定性状态。</summary>
    /// <param name="tokensUsed">本次步骤消耗的 Token 数量。</param>
    /// <param name="errors">本次步骤产生的错误数量，默认为 0。</param>
    /// <returns>当前稳定性状态。</returns>
    StabilityStatus RecordStep(int tokensUsed, int errors = 0);

    /// <summary>
    /// 记录一次执行步骤（thinking-aware），返回当前稳定性状态。
    /// 在 ratio/baseline 计算中排除 thinking tokens，避免 extended thinking 模型的 baseline 被 thinking 深度波动污染。
    /// 不分离 thinking tokens 时，监控器无法区分"模型正常 thinking 4K tokens"和"失控消耗 4K tokens"。
    /// </summary>
    /// <param name="tokensUsed">本次步骤消耗的 Token 总量（含 thinking）。</param>
    /// <param name="thinkingTokens">本次步骤消耗的 thinking tokens（含在 tokensUsed 中）。0 表示无 thinking 或未知。</param>
    /// <param name="errors">本次步骤产生的错误数量，默认为 0。</param>
    /// <returns>当前稳定性状态。</returns>
    StabilityStatus RecordStep(int tokensUsed, int thinkingTokens, int errors = 0);

    /// <summary>检查当前稳定性状态。</summary>
    /// <returns>当前稳定性状态。</returns>
    StabilityStatus CheckStability();

    /// <summary>重置监控器状态。</summary>
    void Reset();

    /// <summary>
    /// 解冻监控器（用于 auto-compact 后恢复）。
    /// 重置 Frozen 状态，保留能量历史和基线信息（compact 后这些仍有参考价值）。
    /// Token 总量由调用方在 compact 后调整。
    /// </summary>
    /// <param name="currentTokens">compact 后的当前 token 总量。≤0 表示不更新 TotalTokens。</param>
    void Unfreeze(long currentTokens = -1);

    /// <summary>更新 token 预算上限。用于在用户切换模型/上下文窗口大小时同步 budget。</summary>
    void UpdateTokenBudget(long budgetCeiling);

    /// <summary>
    /// 应用 ModelContextProfile 到所有动态阈值。
    /// 使 BudgetGate、RatioThreshold、Window、WarmupTurns、Lambda 等随上下文窗口和 thinking 级别自适应。
    /// </summary>
    void ApplyProfile(ModelContextProfile profile);

    /// <summary>
    /// 设置初始 token 计数（用于会话恢复场景）。
    /// 在 RestoreFromMessages 后调用，使 pre-flight 检查能基于历史消息的 token 占用
    /// 提前检测上下文溢出，而非等到首次 LLM 调用返回 400 错误。
    /// 不影响 Frozen/Tripped 状态，不触发 baseline 计算。
    /// </summary>
    /// <param name="tokens">历史消息的估算 token 占用。</param>
    void SeedTokenCount(long tokens);

    /// <summary>获取当前 token 预算上限。返回 0 表示无限制。</summary>
    long GetTokenBudget();

    /// <summary>
    /// 当前上下文占用估算（seed + 每轮增量，≈ 最近一次调用的 PromptTokens）。
    /// 调用方应传入每轮增量而非全量 prompt，否则全量会被重复累计导致虚高。
    /// </summary>
    long TotalTokens { get; }

    /// <summary>累计错误总数。</summary>
    long TotalErrors { get; }

    /// <summary>累计执行步骤数。</summary>
    int TotalSteps { get; }

    /// <summary>是否已触发断路（Tripped）。</summary>
    bool IsTripped { get; }

    /// <summary>是否已冻结（Frozen）。</summary>
    bool IsFrozen { get; }

    /// <summary>当前是否处于稳定状态。</summary>
    bool IsStable { get; }

    /// <summary>能量历史记录，用于分析稳定性趋势。</summary>
    IReadOnlyList<double> EnergyHistory { get; }
}

/// <summary>
/// 扩展的稳定性监控器，支持增长比率检测。
/// </summary>
public interface IGrowthRatioGuard : IStabilityMonitor
{
    /// <summary>基线值，用于计算增长比率。</summary>
    double? Baseline { get; }

    /// <summary>当前增长比率。</summary>
    double? CurrentRatio { get; }

    /// <summary>连续升级（Escalating）的次数。</summary>
    int ConsecutiveEscalating { get; }

    /// <summary>重置升级计数与断路状态（新任务边界调用；保留 token 总量、baseline 与能量历史）。</summary>
    void ResetEscalation();

    /// <summary>
    /// 重置 baseline 和自适应阈值，使监控器重新进入 warmup 阶段。
    /// 用于 auto-compact 后上下文显著变化、单轮 token 消耗模式改变的场景。
    /// compact 后旧的 baseline 不再代表当前消耗水平，ratio 计算会持续偏低
    /// （compact 后 prompt 更短，单轮消耗更少），导致监控器变"盲"。
    /// 重置后监控器会重新收集 warmup 轮次的数据建立新 baseline。
    /// </summary>
    void ResetBaseline();
}
