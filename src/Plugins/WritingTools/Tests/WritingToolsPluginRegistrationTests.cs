using Core.Agent;
using Core.Cloud;
using Core.Data;
using Core.Novels;
using Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Plugins.WritingTools;
using Xunit;

namespace WritingTools.Tests;

/// <summary>
/// WritingToolsPlugin 工具注册测试（CHANGE_07 harness 接线）：
/// IFictionHarnessClient 注入后应注册全部 10 个工具（diagnose_writing + 9 enhance_*）；
/// harness 缺失时仅注册 diagnose_writing（增强工具依赖云端诊断）。
/// </summary>
public sealed class WritingToolsPluginRegistrationTests
{
    private sealed class StoreStub : INovelStore
    {
        public event Func<EntityChangeEvent, Task>? OnEntityChanged
        {
            add { }
            remove { }
        }

        public Task<Novel?> GetNovel(string id, CancellationToken ct = default) =>
            Task.FromResult<Novel?>(null);

        public Task<Novel?> GetNovelSummary(string id, CancellationToken ct = default) =>
            Task.FromResult<Novel?>(null);

        public Task<List<Novel>> GetAllNovels(CancellationToken ct = default) =>
            Task.FromResult<List<Novel>>([]);

        public Task SaveNovel(Novel novel, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteNovel(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<Chapter?> GetChapter(string id, CancellationToken ct = default) =>
            Task.FromResult<Chapter?>(null);

        public Task<Chapter?> GetChapter(
            string novelId,
            string id,
            CancellationToken ct = default
        ) => Task.FromResult<Chapter?>(null);

        public Task<List<Chapter>> GetChapters(string novelId, CancellationToken ct = default) =>
            Task.FromResult<List<Chapter>>([]);

        public Task SaveChapter(Chapter chapter, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ReorderAllChapters(
            string novelId,
            IReadOnlyList<ChapterOrderAssignment> assignments,
            CancellationToken ct = default
        ) => Task.CompletedTask;

        public Task DeleteChapter(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteChapter(string novelId, string id, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<int> GetChapterCount(string novelId, CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<Character?> GetCharacter(string id, CancellationToken ct = default) =>
            Task.FromResult<Character?>(null);

        public Task<Character?> GetCharacter(
            string novelId,
            string id,
            CancellationToken ct = default
        ) => Task.FromResult<Character?>(null);

        public Task<List<Character>> GetCharacters(
            string novelId,
            CancellationToken ct = default
        ) => Task.FromResult<List<Character>>([]);

        public Task SaveCharacter(Character character, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteCharacter(string id, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteCharacter(string novelId, string id, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<int> GetCharacterCount(string novelId, CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<Plotline?> GetPlotline(string id, CancellationToken ct = default) =>
            Task.FromResult<Plotline?>(null);

        public Task<Plotline?> GetPlotline(
            string novelId,
            string id,
            CancellationToken ct = default
        ) => Task.FromResult<Plotline?>(null);

        public Task<List<Plotline>> GetPlotlines(string novelId, CancellationToken ct = default) =>
            Task.FromResult<List<Plotline>>([]);

        public Task<List<Plotline>> GetOpenPlotlines(
            string novelId,
            CancellationToken ct = default
        ) => Task.FromResult<List<Plotline>>([]);

        public Task SavePlotline(Plotline plotline, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeletePlotline(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeletePlotline(string novelId, string id, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<Faction?> GetFaction(string id, CancellationToken ct = default) =>
            Task.FromResult<Faction?>(null);

        public Task<Faction?> GetFaction(
            string novelId,
            string id,
            CancellationToken ct = default
        ) => Task.FromResult<Faction?>(null);

        public Task<List<Faction>> GetFactions(string novelId, CancellationToken ct = default) =>
            Task.FromResult<List<Faction>>([]);

        public Task SaveFaction(Faction faction, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteFaction(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteFaction(string novelId, string id, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SaveFactionRelationship(
            FactionRelationship rel,
            CancellationToken ct = default
        ) => Task.CompletedTask;

        public Task<List<WorldRule>> GetWorldRules(
            string novelId,
            CancellationToken ct = default
        ) => Task.FromResult(new List<WorldRule>());

        public Task SaveWorldRule(string novelId, WorldRule rule, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteWorldRule(
            string novelId,
            string ruleId,
            CancellationToken ct = default
        ) => Task.CompletedTask;

        public Task<List<FactionRelationship>> GetFactionRelationships(
            string novelId,
            CancellationToken ct = default
        ) => Task.FromResult<List<FactionRelationship>>([]);

        public Task<Location?> GetLocation(string id, CancellationToken ct = default) =>
            Task.FromResult<Location?>(null);

        public Task<Location?> GetLocation(
            string novelId,
            string id,
            CancellationToken ct = default
        ) => Task.FromResult<Location?>(null);

        public Task<List<Location>> GetLocations(string novelId, CancellationToken ct = default) =>
            Task.FromResult<List<Location>>([]);

        public Task SaveLocation(Location location, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteLocation(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteLocation(string novelId, string id, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<Item?> GetItem(string id, CancellationToken ct = default) =>
            Task.FromResult<Item?>(null);

        public Task<Item?> GetItem(string novelId, string id, CancellationToken ct = default) =>
            Task.FromResult<Item?>(null);

        public Task<List<Item>> GetItems(string novelId, CancellationToken ct = default) =>
            Task.FromResult<List<Item>>([]);

        public Task SaveItem(Item item, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteItem(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteItem(string novelId, string id, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<List<TimelineEvent>> GetTimelineEvents(
            string novelId,
            CancellationToken ct = default
        ) => Task.FromResult<List<TimelineEvent>>([]);

        public Task SaveTimelineEvent(TimelineEvent evt, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<List<AuthorNote>> GetNotes(string novelId, CancellationToken ct = default) =>
            Task.FromResult<List<AuthorNote>>([]);

        public Task SaveNote(AuthorNote note, CancellationToken ct = default) => Task.CompletedTask;

        public Task<Relationship?> GetRelationship(string id, CancellationToken ct = default) =>
            Task.FromResult<Relationship?>(null);

        public Task<Relationship?> GetRelationship(
            string novelId,
            string id,
            CancellationToken ct = default
        ) => Task.FromResult<Relationship?>(null);

        public Task<List<Relationship>> GetRelationshipsByCharacter(
            string characterId,
            CancellationToken ct = default
        ) => Task.FromResult<List<Relationship>>([]);

        public Task<List<Relationship>> GetRelationshipsByNovel(
            string novelId,
            CancellationToken ct = default
        ) => Task.FromResult<List<Relationship>>([]);

        public Task SaveRelationship(Relationship relationship, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteRelationship(string id, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteRelationship(string novelId, string id, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<List<EntityAuditLog>> GetEntityAuditLog(
            string entityId,
            CancellationToken ct = default
        ) => Task.FromResult<List<EntityAuditLog>>([]);

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

        public Task<SceneDefinition?> GetScene(string id, CancellationToken ct = default) =>
            Task.FromResult<SceneDefinition?>(null);

        public Task<SceneDefinition?> GetScene(
            string novelId,
            string id,
            CancellationToken ct = default
        ) => Task.FromResult<SceneDefinition?>(null);

        public Task<List<SceneDefinition>> GetScenesByChapter(
            string chapterId,
            CancellationToken ct = default
        ) => Task.FromResult<List<SceneDefinition>>([]);

        public Task<List<SceneDefinition>> GetScenesByNovel(
            string novelId,
            CancellationToken ct = default
        ) => Task.FromResult<List<SceneDefinition>>([]);

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

        // ── Entity Evolution（entity-evolution-lifecycle：桩不消费演化数据）──
        public Task<List<AppearanceRecord>> GetAppearances(
            string novelId,
            CancellationToken ct = default
        ) => Task.FromResult(new List<AppearanceRecord>());

        public Task<List<AppearanceRecord>> GetAppearancesByChapter(
            string novelId,
            string chapterId,
            CancellationToken ct = default
        ) => Task.FromResult(new List<AppearanceRecord>());

        public Task<NovelStructure?> GetStructure(string novelId, CancellationToken ct = default) =>
            Task.FromResult<NovelStructure?>(null);

        public Task<Chapter?> GetChapterByOrder(
            string novelId,
            int order,
            CancellationToken ct = default
        ) => Task.FromResult<Chapter?>(null);

        public Task<List<EntityStateChange>> GetEntityStateChangesByChapter(
            string novelId,
            string chapterId,
            CancellationToken ct = default
        ) => Task.FromResult(new List<EntityStateChange>());

        public Task SaveChapterAppearances(
            string novelId,
            string chapterId,
            IReadOnlyList<AppearanceRecord> records,
            CancellationToken ct = default
        ) => Task.CompletedTask;

        public Task DeleteChapterAppearances(
            string novelId,
            string chapterId,
            CancellationToken ct = default
        ) => Task.CompletedTask;

        public Task<List<EntityStateChange>> GetEntityStateChanges(
            string novelId,
            CancellationToken ct = default
        ) => Task.FromResult(new List<EntityStateChange>());

        public Task<List<EntityStateChange>> GetEntityStateChangesByEntity(
            string novelId,
            EntityType entityType,
            string entityId,
            CancellationToken ct = default
        ) => Task.FromResult(new List<EntityStateChange>());

        public Task SaveEntityStateChanges(
            string novelId,
            string chapterId,
            IReadOnlyList<EntityStateChange> changes,
            CancellationToken ct = default
        ) => Task.CompletedTask;

        public Task DeleteEntityStateChanges(
            string novelId,
            string chapterId,
            CancellationToken ct = default
        ) => Task.CompletedTask;

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

    private sealed class StubHarness : IFictionHarnessClient
    {
        public Task<HarnessResult> ExecuteAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken ct
        ) => Task.FromResult(new HarnessResult { Status = "completed" });

        public Task<AgentContextSnapshot> GetContextAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken ct
        ) => Task.FromResult(new AgentContextSnapshot());

        public Task ReportSetupFeedbackAsync(
            IReadOnlyList<(string FindingId, string Decision)> items,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    public Task ReportSetupFeedbackAsync(
        IReadOnlyList<(string FindingId, string Decision)> items,
        CancellationToken cancellationToken
    ) => Task.CompletedTask;

    private static IServiceProvider BuildProvider(IFictionHarnessClient? harness)
    {
        var services = new ServiceCollection();
        services.AddSingleton<INovelStore>(new StoreStub());
        services.AddSingleton<ToolRegistry>();
        services.AddSingleton(sp => new Agent(sp.GetRequiredService<ToolRegistry>()));
        if (harness is not null)
            services.AddSingleton(harness);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void GetTools_WithCloudHarness_RegistersAllTenTools()
    {
        var plugin = new WritingToolsPlugin();
        plugin.Initialize(BuildProvider(new StubHarness()));

        var tools = plugin.GetTools().ToList();

        Assert.Equal(10, tools.Count);
        Assert.Contains(tools, t => t.Name == "diagnose_writing");
        Assert.Contains(tools, t => t.Name == "enhance_immersion");
        Assert.Contains(tools, t => t.Name == "enhance_satisfaction");
        Assert.Contains(tools, t => t.Name == "enhance_pacing");
        Assert.Contains(tools, t => t.Name == "enhance_description");
        Assert.Contains(tools, t => t.Name == "enhance_dialogue");
        Assert.Contains(tools, t => t.Name == "enhance_foreshadowing");
        Assert.Contains(tools, t => t.Name == "enhance_humanizer");
        Assert.Contains(tools, t => t.Name == "enhance_prose_quality");
        Assert.Contains(tools, t => t.Name == "enhance_chapter_hook");
    }

    [Fact]
    public void GetTools_WithoutCloudHarness_RegistersOnlyDiagnoseTool()
    {
        var plugin = new WritingToolsPlugin();
        plugin.Initialize(BuildProvider(null));

        var tools = plugin.GetTools().ToList();

        var diagnose = Assert.Single(tools);
        Assert.Equal("diagnose_writing", diagnose.Name);
    }
}
