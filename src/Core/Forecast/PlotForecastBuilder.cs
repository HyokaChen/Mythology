using System.Text;
using Core.Cloud;
using Core.Data;

namespace Core.Forecast;

/// <summary>
/// 大纲驱动的远期推演（纯计算）：把总纲/卷纲/细纲、情节线的计划揭示章与转折点、出场记录
/// 折算成「下一章 + 未来 N 章」的到期义务、计划转折、播种缺口与风险。
///
/// 只用 <see cref="Chapter.CharCount"/> 判「是否已写」，不读正文——结构索引就够了。
/// </summary>
public static class PlotForecastBuilder
{
    /// <summary>默认前瞻窗口章数。</summary>
    public const int DefaultWindow = 5;

    /// <summary>窗口上限。</summary>
    public const int MaxWindow = 10;

    /// <summary>计划转折最多条数。</summary>
    public const int MaxBeats = 8;

    /// <summary>义务最多条数。</summary>
    public const int MaxObligations = 8;

    /// <summary>播种缺口最多条数。</summary>
    public const int MaxSeedGaps = 6;

    /// <summary>风险最多条数。</summary>
    public const int MaxRisks = 6;

    /// <summary>查询文本字符上限。</summary>
    public const int MaxQueryChars = 800;

    public static PlotForecast Build(
        Novel novel,
        IReadOnlyList<Chapter> chapters,
        IReadOnlyList<Volume> volumes,
        IReadOnlyList<Plotline> plotlines,
        IReadOnlyList<Character> characters,
        IReadOnlyList<AppearanceRecord> appearances,
        int window = DefaultWindow
    )
    {
        window = Math.Clamp(window, 1, MaxWindow);

        var written = chapters.Where(c => c.CharCount > 0).ToList();
        var current = written.Count > 0 ? written.Max(c => c.Order) : 0;
        var writtenIds = written.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var writtenOrders = written.Select(c => c.Order).ToHashSet();

        var target = chapters.Where(c => c.Order > current).OrderBy(c => c.Order).FirstOrDefault();
        var targetOrder = target?.Order ?? current + 1;

        var windowChapters = chapters
            .Where(c => c.Order >= targetOrder && c.Order < targetOrder + window)
            .OrderBy(c => c.Order)
            .ToList();
        var briefs = windowChapters
            .Select(c => new ForecastChapterBrief(c.Order, c.Title, c.Summary))
            .ToList();

        var volumeById = volumes.ToDictionary(v => v.Id, StringComparer.Ordinal);
        var volumeGoals = WindowVolumeGoals(windowChapters, volumeById);
        string? volumeGoal = null;
        if (
            target?.VolumeId is { Length: > 0 } targetVolumeId
            && volumeById.TryGetValue(targetVolumeId, out var targetVolume)
        )
        {
            volumeGoal = FirstNonEmpty(targetVolume.Goal, targetVolume.Summary);
        }

        var open = plotlines
            .Where(p => string.IsNullOrEmpty(p.ResolvedAtChapterId))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
        var plotlineById = plotlines.ToDictionary(p => p.Id, StringComparer.Ordinal);

        // 转折点：解析全量（供播种判定），未来部分供渲染
        var validOrders = chapters.Select(c => c.Order).ToHashSet();
        var beatsAll = plotlines
            .SelectMany(p =>
                TurnpointsBeatParser
                    .Parse(p.Turnpoints, validOrders)
                    .Select(b => new ForecastBeat(
                        b.ChapterOrder,
                        p.Id,
                        p.Name,
                        b.Description,
                        b.Type,
                        b.Tension
                    ))
            )
            .ToList();
        // 未截断的未来转折供风险判定（§6 步骤 6：allFutureBeats 保留全量供风险用）
        var futureBeatsAll = beatsAll
            .Where(b => b.ChapterOrder > current)
            .OrderBy(b => b.ChapterOrder)
            .ThenBy(b => b.PlotlineName, StringComparer.Ordinal)
            .ToList();
        var futureBeats = futureBeatsAll.Take(MaxBeats).ToList();

        var orderById = chapters
            .GroupBy(c => c.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Order, StringComparer.Ordinal);

        var obligations = BuildObligations(open, orderById, written, current, targetOrder, window);
        var seedGaps = BuildSeedGaps(
            windowChapters,
            writtenIds,
            written,
            appearances,
            characters,
            plotlineById,
            beatsAll,
            current
        );
        var risks = BuildRisks(
            obligations,
            windowChapters,
            plotlineById,
            futureBeatsAll,
            chapters,
            writtenOrders,
            current,
            appearances.Count > 0
        );

        var hasPlanData =
            windowChapters.Any(c => !string.IsNullOrWhiteSpace(c.Summary))
            || obligations.Count > 0
            || futureBeats.Count > 0;

        var queryText = Truncate(
            BuildNearQuery(target, windowChapters, obligations, volumeGoals),
            MaxQueryChars
        );
        var longRangeQueryText = Truncate(
            BuildLongRangeQuery(novel, open, volumeGoals),
            MaxQueryChars
        );

        return new PlotForecast(
            current,
            targetOrder,
            target?.Id,
            target?.Title,
            volumeGoal,
            briefs,
            futureBeats,
            obligations,
            seedGaps,
            risks,
            queryText,
            longRangeQueryText,
            hasPlanData,
            window
        );
    }

    private static List<ForecastObligation> BuildObligations(
        IReadOnlyList<Plotline> open,
        IReadOnlyDictionary<string, int> orderById,
        IReadOnlyList<Chapter> written,
        int current,
        int targetOrder,
        int window
    )
    {
        var result = new List<ForecastObligation>();
        foreach (var plotline in open)
        {
            // 计划揭示章解析不到就跳过该线（不猜）
            if (
                plotline.PlannedRevealChapterId is not { Length: > 0 } revealId
                || !orderById.TryGetValue(revealId, out var plannedOrder)
            )
            {
                continue;
            }

            var remaining = plannedOrder - current;
            var overdue = remaining <= 0;
            if (!overdue && plannedOrder >= targetOrder + window)
                continue;

            var advanceOrders = written
                .Where(c => c.PlotlineIds.Contains(plotline.Id, StringComparer.Ordinal))
                .Select(c => c.Order)
                .Distinct()
                .OrderBy(o => o)
                .ToList();

            result.Add(
                new ForecastObligation(
                    plotline.Id,
                    plotline.Name,
                    plotline.Type,
                    plannedOrder,
                    remaining,
                    overdue,
                    advanceOrders,
                    plotline.Turnpoints
                )
            );
        }

        return result
            .OrderBy(o => o.Overdue ? 0 : 1)
            .ThenBy(o => o.PlannedOrder)
            .ThenBy(o => o.Name, StringComparer.Ordinal)
            .Take(MaxObligations)
            .ToList();
    }

    private static List<ForecastSeedGap> BuildSeedGaps(
        IReadOnlyList<Chapter> windowChapters,
        HashSet<string> writtenIds,
        IReadOnlyList<Chapter> written,
        IReadOnlyList<AppearanceRecord> appearances,
        IReadOnlyList<Character> characters,
        IReadOnlyDictionary<string, Plotline> plotlineById,
        IReadOnlyList<ForecastBeat> beatsAll,
        int current
    )
    {
        var result = new List<ForecastSeedGap>();
        var characterNames = characters
            .GroupBy(c => c.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);

        foreach (var chapter in windowChapters)
        {
            foreach (var characterId in chapter.CharacterIds.Distinct(StringComparer.Ordinal))
            {
                if (appearances.Count == 0)
                    continue; // 无出场记录时整本都不可核对，风险里单独提示一次
                var appeared = appearances.Any(a =>
                    a.EntityType == EntityType.Character
                    && string.Equals(a.EntityId, characterId, StringComparison.Ordinal)
                    && writtenIds.Contains(a.ChapterId)
                );
                if (appeared)
                    continue;
                result.Add(
                    new ForecastSeedGap(
                        "Character",
                        characterId,
                        characterNames.GetValueOrDefault(characterId, characterId),
                        chapter.Order
                    )
                );
            }

            foreach (var plotlineId in chapter.PlotlineIds.Distinct(StringComparer.Ordinal))
            {
                var seeded =
                    written.Any(c => c.PlotlineIds.Contains(plotlineId, StringComparer.Ordinal))
                    || beatsAll.Any(b =>
                        string.Equals(b.PlotlineId, plotlineId, StringComparison.Ordinal)
                        && b.ChapterOrder <= current
                    )
                    || appearances.Any(a =>
                        a.EntityType == EntityType.Plotline
                        && string.Equals(a.EntityId, plotlineId, StringComparison.Ordinal)
                        && writtenIds.Contains(a.ChapterId)
                    );
                if (seeded)
                    continue;
                var name = plotlineById.TryGetValue(plotlineId, out var plotline)
                    ? plotline.Name
                    : plotlineId;
                result.Add(new ForecastSeedGap("Plotline", plotlineId, name, chapter.Order));
            }
        }

        // 同一实体在多章细纲里出现只算一条缺口（保留最早的引用章）
        return result
            .GroupBy(g => (g.Kind, g.EntityId))
            .Select(group => group.OrderBy(g => g.ReferencedOrder).First())
            .OrderBy(g => g.ReferencedOrder)
            .ThenBy(g => g.Kind, StringComparer.Ordinal)
            .ThenBy(g => g.Name, StringComparer.Ordinal)
            .Take(MaxSeedGaps)
            .ToList();
    }

    private static List<ForecastRisk> BuildRisks(
        IReadOnlyList<ForecastObligation> obligations,
        IReadOnlyList<Chapter> windowChapters,
        IReadOnlyDictionary<string, Plotline> plotlineById,
        IReadOnlyList<ForecastBeat> futureBeats,
        IReadOnlyList<Chapter> chapters,
        HashSet<int> writtenOrders,
        int current,
        bool hasAppearances
    )
    {
        var risks = new List<ForecastRisk>();

        foreach (var obligation in obligations)
        {
            if (obligation.Overdue)
            {
                risks.Add(
                    new ForecastRisk(
                        ForecastRiskKind.Overdue,
                        $"《{obligation.Name}》计划第{obligation.PlannedOrder}章揭示，已逾期 {current - obligation.PlannedOrder} 章",
                        obligation.PlannedOrder
                    )
                );
            }
            else if (obligation.AdvanceOrders.Count == 0)
            {
                risks.Add(
                    new ForecastRisk(
                        ForecastRiskKind.DueWithoutAdvance,
                        $"《{obligation.Name}》计划第{obligation.PlannedOrder}章揭示（还有 {obligation.RemainingChapters} 章），但已写章节中从未推进过该线",
                        obligation.PlannedOrder
                    )
                );
            }
        }

        // 窗口细纲提到的情节线没有计划揭示章
        var seenNoPlan = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chapter in windowChapters)
        {
            foreach (var plotlineId in chapter.PlotlineIds.Distinct(StringComparer.Ordinal))
            {
                if (!plotlineById.TryGetValue(plotlineId, out var plotline))
                    continue;
                if (!string.IsNullOrEmpty(plotline.PlannedRevealChapterId))
                    continue;
                if (!seenNoPlan.Add($"{plotlineId}:{chapter.Order}"))
                    continue;
                risks.Add(
                    new ForecastRisk(
                        ForecastRiskKind.NoPlanOnWindowLine,
                        $"《{plotline.Name}》出现在第{chapter.Order}章细纲中，但没有计划揭示章 → 无法判断何时收束",
                        chapter.Order
                    )
                );
            }
        }

        // 转折点排在该线计划揭示章之后
        var orderById = chapters
            .GroupBy(c => c.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Order, StringComparer.Ordinal);
        foreach (var beat in futureBeats)
        {
            if (!plotlineById.TryGetValue(beat.PlotlineId, out var plotline))
                continue;
            if (
                plotline.PlannedRevealChapterId is not { Length: > 0 } revealId
                || !orderById.TryGetValue(revealId, out var plannedOrder)
            )
            {
                continue;
            }
            if (beat.ChapterOrder <= plannedOrder)
                continue;
            risks.Add(
                new ForecastRisk(
                    ForecastRiskKind.BeatAfterReveal,
                    $"《{beat.PlotlineName}》的转折「第{beat.ChapterOrder}章：{beat.Description}」排在其计划揭示章（{plannedOrder}）之后",
                    beat.ChapterOrder
                )
            );
        }

        // 已写章之间跳章
        if (current > 0)
        {
            var holes = chapters
                .Where(c => c.Order < current && !writtenOrders.Contains(c.Order))
                .Select(c => c.Order)
                .OrderBy(o => o);
            foreach (var order in holes)
            {
                risks.Add(
                    new ForecastRisk(
                        ForecastRiskKind.UnwrittenHole,
                        $"第{order}章尚未写（跳章）",
                        order
                    )
                );
            }
        }

        if (!hasAppearances)
        {
            risks.Add(
                new ForecastRisk(
                    ForecastRiskKind.NoAppearanceRecords,
                    "本书尚无出场记录，角色播种缺口未核对（写章后自动生成）",
                    null
                )
            );
        }

        return risks
            .OrderBy(r => r.ChapterOrder ?? int.MaxValue)
            .ThenBy(r => r.Message, StringComparer.Ordinal)
            .DistinctBy(r => r.Message, StringComparer.Ordinal)
            .Take(MaxRisks)
            .ToList();
    }

    private static List<string> WindowVolumeGoals(
        IReadOnlyList<Chapter> windowChapters,
        IReadOnlyDictionary<string, Volume> volumeById
    )
    {
        var goals = new List<string>();
        foreach (
            var volume in windowChapters
                .Where(c => c.VolumeId is { Length: > 0 })
                .Select(c => volumeById.GetValueOrDefault(c.VolumeId!))
                .Where(v => v is not null)
                .Select(v => v!)
                .DistinctBy(v => v.Id)
                .OrderBy(v => v.Order)
        )
        {
            var goal = FirstNonEmpty(volume.Goal, volume.Summary);
            if (goal is not null)
                goals.Add($"第{volume.Order}卷《{volume.Name}》目标：{goal}");
        }
        return goals;
    }

    private static string BuildNearQuery(
        Chapter? target,
        IReadOnlyList<Chapter> windowChapters,
        IReadOnlyList<ForecastObligation> obligations,
        IReadOnlyList<string> volumeGoals
    )
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(target?.Summary))
            sb.AppendLine(target.Summary);
        foreach (var chapter in windowChapters)
        {
            if (!string.IsNullOrWhiteSpace(chapter.Summary))
                sb.Append("第").Append(chapter.Order).Append("章：").AppendLine(chapter.Summary);
        }
        foreach (var obligation in obligations)
        {
            if (!string.IsNullOrWhiteSpace(obligation.Turnpoints))
                sb.Append(obligation.Name).Append('：').AppendLine(obligation.Turnpoints);
        }
        foreach (var goal in volumeGoals)
            sb.AppendLine(goal);
        return sb.ToString().Trim();
    }

    private static string BuildLongRangeQuery(
        Novel novel,
        IReadOnlyList<Plotline> open,
        IReadOnlyList<string> volumeGoals
    )
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(novel.Outline))
            sb.AppendLine(Truncate(novel.Outline, 200));
        foreach (var plotline in open)
        {
            if (!string.IsNullOrWhiteSpace(plotline.Turnpoints))
                sb.Append(plotline.Name).Append('：').AppendLine(plotline.Turnpoints);
        }
        foreach (var goal in volumeGoals)
            sb.AppendLine(goal);
        return sb.ToString().Trim();
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
