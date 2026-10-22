using Core.Data;

namespace Core.Novels;

/// <summary>命中位置：正文 / 细纲 / 标题。</summary>
public enum ChapterSearchField
{
    /// <summary>章节正文。</summary>
    Content,

    /// <summary>章节细纲（梗概）。</summary>
    Summary,

    /// <summary>章节标题。</summary>
    Title,
}

/// <summary>一条章节命中（含上下文片段，供列表直接展示）。</summary>
public readonly record struct ChapterSearchHit(
    Chapter Chapter,
    ChapterSearchField Field,
    /// <summary>命中片段（两侧上下文，已折叠空白）。</summary>
    string Snippet,
    /// <summary>该章命中次数（正文 + 细纲 + 标题）。</summary>
    int MatchCount
);

/// <summary>
/// 全书只读全文检索（纯函数，可单测）：跨章节按大小写不敏感子串找内容。
/// 定位是「我忘了这个设定在哪章提过」——不依赖 embedding，离线可用、结果确定；
/// 语义检索（RetrievalKernel）是另一条通道，不在这里混。
/// </summary>
public static class NovelTextSearch
{
    /// <summary>默认命中上限（列表不适合铺几百条）。</summary>
    public const int DefaultMaxHits = 30;

    /// <summary>片段左右各取多少字符。</summary>
    public const int SnippetContextChars = 40;

    /// <summary>
    /// 搜索。<paramref name="chapters"/> 需已按序号升序；每条章节最多产出一条命中
    /// （优先正文 → 细纲 → 标题），列表按「命中次数降序 → 章节序号升序」排序。
    /// </summary>
    public static List<ChapterSearchHit> Search(
        IReadOnlyList<Chapter> chapters,
        string? query,
        int maxHits = DefaultMaxHits
    )
    {
        if (string.IsNullOrWhiteSpace(query) || chapters.Count == 0)
            return [];

        var keyword = query.Trim();
        var limit = maxHits <= 0 ? DefaultMaxHits : maxHits;
        var hits = new List<ChapterSearchHit>();

        foreach (var chapter in chapters)
        {
            var contentCount = CountOccurrences(chapter.Content, keyword);
            var summaryCount = CountOccurrences(chapter.Summary, keyword);
            var titleCount = CountOccurrences(chapter.Title, keyword);
            var total = contentCount + summaryCount + titleCount;
            if (total == 0)
                continue;

            var (field, snippet) =
                contentCount > 0
                    ? (ChapterSearchField.Content, BuildSnippet(chapter.Content, keyword))
                : summaryCount > 0
                    ? (ChapterSearchField.Summary, BuildSnippet(chapter.Summary, keyword))
                : (ChapterSearchField.Title, chapter.Title);

            hits.Add(new ChapterSearchHit(chapter, field, snippet, total));
        }

        return hits.OrderByDescending(h => h.MatchCount)
            .ThenBy(h => h.Chapter.Order)
            .Take(limit)
            .ToList();
    }

    /// <summary>字段显示名（列表行用）。</summary>
    public static string FieldLabel(ChapterSearchField field) =>
        field switch
        {
            ChapterSearchField.Content => "正文",
            ChapterSearchField.Summary => "细纲",
            _ => "标题",
        };

    /// <summary>大小写不敏感出现次数。</summary>
    private static int CountOccurrences(string? text, string keyword)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(keyword, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += keyword.Length;
        }
        return count;
    }

    /// <summary>
    /// 命中片段：取首次命中的左右各 <see cref="SnippetContextChars"/> 字符，折叠换行为空格，
    /// 两端按需加省略号（列表行保持单行）。
    /// </summary>
    private static string BuildSnippet(string text, string keyword)
    {
        var index = text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return "";

        var start = Math.Max(0, index - SnippetContextChars);
        var end = Math.Min(text.Length, index + keyword.Length + SnippetContextChars);
        var snippet = text[start..end].ReplaceLineEndings(" ").Trim();
        if (start > 0)
            snippet = "…" + snippet;
        if (end < text.Length)
            snippet += "…";
        return snippet;
    }
}
