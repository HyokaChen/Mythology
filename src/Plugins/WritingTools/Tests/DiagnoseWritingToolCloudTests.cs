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

        // ── Entity Evolution（entity-evolution-lifecycle：桩不消费演化数据）──
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

    private static string FindingJson(
        int index,
        string category = "角色",
        string severity = "high",
        string action = "fix-now",
        string problem = "云端诊断问题"
    ) =>
        $$"""
            {"id":"f_00000000000000{{index:D2}}","category":"{{category}}","severity":"{{severity}}","severity-label":"{{(
                severity == "high" ? "严重" : "中等"
            )}}","action":"{{action}}","action-label":"{{(
                action == "fix-now" ? "直接修改" : "建议修改"
            )}}","location":"第 1 章 · 全章","problem":"{{problem}}","evidence":"证据 {{index}}","suggestion":"建议 {{index}}"}
            """;

    private static string ReportJson(double score, string summary, params string[] findings) =>
        $$"""
            {"verdict":"需要修改","action":"revise","summary":"{{summary}}","score":{{score}},"findings":[{{string.Join(
                ",",
                findings
            )}}],"decision-question":null,"decision-options":null,"snapshot":null,"strengths":null}
            """;

    private static HarnessResult ReportResult(
        double score,
        string summary,
        params string[] findings
    ) =>
        new()
        {
            Status = "completed",
            Capability = "fiction.story.check",
            Result = JsonSerializer.Deserialize<JsonElement>(ReportJson(score, summary, findings)),
        };

    [Fact]
    public async Task ExecuteAsync_WithCloudHarness_UsesCloudResults()
    {
        var harness = new StubHarness(
            ReportResult(
                91,
                "云端诊断：精确身高问题",
                FindingJson(1, problem: "云端诊断：精确身高问题")
            )
        );
        var tool = new DiagnoseWritingTool(new StoreStub(), null, harness);

        var result = await tool.ExecuteAsync("call-1", Params(), CancellationToken.None, null);

        Assert.Equal(1, harness.CallCount);
        Assert.False(result.IsError);
        Assert.Equal("fiction.story.check", harness.LastRequest!.Capability);
        var report = Assert.IsType<DiagnosticReport>(result.Details);
        Assert.Equal(91, report.OverallScore, 1);
        Assert.Contains(report.Issues, i => i.Dimension == "角色");
    }

    [Fact]
    public async Task ExecuteAsync_NetworkDimension_InjectsNRules()
    {
        var harness = new StubHarness(
            ReportResult(
                90,
                "关系网络问题。",
                FindingJson(1, problem: "配角喧宾夺主"),
                FindingJson(2, problem: "主角 PR 排名连续下降"),
                FindingJson(3, category: "情节", problem: "支线漂移")
            )
        );
        var tool = new DiagnoseWritingTool(new StoreStub(), null, harness);

        var result = await tool.ExecuteAsync("call-4", Params(), CancellationToken.None, null);

        Assert.False(result.IsError);
        Assert.Equal("fiction.story.check", harness.LastRequest!.Capability);
        var report = Assert.IsType<DiagnosticReport>(result.Details);
        Assert.Contains(report.Issues, i => i.Description.Contains("配角喧宾夺主"));
        Assert.Contains(report.Issues, i => i.Description.Contains("主角 PR 排名连续"));
        Assert.Contains(report.Issues, i => i.Description.Contains("支线漂移"));
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

    private static HarnessResult DeAiResult(int issueCount = 3) =>
        ReportResult(
            60,
            "模板问题 1",
            Enumerable
                .Range(0, issueCount)
                .Select(i => FindingJson(i + 1, "文字", problem: $"模板问题 {i + 1}"))
                .ToArray()
        );

    private static DiagnoseWritingParams DeAiParams(int maxFindings = 3, bool fullReport = false) =>
        new()
        {
            NovelId = "n1",
            Mode = "deai",
            MaxFindings = maxFindings,
            FullReport = fullReport,
        };

    [Fact]
    public async Task ExecuteAsync_DeAiMode_UsesFocusedQuickAndReturnsAudit()
    {
        var harness = new StubHarness(DeAiResult(4));
        var tool = new DiagnoseWritingTool(new StoreStub(), null, harness);

        var result = await tool.ExecuteAsync(
            "deai-1",
            DeAiParams(maxFindings: 2),
            CancellationToken.None,
            null
        );

        Assert.Equal("quick", harness.LastRequest!.Depth);
        Assert.Equal(["humanizer"], harness.LastRequest.Options!.AnalyzerFocus);
        Assert.Single(harness.LastRequest.Chapters);
        Assert.Equal("target", harness.LastRequest.Chapters[0].Role);

        var report = Assert.IsType<DiagnosticReport>(result.Details);
        Assert.NotNull(report.DeAi);
        Assert.True(report.DeAi.CloudAuditAvailable);
        Assert.Equal(4, report.DeAi.Findings.Count);
        Assert.Equal(0, report.DeAi.SuppressedCount);
        Assert.Equal("high", report.DeAi.RiskLevel);
        var text = result.Content[0].ToString();
        Assert.Contains("去 AI 味检测", text);
        Assert.Contains("模板问题 1", text);
        Assert.DoesNotContain("综合评分", text);
    }

    [Fact]
    public async Task ExecuteAsync_DeAiMode_FullReport_ExpandsAllFindings()
    {
        var tool = new DiagnoseWritingTool(new StoreStub(), null, new StubHarness(DeAiResult(4)));

        var result = await tool.ExecuteAsync(
            "deai-2",
            DeAiParams(maxFindings: 1, fullReport: true),
            CancellationToken.None,
            null
        );

        var report = Assert.IsType<DiagnosticReport>(result.Details);
        Assert.Equal(4, report.DeAi!.Findings.Count);
        Assert.Equal(0, report.DeAi.SuppressedCount);
    }

    [Fact]
    public async Task ExecuteAsync_DeAiMode_CloudFailure_ReturnsSelfCheckWithoutFindings()
    {
        var tool = new DiagnoseWritingTool(new StoreStub(), null, new ThrowingHarness());

        var result = await tool.ExecuteAsync("deai-3", DeAiParams(), CancellationToken.None, null);

        Assert.False(result.IsError);
        var report = Assert.IsType<DiagnosticReport>(result.Details);
        Assert.NotNull(report.DeAi);
        Assert.False(report.DeAi.CloudAuditAvailable);
        Assert.Empty(report.DeAi.Findings);
        Assert.Contains("本次未执行云端检测", result.Content[0].ToString());
    }

    [Fact]
    public async Task ExecuteAsync_DeAiMode_DefaultHidesNoChangeLevels()
    {
        var harness = new StubHarness(
            ReportResult(90, "翻案腔", FindingJson(1, "文字", problem: "翻案腔"))
        );
        var tool = new DiagnoseWritingTool(new StoreStub(), null, harness);

        var result = await tool.ExecuteAsync(
            "deai-levels",
            DeAiParams(),
            CancellationToken.None,
            null
        );

        var report = Assert.IsType<DiagnosticReport>(result.Details);
        var finding = Assert.Single(report.DeAi!.Findings);
        Assert.Equal("翻案腔", finding.Description);
        Assert.DoesNotContain("这意味着", result.Content[0].ToString());
    }

    [Fact]
    public async Task ExecuteAsync_DeAiMode_MissingHumanizerDimension_DoesNotFakePass()
    {
        var harness = new StubHarness(ReportResult(90, "未发现文字问题。"));
        var tool = new DiagnoseWritingTool(new StoreStub(), null, harness);

        var result = await tool.ExecuteAsync("deai-5", DeAiParams(), CancellationToken.None, null);

        Assert.False(result.IsError);
        var report = Assert.IsType<DiagnosticReport>(result.Details);
        Assert.False(report.DeAi!.CloudAuditAvailable);
        Assert.Empty(report.DeAi.Findings);
        Assert.Contains("未完成检测", result.Content[0].ToString());
    }

    [Fact]
    public async Task ExecuteAsync_DeAiMode_NoHarness_ReturnsSelfCheck()
    {
        var tool = new DiagnoseWritingTool(new StoreStub(), null, null);

        var result = await tool.ExecuteAsync("deai-4", DeAiParams(), CancellationToken.None, null);

        Assert.False(result.IsError);
        var report = Assert.IsType<DiagnosticReport>(result.Details);
        Assert.False(report.DeAi!.CloudAuditAvailable);
        Assert.Contains("云端去AI味校验未完成", result.Content[0].ToString());
    }
}
