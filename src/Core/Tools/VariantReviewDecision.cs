namespace Core.Tools;

/// <summary>用户在变体审阅中选择的操作（采用/继续修改/忽略）。</summary>
public enum VariantReviewAction
{
    /// <summary>采用此变体（写入章节或加入对话）。</summary>
    Apply,

    /// <summary>基于此变体继续修改。</summary>
    Refine,

    /// <summary>忽略此变体组。</summary>
    Dismiss,
}

/// <summary>
/// 变体审阅决策（Core 唯一契约）。
/// UI 只提交 Action/VariantId；内容与标签由 Core 从 <see cref="ExploreDetails"/> 解析，
/// 避免过期窗口把不同版本的正文写回。
/// </summary>
public sealed record VariantReviewDecision
{
    public VariantReviewAction Action { get; init; }

    public string? VariantId { get; init; }

    public string? VariantLabel { get; init; }

    public string? VariantContent { get; init; }
}
