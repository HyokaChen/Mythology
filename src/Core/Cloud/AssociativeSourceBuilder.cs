using Core.Data;
using Core.Vector;

namespace Core.Cloud;

/// <summary>
/// 联想来源单元构建：把一章及其场景/正文组装成 <see cref="AssociativeSourceInput"/> 列表。
/// 触发钩子（写后索引）与 <c>index_associative_memory</c> 工具共用，避免两处口径漂移。
/// </summary>
public static class AssociativeSourceBuilder
{
    /// <summary>每章最多提交的正文块数上限（超长章节按此截断，控制云端 LLM 成本）。</summary>
    public const int MaxChunksPerChapter = 50;

    /// <summary>正文块默认上限。</summary>
    public const int DefaultChunksPerChapter = 20;

    /// <summary>
    /// 把一个章节组成本地来源单元：章级（梗概 + 相关情节线 + 实体变化）
    /// + 每场景一个场景单元 + 每正文块一个块单元。
    /// </summary>
    /// <param name="chapter">目标章节（须含正文，否则不产生块单元）。</param>
    /// <param name="scenes">该章的场景定义。</param>
    /// <param name="changes">该章的实体变化记录。</param>
    /// <param name="plotlines">全书情节线（用于筛出本章相关的）。</param>
    /// <param name="locationNames">地点 ID → 名称。</param>
    /// <param name="characterNames">角色 ID → 名称。</param>
    /// <param name="plotlineNames">情节线 ID → 名称。</param>
    /// <param name="knownEntities">已知实体名（供块级提示词避免过度泛化）。</param>
    /// <param name="includeBodyChunks">是否包含正文块单元。</param>
    /// <param name="maxChunks">正文块数上限（超出取前 N 块）。</param>
    public static List<AssociativeSourceInput> BuildForChapter(
        Chapter chapter,
        IReadOnlyList<SceneDefinition> scenes,
        IReadOnlyList<EntityStateChange> changes,
        IReadOnlyList<Plotline> plotlines,
        IReadOnlyDictionary<string, string> locationNames,
        IReadOnlyDictionary<string, string> characterNames,
        IReadOnlyDictionary<string, string> plotlineNames,
        string knownEntities,
        bool includeBodyChunks = true,
        int maxChunks = DefaultChunksPerChapter,
        ChineseTextChunker? chunker = null
    )
    {
        var sources = new List<AssociativeSourceInput>();
        var chapterPlotlineIds = chapter.PlotlineIds ?? [];

        // 章级：梗概 + 相关情节线 + 实体变化
        var relatedPlotlines = plotlines
            .Where(p =>
                chapterPlotlineIds.Contains(p.Id)
                || p.PlannedRevealChapterId == chapter.Id
                || p.ResolvedAtChapterId == chapter.Id
            )
            .ToList();
        var plotlinesText = string.Join(
            "；",
            relatedPlotlines.Select(p =>
                $"{p.Name}（{p.Type}，紧张度 {p.Tension}）"
                + (string.IsNullOrWhiteSpace(p.Turnpoints) ? "" : $" 转折：{p.Turnpoints}")
            )
        );
        var stateChangesText = string.Join(
            "；",
            changes.Select(s =>
                $"{s.TargetEntityType}：{s.Summary}"
                + (
                    string.IsNullOrWhiteSpace(s.FromSummary)
                    && string.IsNullOrWhiteSpace(s.ToSummary)
                        ? ""
                        : $"（{s.FromSummary}→{s.ToSummary}）"
                )
            )
        );
        sources.Add(
            new AssociativeSourceInput(
                Kind: "chapter",
                Title: chapter.Title,
                Summary: chapter.Summary,
                PlotlinesText: plotlinesText,
                StateChangesText: stateChangesText,
                SourceEntityType: "Chapter",
                SourceEntityId: chapter.Id,
                SourceChapterId: chapter.Id,
                SourceChapterOrder: chapter.Order
            )
        );

        // 场景级：每场景一个单元（SourceChapterId 归属本章）
        foreach (var scene in scenes)
        {
            sources.Add(
                new AssociativeSourceInput(
                    Kind: "scene",
                    Title: scene.Title,
                    Conflict: scene.Conflict,
                    Goal: scene.Goal,
                    EmotionalArc: scene.EmotionalArc,
                    TimeDescription: scene.TimeDescription,
                    LocationName: scene.LocationId is { } lid
                    && locationNames.TryGetValue(lid, out var locationName)
                        ? locationName
                        : "",
                    Participants: string.Join(
                        "、",
                        scene.ParticipantIds.Select(id =>
                            characterNames.TryGetValue(id, out var nm) ? nm : id
                        )
                    ),
                    PlotlineNames: string.Join(
                        "、",
                        scene.PlotlineIds.Select(id =>
                            plotlineNames.TryGetValue(id, out var nm) ? nm : id
                        )
                    ),
                    Notes: scene.Notes,
                    SourceEntityType: "SceneDefinition",
                    SourceEntityId: scene.Id,
                    SourceChapterId: chapter.Id,
                    SourceChapterOrder: chapter.Order
                )
            );
        }

        // 正文块级
        if (includeBodyChunks && !string.IsNullOrWhiteSpace(chapter.Content))
        {
            var effectiveChunker = chunker ?? new ChineseTextChunker();
            var chunks = effectiveChunker.Chunk(chapter.Content);
            var take = Math.Clamp(maxChunks, 1, MaxChunksPerChapter);
            for (var i = 0; i < chunks.Count && i < take; i++)
            {
                sources.Add(
                    new AssociativeSourceInput(
                        Kind: "chunk",
                        Text: chunks[i].Text,
                        KnownEntities: knownEntities,
                        SourceEntityType: "Chapter",
                        SourceEntityId: chapter.Id,
                        SourceChapterId: chapter.Id,
                        SourceChapterOrder: chapter.Order,
                        ChunkOrdinal: i
                    )
                );
            }
        }

        return sources;
    }
}
