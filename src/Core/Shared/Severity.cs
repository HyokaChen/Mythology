namespace Core.Shared;

/// <summary>
/// 严重度常量 — 替代散布全代码的 "high"/"medium"/"low" 硬编码。
/// 值使用中文以保持与 AI 上下文的可读性一致。
/// </summary>
public static class Severity
{
    /// <summary>严重 — 必须修复</summary>
    public const string High = "严重";

    /// <summary>中等 — 建议修复</summary>
    public const string Medium = "中等";

    /// <summary>轻微 — 可选优化</summary>
    public const string Low = "轻微";

    /// <summary>
    /// 严重度权重 — 用于加权评分。
    /// </summary>
    public static int Weight(string s) =>
        s switch
        {
            High => 8,
            Medium => 4,
            Low => 2,
            _ => 1,
        };

    /// <summary>
    /// 语境降级：严重→中等，中等→轻微。
    /// 用于对话引号内/角色台词中等语境豁免场景。
    /// </summary>
    public static string Demote(string s) =>
        s switch
        {
            High => Medium,
            _ => Low,
        };
}
