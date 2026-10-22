using Core.Data;
using Core.Vector;

namespace Tui;

public sealed partial class TuiApp
{
    /// <summary>
    /// 将实体变更事件入队到检索索引。
    /// 对于删除操作，直接删除索引中的实体；对于创建/更新操作，创建异步文档构建函数并入队。
    /// </summary>
    private async Task EnqueueIndexTaskAsync(EntityChangeEvent evt)
    {
        if (_retrievalKernel is null)
            return;

        try
        {
            if (evt.Action == AuditAction.Delete)
            {
                var sourceEntityType = EntityTypeToSourceType(evt.EntityType);
                if (sourceEntityType is not null)
                    await _retrievalKernel.DeleteEntityAsync(
                        evt.NovelId,
                        sourceEntityType,
                        evt.EntityId
                    );
                return;
            }

            var buildFunc = CreateBuildFunc(evt.EntityType, evt.EntityId, evt.NovelId);
            if (buildFunc is null)
                return;

            _retrievalKernel.EnqueueIndexTask(
                new IndexTask
                {
                    NovelId = evt.NovelId,
                    EntityType =
                        EntityTypeToSourceType(evt.EntityType) ?? evt.EntityType.ToString(),
                    EntityId = evt.EntityId,
                    Action = evt.Action == AuditAction.Create ? "create" : "update",
                    BuildFunc = buildFunc,
                }
            );
        }
        catch (Exception ex)
        {
            Services.TuiLogger.OperationFailed(_logger, ex, "enqueue indexing task");
        }
    }

    /// <summary>
    /// 创建异步文档构建函数。根据实体类型返回一个在 Worker 线程中异步读取存储并构建检索文档的委托。
    /// 所有 <c>BuildXxxDoc</c> 方法均为 async，避免同步阻塞异步（GetAwaiter().GetResult()）。
    /// </summary>
    /// <param name="entityType">实体类型</param>
    /// <param name="entityId">实体 ID</param>
    /// <param name="novelId">小说 ID</param>
    /// <returns>异步文档构建委托，未知类型返回 null</returns>
    private Func<
        DocumentBuilder,
        Task<(RetrievalDocument Doc, List<RetrievalChunk> Chunks)>
    >? CreateBuildFunc(EntityType entityType, string entityId, string novelId)
    {
        return entityType switch
        {
            EntityType.Character => b => BuildCharacterDocAsync(b, entityId, novelId),
            EntityType.Chapter => b => BuildChapterDocAsync(b, entityId, novelId),
            EntityType.Plotline => b => BuildPlotlineDocAsync(b, entityId, novelId),
            EntityType.Location => b => BuildLocationDocAsync(b, entityId, novelId),
            EntityType.Item => b => BuildItemDocAsync(b, entityId, novelId),
            EntityType.Faction => b => BuildFactionDocAsync(b, entityId, novelId),
            EntityType.Scene => b => BuildSceneDocAsync(b, entityId, novelId),
            EntityType.TimelineEvent => b => BuildTimelineEventDocAsync(b, entityId, novelId),
            EntityType.Note => b => BuildAuthorNoteDocAsync(b, entityId, novelId),
            EntityType.Relationship => b => BuildRelationshipDocAsync(b, entityId, novelId),
            EntityType.FactionRelationship => b =>
                BuildFactionRelationshipDocAsync(b, entityId, novelId),
            _ => null,
        };
    }

    // ── 异步文档构建方法（在 Worker 线程中执行，端到端异步） ──

    /// <summary>
    /// 从存储中异步读取角色并构建检索文档。附带所属组织名称。
    /// </summary>
    /// <param name="b">文档构建器</param>
    /// <param name="id">实体 ID</param>
    /// <param name="novelId">小说 ID</param>
    private async Task<(RetrievalDocument Doc, List<RetrievalChunk> Chunks)> BuildCharacterDocAsync(
        DocumentBuilder b,
        string id,
        string novelId
    )
    {
        var ch = await _store.GetCharacter(novelId, id);
        if (ch is null)
            return (null!, []);
        string? factionName = null;
        if (ch.FactionId is not null)
        {
            var faction = await _store.GetFaction(novelId, ch.FactionId);
            factionName = faction?.Name;
        }
        return b.BuildCharacter(ch, factionName);
    }

    /// <summary>
    /// 从存储中异步读取章节并构建正文块检索文档。同时将章节摘要作为独立任务入队。
    /// </summary>
    /// <param name="b">文档构建器</param>
    /// <param name="id">实体 ID</param>
    /// <param name="novelId">小说 ID</param>
    private async Task<(RetrievalDocument Doc, List<RetrievalChunk> Chunks)> BuildChapterDocAsync(
        DocumentBuilder b,
        string id,
        string novelId
    )
    {
        var ch = await _store.GetChapter(novelId, id);
        if (ch is null)
            return (null!, []);
        // 章节摘要 + 正文块（摘要构建为同步委托，不涉及 I/O）
        _retrievalKernel!.EnqueueIndexTask(
            new IndexTask
            {
                NovelId = novelId,
                EntityType = "Chapter",
                EntityId = id,
                Action = "update",
                BuildFunc = bb => Task.FromResult(bb.BuildChapterSummary(ch)),
            }
        );
        return b.BuildChapterBodyChunks(ch);
    }

    /// <summary>
    /// 从存储中异步读取情节线并构建检索文档。
    /// </summary>
    private async Task<(RetrievalDocument Doc, List<RetrievalChunk> Chunks)> BuildPlotlineDocAsync(
        DocumentBuilder b,
        string id,
        string novelId
    )
    {
        var p = await _store.GetPlotline(novelId, id);
        return p is null ? (null!, []) : b.BuildPlotline(p);
    }

    /// <summary>
    /// 从存储中异步读取地点并构建检索文档。
    /// </summary>
    private async Task<(RetrievalDocument Doc, List<RetrievalChunk> Chunks)> BuildLocationDocAsync(
        DocumentBuilder b,
        string id,
        string novelId
    )
    {
        var l = await _store.GetLocation(novelId, id);
        return l is null ? (null!, []) : b.BuildLocation(l);
    }

    /// <summary>
    /// 从存储中异步读取物品并构建检索文档。
    /// </summary>
    private async Task<(RetrievalDocument Doc, List<RetrievalChunk> Chunks)> BuildItemDocAsync(
        DocumentBuilder b,
        string id,
        string novelId
    )
    {
        var item = await _store.GetItem(novelId, id);
        return item is null ? (null!, []) : b.BuildItem(item);
    }

    /// <summary>
    /// 从存储中异步读取组织并构建检索文档。
    /// </summary>
    private async Task<(RetrievalDocument Doc, List<RetrievalChunk> Chunks)> BuildFactionDocAsync(
        DocumentBuilder b,
        string id,
        string novelId
    )
    {
        var f = await _store.GetFaction(novelId, id);
        return f is null ? (null!, []) : b.BuildFaction(f);
    }

    /// <summary>
    /// 从存储中异步读取场景并构建检索文档。附带地点名称。
    /// </summary>
    private async Task<(RetrievalDocument Doc, List<RetrievalChunk> Chunks)> BuildSceneDocAsync(
        DocumentBuilder b,
        string id,
        string novelId
    )
    {
        var scene = await _store.GetScene(novelId, id);
        if (scene is null)
            return (null!, []);
        string? locName = null;
        if (scene.LocationId is not null)
        {
            var loc = await _store.GetLocation(novelId, scene.LocationId);
            locName = loc?.Name;
        }
        return b.BuildSceneDefinition(scene, locationName: locName);
    }

    /// <summary>
    /// 从存储中异步读取时间线事件并构建检索文档。
    /// </summary>
    private async Task<(
        RetrievalDocument Doc,
        List<RetrievalChunk> Chunks
    )> BuildTimelineEventDocAsync(DocumentBuilder b, string id, string novelId)
    {
        var events = await _store.GetTimelineEvents(novelId);
        var evt = events.FirstOrDefault(e => e.Id == id);
        return evt is null ? (null!, []) : b.BuildTimelineEvent(evt);
    }

    /// <summary>
    /// 从存储中异步读取作者随手记并构建检索文档。
    /// </summary>
    private async Task<(
        RetrievalDocument Doc,
        List<RetrievalChunk> Chunks
    )> BuildAuthorNoteDocAsync(DocumentBuilder b, string id, string novelId)
    {
        var notes = await _store.GetNotes(novelId);
        var note = notes.FirstOrDefault(n => n.Id == id);
        return note is null ? (null!, []) : b.BuildAuthorNote(note);
    }

    /// <summary>
    /// 从存储中异步读取角色关系并构建检索文档。
    /// </summary>
    private async Task<(
        RetrievalDocument Doc,
        List<RetrievalChunk> Chunks
    )> BuildRelationshipDocAsync(DocumentBuilder b, string id, string novelId)
    {
        var rel = await _store.GetRelationship(novelId, id);
        return rel is null ? (null!, []) : b.BuildRelationship(rel);
    }

    /// <summary>
    /// 从存储中异步读取组织关系并构建检索文档。
    /// </summary>
    private async Task<(
        RetrievalDocument Doc,
        List<RetrievalChunk> Chunks
    )> BuildFactionRelationshipDocAsync(DocumentBuilder b, string id, string novelId)
    {
        var rels = await _store.GetFactionRelationships(novelId);
        var rel = rels.FirstOrDefault(r => r.Id == id);
        return rel is null ? (null!, []) : b.BuildFactionRelationship(rel);
    }

    /// <summary>
    /// 将 UI 实体类型映射到检索索引中的 SourceEntityType 字符串。
    /// 用于索引任务的 EntityType 字段和删除操作的实体类型标识。
    /// </summary>
    /// <param name="type">UI 实体类型</param>
    /// <returns>检索索引实体类型字符串，未知类型返回 null</returns>
    private static string? EntityTypeToSourceType(EntityType type) =>
        type switch
        {
            EntityType.Character => "Character",
            EntityType.Chapter => "Chapter",
            EntityType.Plotline => "Plotline",
            EntityType.Location => "Location",
            EntityType.Item => "Item",
            EntityType.Faction => "Faction",
            EntityType.Scene => "SceneDefinition",
            EntityType.TimelineEvent => "TimelineEvent",
            EntityType.Relationship => "Relationship",
            EntityType.FactionRelationship => "FactionRelationship",
            EntityType.Note => "Note",
            _ => null,
        };
}
