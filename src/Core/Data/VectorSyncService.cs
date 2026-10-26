using Core.Vector;
using Microsoft.Extensions.Logging;

namespace Core.Data;

/// <summary>
/// 向量同步服务（V2）：使用新的检索内核进行增量同步。
/// 启动时执行补偿对账，确保所有实体已索引。
/// 实时索引由 OnEntityChanged → RetrievalKernel 异步队列处理。
/// </summary>
public sealed class VectorSyncService(
    INovelStore store,
    IRetrievalStore retrievalStore,
    RetrievalKernel kernel,
    DocumentBuilder builder,
    ILogger<VectorSyncService> logger
)
{
    /// <summary>
    /// 执行所有小说的检索索引增量同步。
    /// 遍历每部小说，同步所有实体类型，然后清理已删除的索引。
    /// </summary>
    /// <param name="ct">取消令牌</param>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        logger.LogInformation("开始检索索引增量同步...");

        var novels = await store.GetAllNovels(ct);
        foreach (var novel in novels)
        {
            try
            {
                await SyncNovelAsync(novel, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "同步小说 '{NovelId}' 检索索引时出错", novel.Id);
            }
        }

        // 清理已删除的索引
        try
        {
            await retrievalStore.PurgeDeletedAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "清理已删除索引时出错");
        }

        logger.LogInformation("检索索引增量同步完成");
    }

    /// <summary>按 novelId 同步单部小说（实时实体词典变化后调用）。</summary>
    public async Task SyncNovelAsync(string novelId, CancellationToken ct = default)
    {
        var novel = await store.GetNovel(novelId, ct);
        if (novel is null)
            return;
        await SyncNovelAsync(novel, ct);
    }

    /// <summary>
    /// 同步单部小说的所有实体类型到检索索引。
    /// 并行处理各类实体（角色、章节、情节线等），最后清理已删除的实体。
    /// </summary>
    /// <param name="novel">小说对象</param>
    /// <param name="ct">取消令牌</param>
    private async Task SyncNovelAsync(Novel novel, CancellationToken ct)
    {
        logger.LogDebug("同步小说 '{NovelTitle}' 的检索索引", novel.Title);

        // 实体名称/标题变化（新增角色/地点/道具、改名、章节标题等）会改变分词结果；
        // 先更新词典，lexical_version 变化会在下面的 IsUpToDate 中触发重建。
        var names = await NovelLexiconCollector.CollectAsync(store, novel.Id, ct);
        if (NovelLexicon.Update(novel.Id, names))
        {
            logger.LogInformation(
                "小说 '{NovelId}' 实体词典已更新（{NameCount} 个名称），词法版本变化将触发必要重建",
                novel.Id,
                names.Count
            );
        }

        // 已索引实体 ID（用于清理已删除实体）
        var indexedIds = await retrievalStore.GetIndexedDocumentIdsAsync(novel.Id, ct);
        // 已索引文档版本快照（用于增量判断：revision + content hash）
        var versions = await retrievalStore.GetDocumentVersionsAsync(novel.Id, ct);

        // 并行同步各类实体
        var tasks = new List<Task>
        {
            SyncCharactersAsync(novel.Id, versions, ct),
            SyncChaptersAsync(novel.Id, versions, ct),
            SyncPlotlinesAsync(novel.Id, versions, ct),
            SyncLocationsAsync(novel.Id, versions, ct),
            SyncItemsAsync(novel.Id, versions, ct),
            SyncFactionsAsync(novel.Id, versions, ct),
            SyncScenesAsync(novel.Id, versions, ct),
            SyncTimelineEventsAsync(novel.Id, versions, ct),
            SyncAuthorNotesAsync(novel.Id, versions, ct),
            SyncRelationshipsAsync(novel.Id, versions, ct),
            SyncFactionRelationshipsAsync(novel.Id, versions, ct),
            SyncNovelProfileAsync(novel, versions, ct),
        };

        await Task.WhenAll(tasks);

        // 删除不再存在的实体索引
        await RemoveDeletedEntitiesAsync(novel.Id, indexedIds, ct);
    }

    /// <summary>
    /// 同步角色实体到检索索引。预加载组织名称以丰富角色文档。
    /// </summary>
    /// <param name="novelId">小说 ID</param>
    /// <param name="versions">已索引文档版本快照（document_id → revision + content hash）</param>
    /// <param name="ct">取消令牌</param>
    private async Task SyncCharactersAsync(
        string novelId,
        Dictionary<string, RetrievalDocumentVersion> versions,
        CancellationToken ct
    )
    {
        var characters = await store.GetCharacters(novelId, ct);
        var factionNames = new Dictionary<string, string?>();

        // 预加载组织名称
        var factions = await store.GetFactions(novelId, ct);
        foreach (var f in factions)
            factionNames[f.Id] = f.Name;

        foreach (var character in characters)
        {
            var factionName =
                character.FactionId is not null
                && factionNames.TryGetValue(character.FactionId, out var fn)
                    ? fn
                    : null;
            var (doc, chunks) = builder.BuildCharacter(character, factionName);
            if (IsUpToDate(versions, doc))
                continue;

            await kernel.IndexDocumentAsync(doc, chunks, ct);
        }
    }

    /// <summary>
    /// 同步章节实体到检索索引。章节有两条文档：摘要（summary）和正文块（body chunks），
    /// 各自按版本快照独立判断是否需要重索引。
    /// </summary>
    /// <param name="novelId">小说 ID</param>
    /// <param name="versions">已索引文档版本快照（document_id → revision + content hash）</param>
    /// <param name="ct">取消令牌</param>
    private async Task SyncChaptersAsync(
        string novelId,
        Dictionary<string, RetrievalDocumentVersion> versions,
        CancellationToken ct
    )
    {
        var chapters = await store.GetChapters(novelId, ct);

        foreach (var chapter in chapters)
        {
            // 摘要文档：版本一致则跳过
            var (summaryDoc, summaryChunks) = builder.BuildChapterSummary(chapter);
            if (!IsUpToDate(versions, summaryDoc))
                await kernel.IndexDocumentAsync(summaryDoc, summaryChunks, ct);

            // 正文块文档：无块或版本一致则跳过
            var (bodyDoc, bodyChunks) = builder.BuildChapterBodyChunks(chapter);
            if (bodyChunks.Count > 0 && !IsUpToDate(versions, bodyDoc))
                await kernel.IndexDocumentAsync(bodyDoc, bodyChunks, ct);
        }
    }

    /// <summary>
    /// 同步情节线实体到检索索引。
    /// </summary>
    /// <param name="novelId">小说 ID</param>
    /// <param name="versions">已索引文档版本快照（document_id → revision + content hash）</param>
    /// <param name="ct">取消令牌</param>
    private async Task SyncPlotlinesAsync(
        string novelId,
        Dictionary<string, RetrievalDocumentVersion> versions,
        CancellationToken ct
    )
    {
        var plotlines = await store.GetPlotlines(novelId, ct);
        foreach (var plotline in plotlines)
        {
            var (doc, chunks) = builder.BuildPlotline(plotline);
            if (IsUpToDate(versions, doc))
                continue;

            await kernel.IndexDocumentAsync(doc, chunks, ct);
        }
    }

    /// <summary>
    /// 同步地点实体到检索索引。
    /// </summary>
    /// <param name="novelId">小说 ID</param>
    /// <param name="versions">已索引文档版本快照（document_id → revision + content hash）</param>
    /// <param name="ct">取消令牌</param>
    private async Task SyncLocationsAsync(
        string novelId,
        Dictionary<string, RetrievalDocumentVersion> versions,
        CancellationToken ct
    )
    {
        var locations = await store.GetLocations(novelId, ct);
        foreach (var location in locations)
        {
            var (doc, chunks) = builder.BuildLocation(location);
            if (IsUpToDate(versions, doc))
                continue;

            await kernel.IndexDocumentAsync(doc, chunks, ct);
        }
    }

    /// <summary>
    /// 同步物品实体到检索索引。附带物品持有者名称以丰富文档。
    /// </summary>
    /// <param name="novelId">小说 ID</param>
    /// <param name="versions">已索引文档版本快照（document_id → revision + content hash）</param>
    /// <param name="ct">取消令牌</param>
    private async Task SyncItemsAsync(
        string novelId,
        Dictionary<string, RetrievalDocumentVersion> versions,
        CancellationToken ct
    )
    {
        var items = await store.GetItems(novelId, ct);
        foreach (var item in items)
        {
            string? ownerName = null;
            if (item.OwnerId is not null)
                ownerName = await store.GetCharacterNameById(novelId, item.OwnerId, ct);

            var (doc, chunks) = builder.BuildItem(item, ownerName);
            if (IsUpToDate(versions, doc))
                continue;

            await kernel.IndexDocumentAsync(doc, chunks, ct);
        }
    }

    /// <summary>
    /// 同步组织实体到检索索引。
    /// </summary>
    /// <param name="novelId">小说 ID</param>
    /// <param name="versions">已索引文档版本快照（document_id → revision + content hash）</param>
    /// <param name="ct">取消令牌</param>
    private async Task SyncFactionsAsync(
        string novelId,
        Dictionary<string, RetrievalDocumentVersion> versions,
        CancellationToken ct
    )
    {
        var factions = await store.GetFactions(novelId, ct);
        foreach (var faction in factions)
        {
            var (doc, chunks) = builder.BuildFaction(faction);
            if (IsUpToDate(versions, doc))
                continue;

            await kernel.IndexDocumentAsync(doc, chunks, ct);
        }
    }

    /// <summary>
    /// 同步场景实体到检索索引。预加载章节、角色、情节线和地点信息以丰富场景文档。
    /// </summary>
    /// <param name="novelId">小说 ID</param>
    /// <param name="versions">已索引文档版本快照（document_id → revision + content hash）</param>
    /// <param name="ct">取消令牌</param>
    private async Task SyncScenesAsync(
        string novelId,
        Dictionary<string, RetrievalDocumentVersion> versions,
        CancellationToken ct
    )
    {
        var scenes = await store.GetScenesByNovel(novelId, ct);
        var chapters = await store.GetChapters(novelId, ct);
        var chapterMap = chapters.ToDictionary(c => c.Id);
        var characters = await store.GetCharacters(novelId, ct);
        var characterMap = characters.ToDictionary(c => c.Id);
        var plotlines = await store.GetPlotlines(novelId, ct);
        var plotlineMap = plotlines.ToDictionary(p => p.Id);
        var locations = await store.GetLocations(novelId, ct);
        var locationMap = locations.ToDictionary(l => l.Id);

        foreach (var scene in scenes)
        {
            var locationName =
                scene.LocationId is not null
                && locationMap.TryGetValue(scene.LocationId, out var loc)
                    ? loc.Name
                    : null;
            var participantNames = scene
                .ParticipantIds.Where(characterMap.ContainsKey)
                .Select(id => characterMap[id].Name)
                .ToList();
            var plotlineNames = scene
                .PlotlineIds.Where(plotlineMap.ContainsKey)
                .Select(id => plotlineMap[id].Name)
                .ToList();
            var chapterOrder =
                scene.ChapterId is not null && chapterMap.TryGetValue(scene.ChapterId, out var ch)
                    ? (int?)ch.Order
                    : null;
            var chapterTitle =
                scene.ChapterId is not null && chapterMap.TryGetValue(scene.ChapterId, out var ch2)
                    ? ch2.Title
                    : null;

            var (doc, chunks) = builder.BuildSceneDefinition(
                scene,
                locationName,
                participantNames,
                plotlineNames,
                chapterOrder,
                chapterTitle
            );
            if (IsUpToDate(versions, doc))
                continue;

            await kernel.IndexDocumentAsync(doc, chunks, ct);
        }
    }

    /// <summary>
    /// 同步时间线事件实体到检索索引。附带所属章节序号以丰富文档。
    /// </summary>
    /// <param name="novelId">小说 ID</param>
    /// <param name="versions">已索引文档版本快照（document_id → revision + content hash）</param>
    /// <param name="ct">取消令牌</param>
    private async Task SyncTimelineEventsAsync(
        string novelId,
        Dictionary<string, RetrievalDocumentVersion> versions,
        CancellationToken ct
    )
    {
        var events = await store.GetTimelineEvents(novelId, ct);
        var chapters = await store.GetChapters(novelId, ct);
        var chapterMap = chapters.ToDictionary(c => c.Id);

        foreach (var evt in events)
        {
            var chapterOrder =
                evt.ChapterId is not null && chapterMap.TryGetValue(evt.ChapterId, out var ch)
                    ? (int?)ch.Order
                    : null;
            var (doc, chunks) = builder.BuildTimelineEvent(evt, chapterOrder);
            if (IsUpToDate(versions, doc))
                continue;

            await kernel.IndexDocumentAsync(doc, chunks, ct);
        }
    }

    /// <summary>
    /// 同步作者随手记到检索索引（含收件箱候选；推演侧按 status metadata 过滤）。
    /// </summary>
    private async Task SyncAuthorNotesAsync(
        string novelId,
        Dictionary<string, RetrievalDocumentVersion> versions,
        CancellationToken ct
    )
    {
        var notes = await store.GetNotes(novelId, ct);

        foreach (var note in notes)
        {
            var (doc, chunks) = builder.BuildAuthorNote(note);
            if (IsUpToDate(versions, doc))
                continue;

            await kernel.IndexDocumentAsync(doc, chunks, ct);
        }
    }

    /// <summary>
    /// 同步角色关系实体到检索索引。附带角色名称以丰富文档。
    /// </summary>
    /// <param name="novelId">小说 ID</param>
    /// <param name="versions">已索引文档版本快照（document_id → revision + content hash）</param>
    /// <param name="ct">取消令牌</param>
    private async Task SyncRelationshipsAsync(
        string novelId,
        Dictionary<string, RetrievalDocumentVersion> versions,
        CancellationToken ct
    )
    {
        var relationships = await store.GetRelationshipsByNovel(novelId, ct);
        var characters = await store.GetCharacters(novelId, ct);
        var characterMap = characters.ToDictionary(c => c.Id);

        foreach (var rel in relationships)
        {
            var nameA = characterMap.TryGetValue(rel.CharacterAId, out var chA) ? chA.Name : null;
            var nameB = characterMap.TryGetValue(rel.CharacterBId, out var chB) ? chB.Name : null;
            var (doc, chunks) = builder.BuildRelationship(rel, nameA, nameB);
            if (IsUpToDate(versions, doc))
                continue;

            await kernel.IndexDocumentAsync(doc, chunks, ct);
        }
    }

    /// <summary>
    /// 同步组织关系实体到检索索引。附带组织名称以丰富文档。
    /// </summary>
    /// <param name="novelId">小说 ID</param>
    /// <param name="versions">已索引文档版本快照（document_id → revision + content hash）</param>
    /// <param name="ct">取消令牌</param>
    private async Task SyncFactionRelationshipsAsync(
        string novelId,
        Dictionary<string, RetrievalDocumentVersion> versions,
        CancellationToken ct
    )
    {
        var factionRels = await store.GetFactionRelationships(novelId, ct);
        var factions = await store.GetFactions(novelId, ct);
        var factionMap = factions.ToDictionary(f => f.Id);

        foreach (var rel in factionRels)
        {
            var nameA = factionMap.TryGetValue(rel.FactionAId, out var fA) ? fA.Name : null;
            var nameB = factionMap.TryGetValue(rel.FactionBId, out var fB) ? fB.Name : null;
            var (doc, chunks) = builder.BuildFactionRelationship(rel, nameA, nameB);
            if (IsUpToDate(versions, doc))
                continue;

            await kernel.IndexDocumentAsync(doc, chunks, ct);
        }
    }

    /// <summary>
    /// 同步小说概要文档到检索索引。
    /// </summary>
    /// <param name="novel">小说对象</param>
    /// <param name="versions">已索引文档版本快照（document_id → revision + content hash）</param>
    /// <param name="ct">取消令牌</param>
    private async Task SyncNovelProfileAsync(
        Novel novel,
        Dictionary<string, RetrievalDocumentVersion> versions,
        CancellationToken ct
    )
    {
        var (doc, chunks) = builder.BuildNovelProfile(novel);
        if (IsUpToDate(versions, doc))
            return;

        await kernel.IndexDocumentAsync(doc, chunks, ct);
    }

    /// <summary>
    /// 删除已索引但不再存在于存储中的实体索引。
    /// 对比当前所有实体 ID 与已索引 ID 集合，用 LINQ Except 找出差异。
    /// </summary>
    /// <param name="novelId">小说 ID</param>
    /// <param name="indexedIds">已索引实体 ID 集合</param>
    /// <param name="ct">取消令牌</param>
    private async Task RemoveDeletedEntitiesAsync(
        string novelId,
        Dictionary<string, HashSet<string>> indexedIds,
        CancellationToken ct
    )
    {
        // 获取当前所有实体 ID（Novel 概要文档的 source_entity_type 为 "Novel"，必须登记，
        // 否则小说概要文档每次同步都被当作孤儿清理——旧「总是全量重索引」掩盖了这一点）
        var currentIds = new Dictionary<string, HashSet<string>>();
        var novel = await store.GetNovel(novelId, ct);
        currentIds["Novel"] = novel is null ? [] : new HashSet<string>([novel.Id]);
        var characters = await store.GetCharacters(novelId, ct);
        var chapters = await store.GetChapters(novelId, ct);
        var plotlines = await store.GetPlotlines(novelId, ct);
        var locations = await store.GetLocations(novelId, ct);
        var items = await store.GetItems(novelId, ct);
        var factions = await store.GetFactions(novelId, ct);
        var scenes = await store.GetScenesByNovel(novelId, ct);
        var timelineEvents = await store.GetTimelineEvents(novelId, ct);
        var relationships = await store.GetRelationshipsByNovel(novelId, ct);
        var factionRels = await store.GetFactionRelationships(novelId, ct);

        currentIds["Character"] = new HashSet<string>(characters.Select(c => c.Id));
        currentIds["Chapter"] = new HashSet<string>(chapters.Select(c => c.Id));
        currentIds["Plotline"] = new HashSet<string>(plotlines.Select(p => p.Id));
        currentIds["Location"] = new HashSet<string>(locations.Select(l => l.Id));
        currentIds["Item"] = new HashSet<string>(items.Select(i => i.Id));
        currentIds["Faction"] = new HashSet<string>(factions.Select(f => f.Id));
        currentIds["SceneDefinition"] = new HashSet<string>(scenes.Select(s => s.Id));
        currentIds["TimelineEvent"] = new HashSet<string>(timelineEvents.Select(e => e.Id));
        currentIds["Relationship"] = new HashSet<string>(relationships.Select(r => r.Id));
        currentIds["FactionRelationship"] = new HashSet<string>(factionRels.Select(r => r.Id));
        // 笔记必须登记：否则每次启动对账都把笔记索引当孤儿清掉
        var notes = await store.GetNotes(novelId, ct);
        currentIds["Note"] = new HashSet<string>(notes.Select(n => n.Id));
        // 找出已索引但不再存在的实体（使用 LINQ Except 替代嵌套循环）
        foreach (var (entityType, ids) in indexedIds)
        {
            var current = currentIds.GetValueOrDefault(entityType) ?? [];
            var deletedIds = ids.Except(current);
            foreach (var id in deletedIds)
            {
                logger.LogDebug("删除过期检索索引: {EntityType} '{EntityId}'", entityType, id);
                await kernel.DeleteEntityAsync(novelId, entityType, id, ct);
            }
        }
    }

    /// <summary>
    /// 检查文档是否已是最新：已就绪（Ready）且 source_revision / content_hash 与当前一致。
    /// revision 覆盖实体 updatedAt 变更；content hash 覆盖同 revision 下的内容变更；
    /// 版本快照仅收录 Ready → 上次索引失败/部分索引的文档必然重跑。
    /// </summary>
    private bool IsUpToDate(
        Dictionary<string, RetrievalDocumentVersion> versions,
        RetrievalDocument doc
    ) =>
        versions.TryGetValue(doc.DocumentId, out var v)
        && v.SourceRevision == doc.SourceRevision
        && v.ContentHash == doc.ContentHash
        && string.Equals(v.EmbeddingModelId, kernel.EmbeddingModelId, StringComparison.Ordinal)
        && string.Equals(
            v.LexicalVersion,
            NovelLexicon.GetVersion(doc.NovelId),
            StringComparison.Ordinal
        );
}
