namespace Core.Shared;

/// <summary>
/// 单次替换操作的结构化差异数据。
/// 存储原始旧文本和新文本 — 编辑的最小真相。
/// TUI 渲染层按需从此数据生成可视化差异。
/// </summary>
public sealed record EditHunk
{
    /// <summary>原始文本中的起始行号（从 1 开始）。</summary>
    public int OldStartLine { get; init; }

    /// <summary>被替换的原始文本。</summary>
    public string OldText { get; init; } = "";

    /// <summary>替换后的新文本。</summary>
    public string NewText { get; init; } = "";

    /// <summary>
    /// 从旧/新完整内容创建单个 EditHunk。
    /// 用于 WriteFileTool / WriteChapterTool 等替换整个文件的场景。
    /// 存储完整内容 — TUI 渲染层会应用上下文折叠以保持视觉输出紧凑。
    /// </summary>
    /// <param name="oldText">原始完整内容。</param>
    /// <param name="newText">替换后的完整内容。</param>
    /// <returns>包含完整内容差异的 EditHunk 实例。</returns>
    public static EditHunk FromFullContent(string oldText, string newText)
    {
        return new EditHunk
        {
            OldStartLine = 1,
            OldText = oldText,
            NewText = newText,
        };
    }
}
