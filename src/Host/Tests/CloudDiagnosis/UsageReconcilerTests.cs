using System.Net;
using System.Text;
using System.Text.Json;
using ClientPlatform;
using CloudDiagnosis;
using Core.Agent;
using Core.Cloud;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Xunit;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace Host.Tests.CloudDiagnosis;

/// <summary>
/// C-4 用量对账验收：节流 / force 旁路 / 失败静默 / 用户取消上抛。
/// </summary>
public sealed class UsageReconcilerTests
{
    [Fact]
    public async Task TryFetch_ThrottlesByChapterInterval_ForceBypasses()
    {
        var handler = new UsageStubHandler(_ => UsageFixture.Json(remaining: 4900));
        var reconciler = NewReconciler(
            handler,
            new CloudDiagnosisOptions { UsageReconcileChapterInterval = 5 }
        );

        Assert.NotNull(await reconciler.TryFetchAsync(1, force: false, CancellationToken.None)); // 首次 → 建立基线
        Assert.Null(await reconciler.TryFetchAsync(3, force: false, CancellationToken.None)); // 间隔内 → 不发网络
        Assert.Single(handler.Requests);
        Assert.NotNull(await reconciler.TryFetchAsync(6, force: false, CancellationToken.None)); // 1+5 → 触发
        Assert.NotNull(await reconciler.TryFetchAsync(7, force: true, CancellationToken.None)); // force 旁路
        Assert.Null(await reconciler.TryFetchAsync(8, force: false, CancellationToken.None)); // force 不改变非 force 节奏（6+5=11 前不发）
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, path => Assert.StartsWith("/v1/agent/usage", path));
    }

    [Fact]
    public async Task TryFetch_ServerError_DegradesSilently_ReturnsNull()
    {
        var handler = new UsageStubHandler(_ => new HttpResponseMessage(
            HttpStatusCode.InternalServerError
        )
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        var reconciler = NewReconciler(handler, new CloudDiagnosisOptions());

        Assert.Null(await reconciler.TryFetchAsync(1, force: false, CancellationToken.None));
    }

    [Fact]
    public async Task TryFetch_UserCancellation_Propagates()
    {
        var reconciler = NewReconciler(new HangingHandler(), new CloudDiagnosisOptions());
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reconciler.TryFetchAsync(1, force: false, cts.Token)
        );
    }

    private static UsageReconciler NewReconciler(
        HttpMessageHandler handler,
        CloudDiagnosisOptions options
    ) =>
        new(
            new UsageRestClient(
                "http://localhost/",
                () => "mk_test_public-secret",
                http: new HttpClient(handler)
            ),
            options
        );
}

/// <summary>
/// C-4/C-5 Hook 级验收：余额预警 / 额度耗尽提示（一次性）+ 预算告警附服务端余额。
/// </summary>
public sealed class UsageWarningHookTests
{
    private const string NovelId = "novel_usage";

    [Fact]
    public async Task LowRemaining_EmitsOneTimeBalanceWarning()
    {
        var store = await NewStoreAsync();
        var agent = NewAgent();
        var options = new CloudDiagnosisOptions { UsageReconcileChapterInterval = 1 };
        var reconciler = new UsageReconciler(
            new UsageRestClient(
                "http://localhost/",
                () => "mk_test_public-secret",
                http: new HttpClient(new UsageStubHandler(_ => UsageFixture.Json(remaining: 50)))
            ),
            options
        );
        var hook = new WritingFlowHook(
            agent,
            store,
            new DiagnosisScheduler(new PassHarness(), options),
            null,
            options,
            null,
            null,
            null,
            reconciler
        );

        store.AddChapters(
            NovelId,
            new Chapter
            {
                Id = "ch_1",
                Order = 1,
                Content = "少年在山巅睁开眼。",
            }
        );
        await hook.AfterToolCallAsync(WriteContext("ch_1"), CancellationToken.None);
        store.AddChapters(
            NovelId,
            new Chapter
            {
                Id = "ch_2",
                Order = 2,
                Content = "少年在山巅睁开眼。",
            }
        );
        await hook.AfterToolCallAsync(WriteContext("ch_2"), CancellationToken.None);

        var messages = CollectMessages(agent);
        Assert.Equal(1, messages.Count(m => m.Contains("云端额度预警"))); // 两轮仅提示一次
    }

    [Fact]
    public async Task UsageLimitExceeded_EmitsOneTimeHint_AndKeepsLocalSignal()
    {
        var store = await NewStoreAsync();
        var agent = NewAgent();
        var options = new CloudDiagnosisOptions();
        var hook = new WritingFlowHook(
            agent,
            store,
            new DiagnosisScheduler(new UsageLimitHarness(), options),
            null,
            options
        );

        store.AddChapters(
            NovelId,
            new Chapter
            {
                Id = "ch_1",
                Order = 1,
                Content = "少年在山巅睁开眼。",
            }
        );
        await hook.AfterToolCallAsync(WriteContext("ch_1"), CancellationToken.None);
        store.AddChapters(
            NovelId,
            new Chapter
            {
                Id = "ch_2",
                Order = 2,
                Content = "少年在山巅睁开眼。",
            }
        );
        await hook.AfterToolCallAsync(WriteContext("ch_2"), CancellationToken.None);

        var messages = CollectMessages(agent);
        Assert.Equal(1, messages.Count(m => m.Contains("云端积分已耗尽"))); // 两轮仅提示一次
        Assert.Contains(messages, m => m.Contains("写作事件信号")); // 本地降级信号仍在
    }

    [Fact]
    public async Task BudgetExhausted_AppendsServerBalance()
    {
        var store = await NewStoreAsync();
        var agent = NewAgent();
        var options = new CloudDiagnosisOptions
        {
            DeepBudgetPerSession = 0,
            DeepBudgetPerNovel = 0,
        };
        var reconciler = new UsageReconciler(
            new UsageRestClient(
                "http://localhost/",
                () => "mk_test_public-secret",
                http: new HttpClient(
                    new UsageStubHandler(_ =>
                        UsageFixture.Json(remaining: 4880, used: 120, storyCheck: 30)
                    )
                )
            ),
            options
        );
        var hook = new WritingFlowHook(
            agent,
            store,
            new DiagnosisScheduler(new PassHarness(), options),
            null,
            options,
            null,
            null,
            null,
            reconciler
        );

        store.AddChapters(
            NovelId,
            new Chapter
            {
                Id = "ch_1",
                Order = 1,
                Content = "少年在山巅睁开眼。",
            }
        );
        await hook.AfterToolCallAsync(WriteContext("ch_1"), CancellationToken.None);

        var messages = CollectMessages(agent);
        var budgetMessage = Assert.Single(messages, m => m.Contains("深度诊断本地预算已耗尽"));
        Assert.Contains("服务端本月剩余 4880 单位", budgetMessage);
    }

    private static List<string> CollectMessages(Agent agent) =>
        agent
            .InjectQueue.DequeueAllWithIds()
            .Select(item => ((LlmChatMessage)item.Message).Content ?? "")
            .ToList();

    private static Agent NewAgent()
    {
        var agent = new Agent(new ToolRegistry());
        agent.CurrentNovelId = NovelId;
        return agent;
    }

    private static async Task<MockNovelStore> NewStoreAsync()
    {
        var store = new MockNovelStore();
        await store.SaveNovel(
            new Novel
            {
                Id = NovelId,
                Title = "用量测试",
                AutoDiagnose = true,
            }
        );
        return store;
    }

    private static ToolHookContext WriteContext(string chapterId) =>
        new()
        {
            ToolName = "write_chapter",
            Result = new AgentToolResult<ChapterWriteDetails>
            {
                Content = [new TextContent("ok")],
                Details = new ChapterWriteDetails(chapterId, 800),
            },
        };

    private sealed record ChapterWriteDetails(string Id, int WordCount) : IChapterWriteResult;

    private sealed class PassHarness : IFictionHarnessClient
    {
        public Task<HarnessResult> ExecuteAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken ct
        ) =>
            Task.FromResult(
                new HarnessResult
                {
                    Capability = request.Capability,
                    Result = JsonSerializer.Deserialize<JsonElement>(
                        """{"capability":"fiction.scene.plan","status":"pass","features":{"overall-score":0.88}}"""
                    ),
                }
            );

        public Task<AgentContextSnapshot> GetContextAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken ct
        ) =>
            Task.FromResult(
                new AgentContextSnapshot
                {
                    ChapterCount = 1,
                    Features = JsonSerializer.Deserialize<JsonElement>(
                        """{"overall-score":0.9,"narrative-entropy":1.2}"""
                    ),
                }
            );

        public Task ReportSetupFeedbackAsync(
            IReadOnlyList<(string FindingId, string Decision)> items,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    private sealed class UsageLimitHarness : IFictionHarnessClient
    {
        public Task<HarnessResult> ExecuteAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken ct
        ) => throw new CloudApiException("usage_limit_exceeded", "no credits");

        public Task<AgentContextSnapshot> GetContextAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken ct
        ) => throw new CloudApiException("usage_limit_exceeded", "no credits");

        public Task ReportSetupFeedbackAsync(
            IReadOnlyList<(string FindingId, string Decision)> items,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }
}

file sealed class UsageStubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    : HttpMessageHandler
{
    public List<string> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        Requests.Add(request.RequestUri!.PathAndQuery);
        return Task.FromResult(respond(request));
    }
}

file sealed class HangingHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("unreachable");
    }
}

file static class UsageFixture
{
    public static HttpResponseMessage Json(
        int remaining,
        int included = 5000,
        int used = 0,
        int reserved = 0,
        int storyCheck = 0
    ) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"period\":\"2026-10\",\"plan\":\"creator\","
                    + "\"included-units\":"
                    + included
                    + ",\"used-units\":"
                    + used
                    + ",\"reserved-units\":"
                    + reserved
                    + ",\"remaining-units\":"
                    + remaining
                    + ",\"breakdown\":{\"fiction.story.check\":"
                    + storyCheck
                    + "}}",
                Encoding.UTF8,
                "application/json"
            ),
        };
}
