using System.Collections.Concurrent;
using Core.Data;
using Core.Novels;

namespace NovelTools.Tests;

/// <summary>
/// 内存版 INovelStore，用于单元测试。仅实现 batch tool 需要的子集方法。
/// </summary>
public sealed class FakeNovelStore : INovelStore
{
    // ── 数据存储字典 ──
    private readonly ConcurrentDictionary<string, Chapter> _chapters = new();
    private readonly ConcurrentDictionary<string, Character> _characters = new();
    private readonly ConcurrentDictionary<string, Plotline> _plotlines = new();
    private readonly ConcurrentDictionary<string, Faction> _factions = new();
    private readonly ConcurrentDictionary<string, Location> _locations = new();
    private readonly ConcurrentDictionary<string, Item> _items = new();
    private readonly ConcurrentDictionary<string, TimelineEvent> _timelineEvents = new();
    private readonly ConcurrentDictionary<string, AuthorNote> _notes = new();
    private readonly ConcurrentDictionary<string, Relationship> _relationships = new();
    private readonly ConcurrentDictionary<string, Novel> _novels = new();
    private readonly ConcurrentDictionary<string, Volume> _volumes = new();

    public event Func<EntityChangeEvent, Task>? OnEntityChanged
    {
        add { }
        remove { }
    }

    // ── Novel 小说 ──
    public Task<Novel?> GetNovel(string id, CancellationToken ct = default) =>
        Task.FromResult(_novels.GetValueOrDefault(id));

    public Task<Novel?> GetNovelSummary(string id, CancellationToken ct = default) =>
        Task.FromResult(_novels.GetValueOrDefault(id));

    public Task<List<Novel>> GetAllNovels(CancellationToken ct = default) =>
        Task.FromResult(_novels.Values.ToList());

    public Task SaveNovel(Novel novel, CancellationToken ct = default)
    {
        _novels[novel.Id] = novel;
        return Task.CompletedTask;
    }

    public Task DeleteNovel(string id, CancellationToken ct = default)
    {
        _novels.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    // ── Chapter 章节 ──
    public Task<Chapter?> GetChapter(string id, CancellationToken ct = default) =>
        Task.FromResult(_chapters.TryGetValue(id, out var ch) ? ch : null);

    public Task<Chapter?> GetChapter(string novelId, string id, CancellationToken ct = default) =>
        GetChapter(id);

    public Task<List<Chapter>> GetChapters(string novelId, CancellationToken ct = default) =>
        Task.FromResult(_chapters.Values.Where(c => c.NovelId == novelId).ToList());

    public Task<NovelStructure?> GetStructure(string novelId, CancellationToken ct = default)
    {
        var novel = _novels.GetValueOrDefault(novelId);
        if (novel is null)
            return Task.FromResult<NovelStructure?>(null);

        var volumes = _volumes
            .Values.Where(v => v.NovelId == novelId)
            .OrderBy(v => v.Order)
            .ToList();
        var volumeIds = volumes.Select(v => v.Id).ToHashSet(StringComparer.Ordinal);
        var chapters = _chapters
            .Values.Where(c => c.NovelId == novelId)
            .OrderBy(c => c.Order)
            .Select(ToHeader)
            .ToList();
        var unassigned = chapters
            .Where(c => string.IsNullOrEmpty(c.VolumeId) || !volumeIds.Contains(c.VolumeId))
            .ToList();

        return Task.FromResult<NovelStructure?>(
            new NovelStructure(novel, volumes, chapters, unassigned)
        );
    }

    public async Task<Chapter?> GetChapterByOrder(
        string novelId,
        int order,
        CancellationToken ct = default
    ) => (await GetChapters(novelId, ct)).FirstOrDefault(c => c.Order == order);

    /// <summary>骨架化：清空正文、把字符数搬进 CharCount（与 FileNovelStore 的结构索引语义一致）。</summary>
    private static Chapter ToHeader(Chapter c) =>
        new()
        {
            Id = c.Id,
            NovelId = c.NovelId,
            Title = c.Title,
            Order = c.Order,
            Status = c.Status,
            VolumeId = c.VolumeId,
            Summary = c.Summary,
            PlotlineIds = c.PlotlineIds,
            CharacterIds = c.CharacterIds,
            Notes = c.Notes,
            CreatedAt = c.CreatedAt,
            UpdatedAt = c.UpdatedAt,
            CharCount = c.CharCount,
        };

    public Task SaveChapter(Chapter chapter, CancellationToken ct = default)
    {
        _chapters[chapter.Id] = chapter;
        return Task.CompletedTask;
    }

    public Task ReorderAllChapters(
        string novelId,
        IReadOnlyList<ChapterOrderAssignment> assignments,
        CancellationToken ct = default
    )
    {
        foreach (var assignment in assignments)
        {
            if (_chapters.TryGetValue(assignment.ChapterId, out var chapter))
                chapter.Order = assignment.Order;
        }
        return Task.CompletedTask;
    }

    public Task DeleteChapter(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteChapter(string novelId, string id, CancellationToken ct = default) =>
        DeleteChapter(id);

    public Task<int> GetChapterCount(string novelId, CancellationToken ct = default) =>
        Task.FromResult(0);

    // ── Character 角色 ──
    public Task<Character?> GetCharacter(string id, CancellationToken ct = default) =>
        Task.FromResult(_characters.GetValueOrDefault(id));

    public Task<Character?> GetCharacter(
        string novelId,
        string id,
        CancellationToken ct = default
    ) => GetCharacter(id);

    public Task<List<Character>> GetCharacters(string novelId, CancellationToken ct = default) =>
        Task.FromResult(_characters.Values.Where(c => c.NovelId == novelId).ToList());

    public Task SaveCharacter(Character character, CancellationToken ct = default)
    {
        _characters[character.Id] = character;
        return Task.CompletedTask;
    }

    public Task DeleteCharacter(string id, CancellationToken ct = default)
    {
        _characters.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public Task DeleteCharacter(string novelId, string id, CancellationToken ct = default) =>
        DeleteCharacter(id);

    public Task<int> GetCharacterCount(string novelId, CancellationToken ct = default) =>
        Task.FromResult(_characters.Values.Count(c => c.NovelId == novelId));

    // ── Plotline 情节线 ──
    public Task<Plotline?> GetPlotline(string id, CancellationToken ct = default) =>
        Task.FromResult(_plotlines.GetValueOrDefault(id));

    public Task<Plotline?> GetPlotline(string novelId, string id, CancellationToken ct = default) =>
        GetPlotline(id);

    public Task<List<Plotline>> GetPlotlines(string novelId, CancellationToken ct = default) =>
        Task.FromResult(_plotlines.Values.Where(p => p.NovelId == novelId).ToList());

    public Task<List<Plotline>> GetOpenPlotlines(string novelId, CancellationToken ct = default) =>
        Task.FromResult(
            _plotlines
                .Values.Where(p => p.NovelId == novelId && p.ResolvedAtChapterId == null)
                .ToList()
        );

    public Task SavePlotline(Plotline plotline, CancellationToken ct = default)
    {
        _plotlines[plotline.Id] = plotline;
        return Task.CompletedTask;
    }

    public Task DeletePlotline(string id, CancellationToken ct = default)
    {
        _plotlines.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public Task DeletePlotline(string novelId, string id, CancellationToken ct = default) =>
        DeletePlotline(id);

    // ── Faction 组织 ──
    public Task<Faction?> GetFaction(string id, CancellationToken ct = default) =>
        Task.FromResult(_factions.GetValueOrDefault(id));

    public Task<Faction?> GetFaction(string novelId, string id, CancellationToken ct = default) =>
        GetFaction(id);

    public Task<List<Faction>> GetFactions(string novelId, CancellationToken ct = default) =>
        Task.FromResult(_factions.Values.Where(f => f.NovelId == novelId).ToList());

    public Task SaveFaction(Faction faction, CancellationToken ct = default)
    {
        _factions[faction.Id] = faction;
        return Task.CompletedTask;
    }

    public Task DeleteFaction(string id, CancellationToken ct = default)
    {
        _factions.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public Task DeleteFaction(string novelId, string id, CancellationToken ct = default) =>
        DeleteFaction(id);

    private readonly List<FactionRelationship> _factionRelationships = [];

    public Task SaveFactionRelationship(FactionRelationship rel, CancellationToken ct = default)
    {
        _factionRelationships.Add(rel);
        return Task.CompletedTask;
    }

    public Task<List<WorldRule>> GetWorldRules(string novelId, CancellationToken ct = default) =>
        Task.FromResult(new List<WorldRule>());

    public Task SaveWorldRule(string novelId, WorldRule rule, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task DeleteWorldRule(string novelId, string ruleId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<List<FactionRelationship>> GetFactionRelationships(
        string novelId,
        CancellationToken ct = default
    ) => Task.FromResult(_factionRelationships.Where(r => r.NovelId == novelId).ToList());

    // ── Location 地点 ──
    public Task<Location?> GetLocation(string id, CancellationToken ct = default) =>
        Task.FromResult(_locations.GetValueOrDefault(id));

    public Task<Location?> GetLocation(string novelId, string id, CancellationToken ct = default) =>
        GetLocation(id);

    public Task<List<Location>> GetLocations(string novelId, CancellationToken ct = default) =>
        Task.FromResult(_locations.Values.Where(l => l.NovelId == novelId).ToList());

    public Task SaveLocation(Location location, CancellationToken ct = default)
    {
        _locations[location.Id] = location;
        return Task.CompletedTask;
    }

    public Task DeleteLocation(string id, CancellationToken ct = default)
    {
        _locations.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public Task DeleteLocation(string novelId, string id, CancellationToken ct = default) =>
        DeleteLocation(id);

    // ── Item 道具 ──
    public Task<Item?> GetItem(string id, CancellationToken ct = default) =>
        Task.FromResult(_items.GetValueOrDefault(id));

    public Task<Item?> GetItem(string novelId, string id, CancellationToken ct = default) =>
        GetItem(id);

    public Task<List<Item>> GetItems(string novelId, CancellationToken ct = default) =>
        Task.FromResult(_items.Values.Where(i => i.NovelId == novelId).ToList());

    public Task SaveItem(Item item, CancellationToken ct = default)
    {
        _items[item.Id] = item;
        return Task.CompletedTask;
    }

    public Task DeleteItem(string id, CancellationToken ct = default)
    {
        _items.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public Task DeleteItem(string novelId, string id, CancellationToken ct = default) =>
        DeleteItem(id);

    // ── TimelineEvent 时间线事件 ──
    public Task<List<TimelineEvent>> GetTimelineEvents(
        string novelId,
        CancellationToken ct = default
    ) => Task.FromResult(_timelineEvents.Values.Where(e => e.NovelId == novelId).ToList());

    public Task SaveTimelineEvent(TimelineEvent evt, CancellationToken ct = default)
    {
        _timelineEvents[evt.Id] = evt;
        return Task.CompletedTask;
    }

    public Task<List<AuthorNote>> GetNotes(string novelId, CancellationToken ct = default) =>
        Task.FromResult(_notes.Values.Where(n => n.NovelId == novelId).ToList());

    public Task SaveNote(AuthorNote note, CancellationToken ct = default)
    {
        _notes[note.Id] = note;
        return Task.CompletedTask;
    }

    // ── Relationship 关系 ──
    public Task<Relationship?> GetRelationship(string id, CancellationToken ct = default) =>
        Task.FromResult(_relationships.GetValueOrDefault(id));

    public Task<Relationship?> GetRelationship(
        string novelId,
        string id,
        CancellationToken ct = default
    ) => GetRelationship(id);

    public Task<List<Relationship>> GetRelationshipsByCharacter(
        string characterId,
        CancellationToken ct = default
    ) => Task.FromResult(new List<Relationship>());

    public Task<List<Relationship>> GetRelationshipsByNovel(
        string novelId,
        CancellationToken ct = default
    ) => Task.FromResult(_relationships.Values.Where(r => r.NovelId == novelId).ToList());

    public Task SaveRelationship(Relationship relationship, CancellationToken ct = default)
    {
        _relationships[relationship.Id] = relationship;
        return Task.CompletedTask;
    }

    public Task DeleteRelationship(string id, CancellationToken ct = default)
    {
        _relationships.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public Task DeleteRelationship(string novelId, string id, CancellationToken ct = default) =>
        DeleteRelationship(id);

    // ── Session — moved to FileSessionStore ──

    // ── Audit 审计日志 ──
    public Task<List<EntityAuditLog>> GetEntityAuditLog(
        string entityId,
        CancellationToken ct = default
    ) => Task.FromResult(new List<EntityAuditLog>());

    public Task WriteAuditLogs(
        string novelId,
        EntityType entityType,
        string entityId,
        AuditAction action,
        Dictionary<string, string>? fieldChanges = null,
        AuditSource source = AuditSource.Ai,
        string? toolName = null,
        string? sessionId = null,
        CancellationToken ct = default
    ) => Task.CompletedTask;

    public Task AppendOntologyActions(
        string novelId,
        IReadOnlyList<OntologyAction> actions,
        CancellationToken ct = default
    ) => Task.CompletedTask;

    public Task<List<OntologyAction>> GetOntologyActions(
        string novelId,
        CancellationToken ct = default
    ) => Task.FromResult(new List<OntologyAction>());

    public Task<long> GetOntologyJournalStamp(string novelId, CancellationToken ct = default) =>
        Task.FromResult(0L);

    // ── Scene 场景 ──
    private readonly ConcurrentDictionary<string, SceneDefinition> _scenes = new();

    public Task<SceneDefinition?> GetScene(string id, CancellationToken ct = default) =>
        Task.FromResult(_scenes.GetValueOrDefault(id));

    public Task<SceneDefinition?> GetScene(
        string novelId,
        string id,
        CancellationToken ct = default
    ) => GetScene(id);

    public Task<List<SceneDefinition>> GetScenesByChapter(
        string chapterId,
        CancellationToken ct = default
    ) => Task.FromResult(_scenes.Values.Where(s => s.ChapterId == chapterId).ToList());

    public Task<List<SceneDefinition>> GetScenesByNovel(
        string novelId,
        CancellationToken ct = default
    ) => Task.FromResult(_scenes.Values.Where(s => s.NovelId == novelId).ToList());

    public Task SaveScene(SceneDefinition scene, CancellationToken ct = default)
    {
        _scenes[scene.Id] = scene;
        return Task.CompletedTask;
    }

    public Task DeleteScene(string id, CancellationToken ct = default)
    {
        _scenes.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public Task DeleteScene(string novelId, string id, CancellationToken ct = default) =>
        DeleteScene(id);

    public Task<int> GetSceneCount(string novelId, CancellationToken ct = default) =>
        Task.FromResult(_scenes.Values.Count(s => s.NovelId == novelId));

    // ── Stats 统计 ──
    public Task<int> GetTotalWordCount(string novelId, CancellationToken ct = default) =>
        Task.FromResult(0);

    // ── Lightweight queries ──
    public Task<string?> GetCharacterNameById(
        string novelId,
        string characterId,
        CancellationToken ct = default
    ) => Task.FromResult(_characters.TryGetValue(characterId, out var c) ? c.Name : null);

    // ── Entity Evolution（ENTITY-EVOLUTION：测试桩内存实现）──
    private readonly Dictionary<string, List<AppearanceRecord>> _appearances = [];
    private readonly Dictionary<string, List<EntityStateChange>> _stateChanges = [];

    public Task<List<AppearanceRecord>> GetAppearances(
        string novelId,
        CancellationToken ct = default
    ) => Task.FromResult(_appearances.TryGetValue(novelId, out var list) ? list.ToList() : []);

    public Task<List<AppearanceRecord>> GetAppearancesByChapter(
        string novelId,
        string chapterId,
        CancellationToken ct = default
    ) =>
        Task.FromResult(
            (
                _appearances.TryGetValue(novelId, out var list)
                    ? list.Where(r => r.ChapterId == chapterId)
                    : []
            ).ToList()
        );

    public Task SaveChapterAppearances(
        string novelId,
        string chapterId,
        IReadOnlyList<AppearanceRecord> records,
        CancellationToken ct = default
    )
    {
        if (!_appearances.TryGetValue(novelId, out var list))
        {
            list = [];
            _appearances[novelId] = list;
        }
        list.RemoveAll(r => r.ChapterId == chapterId);
        list.AddRange(records);
        return Task.CompletedTask;
    }

    public Task DeleteChapterAppearances(
        string novelId,
        string chapterId,
        CancellationToken ct = default
    )
    {
        if (_appearances.TryGetValue(novelId, out var list))
            list.RemoveAll(r => r.ChapterId == chapterId);
        return Task.CompletedTask;
    }

    public Task<List<EntityStateChange>> GetEntityStateChanges(
        string novelId,
        CancellationToken ct = default
    ) => Task.FromResult(_stateChanges.TryGetValue(novelId, out var list) ? list.ToList() : []);

    public Task<List<EntityStateChange>> GetEntityStateChangesByEntity(
        string novelId,
        EntityType entityType,
        string entityId,
        CancellationToken ct = default
    ) =>
        Task.FromResult(
            (
                _stateChanges.TryGetValue(novelId, out var list)
                    ? list.Where(c => c.TargetEntityType == entityType && c.TargetId == entityId)
                    : []
            ).ToList()
        );

    public Task<List<EntityStateChange>> GetEntityStateChangesByChapter(
        string novelId,
        string chapterId,
        CancellationToken ct = default
    ) =>
        Task.FromResult(
            (
                _stateChanges.TryGetValue(novelId, out var list)
                    ? list.Where(c => c.ChapterId == chapterId)
                    : []
            ).ToList()
        );

    public Task SaveEntityStateChanges(
        string novelId,
        string chapterId,
        IReadOnlyList<EntityStateChange> changes,
        CancellationToken ct = default
    )
    {
        if (!_stateChanges.TryGetValue(novelId, out var list))
        {
            list = [];
            _stateChanges[novelId] = list;
        }
        list.RemoveAll(c => c.ChapterId == chapterId);
        list.AddRange(changes);
        return Task.CompletedTask;
    }

    public Task DeleteEntityStateChanges(
        string novelId,
        string chapterId,
        CancellationToken ct = default
    )
    {
        if (_stateChanges.TryGetValue(novelId, out var list))
            list.RemoveAll(c => c.ChapterId == chapterId);
        return Task.CompletedTask;
    }

    // Volume（大纲二层：卷纲）— 内存存储（含删卷级联清空章节归属，与 FileNovelStore 同语义）
    public Task<Volume?> GetVolume(string id, CancellationToken ct = default) =>
        Task.FromResult(_volumes.GetValueOrDefault(id));

    public Task<Volume?> GetVolume(string novelId, string id, CancellationToken ct = default) =>
        GetVolume(id);

    public Task<List<Volume>> GetVolumes(string novelId, CancellationToken ct = default) =>
        Task.FromResult(_volumes.Values.Where(v => v.NovelId == novelId).ToList());

    public Task SaveVolume(Volume volume, CancellationToken ct = default)
    {
        _volumes[volume.Id] = volume;
        return Task.CompletedTask;
    }

    public Task DeleteVolume(string id, CancellationToken ct = default)
    {
        _volumes.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public Task DeleteVolume(string novelId, string id, CancellationToken ct = default)
    {
        _volumes.TryRemove(id, out _);
        foreach (var chapter in _chapters.Values.Where(c => c.VolumeId == id))
            chapter.VolumeId = null;
        return Task.CompletedTask;
    }
}
