using Core.Stability;

namespace StabilityHarness.RGDecimator;

/// <summary>
/// 消息冗余裁剪器配置。
/// MaxRetained 和 Threshold 通过 ModelContextProfile 动态化，
/// 不再硬编码绝对值。
/// </summary>
public sealed record RGConfig
{
    /// <summary>
    /// 信息密度保留阈值（0~1）。低于此分数的消息可能被裁剪。
    /// 默认 null（未设置），ApplyProfile 后按上下文窗口动态调整。
    /// 大上下文 → 0.2（更宽松），小上下文 → 0.5（更激进）。
    /// </summary>
    public float? Threshold { get; set; }

    /// <summary>
    /// 消息保留上限。默认 null（未设置），ApplyProfile 后按上下文窗口缩放。
    /// 1M → ~100, 128K → ~50, 8K → ~10。
    /// </summary>
    public int? MaxRetained { get; set; }

    /// <summary>
    /// 嵌入式冗余检测的 lookback 窗口：与最近 N 条消息比较相似度。
    /// 默认 null（未设置），ApplyProfile 后按上下文窗口动态调整。
    /// 1M → 20, 128K → 10, 8K → 5。
    /// </summary>
    public int? LookbackWindow { get; set; }

    public float KeywordWeight { get; init; } = 0.35f;
    public float InfoWeight { get; init; } = 0.30f;
    public float NoveltyWeight { get; init; } = 0.35f;

    /// <summary>
    /// 结构关键词列表。用于消息评分中的 keyword density 计算。
    /// 包含英文技术关键词和中文写作关键词。
    /// </summary>
    public IReadOnlyList<string> StructuralKeywords { get; init; } = KeywordsSet.DefaultKeywords;

    /// <summary>
    /// 从 ModelContextProfile 应用动态阈值。
    /// 使用 ??= 语义：手动设置的值不会被覆盖。
    /// </summary>
    public void ApplyProfile(ModelContextProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        MaxRetained ??= profile.ScaledMaxRetained;
        Threshold ??= profile.ScaledRGThreshold;
        LookbackWindow ??= profile.ScaledDriftContextWindow * 3; // 漂移窗口的 3 倍作为冗余检测窗口
    }

    /// <summary>获取有效的保留阈值。null 时返回 0.3。</summary>
    public float EffectiveThreshold => Threshold ?? 0.3f;

    /// <summary>获取有效的最大保留数。null 时返回 50。</summary>
    public int EffectiveMaxRetained => MaxRetained ?? 50;

    /// <summary>获取有效的 lookback 窗口。null 时返回 10。</summary>
    public int EffectiveLookbackWindow => LookbackWindow ?? 10;
}

public static class KeywordsSet
{
    /// <summary>
    /// 默认关键词列表：英文技术关键词 + 中文写作关键词。
    /// 中文关键词覆盖小说写作场景（角色、情节、伏笔等）。
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultKeywords =
    [
        // 英文技术关键词（Agent/工具调用场景）
        "execute",
        "create",
        "update",
        "delete",
        "insert",
        "query",
        "deploy",
        "migrate",
        "validate",
        "transform",
        "invoke",
        "call",
        "error",
        "failed",
        "exception",
        "timeout",
        "retry",
        "rejected",
        "invalid",
        "violation",
        "denied",
        "abort",
        "crashed",
        "tool_call",
        "function_call",
        "api_response",
        "result",
        "observation",
        "action",
        "thought",
        "plan",
        "response",
        "completed",
        "started",
        "pending",
        "approved",
        "blocked",
        "resolved",
        "committed",
        "rolled_back",
        "cancelled",
        // 中文写作关键词（小说创作场景）
        "角色",
        "人物",
        "情节",
        "伏笔",
        "悬念",
        "冲突",
        "高潮",
        "转折",
        "结局",
        "开头",
        "对话",
        "叙述",
        "描写",
        "心理",
        "氛围",
        "节奏",
        "主题",
        "线索",
        "铺垫",
        "回收",
        "视角",
        "人设",
        "世界观",
        "大纲",
        "章节",
        "场景",
        "动机",
        "情感",
        "弧线",
        "成长",
    ];
}
