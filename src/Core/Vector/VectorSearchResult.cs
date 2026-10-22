namespace Core.Vector;

/// <summary>
/// 向量搜索结果（VectorSearchResult）。
/// 封装语义搜索返回的单个结果，包含条目信息和相似度分数。
/// </summary>
public sealed record VectorSearchResult
{
    /// <summary>条目唯一标识。</summary>
    public string Id { get; init; } = "";

    /// <summary>条目类型（如 character、chapter、plotline 等）。</summary>
    public string Type { get; init; } = "";

    /// <summary>原始文本内容（用于展示上下文）。</summary>
    public string Text { get; init; } = "";

    /// <summary>相似度分数（范围通常为 [0, 1]，越高越相似）。</summary>
    public double Score { get; init; }
}
