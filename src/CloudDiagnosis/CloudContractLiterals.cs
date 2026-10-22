namespace CloudDiagnosis;

/// <summary>
/// 云端契约字面量（HARNESS-CLOUD §4/Q1/Q3/§15.1）——集中定义跨文件共享的封闭集合取值，
/// 避免散落魔法字符串。取值变更须与云端契约（云端契约输出）同步。
/// </summary>
internal static class CloudContractLiterals
{
    /// <summary>问题严重度 high：基础档反馈升级与规则匹配的关键档（signal.severity）。</summary>
    public const string SeverityHigh = "high";

    /// <summary>问题严重度 medium（severity 归一化目标值之一）。</summary>
    public const string SeverityMedium = "medium";

    /// <summary>问题严重度 low（severity 归一化的缺省目标值）。</summary>
    public const string SeverityLow = "low";

    /// <summary>基础档深度档位（capability 请求 Depth）。</summary>
    public const string DepthQuick = "quick";

    /// <summary>深度档深度档位（story.check 升档启用 RST Layer B）。</summary>
    public const string DepthStandard = "standard";

    /// <summary>repair-intent.action = choose_focus（Q1 三态之一；A-8 规则消费 focus-areas）。</summary>
    public const string RepairChooseFocus = "choose_focus";

    /// <summary>
    /// repair-intent.action = insufficient_data（层 1 采样门槛：目标章实质字符不足/退化输入）。
    /// 不产出评分、不触发人工裁决；客户端注入一行跳过说明，不落入 PASS 文案
    /// （依据 CHANGE_06 §6.3 InsufficientEvidence；未定态不得报成低分）。
    /// </summary>
    public const string RepairInsufficientData = "insufficient_data";

    /// <summary>features.trends[].direction = declining（§15.1 趋势信号）。</summary>
    public const string TrendDeclining = "declining";

    /// <summary>features.trends[].direction = rising（§15.1 趋势信号）。</summary>
    public const string TrendRising = "rising";
}
