using Core.Data;

namespace Core.Prose;

/// <summary>
/// 三条写入路径共用的 harness 入口：加载邻章上下文、调用 harness、
/// 并在返回内容落盘前再做一次正文保持校验。
///
/// 这里没有分隔符数量、密度、间距、首尾或位置规则；harness 不可用时按候选正文保存。
/// </summary>
public static class ProseHarnessRunner
{
    public static async Task<ProseHarnessResult> ReviewAsync(
        IProseHarness? harness,
        INovelStore store,
        string novelId,
        string chapterId,
        int chapterOrder,
        string chapterTitle,
        string chapterSummary,
        string content,
        string operation,
        CancellationToken ct
    )
    {
        if (harness is null)
            return ProseHarnessResult.Unavailable(content, "未装配 AI 分隔符 harness");

        ProseHarnessRequest request;
        try
        {
            request = await ProseHarnessContextBuilder.BuildAsync(
                store,
                novelId,
                chapterId,
                chapterOrder,
                chapterTitle,
                chapterSummary,
                content,
                operation,
                ct
            );
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ProseHarnessResult.Failed(content, "加载章节上下文超时");
        }
        catch (Exception ex)
        {
            return ProseHarnessResult.Failed(content, "加载章节上下文失败：" + ex.Message);
        }

        ProseHarnessResult result;
        try
        {
            result = await harness.ReviewAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ProseHarnessResult.Failed(content, "AI 分隔符 harness 超时");
        }
        catch (Exception ex)
        {
            return ProseHarnessResult.Failed(content, "AI 分隔符 harness 调用失败：" + ex.Message);
        }

        if (result.Content is null)
            return ProseHarnessResult.Failed(content, "AI 分隔符 harness 未返回正文");

        if (result.Status is not (ProseHarnessStatus.Applied or ProseHarnessStatus.Unchanged))
        {
            return result with { Content = content, Summary = default };
        }

        if (
            !ProseHarnessValidator.TryValidate(
                content,
                result.Content,
                out var validated,
                out var summary,
                out var error
            )
        )
        {
            return ProseHarnessResult.Failed(content, error ?? "AI 分隔符 harness 输出改动了正文");
        }

        return string.Equals(validated, content, StringComparison.Ordinal)
            ? ProseHarnessResult.Unchanged(content, summary.RevisedSeparators)
            : result with
            {
                Status = ProseHarnessStatus.Applied,
                Content = validated,
                Summary = summary,
            };
    }
}

internal static class ProseHarnessContextBuilder
{
    private const int NeighborExcerptChars = 1600;

    public static async Task<ProseHarnessRequest> BuildAsync(
        INovelStore store,
        string novelId,
        string chapterId,
        int chapterOrder,
        string chapterTitle,
        string chapterSummary,
        string content,
        string operation,
        CancellationToken ct
    )
    {
        var novel = await store.GetNovel(novelId, ct);
        var chapters = await store.GetChapters(novelId, ct);

        var previous = chapters
            .Where(c => c.Order < chapterOrder && !string.IsNullOrWhiteSpace(c.Content))
            .OrderByDescending(c => c.Order)
            .FirstOrDefault();
        var next = chapters
            .Where(c => c.Order > chapterOrder && !string.IsNullOrWhiteSpace(c.Content))
            .OrderBy(c => c.Order)
            .FirstOrDefault();

        return new ProseHarnessRequest
        {
            NovelId = novelId,
            ChapterId = chapterId,
            ChapterOrder = chapterOrder,
            ChapterTitle = chapterTitle,
            ChapterSummary = chapterSummary,
            NovelTitle = novel?.Title ?? "",
            Genre = novel?.Genre ?? "",
            Content = content,
            Operation = operation,
            PreviousChapterTitle = previous?.Title ?? "",
            PreviousChapterSummary = previous?.Summary ?? "",
            PreviousChapterExcerpt = Tail(previous?.Content, NeighborExcerptChars),
            NextChapterTitle = next?.Title ?? "",
            NextChapterSummary = next?.Summary ?? "",
            NextChapterExcerpt = Head(next?.Content, NeighborExcerptChars),
        };
    }

    private static string Head(string? text, int max) =>
        string.IsNullOrEmpty(text) ? ""
        : text.Length <= max ? text
        : text[..max];

    private static string Tail(string? text, int max) =>
        string.IsNullOrEmpty(text) ? ""
        : text.Length <= max ? text
        : text[^max..];
}
