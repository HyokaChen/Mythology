using System.Text.Json;
using ClientPlatform;
using CloudDiagnosis;
using Core.Cloud;
using Core.Data;
using Xunit;

namespace Host.Tests.CloudDiagnosis;

public sealed class HumanizerAuditServiceTests
{
    private const string NovelId = "novel_humanizer";
    private const string ChapterId = "ch_1";

    private const string ReportJson = """
        {"verdict":"需要修改","action":"revise","summary":"发现文字问题。","score":70,
         "findings":[{"id":"f_0123456789abcdef","category":"文字","severity":"high","severity-label":"严重",
           "action":"fix-now","action-label":"直接修改","location":"第 1 章 · 第 2 段",
           "problem":"否定式对举。","evidence":"不是技术，而是认知。","suggestion":"直接陈述。"}],
         "decision-question":null,"decision-options":null,"snapshot":null,"strengths":null}
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
            return Task.FromResult(
                new HarnessResult
                {
                    Capability = "fiction.story.check",
                    Result = JsonSerializer.Deserialize<JsonElement>(ReportJson),
                }
            );
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

    [Fact]
    public async Task RunAsync_SendsReportModeFocusedQuickRequest()
    {
        var harness = new RecordingHarness();
        var service = new HumanizerAuditService(harness, new CloudDiagnosisOptions());

        var outcome = await service.RunAsync(Input(new string('林', 80)), CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.False(outcome.Skipped);
        Assert.Equal(1, outcome.PassNumber);
        Assert.Equal("否定式对举。", outcome.Report!.Findings[0].Problem);
        var request = Assert.Single(harness.Requests);
        Assert.Equal("report", request.Output);
        Assert.Equal("fiction.story.check", request.Capability);
        Assert.Equal("quick", request.Depth);
        Assert.Equal(["humanizer"], request.Options!.AnalyzerFocus);
        Assert.Null(request.Context);
    }

    [Fact]
    public async Task RunAsync_SameContent_DoesNotCallTwice()
    {
        var harness = new RecordingHarness();
        var service = new HumanizerAuditService(harness, new CloudDiagnosisOptions());
        var input = Input(new string('林', 80));

        var first = await service.RunAsync(input, CancellationToken.None);
        var second = await service.RunAsync(input, CancellationToken.None);

        Assert.NotNull(first);
        Assert.True(second!.Skipped);
        Assert.Equal("same-content", second.SkipReason);
        Assert.Single(harness.Requests);
    }

    [Fact]
    public async Task RunAsync_MaxTwoPasses_ThenSkips()
    {
        var harness = new RecordingHarness();
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
    public async Task RunAsync_ShortContent_SkipsWithoutCloud()
    {
        var harness = new RecordingHarness();
        var service = new HumanizerAuditService(harness, new CloudDiagnosisOptions());

        var outcome = await service.RunAsync(Input("太短"), CancellationToken.None);

        Assert.True(outcome!.Skipped);
        Assert.Equal("too-short", outcome.SkipReason);
        Assert.Empty(harness.Requests);
    }
}
