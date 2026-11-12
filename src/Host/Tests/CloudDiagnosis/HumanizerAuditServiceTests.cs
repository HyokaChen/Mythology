using System.Text.Json;
using ClientPlatform;
using CloudDiagnosis;
using Core.Agent;
using Core.Cloud;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Xunit;

namespace Host.Tests.CloudDiagnosis;

public sealed class HumanizerAuditServiceTests
{
    private const string NovelId = "novel_humanizer";
    private const string ChapterId = "ch_1";

    private const string HumanizerJson = """
        {"dimensions":{"humanizer":{"overall-score":70,"issues":[
          {"severity":"high","sub-type":"formulaic_pattern","description":"公式化句式","evidence":"不是技术，而是认知","suggestion":"直接陈述","paragraph-index":1,"impact-score":80},
          {"severity":"high","sub-type":"formulaic_pattern","description":"公式化句式","evidence":"不是运气，而是选择","suggestion":"直接陈述","paragraph-index":2,"impact-score":70},
          {"severity":"medium","sub-type":"ai_buzzword","description":"AI高频词","evidence":"此外","suggestion":"删除","paragraph-index":3,"impact-score":40}
        ]}}}
        """;

    private static DiagnosisInput Input(string content) =>
        new()
        {
            NovelId = NovelId,
            ChapterId = ChapterId,
            ChapterTitle = "第一章",
            ChapterOrder = 1,
            CurrentContent = content,
            IsRewrite = false,
            Chapters =
            [
                new Chapter
                {
                    Id = ChapterId,
                    Title = "第一章",
                    Order = 1,
                    Content = content,
                },
            ],
            Novel = new Novel
            {
                Id = NovelId,
                Title = "测试",
                AutoDiagnose = true,
            },
        };

    private sealed class RecordingHarness(Func<HarnessRequest, HarnessResult> respond)
        : IFictionHarnessClient
    {
        public List<HarnessRequest> Requests { get; } = [];

        public Task<HarnessResult> ExecuteAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }

        public Task<AgentContextSnapshot> GetContextAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task ReportSetupFeedbackAsync(
            IReadOnlyList<(string FindingId, string Decision)> items,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    private static HarnessResult HumanizerResult() =>
        new()
        {
            Capability = "fiction.story.check",
            Result = JsonSerializer.Deserialize<JsonElement>(HumanizerJson),
        };

    [Fact]
    public async Task RunAsync_SendsMinimalFocusedQuickRequest_AndMergesSameSubtype()
    {
        var harness = new RecordingHarness(_ => HumanizerResult());
        var service = new HumanizerAuditService(harness, new CloudDiagnosisOptions());

        var outcome = await service.RunAsync(Input(new string('林', 80)), CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.True(outcome.CloudAvailable);
        Assert.False(outcome.Skipped);
        Assert.Equal(1, outcome.PassNumber);
        Assert.Equal("high", outcome.RiskLevel);
        Assert.Equal(2, outcome.Findings.Count); // formulaic 两条合并，ai_buzzword 一条
        Assert.Equal(1, outcome.SuppressedCount);

        var request = Assert.Single(harness.Requests);
        Assert.Equal("fiction.story.check", request.Capability);
        Assert.Equal("quick", request.Depth);
        Assert.Equal(["humanizer"], request.Options!.AnalyzerFocus);
        var chapter = Assert.Single(request.Chapters);
        Assert.Equal("target", chapter.Role);
        Assert.Equal(ChapterId, chapter.ClientChapterRef);
        Assert.Null(request.Context); // 最小上下文：不上传整本/设定图
    }

    [Fact]
    public async Task RunAsync_SameContent_DoesNotCallTwice()
    {
        var harness = new RecordingHarness(_ => HumanizerResult());
        var service = new HumanizerAuditService(harness, new CloudDiagnosisOptions());
        var input = Input(new string('林', 80));

        var first = await service.RunAsync(input, CancellationToken.None);
        var second = await service.RunAsync(input, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(second.Skipped);
        Assert.Equal("same-content", second.SkipReason);
        Assert.Single(harness.Requests);
    }

    [Fact]
    public async Task RunAsync_MaxTwoPasses_ThenSkips()
    {
        var harness = new RecordingHarness(_ => HumanizerResult());
        var service = new HumanizerAuditService(harness, new CloudDiagnosisOptions());

        var first = await service.RunAsync(Input(new string('林', 80)), CancellationToken.None);
        var second = await service.RunAsync(Input(new string('林', 81)), CancellationToken.None);
        var third = await service.RunAsync(Input(new string('林', 82)), CancellationToken.None);

        Assert.Equal(1, first!.PassNumber);
        Assert.Equal(2, second!.PassNumber);
        Assert.True(third!.Skipped);
        Assert.Equal("max-passes", third.SkipReason);
        Assert.Equal(2, harness.Requests.Count);
    }

    [Fact]
    public async Task RunAsync_CloudFailure_ReturnsNull()
    {
        var harness = new RecordingHarness(_ =>
            throw new CloudApiException("network_error", "offline")
        );
        var service = new HumanizerAuditService(harness, new CloudDiagnosisOptions());

        var outcome = await service.RunAsync(Input(new string('林', 80)), CancellationToken.None);

        Assert.Null(outcome);
    }

    [Fact]
    public async Task RunAsync_TooShort_DoesNotCallCloud()
    {
        var harness = new RecordingHarness(_ => HumanizerResult());
        var service = new HumanizerAuditService(harness, new CloudDiagnosisOptions());

        var outcome = await service.RunAsync(Input("短"), CancellationToken.None);

        Assert.True(outcome!.Skipped);
        Assert.Equal("too-short", outcome.SkipReason);
        Assert.Empty(harness.Requests);
    }

    [Fact]
    public async Task WritingFlowHook_InjectsHumanizerSignal_WithoutSchedulerDecision()
    {
        const string novelId = "novel_hook";
        var store = new MockNovelStore();
        await store.SaveNovel(
            new Novel
            {
                Id = novelId,
                Title = "测试",
                AutoDiagnose = true,
            }
        );
        var content = string.Concat(Enumerable.Repeat("林云握紧拳头。", 20));
        store.AddChapters(
            novelId,
            new Chapter
            {
                Id = ChapterId,
                Title = "第一章",
                Order = 1,
                Content = content,
            }
        );

        var harness = new RecordingHarness(_ => HumanizerResult());
        var options = new CloudDiagnosisOptions { OntologyHarnessEnabled = false };
        var agent = new Agent(new ToolRegistry()) { CurrentNovelId = novelId };
        var hook = new WritingFlowHook(
            agent,
            store,
            scheduler: new DiagnosisScheduler(harness, options),
            options: options,
            humanizerAudit: new HumanizerAuditService(harness, options)
        );

        await hook.AfterToolCallAsync(
            new ToolHookContext
            {
                ToolName = "write_chapter",
                Result = new AgentToolResult<IChapterWriteResult>
                {
                    Content = [new TextContent("ok")],
                    Details = new ChapterWriteDetails(ChapterId, 160),
                },
            },
            CancellationToken.None
        );

        var injected = agent
            .InjectQueue.DequeueAllWithIds()
            .Select(item => item.Message.Content ?? "")
            .ToList();
        Assert.Contains(injected, text => text.Contains("[云端诊断] 去AI味 status=action"));

        // humanizer 在 scheduler.Decide/RunAsync 之前发出：第一个请求必须是 focused quick。
        var first = Assert.Single(harness.Requests.Take(1));
        Assert.Equal("fiction.story.check", first.Capability);
        Assert.Equal(["humanizer"], first.Options!.AnalyzerFocus);
    }

    [Fact]
    public async Task RunAsync_OnlyActionableLevelsBecomeFindings()
    {
        const string json = """
            {"dimensions":{"humanizer":{"overall-score":100,"issues":[
              {"severity":"high","level":"A","sub-type":"negation_contrast","description":"翻案腔","evidence":"不是A而是B","suggestion":"直接说B"},
              {"severity":"low","level":"X","sub-type":"this_means","description":"这意味着","evidence":"这意味着"},
              {"severity":"low","level":"H","sub-type":"personification","description":"拟人","evidence":"像一位导师"}
            ]}}}
            """;
        var harness = new RecordingHarness(_ => new HarnessResult
        {
            Capability = "fiction.story.check",
            Result = JsonSerializer.Deserialize<JsonElement>(json),
        });
        var service = new HumanizerAuditService(harness, new CloudDiagnosisOptions());

        var outcome = await service.RunAsync(Input(new string('林', 80)), CancellationToken.None);

        Assert.NotNull(outcome);
        var finding = Assert.Single(outcome.Findings);
        Assert.Equal("negation_contrast", finding.SubType);
        Assert.Equal("A", finding.Level);
        Assert.Equal(2, outcome.NoChangeCount);
        Assert.Equal(0, outcome.SuppressedCount);
    }

    [Fact]
    public async Task RunAsync_NoIssues_BuildsPassSignal()
    {
        var harness = new RecordingHarness(_ => new HarnessResult
        {
            Capability = "fiction.story.check",
            Result = JsonSerializer.Deserialize<JsonElement>(
                """{"dimensions":{"humanizer":{"overall-score":100,"issues":[]}}}"""
            ),
        });
        var service = new HumanizerAuditService(harness, new CloudDiagnosisOptions());

        var outcome = await service.RunAsync(Input(new string('林', 80)), CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.Empty(outcome.Findings);
        Assert.Equal("none", outcome.RiskLevel);
        Assert.Contains("status=pass", SignalBuilder.BuildHumanizerSignal(outcome));
    }

    [Fact]
    public void BuildHumanizerSignal_Pass1_ScopesAutoFixToAB()
    {
        var outcome = new HumanizerAuditOutcome(
            "high",
            [
                new HumanizerFinding(
                    "formulaic_pattern",
                    "A",
                    "high",
                    "公式化句式",
                    "不是A而是B",
                    "直接陈述",
                    0,
                    80
                ),
            ],
            0,
            1
        );

        var signal = SignalBuilder.BuildHumanizerSignal(outcome);

        Assert.Contains("A/B 级", signal);
        Assert.Contains("F/H/X 只报告", signal);
        Assert.DoesNotContain("请先自动修 1–3 处再交付", signal);
    }

    [Fact]
    public async Task WritingFlowHook_HumanizerFailure_InjectsLocalSelfCheck()
    {
        const string novelId = "novel_hook_fail";
        var store = new MockNovelStore();
        await store.SaveNovel(
            new Novel
            {
                Id = novelId,
                Title = "测试",
                AutoDiagnose = true,
            }
        );
        var content = string.Concat(Enumerable.Repeat("林云握紧拳头。", 20));
        store.AddChapters(
            novelId,
            new Chapter
            {
                Id = ChapterId,
                Title = "第一章",
                Order = 1,
                Content = content,
            }
        );

        var harness = new RecordingHarness(_ =>
            throw new CloudApiException("network_error", "offline")
        );
        var options = new CloudDiagnosisOptions { OntologyHarnessEnabled = false };
        var agent = new Agent(new ToolRegistry()) { CurrentNovelId = novelId };
        var hook = new WritingFlowHook(
            agent,
            store,
            options: options,
            humanizerAudit: new HumanizerAuditService(harness, options)
        );

        await hook.AfterToolCallAsync(
            new ToolHookContext
            {
                ToolName = "write_chapter",
                Result = new AgentToolResult<IChapterWriteResult>
                {
                    Content = [new TextContent("ok")],
                    Details = new ChapterWriteDetails(ChapterId, 160),
                },
            },
            CancellationToken.None
        );

        var injected = agent
            .InjectQueue.DequeueAllWithIds()
            .Select(item => item.Message.Content ?? "")
            .ToList();
        Assert.Contains(injected, text => text.Contains("云端去AI味校验未完成"));
    }

    private sealed record ChapterWriteDetails(string Id, int WordCount) : IChapterWriteResult;
}
