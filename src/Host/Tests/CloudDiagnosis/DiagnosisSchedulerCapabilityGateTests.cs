using System.Text.Json;
using ClientPlatform;
using CloudDiagnosis;
using Core.Cloud;
using Core.Data;
using Xunit;

namespace Host.Tests.CloudDiagnosis;

/// <summary>
/// 能力目录消费验收（2026-10-18）：探活快照约束请求组装与调用——
/// 深度按 supported-depths 回退、跨章窗口按 scope 门禁、未声明能力跳过；
/// 无快照时全部 fail-open（既有行为不变）。
/// </summary>
[Collection("CloudDiagnosisSchedulerSerial")]
public sealed class DiagnosisSchedulerCapabilityGateTests
{
    private const string NovelId = "novel_catalog";

    private const string ContextFeaturesJson =
        """{"overall-score":0.88,"word-count":1000,"paragraph-count":5,"dialogue-ratio":0.2,"narrative-entropy":1.4,"mattr":0.7,"yule-k":120,"sentence-cv":0.4,"paragraph-gini":0.3,"r-indicator":0.17}""";

    private const string PlanJson =
        """{"capability":"fiction.scene.plan","status":"pass","features":{"overall-score":0.88}}""";

    private const string CheckJson =
        """{"capability":"fiction.story.check","status":"pass","features":{"overall-score":0.8},"dimensions":{"pace":{"overall-score":80}}}""";

    private static Novel NewNovel() =>
        new()
        {
            Id = NovelId,
            Title = "目录门禁测试",
            Genre = "都市",
            AutoDiagnose = true,
            Plotlines =
            [
                new Plotline
                {
                    Id = "pl_1",
                    Name = "师门血案",
                    PlannedRevealChapterId = "ch_9", // L2-1 命中 → story.check（深度档）放行
                },
            ],
        };

    private static List<Chapter> NewChapters()
    {
        var list = new List<Chapter>();
        for (var order = 8; order <= 10; order++)
            list.Add(
                new Chapter
                {
                    Id = $"ch_{order}",
                    Title = $"第{order}章",
                    Order = order,
                    Content = "林云握紧拳头。",
                }
            );
        return list;
    }

    private static DiagnosisInput NewInput(List<Chapter> chapters, string chapterId) =>
        new()
        {
            NovelId = NovelId,
            ChapterId = chapterId,
            ChapterTitle = "当前章",
            ChapterOrder = chapters.First(c => c.Id == chapterId).Order,
            CurrentContent = chapters.First(c => c.Id == chapterId).Content,
            IsRewrite = false,
            Chapters = chapters,
            Novel = NewNovel(),
            PreviousContent = null,
        };

    /// <summary>记录全部出站请求（断言 wire 组装）的 stub harness。</summary>
    private sealed class RecordingHarness : IFictionHarnessClient
    {
        public List<HarnessRequest> Requests { get; } = [];

        public Task<HarnessResult> ExecuteAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(request);
            var json = request.Capability == "fiction.story.check" ? CheckJson : PlanJson;
            return Task.FromResult(
                new HarnessResult
                {
                    Capability = request.Capability,
                    Result = JsonSerializer.Deserialize<JsonElement>(json),
                }
            );
        }

        public Task<AgentContextSnapshot> GetContextAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(request);
            return Task.FromResult(
                new AgentContextSnapshot
                {
                    TargetChapterRef = request.Chapters.FirstOrDefault()?.ClientChapterRef ?? "",
                    Features = JsonSerializer.Deserialize<JsonElement>(ContextFeaturesJson),
                }
            );
        }

        public Task ReportSetupFeedbackAsync(
            IReadOnlyList<(string FindingId, string Decision)> items,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;

        public HarnessRequest? RequestOf(string capability) =>
            Requests.FirstOrDefault(r => r.Capability == capability);
    }

    /// <summary>目录：三个能力齐全，story.check 的档位/范围可参数化。</summary>
    private static CloudCapabilityCatalogStore Catalog(
        string storyCheckDepths = """["quick","standard","deep"]""",
        string storyCheckScope = "chapter-window",
        bool includeStoryCheck = true,
        bool includeContextRetrieve = true
    )
    {
        var entries = new List<string>();
        if (includeContextRetrieve)
            entries.Add(
                """{"id":"fiction.context.retrieve","category":"context","scope":"target-chapter","supported-depths":["quick","standard"]}"""
            );
        entries.Add(
            """{"id":"fiction.scene.plan","category":"planning","scope":"target-chapter","supported-depths":["quick","standard"]}"""
        );
        if (includeStoryCheck)
            entries.Add(
                $$"""{"id":"fiction.story.check","category":"review","scope":"{{storyCheckScope}}","supported-depths":{{storyCheckDepths}}}"""
            );
        var store = new CloudCapabilityCatalogStore();
        store.Update(
            CapabilityCatalogSnapshot.Parse(
                $$"""{"contract-version":"1.0","capabilities":[{{string.Join(",", entries)}}]}"""
            )!
        );
        return store;
    }

    private static (DiagnosisScheduler Scheduler, RecordingHarness Harness) NewScheduler(
        CloudCapabilityCatalogStore? catalog
    )
    {
        var harness = new RecordingHarness();
        var scheduler = new DiagnosisScheduler(
            harness,
            new CloudDiagnosisOptions(),
            capabilityCatalog: catalog
        );
        return (scheduler, harness);
    }

    private static async Task<CloudRound?> RunOnceAsync(
        DiagnosisScheduler scheduler,
        DiagnosisInput input
    )
    {
        var decision = scheduler.Decide(input);
        Assert.True(decision.ShouldRun);
        return await scheduler.RunAsync(input, decision, CancellationToken.None);
    }

    [Fact]
    public async Task StoryCheck_DepthFallsBackToQuick_WhenServerDoesNotAdvertiseStandard()
    {
        var chapters = NewChapters();

        // 服务端只声明 quick → 不得再请求 standard
        var (scheduler, harness) = NewScheduler(Catalog(storyCheckDepths: """["quick"]"""));
        await RunOnceAsync(scheduler, NewInput(chapters, "ch_9"));

        var check = harness.RequestOf("fiction.story.check");
        Assert.NotNull(check);
        Assert.Equal("quick", check!.Depth);

        // 对照：声明含 standard → 深度档保持 standard（原行为）
        var (controlScheduler, controlHarness) = NewScheduler(Catalog());
        await RunOnceAsync(controlScheduler, NewInput(chapters, "ch_9"));
        Assert.Equal("standard", controlHarness.RequestOf("fiction.story.check")!.Depth);
    }

    [Fact]
    public async Task StoryCheck_OmitsChapterWindow_WhenScopeIsNotChapterWindow()
    {
        var chapters = NewChapters();

        // 服务端把 story.check 声明为 target-chapter（不收跨章载荷）→ 不发 PreviousChapters
        var (scheduler, harness) = NewScheduler(Catalog(storyCheckScope: "target-chapter"));
        await RunOnceAsync(scheduler, NewInput(chapters, "ch_9"));

        var check = harness.RequestOf("fiction.story.check");
        Assert.NotNull(check);
        Assert.Null(check!.Context!.PreviousChapters);
        Assert.Equal("none", check.Context.PreviousChapterMode); // 窗口未挂 → 模式保持 none

        // 对照：声明 chapter-window → 窗口照发（target 之前最近 ≤20 章）
        var (controlScheduler, controlHarness) = NewScheduler(Catalog());
        await RunOnceAsync(controlScheduler, NewInput(chapters, "ch_9"));
        var controlCheck = controlHarness.RequestOf("fiction.story.check")!;
        Assert.NotNull(controlCheck.Context!.PreviousChapters);
        Assert.Equal(
            "ch_8",
            Assert.Single(controlCheck.Context.PreviousChapters!).ClientChapterRef
        );
    }

    [Fact]
    public async Task UndeclaredCapability_IsSkipped_WithoutCallingServer()
    {
        var chapters = NewChapters();

        // story.check 未在目录中声明 → 不发请求，记录客户端侧降级码
        var (scheduler, harness) = NewScheduler(Catalog(includeStoryCheck: false));
        await RunOnceAsync(scheduler, NewInput(chapters, "ch_9"));

        Assert.Null(harness.RequestOf("fiction.story.check"));
        Assert.Equal("capability_not_advertised", scheduler.LastDegradeCode);

        // 基础档声明齐全 → 基础档照常调用（门禁只针对未声明能力）
        Assert.NotNull(harness.RequestOf("fiction.scene.plan"));
        Assert.NotNull(harness.RequestOf("fiction.context.retrieve"));

        // context.retrieve 未声明 → 整个基础档不发（不发起必然被拒的调用）
        var (noContextScheduler, noContextHarness) = NewScheduler(
            Catalog(includeContextRetrieve: false)
        );
        await RunOnceAsync(noContextScheduler, NewInput(chapters, "ch_9"));
        Assert.Null(noContextHarness.RequestOf("fiction.context.retrieve"));
        Assert.Equal("capability_not_advertised", noContextScheduler.LastDegradeCode);
    }

    [Fact]
    public async Task NoCatalog_FailOpen_KeepsLegacyBehavior()
    {
        var chapters = NewChapters();

        var (scheduler, harness) = NewScheduler(catalog: null);
        var round = await RunOnceAsync(scheduler, NewInput(chapters, "ch_9"));

        Assert.NotNull(round);
        var check = harness.RequestOf("fiction.story.check");
        Assert.NotNull(check);
        Assert.Equal("standard", check!.Depth); // 无快照 → 保持 standard
        Assert.NotNull(check.Context!.PreviousChapters); // 无快照 → 窗口照发
        Assert.Null(scheduler.LastDegradeCode);
    }
}
