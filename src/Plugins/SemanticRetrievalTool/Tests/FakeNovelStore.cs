using Core.Data;
using Core.Novels;

namespace SemanticRetrievalTool.Tests;

/// <summary>
/// 简化的 FakeNovelStore，仅实现 RagTools 测试所需的少量方法。
/// </summary>
public sealed class FakeNovelStore : INovelStore
{
    private readonly Dictionary<string, Character> _characters = new();
    private readonly Dictionary<string, SceneDefinition> _scenes = new();
    private readonly Dictionary<string, Plotline> _plotlines = new();
    private readonly Dictionary<string, Location> _locations = new();
    private readonly Dictionary<string, Faction> _factions = new();
    private readonly Dictionary<string, Item> _items = new();
    private readonly Dictionary<string, TimelineEvent> _timelineEvents = new();
    private readonly Dictionary<string, AuthorNote> _notes = new();
    private readonly Dictionary<string, Relationship> _relationships = new();
    private readonly Dictionary<string, FactionRelationship> _factionRelationships = new();
    private readonly Dictionary<string, Chapter> _chapters = new();

    public FakeNovelStore AddChapter(Chapter ch)
    {
        _chapters[ch.Id] = ch;
        return this;
    }

    public FakeNovelStore AddCharacter(Character ch)
    {
        _characters[ch.Id] = ch;
        return this;
    }

    public FakeNovelStore AddScene(SceneDefinition s)
    {
        _scenes[s.Id] = s;
        return this;
    }

    public FakeNovelStore AddPlotline(Plotline p)
    {
        _plotlines[p.Id] = p;
        return this;
    }

    public FakeNovelStore AddLocation(Location l)
    {
        _locations[l.Id] = l;
        return this;
    }

    public FakeNovelStore AddFaction(Faction f)
    {
        _factions[f.Id] = f;
        return this;
    }

    public FakeNovelStore AddItem(Item i)
    {
        _items[i.Id] = i;
        return this;
    }

    public FakeNovelStore AddTimelineEvent(TimelineEvent e)
    {
        _timelineEvents[e.Id] = e;
        return this;
    }

    public FakeNovelStore AddRelationship(Relationship r)
    {
        _relationships[r.Id] = r;
        return this;
    }

    public FakeNovelStore AddFactionRelationship(FactionRelationship r)
    {
        _factionRelationships[r.Id] = r;
        return this;
    }

    public event Func<EntityChangeEvent, Task>? OnEntityChanged
    {
        add { }
        remove { }
    }

    public Task<Character?> GetCharacter(string id, CancellationToken ct = default) =>
        Task.FromResult(_characters.GetValueOrDefault(id));

    public Task<Character?> GetCharacter(
        string novelId,
        string id,
        CancellationToken ct = default
    ) =>
        Task.FromResult(
            _characters.TryGetValue(id, out var ch) && ch.NovelId == novelId ? ch : null
        );

    public Task<List<Character>> GetCharacters(string novelId, CancellationToken ct = default) =>
        Task.FromResult(_characters.Values.Where(c => c.NovelId == novelId).ToList());

    public Task<SceneDefinition?> GetScene(string id, CancellationToken ct = default) =>
        Task.FromResult(_scenes.GetValueOrDefault(id));

    public Task<SceneDefinition?> GetScene(
        string novelId,
        string id,
        CancellationToken ct = default
    ) => Task.FromResult(_scenes.TryGetValue(id, out var s) && s.NovelId == novelId ? s : null);

    public Task<List<SceneDefinition>> GetScenesByNovel(
        string novelId,
        CancellationToken ct = default
    ) => Task.FromResult(_scenes.Values.Where(s => s.NovelId == novelId).ToList());

    public Task<List<SceneDefinition>> GetScenesByChapter(
        string chapterId,
        CancellationToken ct = default
    ) => Task.FromResult(_scenes.Values.Where(s => s.ChapterId == chapterId).ToList());

    public Task<Plotline?> GetPlotline(string id, CancellationToken ct = default) =>
        Task.FromResult(_plotlines.GetValueOrDefault(id));

    public Task<Plotline?> GetPlotline(string novelId, string id, CancellationToken ct = default) =>
        Task.FromResult(_plotlines.TryGetValue(id, out var p) && p.NovelId == novelId ? p : null);

    public Task<List<Plotline>> GetPlotlines(string novelId, CancellationToken ct = default) =>
        Task.FromResult(_plotlines.Values.Where(p => p.NovelId == novelId).ToList());

    public Task<Location?> GetLocation(string id, CancellationToken ct = default) =>
        Task.FromResult(_locations.GetValueOrDefault(id));

    public Task<Location?> GetLocation(string novelId, string id, CancellationToken ct = default) =>
        Task.FromResult(_locations.TryGetValue(id, out var l) && l.NovelId == novelId ? l : null);

    public Task<List<Location>> GetLocations(string novelId, CancellationToken ct = default) =>
        Task.FromResult(_locations.Values.Where(l => l.NovelId == novelId).ToList());

    public Task<Faction?> GetFaction(string id, CancellationToken ct = default) =>
        Task.FromResult(_factions.GetValueOrDefault(id));

    public Task<Faction?> GetFaction(string novelId, string id, CancellationToken ct = default) =>
        Task.FromResult(_factions.TryGetValue(id, out var f) && f.NovelId == novelId ? f : null);

    public Task<List<Faction>> GetFactions(string novelId, CancellationToken ct = default) =>
        Task.FromResult(_factions.Values.Where(f => f.NovelId == novelId).ToList());

    public Task<Item?> GetItem(string id, CancellationToken ct = default) =>
        Task.FromResult(_items.GetValueOrDefault(id));

    public Task<Item?> GetItem(string novelId, string id, CancellationToken ct = default) =>
        Task.FromResult(_items.TryGetValue(id, out var i) && i.NovelId == novelId ? i : null);

    public Task<List<Item>> GetItems(string novelId, CancellationToken ct = default) =>
        Task.FromResult(_items.Values.Where(i => i.NovelId == novelId).ToList());

    public Task<List<TimelineEvent>> GetTimelineEvents(
        string novelId,
        CancellationToken ct = default
    ) => Task.FromResult(_timelineEvents.Values.Where(e => e.NovelId == novelId).ToList());

    public Task<List<AuthorNote>> GetNotes(string novelId, CancellationToken ct = default) =>
        Task.FromResult(_notes.Values.Where(n => n.NovelId == novelId).ToList());

    public Task SaveNote(AuthorNote note, CancellationToken ct = default)
    {
        _notes[note.Id] = note;
        return Task.CompletedTask;
    }

    public Task<Relationship?> GetRelationship(string id, CancellationToken ct = default) =>
        Task.FromResult(_relationships.GetValueOrDefault(id));

    public Task<Relationship?> GetRelationship(
        string novelId,
        string id,
        CancellationToken ct = default
    ) =>
        Task.FromResult(
            _relationships.TryGetValue(id, out var r) && r.NovelId == novelId ? r : null
        );

    public Task<List<Relationship>> GetRelationshipsByNovel(
        string novelId,
        CancellationToken ct = default
    ) => Task.FromResult(_relationships.Values.Where(r => r.NovelId == novelId).ToList());

    public Task<List<Relationship>> GetRelationshipsByCharacter(
        string characterId,
        CancellationToken ct = default
    ) =>
        Task.FromResult(
            _relationships
                .Values.Where(r => r.CharacterAId == characterId || r.CharacterBId == characterId)
                .ToList()
        );

    public Task<List<WorldRule>> GetWorldRules(string novelId, CancellationToken ct = default) =>
        Task.FromResult(new List<WorldRule>());

    public Task SaveWorldRule(string novelId, WorldRule rule, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task DeleteWorldRule(string novelId, string ruleId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<List<FactionRelationship>> GetFactionRelationships(
        string novelId,
        CancellationToken ct = default
    ) => Task.FromResult(_factionRelationships.Values.Where(r => r.NovelId == novelId).ToList());

    // Unused stubs
    public Task<Novel?> GetNovel(string id, CancellationToken ct = default) =>
        Task.FromResult<Novel?>(null);

    public Task<Novel?> GetNovelSummary(string id, CancellationToken ct = default) =>
        Task.FromResult<Novel?>(null);

    public Task<List<Novel>> GetAllNovels(CancellationToken ct = default) =>
        Task.FromResult(new List<Novel>());

    public Task SaveNovel(Novel novel, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteNovel(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task<Chapter?> GetChapter(string id, CancellationToken ct = default) =>
        Task.FromResult<Chapter?>(null);

    public Task<Chapter?> GetChapter(string novelId, string id, CancellationToken ct = default) =>
        Task.FromResult<Chapter?>(null);

    public Task<List<Chapter>> GetChapters(string novelId, CancellationToken ct = default) =>
        Task.FromResult(_chapters.Values.Where(ch => ch.NovelId == novelId).ToList());

    /// <summary>该 fake 无小说登记表（GetNovelSummary 恒 null），结构索引同样返回 null。</summary>
    public Task<NovelStructure?> GetStructure(string novelId, CancellationToken ct = default) =>
        Task.FromResult<NovelStructure?>(null);

    public Task<Chapter?> GetChapterByOrder(
        string novelId,
        int order,
        CancellationToken ct = default
    ) =>
        Task.FromResult(
            _chapters.Values.FirstOrDefault(ch => ch.NovelId == novelId && ch.Order == order)
        );

    public Task SaveChapter(Chapter chapter, CancellationToken ct = default) => Task.CompletedTask;

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
        Task.CompletedTask;

    public Task<int> GetChapterCount(string novelId, CancellationToken ct = default) =>
        Task.FromResult(0);

    public Task SaveCharacter(Character character, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task DeleteCharacter(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteCharacter(string novelId, string id, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<int> GetCharacterCount(string novelId, CancellationToken ct = default) =>
        Task.FromResult(0);

    public Task<List<Plotline>> GetOpenPlotlines(string novelId, CancellationToken ct = default) =>
        Task.FromResult(new List<Plotline>());

    public Task SavePlotline(Plotline plotline, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task DeletePlotline(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeletePlotline(string novelId, string id, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task SaveFaction(Faction faction, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteFaction(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteFaction(string novelId, string id, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task SaveFactionRelationship(FactionRelationship rel, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task SaveLocation(Location location, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task DeleteLocation(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteLocation(string novelId, string id, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task SaveItem(Item item, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteItem(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteItem(string novelId, string id, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task SaveTimelineEvent(TimelineEvent evt, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task SaveRelationship(Relationship relationship, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task DeleteRelationship(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteRelationship(string novelId, string id, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<List<EntityAuditLog>> GetEntityAuditLog(
        string entityId,
        CancellationToken ct = default
    ) => Task.FromResult(new List<EntityAuditLog>());

    public Task WriteAuditLogs(
        string novelId,
        EntityType entityType,
        string entityId,
        AuditAction action,
        Dictionary<string, string>? fieldChanges,
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

    public Task SaveScene(SceneDefinition scene, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task DeleteScene(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteScene(string novelId, string id, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<int> GetSceneCount(string novelId, CancellationToken ct = default) =>
        Task.FromResult(0);

    public Task<int> GetTotalWordCount(string novelId, CancellationToken ct = default) =>
        Task.FromResult(0);

    public Task<string?> GetCharacterNameById(
        string novelId,
        string characterId,
        CancellationToken ct = default
    ) => Task.FromResult<string?>(null);

    // ── Entity Evolution（entity-evolution-lifecycle：测试桩内存实现）──
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

    // Volume（大纲二层：卷纲）— 测试桩：内存实现最小可用
    public Task<Volume?> GetVolume(string id, CancellationToken ct = default) =>
        Task.FromResult<Volume?>(null);

    public Task<Volume?> GetVolume(string novelId, string id, CancellationToken ct = default) =>
        Task.FromResult<Volume?>(null);

    public Task<List<Volume>> GetVolumes(string novelId, CancellationToken ct = default) =>
        Task.FromResult(new List<Volume>());

    public Task SaveVolume(Volume volume, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteVolume(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteVolume(string novelId, string id, CancellationToken ct = default) =>
        Task.CompletedTask;
}
