namespace Core.Vector;

/// <summary>
/// 检索结果后处理：去重、相邻块合并、多样性约束。
///
/// 核心规则：
/// - 同一 chunk_id 只返回一次（双通道命中时解释来源）
/// - 同一章节相邻块可合并展示，保留每块独立分数供调试
/// - 多样性约束：同一章节/情节线不能占满结果
/// - 伏笔/暗线搜索启用更高的 chunk 多样性与章节覆盖约束
/// </summary>
public static class RetrievalPostProcessor
{
    /// <summary>
    /// 对融合后的结果执行后处理：去重 → 多样性 → 相邻合并。
    /// </summary>
    public static List<RetrievalSearchResult> Process(
        List<RetrievalSearchResult> results,
        RankingConfig config
    )
    {
        if (results.Count == 0)
            return results;

        // 1. 去重（同一 chunk_id 只保留最高分）
        var deduped = Deduplicate(results);

        // 2. 多样性约束
        var diversified = ApplyDiversity(deduped, config);

        // 3. 相邻块合并（可选）
        return config.MergeAdjacentChunks ? MergeAdjacent(diversified) : diversified;
    }

    /// <summary>
    /// 去重：同一 chunk_id 只保留最高分的结果。
    /// 双通道命中时合并 HitChannel 标记。
    /// </summary>
    private static List<RetrievalSearchResult> Deduplicate(List<RetrievalSearchResult> results)
    {
        return results
            // LINQ 链：GroupBy 按 chunk_id 分组 → 每组取 MaxBy 最高分 → Aggregate 合并 HitChannel 标记
            // 使用 GroupBy 而非可变 Dictionary 是为了保持函数式风格，避免副作用
            .GroupBy(r => r.ChunkId)
            .Select(g =>
            {
                var best = g.MaxBy(r => r.Score)!;
                return g.Aggregate(
                    best,
                    (acc, r) =>
                        acc with
                        {
                            HitChannel = acc.HitChannel | r.HitChannel,
                            DenseRank = acc.DenseRank ?? r.DenseRank,
                            LexicalRank = acc.LexicalRank ?? r.LexicalRank,
                        }
                );
            })
            .OrderByDescending(r => r.Score)
            .ToList();
    }

    /// <summary>
    /// 多样性约束：限制同一章节/情节线的最大结果数。
    /// </summary>
    private static List<RetrievalSearchResult> ApplyDiversity(
        List<RetrievalSearchResult> results,
        RankingConfig config
    )
    {
        if (config.MaxPerChapter is null && config.MaxPerPlotline is null)
            return results;

        var chapterCounts = new Dictionary<string, int>();
        var plotlineCounts = new Dictionary<string, int>();

        return results
            .Where(r =>
            {
                var chapterKey = r.ChapterId ?? "";
                var plotlineKey =
                    r.MetadataSummary?.TryGetValue("plotline-ids", out var plids) == true
                        ? string.Join(",", (plids as List<object?>)?.Cast<string?>() ?? [])
                        : "";

                var chapterCount = chapterCounts.GetValueOrDefault(chapterKey);
                var plotlineCount = plotlineCounts.GetValueOrDefault(plotlineKey);

                if (config.MaxPerChapter is { } maxChapter && chapterCount >= maxChapter)
                    return false;

                if (config.MaxPerPlotline is { } maxPlotline && plotlineCount >= maxPlotline)
                    return false;

                chapterCounts[chapterKey] = chapterCount + 1;
                plotlineCounts[plotlineKey] = plotlineCount + 1;
                return true;
            })
            .ToList();
    }

    /// <summary>
    /// 相邻块合并：同一章节的相邻高分块合并展示。
    /// 保留每块独立分数供调试，但返回一个主块及其前后可展开范围。
    /// </summary>
    private static List<RetrievalSearchResult> MergeAdjacent(List<RetrievalSearchResult> results)
    {
        if (results.Count <= 1)
            return results;

        // 分组逻辑：GroupBy 按 ChapterId 分组 → 每章内部按 ChunkOrdinal 排序 → MergeGroup 合并相邻块
        // 不同章节的块不会合并，确保跨章节的检索结果保持独立
        return results
            .GroupBy(r => r.ChapterId ?? "")
            .SelectMany(g => MergeGroup(g.OrderBy(r => r.ChunkOrdinal).ToList()))
            .OrderByDescending(r => r.Score)
            .ToList();
    }

    /// <summary>
    /// 合并同一章节的相邻块组，返回合并后的结果列表。
    /// </summary>
    private static List<RetrievalSearchResult> MergeGroup(List<RetrievalSearchResult> chunks)
    {
        if (chunks.Count == 0)
            return [];

        // 用 Aggregate 构建相邻块组
        var groups = chunks
            .Skip(1)
            .Aggregate(
                new List<List<RetrievalSearchResult>> { new() { chunks[0] } },
                (acc, curr) =>
                {
                    var lastGroup = acc[^1];
                    var last = lastGroup[^1];
                    if (curr.ChunkOrdinal == last.ChunkOrdinal + 1)
                    {
                        lastGroup.Add(curr);
                    }
                    else
                    {
                        acc.Add([curr]);
                    }
                    return acc;
                }
            );

        // 每组合并为一个结果
        return groups
            .Select(g =>
            {
                if (g.Count == 1)
                    return g[0];

                var best = g.MaxBy(r => r.Score)!;
                var combinedText = string.Join("\n", g.Select(r => r.Text));

                return best with
                {
                    Text = combinedText,
                    SourceOffsetStart = g[0].SourceOffsetStart,
                    SourceOffsetEnd = g[^1].SourceOffsetEnd,
                    PrevChunkId = g[0].PrevChunkId,
                    NextChunkId = g[^1].NextChunkId,
                };
            })
            .ToList();
    }
}
