using System.Text.Json;
using ClientPlatform;
using Core.Cloud;
using Core.Data;
using Core.Novels;
using Core.Shared;
using Plugins.WritingTools;
using Xunit;

namespace WritingTools.Tests;

/// <summary>
/// DiagnoseWritingTool 云端 Harness 测试（CHANGE_07）：
/// 诊断全部走云端，无本地回退。云端不可用时返回可读错误，不伪造分数。
/// </summary>
public sealed class DiagnoseWritingToolCloudTests
{
    private sealed class StoreStub : INovelStore
    {
        public Task<Novel?> GetNovel(string id, CancellationToken ct = default) =>
            Task.FromResult<Novel?>(
                new Novel
                {
                    Id = "n1",
                    Title = "测试",
                    Genre = "玄幻",
                    EnhancementProfile = "commercial",
                    Characters =
                    [
                        new Character { Name = "林风", Role = CharacterRole.Protagonist },
                    ],
                }
            );

        public Task<Novel?> GetNovelSummary(string id, CancellationToken ct = default) =>
            GetNovel(id);

        public Task<List<Novel>> GetAllNovels(CancellationToken ct = default) =>
            Task.FromResult(new List<Novel>());

        public Task<List<Chapter>> GetChapters(string novelId, CancellationToken ct = default) =>
            Task.FromResult(
                new List<Chapter>
                {
                    new()
                    {
                        Id = "ch1",
                        NovelId = "n1",
                        Title = "第一章",
                        Order = 1,
                        Content = "少年握紧长剑，冲向敌阵。",
                    },
                }
            );

        public Task<Chapter?> GetChapter(
            string novelId,
            string id,
            CancellationToken ct = default
        ) =>
            Task.FromResult<Chapter?>(
                new Chapter
                {
                    Id = "ch1",
                    NovelId = "n1",
                    Title = "第一章",
                    Order = 1,
                    Content = "少年握紧长剑，冲向敌阵。",
                }
            );

        public Task<Chapter?> GetChapter(string id, CancellationToken ct = default) =>
            Task.FromResult<Chapter?>(
                new Chapter
                {
                    Id = "ch1",
                    NovelId = "n1",
                    Title = "第一章",
                    Order = 1,
                    Content = "少年握紧长剑，冲向敌阵。",
                }
            );

        public event Func<EntityChangeEvent, Task>? OnEntityChanged
        {
            add { }
            remove { }
        }

        public Task SaveNovel(Novel novel, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteNovel(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task SaveChapter(Chapter chapter, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task ReorderAllChapters(
            string novelId,
            IReadOnlyList<ChapterOrderAssignment> assignments,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task DeleteChapter(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteChapter(string novelId, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<int> GetChapterCount(string novelId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Character?> GetCharacter(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Character?> GetCharacter(
            string novelId,
            string id,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task<List<Character>> GetCharacters(
            string novelId,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task SaveCharacter(Character character, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteCharacter(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteCharacter(string novelId, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<int> GetCharacterCount(string novelId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Plotline?> GetPlotline(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Plotline?> GetPlotline(
            string novelId,
            string id,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task<List<Plotline>> GetPlotlines(string novelId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<List<Plotline>> GetOpenPlotlines(
            string novelId,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task SavePlotline(Plotline plotline, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeletePlotline(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeletePlotline(string novelId, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Faction?> GetFaction(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Faction?> GetFaction(
            string novelId,
            string id,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task<List<Faction>> GetFactions(string novelId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task SaveFaction(Faction faction, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteFaction(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteFaction(string novelId, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task SaveFactionRelationship(
            FactionRelationship rel,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

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
        ) => throw new NotSupportedException();

        public Task<Location?> GetLocation(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Location?> GetLocation(
            string novelId,
            string id,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task<List<Location>> GetLocations(string novelId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task SaveLocation(Location location, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteLocation(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteLocation(string novelId, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Item?> GetItem(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Item?> GetItem(string novelId, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<List<Item>> GetItems(string novelId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task SaveItem(Item item, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteItem(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteItem(string novelId, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<List<TimelineEvent>> GetTimelineEvents(
            string novelId,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task SaveTimelineEvent(TimelineEvent evt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<List<AuthorNote>> GetNotes(string novelId, CancellationToken ct = default) =>
            Task.FromResult<List<AuthorNote>>([]);

        public Task SaveNote(AuthorNote note, CancellationToken ct = default) => Task.CompletedTask;

        public Task<Relationship?> GetRelationship(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Relationship?> GetRelationship(
            string novelId,
            string id,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task<List<Relationship>> GetRelationshipsByCharacter(
            string characterId,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task<List<Relationship>> GetRelationshipsByNovel(
            string novelId,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task SaveRelationship(Relationship relationship, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteRelationship(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteRelationship(string novelId, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<List<EntityAuditLog>> GetEntityAuditLog(
            string entityId,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

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
        ) => throw new NotSupportedException();

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
            throw new NotSupportedException();

        public Task<SceneDefinition?> GetScene(
            string novelId,
            string id,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task<List<SceneDefinition>> GetScenesByChapter(
            string chapterId,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task<List<SceneDefinition>> GetScenesByNovel(
            string novelId,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task SaveScene(SceneDefinition scene, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteScene(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteScene(string novelId, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<int> GetSceneCount(string novelId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<int> GetTotalWordCount(string novelId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string?> GetCharacterNameById(
            string novelId,
            string characterId,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        // ── Entity Evolution（ENTITY-EVOLUTION：桩不消费演化数据）──
        public Task<List<AppearanceRecord>> GetAppearances(
            string novelId,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task<List<AppearanceRecord>> GetAppearancesByChapter(
            string novelId,
            string chapterId,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task<NovelStructure?> GetStructure(string novelId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Chapter?> GetChapterByOrder(
            string novelId,
            int order,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task<List<EntityStateChange>> GetEntityStateChangesByChapter(
            string novelId,
            string chapterId,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task SaveChapterAppearances(
            string novelId,
            string chapterId,
            IReadOnlyList<AppearanceRecord> records,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task DeleteChapterAppearances(
            string novelId,
            string chapterId,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task<List<EntityStateChange>> GetEntityStateChanges(
            string novelId,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task<List<EntityStateChange>> GetEntityStateChangesByEntity(
            string novelId,
            EntityType entityType,
            string entityId,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task SaveEntityStateChanges(
            string novelId,
            string chapterId,
            IReadOnlyList<EntityStateChange> changes,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task DeleteEntityStateChanges(
            string novelId,
            string chapterId,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

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

    private sealed class StubHarness(HarnessResult result) : IFictionHarnessClient
    {
        public int CallCount { get; private set; }
        public HarnessRequest? LastRequest { get; private set; }

        public Task<HarnessResult> ExecuteAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken ct
        )
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(result);
        }

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

    private sealed class ThrowingHarness : IFictionHarnessClient
    {
        public Task<HarnessResult> ExecuteAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken ct
        ) => throw new CloudApiException("service_unavailable", "down");

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

    private sealed class RejectingHarness(CloudApiException exception) : IFictionHarnessClient
    {
        public Task<HarnessResult> ExecuteAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken ct
        ) => throw exception;

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

    private static DiagnoseWritingParams Params() => new() { NovelId = "n1" };

    [Fact]
    public async Task ExecuteAsync_WithCloudHarness_UsesCloudResults()
    {
        var harness = new StubHarness(
            new HarnessResult
            {
                Status = "completed",
                Capability = "fiction.story.check",
                Result = JsonSerializer.SerializeToElement(
                    new Dictionary<string, object>
                    {
                        ["status"] = "pass",
                        ["capability"] = "fiction.story.check",
                        ["dimensions"] = new Dictionary<string, object>
                        {
                            ["immersion"] = new Dictionary<string, object>
                            {
                                ["overall-score"] = 85.0,
                                ["issues"] = new object[]
                                {
                                    new Dictionary<string, object?>
                                    {
                                        ["dimension"] = DimensionKeys.Immersion,
                                        ["severity"] = Severity.High,
                                        ["description"] = "云端诊断：精确身高问题",
                                        ["paragraph-index"] = 2,
                                        ["impact-score"] = 77,
                                    },
                                },
                            },
                            ["pace"] = new Dictionary<string, object> { ["overall-score"] = 97.0 },
                        },
                    }
                ),
            }
        );
        var tool = new DiagnoseWritingTool(new StoreStub(), null, harness);

        var result = await tool.ExecuteAsync("call-1", Params(), CancellationToken.None, null);

        Assert.Equal(1, harness.CallCount);
        Assert.False(result.IsError);
        Assert.Equal("fiction.story.check", harness.LastRequest!.Capability);
        var report = Assert.IsType<DiagnosticReport>(result.Details);
        Assert.Equal(91, report.OverallScore, 1);
        Assert.Equal(85, report.DimensionScores[DimensionKeys.Immersion], 1);
        Assert.Contains(report.Issues, i => i.Dimension == DimensionKeys.Immersion);
    }

    [Fact]
    public async Task ExecuteAsync_NetworkDimension_InjectsNRules()
    {
        var harness = new StubHarness(
            new HarnessResult
            {
                Status = "completed",
                Capability = "fiction.story.check",
                Result = JsonSerializer.SerializeToElement(
                    new Dictionary<string, object>
                    {
                        ["status"] = "pass",
                        ["capability"] = "fiction.story.check",
                        ["dimensions"] = new Dictionary<string, object>
                        {
                            ["immersion"] = new Dictionary<string, object>
                            {
                                ["overall-score"] = 90.0,
                            },
                            ["network"] = new Dictionary<string, object>
                            {
                                ["character-page-rank"] = new Dictionary<string, object?>
                                {
                                    ["top-character"] = "林风",
                                    ["protagonist-rank"] = 5,
                                    ["rank-gini"] = 0.45,
                                    ["hub-ratio"] = 1.8,
                                    ["n1-protagonist-eclipsed"] = true,
                                    ["n2-network-concentrated"] = false,
                                },
                                ["chapter-hubs"] = new Dictionary<string, object?>
                                {
                                    ["hub-chapter-count"] = 1,
                                    ["hub-titles"] = new[] { "第三章" },
                                    ["rank-gini"] = 0.62,
                                    ["n4-hub-poverty"] = false,
                                },
                                ["foreshadowing-network"] = new Dictionary<string, object?>
                                {
                                    ["plotline-ranks"] = new Dictionary<string, double>
                                    {
                                        ["主线"] = 0.3,
                                        ["暗线"] = 0.7,
                                    },
                                    ["n5-plotline-drift"] = true,
                                },
                                ["protagonist-arc"] = new Dictionary<string, object?>
                                {
                                    ["sliding-decline-chapters"] = 3,
                                    ["n3-protagonist-marginalized"] = true,
                                },
                                ["golden-sentences"] = new Dictionary<string, object?>
                                {
                                    ["golden-sentence-density-per-thousand"] = 1.2,
                                    ["last-sentence-hook-score"] = 0.86,
                                },
                            },
                        },
                    }
                ),
            }
        );
        var tool = new DiagnoseWritingTool(new StoreStub(), null, harness);

        var result = await tool.ExecuteAsync("call-4", Params(), CancellationToken.None, null);

        Assert.False(result.IsError);
        Assert.Equal("fiction.story.check", harness.LastRequest!.Capability);
        var report = Assert.IsType<DiagnosticReport>(result.Details);
        Assert.Contains(
            report.Issues,
            i => i.Dimension == DimensionKeys.CharacterArc && i.Description.Contains("配角喧宾夺主")
        );
        Assert.Contains(
            report.Issues,
            i =>
                i.Dimension == DimensionKeys.CharacterArc
                && i.Description.Contains("主角 PR 排名连续")
        );
        Assert.Contains(
            report.Issues,
            i => i.Dimension == DimensionKeys.Foreshadowing && i.Description.Contains("支线漂移")
        );
        Assert.Contains(report.Strengths, s => s.Contains("TextRank"));
    }

    [Fact]
    public async Task ExecuteAsync_CloudFailure_ReturnsCloudUnavailableReport()
    {
        // CHANGE_07: 云端失败不再回退本地，而是返回可读错误报告
        var tool = new DiagnoseWritingTool(new StoreStub(), null, new ThrowingHarness());
        var result = await tool.ExecuteAsync("call-2", Params(), CancellationToken.None, null);

        Assert.False(result.IsError);
        var report = Assert.IsType<DiagnosticReport>(result.Details);
        Assert.Equal(0, report.OverallScore);
        Assert.Empty(report.DimensionScores);
        Assert.Contains(report.Issues, i => i.Dimension == "云端");
    }

    [Fact]
    public async Task ExecuteAsync_BusinessRejection_SurfacesServerDetail()
    {
        // 业务拒绝（如深度超出当前套餐）必须呈现服务端 detail，不伪装成「云端不可用」
        var harness = new RejectingHarness(
            new CloudApiException(
                "invalid_analysis_input",
                "Depth 'deep' is not available for the free plan (available: quick)."
            )
        );
        var tool = new DiagnoseWritingTool(new StoreStub(), null, harness);

        var result = await tool.ExecuteAsync("call-5", Params(), CancellationToken.None, null);

        Assert.False(result.IsError);
        var report = Assert.IsType<DiagnosticReport>(result.Details);
        Assert.Equal(0, report.OverallScore);
        Assert.Empty(report.DimensionScores);
        var issue = Assert.Single(report.Issues);
        Assert.Equal("云端", issue.Dimension);
        Assert.Contains("Depth 'deep' is not available", issue.Description);
        Assert.DoesNotContain("云端不可用", issue.Description);
    }

    [Fact]
    public async Task ExecuteAsync_NoCloudHarness_ReturnsCloudUnavailableError()
    {
        // CHANGE_07: 无云端配置时返回可读错误，不伪造分数
        var tool = new DiagnoseWritingTool(new StoreStub(), null, null);
        var result = await tool.ExecuteAsync("call-3", Params(), CancellationToken.None, null);

        Assert.True(result.IsError);
    }
}
