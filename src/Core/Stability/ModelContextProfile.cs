namespace Core.Stability;

/// <summary>
/// 模型上下文能力运行时 profile — 稳定器动态阈值的第一公民。
/// 在模型切换时构建并注入所有稳定器组件，使阈值随上下文窗口和 thinking 级别自适应。
///
/// 设计原则：
/// - 所有绝对值阈值通过此 profile 比例化，不再硬编码
/// - thinking budget 作为已知量参与计算，避免 baseline 被污染
/// - 上下文窗口从 8K 到 1M 的 128× 差异通过比例值自然消化
/// </summary>
public sealed record ModelContextProfile
{
    /// <summary>上下文窗口大小（tokens），来自 ConfigStore.ContextWindowSize。</summary>
    public long ContextWindowTokens { get; init; }

    /// <summary>当前 thinking 级别的预算 tokens，来自 ThinkingLevelBudgets。</summary>
    public int ThinkingBudgetTokens { get; init; }

    /// <summary>
    /// 是否为 adaptive-only thinking 模型（如 Claude Opus 4.7+, Claude 5）。
    /// 这些模型不接受 manual budget_tokens，thinking 深度由模型自主决定。
    /// 影响 RatioThreshold 和 DeltaVTolerance 的计算方式。
    /// </summary>
    public bool IsAdaptiveThinking { get; init; }

    /// <summary>
    /// 估算的单轮典型 token 消耗。
    /// 使用锚点间线性插值而非分段常量，消除边界跳变。
    /// 锚点基于实际模型观测数据：
    /// - 8K 模型（如 GPT-3.5）：单轮 500-1K token
    /// - 32K 模型（如 GPT-4-32K）：单轮 1-2K token
    /// - 128K 模型（如 Claude 3.5 Sonnet）：单轮 2-4K token
    /// - 200K 模型（如 Gemini 2.5 Pro）：单轮 3-5K token
    /// - 1M 模型（如 Gemini 2.5 Pro 1M）：单轮 4-8K token
    ///
    /// Adaptive-only thinking 模型额外 ×1.5：thinking 深度不可预测，
    /// 单轮可能额外消耗 3-10K thinking tokens。
    /// </summary>
    public long TypicalTurnTokens =>
        (long)(InterpolateTypicalTurn(ContextWindowTokens) * (IsAdaptiveThinking ? 1.5 : 1.0));

    /// <summary>
    /// 锚点间线性插值计算 TypicalTurnTokens。
    /// 锚点：(8K, 800), (32K, 1200), (128K, 3000), (200K, 4000), (1M, 5000)。
    /// 低于最小锚点使用最小值，高于最大锚点使用最大值。
    /// </summary>
    private static double InterpolateTypicalTurn(long contextWindow)
    {
        // 锚点表：(上下文窗口, 典型单轮消耗)
        // 按上下文窗口升序排列
        var anchors = new (long Window, double TurnTokens)[]
        {
            (8_000, 800),
            (32_000, 1_200),
            (128_000, 3_000),
            (200_000, 4_000),
            (1_000_000, 5_000),
        };

        // 低于最小锚点
        if (contextWindow <= anchors[0].Window)
            return anchors[0].TurnTokens;

        // 高于最大锚点
        if (contextWindow >= anchors[^1].Window)
            return anchors[^1].TurnTokens;

        // 在锚点间线性插值
        for (var i = 1; i < anchors.Length; i++)
        {
            if (contextWindow <= anchors[i].Window)
            {
                var (w0, t0) = anchors[i - 1];
                var (w1, t1) = anchors[i];
                var ratio = (double)(contextWindow - w0) / (w1 - w0);
                return t0 + ratio * (t1 - t0);
            }
        }

        return anchors[^1].TurnTokens;
    }

    /// <summary>是否启用了 extended thinking（BudgetTokens > 0 或 adaptive-only thinking）。</summary>
    /// <remarks>
    /// Adaptive-only thinking 模型（Opus 4.7+, Claude 5）不接受 manual budget_tokens，
    /// ThinkingBudgetTokens 不可预测，但 thinking 仍然生效，因此 HasThinking 应为 true。
    /// </remarks>
    public bool HasThinking => ThinkingBudgetTokens > 0 || IsAdaptiveThinking;

    // ── 比例化阈值计算 ──

    /// <summary>
    /// BudgetGate: GrowthRatioGuard 的"门控"阈值，累计 token 低于此值时不检查 ratio。
    /// 按上下文窗口比例计算，但下限为 TypicalTurnTokens × WarmupTurns，
    /// 确保小模型也有足够的 warmup 保护（至少经过 ScaledWarmupTurns 轮后才检查 ratio）。
    /// 1M → 50K (5%), 128K → 6.4K (5%), 8K → 2.4K (TypicalTurnTokens×3, 约 30%)。
    /// </summary>
    public long ScaledBudgetGate =>
        Math.Max(ContextWindowTokens * 5 / 100, TypicalTurnTokens * ScaledWarmupTurns);

    /// <summary>
    /// MaxRetained: RGDecimator 的消息保留上限。
    /// 按上下文窗口可容纳的典型轮次计算，上限 100。
    /// 1M → ~100, 128K → ~50, 8K → ~10。
    /// </summary>
    public int ScaledMaxRetained =>
        Math.Clamp((int)(ContextWindowTokens / TypicalTurnTokens), 10, 100);

    /// <summary>
    /// RGThreshold: 消息保留的信息密度阈值。
    /// 大上下文窗口可以保留更多低密度消息（更宽松），小窗口需要更激进裁剪。
    /// 1M → 0.2, 128K → 0.3, 8K → 0.5。
    /// </summary>
    public float ScaledRGThreshold =>
        ContextWindowTokens switch
        {
            >= 500_000 => 0.20f,
            >= 200_000 => 0.25f,
            >= 100_000 => 0.30f,
            >= 50_000 => 0.35f,
            >= 20_000 => 0.40f,
            _ => 0.50f,
        };

    /// <summary>
    /// RatioThreshold: GrowthRatioGuard 的增长比率阈值。
    /// - 无 thinking：保持 2.0
    /// - 有 manual thinking：模型自主决定 thinking 深度导致 ratio 正常波动 2-3×，
    ///   阈值按 thinkingBudget/2000 放大（上限 5.0）
    /// - Adaptive-only thinking（Opus 4.7+, Claude 5）：thinking 深度不可预测，
    ///   ThinkingBudgetTokens 无意义，使用固定值 3.5
    /// </summary>
    public double ScaledRatioThreshold =>
        IsAdaptiveThinking ? 3.5
        : HasThinking ? Math.Clamp(2.0 + ThinkingBudgetTokens / 2000.0, 2.0, 5.0)
        : 2.0;

    /// <summary>
    /// Window: 连续 ΔV≥0 多少轮触发断路。
    /// 大上下文模型的正常多轮规划可能连续 3-5 轮 token 递增，窗口需更大。
    /// 小上下文模型应更快检测失控。
    /// 1M → 5, 128K → 4, 8K → 3。
    /// </summary>
    public int ScaledWindow =>
        ContextWindowTokens switch
        {
            >= 500_000 => 5,
            >= 100_000 => 4,
            _ => 3,
        };

    /// <summary>
    /// WarmupTurns: baseline 计算所需的 warmup 轮数。
    /// 有 thinking 时需要更多轮建立稳定 baseline（thinking 深度波动大）。
    /// 无 thinking 时 3 轮足够；有 thinking 时 5 轮。
    /// </summary>
    public int ScaledWarmupTurns => HasThinking ? 5 : 3;

    /// <summary>
    /// Lambda: 能量函数 V = sK + λ·θK 中的耦合常数。
    /// 1 error 不应等价于 1 token（大 token 量级下失衡），
    /// 应等价于单轮典型消耗的 1%（即 1 error ≈ 1% TypicalTurnTokens）。
    /// 1M → ~330, 128K → ~40, 8K → ~3。
    /// </summary>
    public double ScaledLambda => Math.Clamp(TypicalTurnTokens / 100.0, 1.0, 1000.0);

    /// <summary>
    /// DeltaVTolerance: LyapunovMonitor 的 ΔV 相对增长容忍度。
    /// 当 ΔV/V_prev 在此比例以内时，不视为"能量递增"。
    /// 大模型单轮绝对递增量大（3-5K token），10% 容忍度过紧易误触发；
    /// 小模型单轮递增量小，10% 合理。
    /// 1M → 0.20, 128K → 0.15, 8K → 0.10。
    /// Adaptive-only thinking 模型额外宽松 0.05（thinking 深度不可预测）。
    /// </summary>
    public double ScaledDeltaVTolerance =>
        ContextWindowTokens switch
        {
            >= 500_000 => 0.20,
            >= 100_000 => 0.15,
            _ => 0.10,
        } + (IsAdaptiveThinking ? 0.05 : 0.0);

    /// <summary>
    /// KeepRecent: 上下文压缩时保留的最近消息条数。
    /// 小模型保留少（每条占比较大），大模型保留多（占比小但有更多上下文需求）。
    /// 1M → 40, 128K → 20, 8K → 8。
    /// </summary>
    public int ScaledKeepRecent =>
        ContextWindowTokens switch
        {
            >= 500_000 => 40,
            >= 200_000 => 30,
            >= 100_000 => 20,
            >= 50_000 => 15,
            >= 20_000 => 10,
            _ => 8,
        };

    /// <summary>
    /// Compact 阈值：上下文 token 使用达到预算的多少百分比时触发主动压缩。
    /// 复用 Pre-flight 阈值的计算逻辑，确保 compact 和 pre-flight 使用一致的百分比。
    /// 1M → 99.25%, 128K → 96.5%, 8K → 85%。
    /// 给当轮 LLM 调用和工具结果留余量（1.5 × TypicalTurnTokens）。
    /// </summary>
    public double CompactThresholdPercent => PreflightThresholdPercent;

    /// <summary>
    /// Pre-flight 阈值：token 使用达到预算的多少百分比时提前触发 compact。
    /// 需要给当轮 LLM 调用留余量。余量 = 1.5 × TypicalTurnTokens。
    /// 1M → 99.25%, 128K → 96.5%, 8K → 85%。
    /// </summary>
    public double PreflightThresholdPercent =>
        ContextWindowTokens > 0 ? 1.0 - TypicalTurnTokens * 1.5 / ContextWindowTokens : 0.85;

    /// <summary>
    /// Nudge 分级提醒阈值（P0）：从 PreflightThresholdPercent 向下推导，确保提醒先于 pre-flight 拦截触发。
    /// 修复：原先硬编码 70%/85%/92% 与 pre-flight 动态阈值层级矛盾——
    /// 8K 模型 pre-flight=85%，92% 提醒在其拦截后永远不可达（死代码）；
    /// 1M 模型 pre-flight=99.25%，92% 提醒远早于上限、过度打扰。
    /// 三级：info（建议压缩）→ warn（收尾后建议）→ critical（立即建议 /compact），
    /// 分别 = preflight − 15% / − 8% / − 3%，并 clamp 到合理区间。
    /// 不变量：NudgeInfo < NudgeWarn < NudgeCritical < PreflightThresholdPercent。
    /// </summary>
    public double NudgeInfoPercent => Math.Clamp(PreflightThresholdPercent - 0.15, 0.50, 0.90);

    public double NudgeWarnPercent => Math.Clamp(PreflightThresholdPercent - 0.08, 0.60, 0.95);

    public double NudgeCriticalPercent => Math.Clamp(PreflightThresholdPercent - 0.03, 0.70, 0.99);

    /// <summary>
    /// 接续换页的「建议换页」阈值（usage 比例）：到这一档就提醒模型「可以开新页」。
    /// 同样从 PreflightThresholdPercent 向下推导——绝对 token 阈值在不同窗口的模型上没有可比性
    /// （8K 窗口的 20000 token 永远不可达；1M 窗口的 20000 token 一开页就触发）。
    /// 1M ≈ 79%, 128K ≈ 76.5%, 8K ≈ 65%。
    /// 不变量：RollAdvicePercent &lt; AutoRollPercent &lt; PreflightThresholdPercent。
    /// </summary>
    public double RollAdvicePercent => Math.Clamp(PreflightThresholdPercent - 0.20, 0.50, 0.85);

    /// <summary>
    /// 接续换页的「自动换页」阈值（usage 比例）：循环自己触发换页，先于 pre-flight 溢出。
    /// 与建议阈值留 15% 间隔，让模型有机会在更自然的接缝上先自己换页。
    /// 1M ≈ 94%, 128K ≈ 91.5%, 8K ≈ 80%。
    /// </summary>
    public double AutoRollPercent => Math.Clamp(PreflightThresholdPercent - 0.05, 0.60, 0.95);

    /// <summary>
    /// VSA 漂移检查频率：每 N 轮检查一次上下文漂移。
    /// 大上下文模型对话更长，检查频率可以稍低（每 8 轮）。
    /// 小上下文模型对话更短，需要更频繁检查（每 3 轮）。
    /// </summary>
    public int ScaledDriftCheckInterval =>
        ContextWindowTokens switch
        {
            >= 500_000 => 8,
            >= 100_000 => 5,
            _ => 3,
        };

    /// <summary>
    /// VSA 上下文采样窗口：取最近 N 条 assistant 消息作为当前上下文。
    /// 大上下文模型输出更长，需要更多消息代表对话方向。
    /// 1M → 6, 128K → 4, 8K → 3。
    /// </summary>
    public int ScaledDriftContextWindow =>
        ContextWindowTokens switch
        {
            >= 500_000 => 6,
            >= 100_000 => 4,
            _ => 3,
        };

    /// <summary>
    /// VSA 漂移检测阈值（cosine similarity）。
    /// 基于 VSA 维度的统计阈值：3σ 置信度 = k / √Dimensionality。
    /// 10K 维 → 0.03, 5K 维 → 0.042, 20K 维 → 0.021。
    /// drift 低于此值表示当前上下文与系统提示词正交（漂移）。
    /// </summary>
    public double ScaledDriftThreshold =>
        VsaDimensionality > 0 ? 3.0 / Math.Sqrt(VsaDimensionality) : 0.03;

    /// <summary>
    /// VSA 向量维度（从 HolographicEngine 传入）。
    /// 默认 10,000，用于计算统计阈值。
    /// </summary>
    public int VsaDimensionality { get; init; } = 10_000;

    /// <summary>
    /// CircuitBreaker 的工具调用历史容量（环形缓冲区大小）。
    /// 大上下文模型对话更长，需要更多历史记录来检测重复模式。
    /// 1M → 30, 128K → 20, 8K → 10。
    /// </summary>
    public int ScaledCallHistoryCapacity =>
        ContextWindowTokens switch
        {
            >= 500_000 => 30,
            >= 100_000 => 20,
            _ => 10,
        };

    /// <summary>
    /// AdaptiveK: GrowthRatioGuard 自适应阈值的缩放系数。
    /// 大模型 baseline 更稳定（IQR 小），K 应更小避免阈值过度放大；
    /// 小模型 baseline 波动大，K 应更大以适应正常波动。
    /// 1M → 1.5, 128K → 2.0, 8K → 2.5。
    /// </summary>
    public double ScaledAdaptiveK =>
        ContextWindowTokens switch
        {
            >= 500_000 => 1.5,
            >= 100_000 => 2.0,
            _ => 2.5,
        };

    /// <summary>
    /// CircuitBreaker 精确重复调用阈值：连续相同调用多少次触发断路。
    /// 大上下文模型合理批量操作更多，需更大阈值避免误触发。
    /// 8K → 3, 128K → 4, 1M → 5。
    /// </summary>
    public int ScaledCircuitBreakerThreshold =>
        ContextWindowTokens switch
        {
            >= 500_000 => 5,
            >= 100_000 => 4,
            _ => 3,
        };

    /// <summary>
    /// Steer 消息每 turn 最大投递条数。
    /// 大上下文模型可承受更多 steer 消息的上下文开销。
    /// 1M → 5, 128K → 3, 8K → 2。
    /// </summary>
    public int ScaledMaxSteerPerTurn =>
        ContextWindowTokens switch
        {
            >= 500_000 => 5,
            >= 100_000 => 3,
            _ => 2,
        };

    /// <summary>
    /// 工具结果截断上限（P1）：超长工具结果头尾截断的最大字符数。
    /// 修复：原先硬编码 10,000 字符——8K 模型一个结果即占 ~31% 窗口（过大），
    /// 1M 模型又过度保守、丢失本可保留的细节。
    /// 按上下文窗口缩放，约取窗口 1/4 token 对应的字符量（CJK ≈ 1 char/token 的上界）。
    /// 8K→8K, 20K→12K, 50K→15K, 100K→20K, 200K→40K, 1M→60K。
    /// </summary>
    public int ScaledToolResultMaxChars =>
        ContextWindowTokens switch
        {
            >= 500_000 => 60_000,
            >= 200_000 => 40_000,
            >= 100_000 => 20_000,
            >= 50_000 => 15_000,
            >= 20_000 => 12_000,
            _ => 8_000,
        };
}
