namespace Core.Prose;

/// <summary>
/// AI 分隔符 harness 的输入。正文传完整候选章节，不传 diff；
/// harness 需要像作者一样通读整章，而不是做局部格式判断。
/// </summary>
public sealed record ProseHarnessRequest
{
    public string NovelId { get; init; } = "";
    public string ChapterId { get; init; } = "";
    public int ChapterOrder { get; init; }
    public string ChapterTitle { get; init; } = "";
    public string ChapterSummary { get; init; } = "";
    public string NovelTitle { get; init; } = "";
    public string Genre { get; init; } = "";
    public string Content { get; init; } = "";

    /// <summary>write / update / edit / rewrite，用于给 harness 说明本次写入路径。</summary>
    public string Operation { get; init; } = "write";

    public string PreviousChapterTitle { get; init; } = "";
    public string PreviousChapterSummary { get; init; } = "";
    public string PreviousChapterExcerpt { get; init; } = "";
    public string NextChapterTitle { get; init; } = "";
    public string NextChapterSummary { get; init; } = "";
    public string NextChapterExcerpt { get; init; } = "";
}

public enum ProseHarnessStatus
{
    /// <summary>harness 返回了通过正文保持校验的不同正文。</summary>
    Applied,

    /// <summary>harness 通读后决定不调整，或返回内容与候选正文一致。</summary>
    Unchanged,

    /// <summary>未配置 LLM / 未装配 harness，无法运行。</summary>
    Unavailable,

    /// <summary>harness 调用失败、超时，或输出改动了正文。</summary>
    Failed,
}

public readonly record struct ProseHarnessSummary(int OriginalSeparators, int RevisedSeparators);

/// <summary>
/// harness 结果。Content 始终是最终应保存的正文；
/// Unavailable / Failed 时也必须是候选正文，调用方不做本地密度兜底。
/// </summary>
public sealed record ProseHarnessResult(
    ProseHarnessStatus Status,
    string Content,
    ProseHarnessSummary Summary,
    string? Detail = null
)
{
    /// <summary>追加到工具返回文本里的简短说明。</summary>
    public string ToolNote =>
        Status switch
        {
            ProseHarnessStatus.Applied
                when Summary.OriginalSeparators == Summary.RevisedSeparators =>
                $"；AI 分隔符 harness：已调整，仍保留 {Summary.RevisedSeparators} 处",
            ProseHarnessStatus.Applied =>
                $"；AI 分隔符 harness：原 {Summary.OriginalSeparators} 处 → 现 {Summary.RevisedSeparators} 处",
            ProseHarnessStatus.Unchanged when Summary.RevisedSeparators == 0 =>
                "；AI 分隔符 harness：未使用独立分隔符",
            ProseHarnessStatus.Unchanged =>
                $"；AI 分隔符 harness：保留 {Summary.RevisedSeparators} 处，未调整",
            ProseHarnessStatus.Unavailable => "；AI 分隔符 harness 未运行，按提交正文保存",
            ProseHarnessStatus.Failed => "；AI 分隔符 harness 未完成，按提交正文保存",
            _ => "",
        };

    public static ProseHarnessResult Unchanged(string content, int separators) =>
        new(ProseHarnessStatus.Unchanged, content, new ProseHarnessSummary(separators, separators));

    public static ProseHarnessResult Unavailable(string content, string detail) =>
        new(ProseHarnessStatus.Unavailable, content, new ProseHarnessSummary(0, 0), detail);

    public static ProseHarnessResult Failed(string content, string detail) =>
        new(ProseHarnessStatus.Failed, content, new ProseHarnessSummary(0, 0), detail);
}
