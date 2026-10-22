namespace Core.Shared;

/// <summary>
/// Thinking level 配置：预算 token 数和 reasoning effort 映射。
/// 统一来源，消除 ConversationLoop 和 TUI 中的硬编码重复。
///
/// 上下文感知：大上下文模型（≥200K）的 budget 按 context window 比例放大，
/// 避免 1M 模型只给 1024 thinking tokens 的过度保守限制。
/// </summary>
public enum ThinkingLevel
{
    Off,
    Minimal,
    Low,
    Medium,
    High,
    XHigh,
}

public static class ThinkingLevelBudgets
{
    /// <summary>根据 thinking level 获取对应的预算 token 数量（基础值，不感知上下文）。</summary>
    public static int GetBudgetTokens(ThinkingLevel level) =>
        level switch
        {
            ThinkingLevel.Off => 0,
            ThinkingLevel.Minimal => 128,
            ThinkingLevel.Low => 512,
            ThinkingLevel.Medium => 1024,
            ThinkingLevel.High => 4096,
            ThinkingLevel.XHigh => 8192,
            _ => 1024,
        };

    /// <summary>
    /// 根据 thinking level 和上下文窗口大小获取预算 token 数量。
    /// 大上下文模型（≥200K）的 budget 按比例放大，上限为上下文窗口的 5%。
    ///
    /// 缩放逻辑：
    /// - contextWindow < 100K：使用基础值（小模型不需要大 thinking budget）
    /// - contextWindow ≥ 100K：按 contextWindow/100K 比例放大，上限 5%
    /// - XHigh 在 1M 模型上：8192 × 10 = 81920，但 cap 在 1M × 5% = 50000
    /// </summary>
    public static int GetBudgetTokens(ThinkingLevel level, long contextWindow)
    {
        if (level == ThinkingLevel.Off)
            return 0;

        var baseBudget = GetBudgetTokens(level);

        if (contextWindow < 100_000)
            return baseBudget;

        // 大上下文缩放：按 contextWindow/100K 比例放大
        var scale = (double)contextWindow / 100_000;
        var scaled = (int)(baseBudget * scale);

        // 上限为上下文窗口的 5%（给 prompt 和 completion 留足够空间）
        var cap = (int)(contextWindow * 0.05);

        return Math.Min(scaled, cap);
    }

    /// <summary>根据 thinking level 获取对应的 LLM reasoning effort 值。</summary>
    public static string GetReasoningEffort(ThinkingLevel level) =>
        level switch
        {
            ThinkingLevel.Minimal or ThinkingLevel.Low => "low",
            ThinkingLevel.Medium => "medium",
            ThinkingLevel.High or ThinkingLevel.XHigh => "high",
            _ => "medium",
        };

    /// <summary>将字符串解析为 ThinkingLevel（用于配置/CLI 输入）。</summary>
    public static ThinkingLevel FromString(string? value) =>
        value?.ToLowerInvariant() switch
        {
            "off" => ThinkingLevel.Off,
            "minimal" => ThinkingLevel.Minimal,
            "low" => ThinkingLevel.Low,
            "high" => ThinkingLevel.High,
            "xhigh" => ThinkingLevel.XHigh,
            _ => ThinkingLevel.Medium,
        };

    /// <summary>
    /// 所有可选的 thinking level（排除 Off），用于 TUI 选择器。
    /// 返回 (key, label) 对，key 是配置值，label 是显示文本。
    /// </summary>
    public static readonly (string Key, string Label)[] Options =
    [
        ("minimal", "minimal  (128)"),
        ("low", "low      (512)"),
        ("medium", "medium   (1024)"),
        ("high", "high     (4096)"),
        ("xhigh", "xhigh    (8192)"),
    ];
}
