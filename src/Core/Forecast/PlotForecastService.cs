using System.Text.RegularExpressions;
using Core.Cloud;
using Core.Data;
using Core.Vector;

namespace Core.Forecast;

/// <summary>
/// 远期推演服务：组装 <see cref="PlotForecast"/>，并复用同一套混合检索内核
/// （FTS/BM25 + 向量 + RRF + 本地判定重排）取前文证据与笔记线索。
///
/// 全部输入来自既有数据源（结构索引 / 情节线 / 出场记录 / 笔记），不新增持久化。
/// </summary>
public sealed partial class PlotForecastService(
    INovelStore store,
    RetrievalKernel? kernel = null,
    IFictionHarnessClient? harness = null
)
{
    [GeneratedRegex(@"第\s*(\d+)\s*章")]
    private static partial Regex ChapterOrderPattern();

    /// <summary>构建推演（结构索引读章头，绝不 GetChapters 拉全书正文）。</summary>
    public async Task<PlotForecast?> BuildAsync(
        string novelId,
        int window = PlotForecastBuilder.DefaultWindow,
        CancellationToken ct = default
    )
    {
        var structure = await store.GetStructure(novelId, ct);
        if (structure is null)
            return null;

        var plotlines = await store.GetPlotlines(novelId, ct);
        var characters = await store.GetCharacters(novelId, ct);
        var appearances = await store.GetAppearances(novelId, ct);

        return PlotForecastBuilder.Build(
            structure.Novel,
            structure.Chapters,
            structure.Volumes,
            plotlines,
            characters,
            appearances,
            window
        );
    }

    /// <summary>
    /// 前文证据：本地混合检索（只检索已写章节）+ 可选云端联想（quick 档，仅召回）。
    /// 本地优先，同章最多 2 条，总数不超过 <paramref name="topK"/>。
    /// </summary>
    public async Task<IReadOnlyList<ForecastEvidence>> RecallAsync(
        string novelId,
        PlotForecast forecast,
        int topK = 6,
        bool includeCloud = true,
        CancellationToken ct = default
    )
    {
        topK = Math.Clamp(topK, 1, 30);
        var evidence = new List<ForecastEvidence>();
        var perChapter = new Dictionary<int, int>();

        if (kernel is not null && forecast.CurrentOrder > 0 && forecast.QueryText.Length > 0)
        {
            try
            {
                var request = new RetrievalSearchRequest
                {
                    Domain = RetrievalDomain.NovelKnowledge,
                    OwnerId = "local",
                    NovelId = novelId,
                    DocumentKinds =
                    [
                        DocumentKind.ChapterSummary,
                        DocumentKind.ChapterBodyChunk,
                        DocumentKind.SceneDefinition,
                        DocumentKind.PlotlineProfile,
                    ],
                    // 硬保证：只检索已写章节（未写章节的细纲不能当「前文证据」）
                    ChapterOrderRange = (1, forecast.CurrentOrder),
                    Query = forecast.QueryText,
                    RankingProfile = RankingProfile.WriteContext,
                    DenseTopK = 20,
                    LexicalTopK = 20,
                    FinalTopK = Math.Clamp(topK * 3, topK, 30),
                };
                var hits = await kernel.SearchAsync(request, ct);
                foreach (var hit in hits)
                {
                    if (evidence.Count >= topK)
                        break;
                    if (hit.ChapterOrder is not { } order)
                        continue;
                    if (perChapter.GetValueOrDefault(order) >= 2)
                        continue; // 同章最多 2 条
                    perChapter[order] = perChapter.GetValueOrDefault(order) + 1;
                    evidence.Add(
                        new ForecastEvidence(
                            "local",
                            hit.ChapterId,
                            order,
                            hit.ChapterTitle,
                            hit.DocumentKind,
                            hit.Text,
                            null,
                            null,
                            hit.Score
                        )
                    );
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 检索是增强：失败不阻断推演
            }
        }

        if (!includeCloud || harness is null || evidence.Count >= topK)
            return evidence;

        try
        {
            var key = $"forecast-{novelId}-{QueryKey(forecast.QueryText)}";
            var result = await AssociativeLinkClient.LinkAsync(
                harness,
                novelId,
                [],
                forecast.QueryText,
                key,
                depth: "quick",
                ct
            );
            if (
                result?.Hits is not { Count: > 0 } hits
                || !string.Equals(result.Status, "pass", StringComparison.OrdinalIgnoreCase)
            )
            {
                return evidence;
            }

            var usedChapters = evidence
                .Where(e => e.ChapterOrder is not null)
                .Select(e => e.ChapterOrder!.Value)
                .ToHashSet();
            foreach (var hit in hits)
            {
                if (evidence.Count >= topK)
                    break;
                if (hit.SourceChapterOrder is { } order)
                {
                    if (order > forecast.CurrentOrder)
                        continue; // 未来章不算前文证据
                    if (!usedChapters.Add(order))
                        continue;
                }
                var text = string.IsNullOrWhiteSpace(hit.Bridge)
                    ? hit.Concept
                    : $"{hit.Concept}（{hit.Bridge}）";
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                evidence.Add(
                    new ForecastEvidence(
                        "cloud",
                        null,
                        hit.SourceChapterOrder,
                        null,
                        null,
                        text,
                        hit.Family,
                        hit.Channel,
                        hit.Cosine
                    )
                );
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 云端不可用 → 只用本地证据
        }

        return evidence;
    }

    /// <summary>
    /// 笔记线索：硬锚点（正文里的「第N章」落在窗口内）→ 已写章锚点（最近更新 1 条，供「是否已落地」）
    /// → 混合检索（near = 近期查询，far = 远期查询；RRF/判定重排由内核统一完成）。
    /// 只有 <see cref="NoteStatus.Active"/> 参与；收件箱候选只报数量。
    /// </summary>
    public async Task<NoteSelection> SelectNotesAsync(
        string novelId,
        PlotForecast forecast,
        int topK = 4,
        CancellationToken ct = default
    )
    {
        topK = Math.Clamp(topK, 1, 20);
        var all = await store.GetNotes(novelId, ct);
        var inboxCount = all.Count(n => n.Status == NoteStatus.Inbox);
        var active = all.Where(n => n.Status == NoteStatus.Active).ToList();
        if (active.Count == 0)
            return new NoteSelection([], inboxCount);

        var selected = new List<ForecastNote>();
        var selectedIds = new HashSet<string>(StringComparer.Ordinal);
        var windowEnd = forecast.TargetOrder + Math.Max(1, forecast.WindowSize) - 1;

        // 1) 硬锚点：正文点名了窗口内的章
        var anchors = active
            .Select(n => (Note: n, Order: ParseChapterOrder(n.Body)))
            .Where(x => x.Order is { } order && order >= forecast.TargetOrder && order <= windowEnd)
            .OrderBy(x => x.Order)
            .ThenBy(x => x.Note.Title, StringComparer.Ordinal);
        foreach (var (note, order) in anchors)
        {
            if (selected.Count >= topK)
                break;
            if (!selectedIds.Add(note.Id))
                continue;
            selected.Add(ToForecastNote(note, order, "anchor"));
        }

        // 2) 已写章锚点：最近更新的一条（「这条灵感是不是已经用掉了」）
        if (selected.Count < topK && forecast.CurrentOrder > 0)
        {
            var past = active
                .Where(n => !selectedIds.Contains(n.Id))
                .Select(n => (Note: n, Order: ParseChapterOrder(n.Body)))
                .Where(x => x.Order is { } order && order <= forecast.CurrentOrder)
                .OrderByDescending(x => x.Note.UpdatedAt)
                .FirstOrDefault();
            if (past.Note is not null && selectedIds.Add(past.Note.Id))
                selected.Add(ToForecastNote(past.Note, past.Order, "past"));
        }

        // 3) 混合检索：near（近期）先于 far（远期）；内核负责 RRF + 判定重排
        if (kernel is not null && selected.Count < topK)
        {
            foreach (
                var (query, source) in new[]
                {
                    (forecast.QueryText, "near"),
                    (forecast.LongRangeQueryText, "far"),
                }
            )
            {
                if (selected.Count >= topK)
                    break;
                if (string.IsNullOrWhiteSpace(query))
                    continue;
                try
                {
                    var hits = await kernel.SearchAsync(
                        new RetrievalSearchRequest
                        {
                            Domain = RetrievalDomain.NovelKnowledge,
                            OwnerId = "local",
                            NovelId = novelId,
                            DocumentKinds = [DocumentKind.AuthorNote],
                            EntityStatuses = [NoteStatus.Active.ToString()],
                            Query = query,
                            RankingProfile = RankingProfile.WriteContext,
                            DenseTopK = 20,
                            LexicalTopK = 20,
                            FinalTopK = topK * 2,
                        },
                        ct
                    );
                    foreach (var hit in hits)
                    {
                        if (selected.Count >= topK)
                            break;
                        if (hit.SourceEntityId is not { Length: > 0 } noteId)
                            continue;
                        if (!selectedIds.Add(noteId))
                            continue;
                        var note = active.FirstOrDefault(n => n.Id == noteId);
                        if (note is null)
                            continue;
                        selected.Add(ToForecastNote(note, ParseChapterOrder(note.Body), source));
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // 检索失败 → 保留已选锚点
                }
            }
        }

        return new NoteSelection(selected, inboxCount);
    }

    private static ForecastNote ToForecastNote(AuthorNote note, int? anchorOrder, string source) =>
        new(note.Id, note.Title, Excerpt(note.Body, 80), anchorOrder, source);

    private static int? ParseChapterOrder(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        var match = ChapterOrderPattern().Match(body);
        return match.Success && int.TryParse(match.Groups[1].Value, out var order) ? order : null;
    }

    /// <summary>摘要：空白归一后截断（换行会打断注入块的行结构）。</summary>
    private static string Excerpt(string body, int max)
    {
        var flat = string.Join(
            ' ',
            (body ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        );
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    private static string QueryKey(string text) =>
        Convert
            .ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(text)
                )
            )[..8]
            .ToLowerInvariant();
}
