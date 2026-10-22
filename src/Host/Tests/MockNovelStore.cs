using Core.Data;
using Core.Novels;

namespace Host.Tests;

/// <summary>
/// 最小化 INovelStore 实现，用于 Host 层单元测试。
/// 仅实现 LoadNovelHook 和基础测试需要的方法。
/// </summary>
public sealed class MockNovelStore : INovelStore
{
    private readonly Dictionary<string, Novel> _novels = new();

    private readonly Dictionary<string, AuthorNote> _notes = new();

    public event Func<EntityChangeEvent, Task>? OnEntityChanged
    {
        add { }
        remove { }
    }

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
        _novels.Remove(id);
        return Task.CompletedTask;
    }

    private readonly Dictionary<string, List<Chapter>> _chapters = new();

    public Task<Chapter?> GetChapter(string id, CancellationToken ct = default) =>
        Task.FromResult(_chapters.Values.SelectMany(c => c).FirstOrDefault(c => c.Id == id));

    public Task<Chapter?> GetChapter(string novelId, string id, CancellationToken ct = default) =>
        GetChapter(id);

    public Task<List<Chapter>> GetChapters(string novelId, CancellationToken ct = default) =>
        Task.FromResult(
            _chapters.TryGetValue(novelId, out var list)
                ? list.OrderBy(c => c.Order).ToList()
                : new List<Chapter>()
        );

    public Task<NovelStructure?> GetStructure(string novelId, CancellationToken ct = default)
    {
        var novel = _novels.GetValueOrDefault(novelId);
        if (novel is null)
            return Task.FromResult<NovelStructure?>(null);

        var volumes = new List<Volume>();
        var chapters = (_chapters.TryGetValue(novelId, out var list) ? list : [])
            .OrderBy(c => c.Order)
            .Select(ToHeader)
            .ToList();

        return Task.FromResult<NovelStructure?>(
            new NovelStructure(novel, volumes, chapters, chapters)
        );
    }

    public async Task<Chapter?> GetChapterByOrder(
        string novelId,
        int order,
        CancellationToken ct = default
    ) => (await GetChapters(novelId, ct)).FirstOrDefault(c => c.Order == order);

    public Task SaveChapter(Chapter chapter, CancellationToken ct = default)
    {
        foreach (var list in _chapters.Values)
            list.RemoveAll(c => c.Id == chapter.Id);
        if (_chapters.Count > 0)
            _chapters.First().Value.Add(chapter);
        return Task.CompletedTask;
    }

    public async Task ReorderAllChapters(
        string novelId,
        IReadOnlyList<ChapterOrderAssignment> assignments,
        CancellationToken ct = default
    )
    {
        var list = _chapters.GetValueOrDefault(novelId);
        if (list is null)
            return;
        foreach (var assignment in assignments)
        {
            var chapter = list.FirstOrDefault(c => c.Id == assignment.ChapterId);
            if (chapter is not null)
                chapter.Order = assignment.Order;
        }
        await Task.CompletedTask;
    }

    /// <summary>测试辅助：向指定小说注入章节（Chapter 实体无 NovelId 字段，按桶分组）。</summary>
    public void AddChapters(string novelId, params Chapter[] chapters)
    {
        var list = _chapters.GetValueOrDefault(novelId) ?? [];
        _chapters[novelId] = list;
        list.AddRange(chapters);
    }

    public Task DeleteChapter(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteChapter(string novelId, string id, CancellationToken ct = default) =>
        DeleteChapter(id);

    public Task<int> GetChapterCount(string novelId, CancellationToken ct = default) =>
        Task.FromResult(_chapters.GetValueOrDefault(novelId)?.Count ?? 0);

    public Task<Character?> GetCharacter(string id, CancellationToken ct = default) =>
        Task.FromResult<Character?>(null);

    public Task<Character?> GetCharacter(
        string novelId,
        string id,
        CancellationToken ct = default
    ) => GetCharacter(id);

    public Task<List<Character>> GetCharacters(string novelId, CancellationToken ct = default) =>
        Task.FromResult(new List<Character>());

    public Task SaveCharacter(Character character, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task DeleteCharacter(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteCharacter(string novelId, string id, CancellationToken ct = default) =>
        DeleteCharacter(id);

    public Task<int> GetCharacterCount(string novelId, CancellationToken ct = default) =>
        Task.FromResult(0);

    public Task<Plotline?> GetPlotline(string id, CancellationToken ct = default) =>
        Task.FromResult<Plotline?>(null);

    public Task<Plotline?> GetPlotline(string novelId, string id, CancellationToken ct = default) =>
        GetPlotline(id);

    public Task<List<Plotline>> GetPlotlines(string novelId, CancellationToken ct = default) =>
        Task.FromResult(new List<Plotline>());

    public Task<List<Plotline>> GetOpenPlotlines(string novelId, CancellationToken ct = default) =>
        Task.FromResult(new List<Plotline>());

    public Task SavePlotline(Plotline plotline, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task DeletePlotline(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeletePlotline(string novelId, string id, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<Faction?> GetFaction(string id, CancellationToken ct = default) =>
        Task.FromResult<Faction?>(null);

    public Task<Faction?> GetFaction(string novelId, string id, CancellationToken ct = default) =>
        GetFaction(id);

    public Task<List<Faction>> GetFactions(string novelId, CancellationToken ct = default) =>
        Task.FromResult(new List<Faction>());

    public Task SaveFaction(Faction faction, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteFaction(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteFaction(string novelId, string id, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task SaveFactionRelationship(FactionRelationship rel, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Phase 5.1：设定图摘要（WritingFlowHook.BuildSetupDigestAsync）的集合读取次数。
    /// 该路径读取 world-rules 一次 → 以此计数断言「只在调度命中后构图」。
    /// </summary>
    public int SetupDigestLoads { get; private set; }

    private readonly List<WorldRule> _worldRules = [];

    public Task<List<WorldRule>> GetWorldRules(string novelId, CancellationToken ct = default)
    {
        SetupDigestLoads++;
        return Task.FromResult(_worldRules.ToList());
    }

    public Task SaveWorldRule(string novelId, WorldRule rule, CancellationToken ct = default)
    {
        _worldRules.RemoveAll(r => string.Equals(r.Id, rule.Id, StringComparison.Ordinal));
        _worldRules.Add(rule);
        return Task.CompletedTask;
    }

    public Task DeleteWorldRule(string novelId, string ruleId, CancellationToken ct = default)
    {
        _worldRules.RemoveAll(r => string.Equals(r.Id, ruleId, StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    public Task<List<FactionRelationship>> GetFactionRelationships(
        string novelId,
        CancellationToken ct = default
    ) => Task.FromResult(new List<FactionRelationship>());

    public Task<Location?> GetLocation(string id, CancellationToken ct = default) =>
        Task.FromResult<Location?>(null);

    public Task<Location?> GetLocation(string novelId, string id, CancellationToken ct = default) =>
        GetLocation(id);

    public Task<List<Location>> GetLocations(string novelId, CancellationToken ct = default) =>
        Task.FromResult(new List<Location>());

    public Task SaveLocation(Location location, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task DeleteLocation(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteLocation(string novelId, string id, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<Item?> GetItem(string id, CancellationToken ct = default) =>
        Task.FromResult<Item?>(null);

    public Task<Item?> GetItem(string novelId, string id, CancellationToken ct = default) =>
        GetItem(id);

    public Task<List<Item>> GetItems(string novelId, CancellationToken ct = default) =>
        Task.FromResult(new List<Item>());

    public Task SaveItem(Item item, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteItem(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteItem(string novelId, string id, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<List<TimelineEvent>> GetTimelineEvents(
        string novelId,
        CancellationToken ct = default
    ) => Task.FromResult(new List<TimelineEvent>());

    public Task SaveTimelineEvent(TimelineEvent evt, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<List<AuthorNote>> GetNotes(string novelId, CancellationToken ct = default) =>
        Task.FromResult(_notes.Values.Where(n => n.NovelId == novelId).ToList());

    public Task SaveNote(AuthorNote note, CancellationToken ct = default)
    {
        _notes[note.Id] = note;
        return Task.CompletedTask;
    }

    public Task<Relationship?> GetRelationship(string id, CancellationToken ct = default) =>
        Task.FromResult<Relationship?>(null);

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
    ) => Task.FromResult(new List<Relationship>());

    public Task SaveRelationship(Relationship relationship, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task DeleteRelationship(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteRelationship(string novelId, string id, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<List<SceneDefinition>> GetScenesByNovel(
        string novelId,
        CancellationToken ct = default
    ) => Task.FromResult(new List<SceneDefinition>());

    private readonly List<SceneDefinition> _scenes = [];

    public Task<SceneDefinition?> GetScene(string id, CancellationToken ct = default) =>
        Task.FromResult<SceneDefinition?>(
            _scenes.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal))
        );

    public Task<SceneDefinition?> GetScene(
        string novelId,
        string id,
        CancellationToken ct = default
    ) => GetScene(id);

    public Task SaveScene(SceneDefinition scene, CancellationToken ct = default)
    {
        _scenes.RemoveAll(s => string.Equals(s.Id, scene.Id, StringComparison.Ordinal));
        _scenes.Add(scene);
        return Task.CompletedTask;
    }

    public Task DeleteScene(string id, CancellationToken ct = default)
    {
        _scenes.RemoveAll(s => string.Equals(s.Id, id, StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    public Task DeleteScene(string novelId, string id, CancellationToken ct = default) =>
        DeleteScene(id);

    public Task<List<SceneDefinition>> GetScenesByChapter(
        string chapterId,
        CancellationToken ct = default
    ) =>
        Task.FromResult(
            _scenes
                .Where(s => string.Equals(s.ChapterId, chapterId, StringComparison.Ordinal))
                .ToList()
        );

    public Task<int> GetSceneCount(string novelId, CancellationToken ct = default) =>
        Task.FromResult(0);

    public Task<int> GetTotalWordCount(string novelId, CancellationToken ct = default) =>
        Task.FromResult(0);

    public Task<string?> GetCharacterNameById(
        string novelId,
        string characterId,
        CancellationToken ct = default
    ) => Task.FromResult<string?>(null);

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
