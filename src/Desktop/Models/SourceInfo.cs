namespace Desktop.Models;

/// <summary>信息来源（检索工具引用的章节/片段）。</summary>
public sealed class SourceInfo
{
    /// <summary>标题。</summary>
    public string Title { get; init; } = "";

    /// <summary>URL / 路径。</summary>
    public string Url { get; init; } = "";

    /// <summary>索引序号。</summary>
    public int Index { get; init; }
}
