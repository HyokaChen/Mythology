using Core.Novels;

namespace Core.Data;

public interface INovelStore
{
    event Func<EntityChangeEvent, Task>? OnEntityChanged;

    // Novel
    Task<Novel?> GetNovel(string id, CancellationToken ct = default);
    Task<Novel?> GetNovelSummary(string id, CancellationToken ct = default);
    Task<List<Novel>> GetAllNovels(CancellationToken ct = default);
    Task SaveNovel(Novel novel, CancellationToken ct = default);
    Task DeleteNovel(string id, CancellationToken ct = default);

    // Chapter
    Task<Chapter?> GetChapter(string id, CancellationToken ct = default);
    Task<Chapter?> GetChapter(string novelId, string id, CancellationToken ct = default);
    Task<List<Chapter>> GetChapters(string novelId, CancellationToken ct = default);

    /// <summary>结构索引：总纲 + 卷 + 章节元数据（不含正文，CharCount 已填）。供设定面板/树形展示使用。</summary>
    Task<NovelStructure?> GetStructure(string novelId, CancellationToken ct = default);

    /// <summary>按章节序号直接读单章（文件名 ch{Order:D3}.md，O(1) 单文件）。</summary>
    Task<Chapter?> GetChapterByOrder(string novelId, int order, CancellationToken ct = default);

    Task SaveChapter(Chapter chapter, CancellationToken ct = default);

    /// <summary>
    /// 全章重排（<see cref="ChapterOrdering.PlanFullReorder"/> 的执行端）。
    /// 章节文件名即序号，置换式改写会互相覆盖，故内部走两阶段临时位搬移，并同步迁移
    /// appearances / evolution 分片（分片同样按 <c>ch{Order:D3}.yaml</c> 命名）。
    /// </summary>
    Task ReorderAllChapters(
        string novelId,
        IReadOnlyList<ChapterOrderAssignment> assignments,
        CancellationToken ct = default
    );
    Task DeleteChapter(string id, CancellationToken ct = default);
    Task DeleteChapter(string novelId, string id, CancellationToken ct = default);
    Task<int> GetChapterCount(string novelId, CancellationToken ct = default);

    // Character
    Task<Character?> GetCharacter(string id, CancellationToken ct = default);
    Task<Character?> GetCharacter(string novelId, string id, CancellationToken ct = default);
    Task<List<Character>> GetCharacters(string novelId, CancellationToken ct = default);
    Task SaveCharacter(Character character, CancellationToken ct = default);
    Task DeleteCharacter(string id, CancellationToken ct = default);
    Task DeleteCharacter(string novelId, string id, CancellationToken ct = default);
    Task<int> GetCharacterCount(string novelId, CancellationToken ct = default);

    // Plotline
    Task<Plotline?> GetPlotline(string id, CancellationToken ct = default);
    Task<Plotline?> GetPlotline(string novelId, string id, CancellationToken ct = default);
    Task<List<Plotline>> GetPlotlines(string novelId, CancellationToken ct = default);
    Task<List<Plotline>> GetOpenPlotlines(string novelId, CancellationToken ct = default);
    Task SavePlotline(Plotline plotline, CancellationToken ct = default);
    Task DeletePlotline(string id, CancellationToken ct = default);
    Task DeletePlotline(string novelId, string id, CancellationToken ct = default);

    // Faction
    Task<Faction?> GetFaction(string id, CancellationToken ct = default);
    Task<Faction?> GetFaction(string novelId, string id, CancellationToken ct = default);
    Task<List<Faction>> GetFactions(string novelId, CancellationToken ct = default);
    Task SaveFaction(Faction faction, CancellationToken ct = default);
    Task DeleteFaction(string id, CancellationToken ct = default);
    Task DeleteFaction(string novelId, string id, CancellationToken ct = default);
    Task SaveFactionRelationship(FactionRelationship rel, CancellationToken ct = default);
    Task<List<FactionRelationship>> GetFactionRelationships(
        string novelId,
        CancellationToken ct = default
    );

    // Location
    Task<Location?> GetLocation(string id, CancellationToken ct = default);
    Task<Location?> GetLocation(string novelId, string id, CancellationToken ct = default);
    Task<List<Location>> GetLocations(string novelId, CancellationToken ct = default);
    Task SaveLocation(Location location, CancellationToken ct = default);
    Task DeleteLocation(string id, CancellationToken ct = default);
    Task DeleteLocation(string novelId, string id, CancellationToken ct = default);

    // Item
    Task<Item?> GetItem(string id, CancellationToken ct = default);
    Task<Item?> GetItem(string novelId, string id, CancellationToken ct = default);
    Task<List<Item>> GetItems(string novelId, CancellationToken ct = default);
    Task SaveItem(Item item, CancellationToken ct = default);
    Task DeleteItem(string id, CancellationToken ct = default);
    Task DeleteItem(string novelId, string id, CancellationToken ct = default);

    // TimelineEvent
    Task<List<TimelineEvent>> GetTimelineEvents(string novelId, CancellationToken ct = default);
    Task SaveTimelineEvent(TimelineEvent evt, CancellationToken ct = default);

    // AuthorNote（书级随手记；软删除 = Status=Dropped，不物理删文件）
    Task<List<AuthorNote>> GetNotes(string novelId, CancellationToken ct = default);
    Task SaveNote(AuthorNote note, CancellationToken ct = default);

    // Relationship
    Task<Relationship?> GetRelationship(string id, CancellationToken ct = default);
    Task<Relationship?> GetRelationship(string novelId, string id, CancellationToken ct = default);
    Task<List<Relationship>> GetRelationshipsByCharacter(
        string characterId,
        CancellationToken ct = default
    );
    Task<List<Relationship>> GetRelationshipsByNovel(
        string novelId,
        CancellationToken ct = default
    );
    Task SaveRelationship(Relationship relationship, CancellationToken ct = default);
    Task DeleteRelationship(string id, CancellationToken ct = default);
    Task DeleteRelationship(string novelId, string id, CancellationToken ct = default);

    // Session — moved to FileSessionStore

    // Audit
    Task<List<EntityAuditLog>> GetEntityAuditLog(string entityId, CancellationToken ct = default);
    Task WriteAuditLogs(
        string novelId,
        EntityType entityType,
        string entityId,
        AuditAction action,
        Dictionary<string, string>? fieldChanges,
        AuditSource source = AuditSource.Ai,
        string? toolName = null,
        string? sessionId = null,
        CancellationToken ct = default
    );

    // Ontology journal（本体动作日志：追加式 ontology/actions.jsonl）
    /// <summary>追加本体动作日志；空列表直接返回。</summary>
    Task AppendOntologyActions(
        string novelId,
        IReadOnlyList<OntologyAction> actions,
        CancellationToken ct = default
    );

    /// <summary>读取全部本体动作日志（文件顺序；损坏行跳过）。</summary>
    Task<List<OntologyAction>> GetOntologyActions(string novelId, CancellationToken ct = default);

    /// <summary>日志文件字节长度（不存在=0）；供每轮 harness 做廉价变更戳。</summary>
    Task<long> GetOntologyJournalStamp(string novelId, CancellationToken ct = default);

    /// <summary>从字节偏移读取动作日志（跨会话摘要用；默认实现忽略偏移读全部）。</summary>
    Task<List<OntologyAction>> GetOntologyActionsAfterOffset(
        string novelId,
        long offset,
        CancellationToken ct = default
    ) => GetOntologyActions(novelId, ct);

    /// <summary>本体日志游标（字节偏移；不存在=0）。</summary>
    Task<long> GetOntologyCursorAsync(string novelId, CancellationToken ct = default) =>
        Task.FromResult(0L);

    /// <summary>推进本体日志游标。</summary>
    Task SetOntologyCursorAsync(string novelId, long offset, CancellationToken ct = default) =>
        Task.CompletedTask;

    // 设定变更（ChapterId=""，evolution/manual.yaml）
    /// <summary>读取全部设定变更（默认实现返回空）。</summary>
    Task<List<EntityStateChange>> GetManualStateChanges(
        string novelId,
        CancellationToken ct = default
    ) => Task.FromResult(new List<EntityStateChange>());

    /// <summary>写入一条设定变更（按 Id 去重）。</summary>
    Task SaveManualStateChange(
        string novelId,
        EntityStateChange change,
        CancellationToken ct = default
    ) => Task.CompletedTask;

    // 变更集（发布/补记事务）
    /// <summary>保存变更集。</summary>
    Task SaveChapterChangeSet(
        string novelId,
        ChapterChangeSet set,
        CancellationToken ct = default
    ) => Task.CompletedTask;

    /// <summary>读取某章的变更集（损坏文件跳过）。</summary>
    Task<List<ChapterChangeSet>> GetChapterChangeSets(
        string novelId,
        string chapterId,
        CancellationToken ct = default
    ) => Task.FromResult(new List<ChapterChangeSet>());

    // Scene
    Task<SceneDefinition?> GetScene(string id, CancellationToken ct = default);
    Task<SceneDefinition?> GetScene(string novelId, string id, CancellationToken ct = default);
    Task<List<SceneDefinition>> GetScenesByChapter(
        string chapterId,
        CancellationToken ct = default
    );
    Task<List<SceneDefinition>> GetScenesByNovel(string novelId, CancellationToken ct = default);
    Task SaveScene(SceneDefinition scene, CancellationToken ct = default);
    Task DeleteScene(string id, CancellationToken ct = default);
    Task DeleteScene(string novelId, string id, CancellationToken ct = default);
    Task<int> GetSceneCount(string novelId, CancellationToken ct = default);

    // Volume（大纲二层：卷纲）
    Task<Volume?> GetVolume(string id, CancellationToken ct = default);
    Task<Volume?> GetVolume(string novelId, string id, CancellationToken ct = default);
    Task<List<Volume>> GetVolumes(string novelId, CancellationToken ct = default);
    Task SaveVolume(Volume volume, CancellationToken ct = default);
    Task DeleteVolume(string id, CancellationToken ct = default);
    Task DeleteVolume(string novelId, string id, CancellationToken ct = default);

    // WorldRules（CHANGE_10 5.3：世界规则/设定，存 world-rules.yaml）
    Task<List<WorldRule>> GetWorldRules(string novelId, CancellationToken ct = default);
    Task SaveWorldRule(string novelId, WorldRule rule, CancellationToken ct = default);
    Task DeleteWorldRule(string novelId, string ruleId, CancellationToken ct = default);

    // Entity Evolution（ENTITY-EVOLUTION §3.3：出场记录 + 变化事件，按章分片，整章替换）
    Task<List<AppearanceRecord>> GetAppearances(string novelId, CancellationToken ct = default);
    Task<List<AppearanceRecord>> GetAppearancesByChapter(
        string novelId,
        string chapterId,
        CancellationToken ct = default
    );
    Task SaveChapterAppearances(
        string novelId,
        string chapterId,
        IReadOnlyList<AppearanceRecord> records,
        CancellationToken ct = default
    );
    Task DeleteChapterAppearances(string novelId, string chapterId, CancellationToken ct = default);
    Task<List<EntityStateChange>> GetEntityStateChanges(
        string novelId,
        CancellationToken ct = default
    );

    /// <summary>按章读取状态变化（按章分片 evolution/ch{Order:D3}.yaml，O(1) 单文件）。</summary>
    Task<List<EntityStateChange>> GetEntityStateChangesByChapter(
        string novelId,
        string chapterId,
        CancellationToken ct = default
    );
    Task<List<EntityStateChange>> GetEntityStateChangesByEntity(
        string novelId,
        EntityType entityType,
        string entityId,
        CancellationToken ct = default
    );
    Task SaveEntityStateChanges(
        string novelId,
        string chapterId,
        IReadOnlyList<EntityStateChange> changes,
        CancellationToken ct = default
    );
    Task DeleteEntityStateChanges(string novelId, string chapterId, CancellationToken ct = default);

    // Stats
    Task<int> GetTotalWordCount(string novelId, CancellationToken ct = default);

    // Lightweight queries
    Task<string?> GetCharacterNameById(
        string novelId,
        string characterId,
        CancellationToken ct = default
    );
}
