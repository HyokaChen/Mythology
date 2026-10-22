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
/// HARNESS-CLOUD §10 P1-E2 验收：预算耗尽不再发 story.check / 同章同哈希幂等 /
/// L1-5 无缓存静默跳过 / 跳过说明信号出现 / 403 降级后仅基础档调用且信号仍产出。
/// 另覆盖 L1/L2 抽样（AI味、信息差泄底、无钩子、伏笔回收章、二次重写绕过幂等）。
/// </summary>
[Collection("CloudDiagnosisSchedulerSerial")]
public sealed class DiagnosisSchedulerTests
{
    private const string NovelId = "novel_e2";

    private static readonly string PassJson =
        """{"capability":"fiction.scene.plan","status":"pass","features":{"overall-score":0.88}}""";

    private static readonly string ContextJson =
        """{"features":{"overall-score":0.88,"narrative-entropy":1.4},"signals":[],"dimension-scores":{"节奏":80}}""";

    private static Novel NewNovel() =>
        new()
        {
            Id = NovelId,
            Title = "E2 测试",
            Genre = "都市",
            AutoDiagnose = true,
            Plotlines =
            [
                new Plotline
                {
                    Id = "pl_1",
                    Name = "师门血案",
                    PlannedRevealChapterId = "ch_9",
                },
            ],
        };

    private static List<Chapter> NewChapters(int count = 3, int startOrder = 8)
    {
        var list = new List<Chapter>();
        for (var i = 0; i < count; i++)
            list.Add(
                new Chapter
                {
                    Id = $"ch_{startOrder + i}",
                    Title = $"第{startOrder + i}章",
                    Order = startOrder + i,
                    Content = "林云握紧拳头。",
                }
            );
        // ch_9 为师门血案的计划揭示章（L2-1 用）
        return list;
    }

    private static DiagnosisInput NewInput(
        List<Chapter> chapters,
        string chapterId,
        bool isRewrite = false
    ) =>
        new()
        {
            NovelId = NovelId,
            ChapterId = chapterId,
            ChapterTitle = "当前章",
            ChapterOrder = chapters.First(c => c.Id == chapterId).Order,
            CurrentContent = chapters.First(c => c.Id == chapterId).Content,
            IsRewrite = isRewrite,
            Chapters = chapters,
            Novel = NewNovel(),
            PreviousContent = null,
        };

    /// <summary>按 capability 计数的多路 stub。</summary>
    private sealed class RoutingHarness(
        Func<string, HarnessResult> onFree, // context.retrieve / scene.plan
        Func<HarnessResult>? onDeep = null, // story.check
        Dictionary<string, int>? counts = null
    ) : IFictionHarnessClient
    {
        public Dictionary<string, int> Counts { get; } = counts ?? new Dictionary<string, int>();

        public Task<HarnessResult> ExecuteAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken
        )
        {
            var key = request.Capability switch
            {
                "fiction.story.check" => "story.check",
                _ => "base:" + request.Capability,
            };
            Counts[key] = Counts.GetValueOrDefault(key) + 1;

            return Task.FromResult(
                request.Capability == "fiction.story.check"
                    ? onDeep?.Invoke()
                        ?? throw new InvalidOperationException("unexpected deep call")
                    : onFree(request.Capability)
            );
        }

        public Task<AgentContextSnapshot> GetContextAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken
        )
        {
            Counts["context.retrieve"] = Counts.GetValueOrDefault("context.retrieve") + 1;
            return Task.FromResult(
                new AgentContextSnapshot
                {
                    TargetChapterRef = inputRef,
                    Features = JsonSerializer.Deserialize<JsonElement>(
                        """{"overall-score":0.88,"word-count":1000,"paragraph-count":5,"dialogue-ratio":0.2,"narrative-entropy":1.4,"mattr":0.7,"yule-k":120,"sentence-cv":0.4,"paragraph-gini":0.3,"r-indicator":0.17}"""
                    ),
                }
            );
        }

        private string inputRef = "ch_9";

        public Task ReportSetupFeedbackAsync(
            IReadOnlyList<(string FindingId, string Decision)> items,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    [Fact]
    public async Task DeepBudgetExhausted_StopsDeepCalls_AndEmitsOneTimeWarning()
    {
        var options = new CloudDiagnosisOptions { DeepBudgetPerSession = 1 };
        var counts = new Dictionary<string, int>();
        var harness = new RoutingHarness(
            cap => new HarnessResult
            {
                Capability = cap,
                Result = JsonSerializer.Deserialize<JsonElement>(DeepStubJson),
            },
            () =>
                new HarnessResult
                {
                    Capability = "fiction.story.check",
                    Result = JsonSerializer.Deserialize<JsonElement>(
                        """{"dimensions":{"pace":{"overall-score":50}}}"""
                    ),
                },
            counts
        );
        var scheduler = new DiagnosisScheduler(harness, options);
        var chapters = NewChapters();

        // 第一章（L2-1 命中 → deep）：深度预算 1 次被消耗
        var d1 = scheduler.Decide(NewInput(chapters, "ch_9"));
        Assert.True(d1.ShouldRun && d1.DeepCheck);
        await scheduler.RunAsync(NewInput(chapters, "ch_9"), d1, CancellationToken.None);

        // 第二章（L1-1 节奏位到期，非 deep）→ 预算耗尽不应再发 story.check
        var d2Input = NewInput(chapters, "ch_10");
        var d2 = scheduler.Decide(d2Input);
        Assert.False(d2.DeepCheck); // 预算耗尽 → 深度档放行失败

        // 强制深度意图（模拟 L2）也因预算不放行：
        var deepButNoBudget = scheduler.Decide(NewInput(chapters, "ch_10", isRewrite: true));
        Assert.False(deepButNoBudget.DeepCheck);

        Assert.Equal(1, counts.GetValueOrDefault("story.check")); // 只消耗了预算内那一次
        Assert.True(scheduler.DeepBudgetExhausted);
        Assert.True(scheduler.TryConsumeBudgetWarning()); // 一次性告警位
        Assert.False(scheduler.TryConsumeBudgetWarning()); // 第二次不重复
    }

    [Fact]
    public async Task SameChapterSameHash_IdempotentPerCapability()
    {
        var counts = new Dictionary<string, int>();
        var harness = new RoutingHarness(
            cap => new HarnessResult
            {
                Capability = cap,
                Result = JsonSerializer.Deserialize<JsonElement>(DeepStubJson),
            },
            counts: counts
        );
        var scheduler = new DiagnosisScheduler(harness, new CloudDiagnosisOptions());
        var chapters = NewChapters();
        var input = NewInput(chapters, "ch_10");

        // 第一次写入：漏斗放行
        var d1 = scheduler.Decide(input);
        Assert.True(d1.ShouldRun);
        var firstRound = await scheduler.RunAsync(input, d1, CancellationToken.None);
        Assert.NotNull(firstRound);
        Assert.Equal(0, firstRound.DeepUnits); // 基础档轮次无深度消耗

        // 第二次（同章同内容）：Decide 幂等守卫拦截 → 不再产生任何云端调用
        var d2 = scheduler.Decide(input);
        Assert.False(d2.ShouldRun);
        await scheduler.RunAsync(input, d2, CancellationToken.None);
        Assert.Equal(1, counts.GetValueOrDefault("context.retrieve"));
        Assert.Equal(1, counts.GetValueOrDefault("base:fiction.scene.plan"));
        Assert.Equal(0, counts.GetValueOrDefault("story.check"));
    }

    /// <summary>
    /// U-6：L2 命中解除该章免费幂等的行为对称——L2-4（快照 stale）与 L2-2（新角色首现）
    /// 与 L2-3（二次重写）一致；此前仅 L2-3 解除，同章重检会被旧幂等记录吞掉。
    /// </summary>
    [Fact]
    public async Task Decide_L2Hits_ReleaseFreeIdempotency_Uniformly()
    {
        var counts = new Dictionary<string, int>();
        var harness = new RoutingHarness(
            cap => new HarnessResult
            {
                Capability = cap,
                Result = JsonSerializer.Deserialize<JsonElement>(DeepStubJson),
            },
            counts: counts
        );
        var scheduler = new DiagnosisScheduler(harness, new CloudDiagnosisOptions());
        var chapters = NewChapters();
        var input = NewInput(chapters, "ch_10");

        var d1 = scheduler.Decide(input);
        Assert.True(d1.ShouldRun);
        await scheduler.RunAsync(input, d1, CancellationToken.None);
        Assert.Equal(1, counts.GetValueOrDefault("context.retrieve"));

        // 同章同内容、无 L2 → 免费幂等拦截
        Assert.False(scheduler.Decide(input).ShouldRun);

        // L2-4 快照 stale → 解除幂等 → 重新拉基础档信号
        var stale = input with
        {
            SnapshotStale = true,
        };
        var dStale = scheduler.Decide(stale);
        Assert.True(dStale.ShouldRun);
        await scheduler.RunAsync(stale, dStale, CancellationToken.None);
        Assert.Equal(2, counts.GetValueOrDefault("context.retrieve"));
        // 注：SnapshotStale 持续为真时每轮都会重新判定（L2-4 命中本身即放行条件，U-6 前后一致）；
        // 生产中该标记由 Hook 从快照缓存计算，成功轮刷新缓存后自动回落。

        // L2-2 新角色首现（权威前序记录为空 + 当前章含该角色名）→ 同样解除幂等
        var novel = NewNovel();
        novel.Characters.Add(new Character { Id = "c_lin", Name = "林云" });
        var debut = input with { Novel = novel, PriorAppearedCharacterIds = new HashSet<string>() };
        var dDebut = scheduler.Decide(debut);
        Assert.True(dDebut.ShouldRun);
        Assert.Contains("L2-2", dDebut.HitSummary);
    }

    /// <summary>
    /// P-1：无权威出场记录时的新角色首现回退扫描按 (chapterId, 正文) 记忆化——
    /// 正文变化后必须重新判定（缓存不得粘住旧结果）。
    /// </summary>
    [Fact]
    public void Decide_DebutFallbackScan_ReflectsContentChange()
    {
        var harness = new RoutingHarness(cap => new HarnessResult
        {
            Capability = cap,
            Result = JsonSerializer.Deserialize<JsonElement>(DeepStubJson),
        });
        var scheduler = new DiagnosisScheduler(harness, new CloudDiagnosisOptions());

        var novel = NewNovel();
        novel.Characters.Add(new Character { Id = "c_lin", Name = "林云" });
        // 其它章节不含该角色名 → 回退扫描的 appearedBefore 判定为「首现」
        var chapters = new List<Chapter>
        {
            new()
            {
                Id = "ch_9",
                Title = "第九章",
                Order = 9,
                Content = "他推开门。",
            },
            new()
            {
                Id = "ch_10",
                Title = "第十章",
                Order = 10,
                Content = "他推开门。",
            },
        };

        var withoutName = NewInput(chapters, "ch_10") with
        {
            Novel = novel,
            CurrentContent = "他推开门。",
        };
        Assert.DoesNotContain("L2-2", scheduler.Decide(withoutName).HitSummary);

        // 同章正文改为含角色名 → 记忆化缓存必须失效并重新判定
        var withName = withoutName with
        {
            CurrentContent = "林云握紧拳头。",
        };
        Assert.Contains("L2-2", scheduler.Decide(withName).HitSummary);
    }

    /// <summary>
    /// U-7 扩展（层 1 采样门槛）：空正文 / 纯标点等退化输入本地短路——RunAsync 不产生任何云端调用
    /// （引擎对退化输入只会在四个维度上返回先验基准分，几何均值恒 ≈0.61，与内容无关）。
    /// </summary>
    [Fact]
    public async Task Decide_DegenerateContent_SkipsWithoutCloudCalls()
    {
        var counts = new Dictionary<string, int>();
        var harness = new RoutingHarness(
            cap => new HarnessResult
            {
                Capability = cap,
                Result = JsonSerializer.Deserialize<JsonElement>(DeepStubJson),
            },
            counts: counts
        );
        var scheduler = new DiagnosisScheduler(harness, new CloudDiagnosisOptions());

        foreach (var content in new[] { "", "。。。。。", "  \n\t  " })
        {
            var input = NewInput(NewChapters(), "ch_10") with { CurrentContent = content };
            var decision = scheduler.Decide(input);

            Assert.False(decision.ShouldRun);
            Assert.Contains("实质性内容不足", decision.SkipReason);
            Assert.Null(await scheduler.RunAsync(input, decision, CancellationToken.None));
        }
        Assert.Empty(counts);
    }

    /// <summary>层 1 边界：实质性字符数达门槛（默认 2）即照常放行——不误拦正常短章。</summary>
    [Fact]
    public void Decide_SubstantiveContentAtThreshold_StillRuns()
    {
        var counts = new Dictionary<string, int>();
        var harness = new RoutingHarness(
            cap => new HarnessResult
            {
                Capability = cap,
                Result = JsonSerializer.Deserialize<JsonElement>(DeepStubJson),
            },
            counts: counts
        );
        var scheduler = new DiagnosisScheduler(harness, new CloudDiagnosisOptions());

        var input = NewInput(NewChapters(), "ch_10") with { CurrentContent = "林云推门。" }; // 4 字 + 1 标点
        Assert.True(scheduler.Decide(input).ShouldRun);
    }

    /// <summary>
    /// C-1：调度器挂载快照缓存时，成功轮仅拉取一次 context.retrieve——
    /// 缓存直接复用基础档已取得的快照（修复前为两次：基础档 + RefreshAsync）。
    /// </summary>
    [Fact]
    public async Task RunWithCache_ReusesBaseTierSnapshot_SingleContextRetrieve()
    {
        var counts = new Dictionary<string, int>();
        var harness = new RoutingHarness(
            cap => new HarnessResult
            {
                Capability = cap,
                Result = JsonSerializer.Deserialize<JsonElement>(DeepStubJson),
            },
            counts: counts
        );
        var cache = new ContextSnapshotCache(harness);
        var scheduler = new DiagnosisScheduler(harness, new CloudDiagnosisOptions(), cache);

        var input = NewInput(NewChapters(), "ch_10");
        var decision = scheduler.Decide(input);
        Assert.True(decision.ShouldRun);
        var round = await scheduler.RunAsync(input, decision, CancellationToken.None);

        Assert.NotNull(round);
        Assert.Equal(1, counts.GetValueOrDefault("context.retrieve")); // C-1：成功轮仅一次
        var entry = cache.TryGet(NovelId);
        Assert.NotNull(entry);
        Assert.Equal(10, entry!.FetchedAtChapterOrder); // 缓存已由本轮响应推进
    }

    /// <summary>
    /// C-1 回退分支：基础档 context.retrieve 失败但 scene.plan 成功时，
    /// 仍走单独刷新兜底（基础档 1 次失败调用 + 兜底 1 次成功调用）。
    /// </summary>
    [Fact]
    public async Task RunWithCache_BaseTierFetchFailed_FallsBackToStandaloneRefresh()
    {
        var harness = new FlakyContextHarness();
        var cache = new ContextSnapshotCache(harness);
        var scheduler = new DiagnosisScheduler(harness, new CloudDiagnosisOptions(), cache);

        var input = NewInput(NewChapters(), "ch_10");
        var decision = scheduler.Decide(input);
        var round = await scheduler.RunAsync(input, decision, CancellationToken.None);

        Assert.NotNull(round);
        Assert.Equal(2, harness.ContextAttempts); // 基础档失败 + 兜底刷新
        var entry = cache.TryGet(NovelId);
        Assert.NotNull(entry);
        Assert.Equal(10, entry!.FetchedAtChapterOrder);
    }

    /// <summary>基础档首次 GetContextAsync 失败、第二次（兜底刷新）成功的双态 stub。</summary>
    private sealed class FlakyContextHarness : IFictionHarnessClient
    {
        public int ContextAttempts;

        public Task<HarnessResult> ExecuteAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken ct
        ) =>
            Task.FromResult(
                new HarnessResult
                {
                    Capability = request.Capability,
                    Result = JsonSerializer.Deserialize<JsonElement>(DeepStubJson),
                }
            );

        public Task<AgentContextSnapshot> GetContextAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken ct
        )
        {
            var attempt = Interlocked.Increment(ref ContextAttempts);
            if (attempt == 1)
                throw new CloudApiException("unavailable", "模拟首次失败");
            return Task.FromResult(
                new AgentContextSnapshot
                {
                    ChapterCount = 10,
                    Features = JsonSerializer.Deserialize<JsonElement>(
                        """{"overall-score":0.88,"narrative-entropy":1.4}"""
                    ),
                }
            );
        }

        public Task ReportSetupFeedbackAsync(
            IReadOnlyList<(string FindingId, string Decision)> items,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    /// <summary>
    /// C-2：幂等键纳入章节集指纹——改写前文（previous 章）而 target 未动 → ctx 键变化；
    /// 同输入重试（新实例）→ 键稳定；键长 ≤ 服务端 200 字符上限（GUID 形状 id 最坏情况）。
    /// </summary>
    [Fact]
    public async Task IdempotencyKey_TracksContextVersion_StableAndBounded()
    {
        var harness = new KeyRecordingSchedulerHarness();
        var options = new CloudDiagnosisOptions();
        var novelId = Guid.NewGuid().ToString();

        var run1 = new DiagnosisScheduler(harness, options);
        var input1 = NewInput(NewChapters(), "ch_10") with { NovelId = novelId };
        var d1 = run1.Decide(input1);
        Assert.True(d1.ShouldRun);
        await run1.RunAsync(input1, d1, CancellationToken.None);

        var run2 = new DiagnosisScheduler(harness, options);
        var input2 = NewInput(NewChapters(), "ch_10") with { NovelId = novelId }; // 同输入（新实例）
        var d2 = run2.Decide(input2);
        await run2.RunAsync(input2, d2, CancellationToken.None);

        var rewritten = NewChapters()
            .Select(c =>
                c.Id == "ch_9"
                    ? new Chapter
                    {
                        Id = c.Id,
                        Title = c.Title,
                        Order = c.Order,
                        Content = "改写后的前文。",
                    }
                    : c
            )
            .ToList();
        var run3 = new DiagnosisScheduler(harness, options);
        var input3 = NewInput(rewritten, "ch_10") with { NovelId = novelId }; // 仅前文改写
        var d3 = run3.Decide(input3);
        await run3.RunAsync(input3, d3, CancellationToken.None);

        var ctxKeys = harness
            .Calls.Where(call => call.Capability == "fiction.context.retrieve")
            .Select(call => call.Key)
            .ToList();
        Assert.Equal(3, ctxKeys.Count);
        Assert.Equal(ctxKeys[0], ctxKeys[1]); // 同输入重试 → 键稳定（幂等仍有效）
        Assert.NotEqual(ctxKeys[0], ctxKeys[2]); // 前文改写（target 未动）→ 新键
        Assert.All(
            harness.Calls,
            call => Assert.True(call.Key.Length <= 200, $"幂等键超长：{call.Key}")
        );
    }

    /// <summary>记录 (capability, idempotencyKey) 的 stub（C-2 键行为断言用）。</summary>
    private sealed class KeyRecordingSchedulerHarness : IFictionHarnessClient
    {
        public List<(string Capability, string Key)> Calls { get; } = [];

        public Task<HarnessResult> ExecuteAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken ct
        )
        {
            Calls.Add((request.Capability, idempotencyKey));
            return Task.FromResult(
                new HarnessResult
                {
                    Capability = request.Capability,
                    Result = JsonSerializer.Deserialize<JsonElement>(DeepStubJson),
                }
            );
        }

        public Task<AgentContextSnapshot> GetContextAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken ct
        )
        {
            Calls.Add((request.Capability, idempotencyKey));
            return Task.FromResult(
                new AgentContextSnapshot
                {
                    ChapterCount = 10,
                    Features = JsonSerializer.Deserialize<JsonElement>(
                        """{"overall-score":0.88,"narrative-entropy":1.4}"""
                    ),
                }
            );
        }

        public Task ReportSetupFeedbackAsync(
            IReadOnlyList<(string FindingId, string Decision)> items,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    /// <summary>
    /// C-5：全阶段 402 → LastDegradeCode 分类为 usage_limit_exceeded；
    /// 下一轮无异常静默失败（解析为空）→ 降级码重置为空（防陈旧码误报额度耗尽）。
    /// </summary>
    [Fact]
    public async Task LastDegradeCode_ClassifiesUsageLimit_AndResetsPerRound()
    {
        var options = new CloudDiagnosisOptions();
        var scheduler = new DiagnosisScheduler(new UsageLimitHarness(), options);
        var input = NewInput(NewChapters(), "ch_10");
        var decision = scheduler.Decide(input);
        Assert.True(decision.ShouldRun);

        Assert.Null(await scheduler.RunAsync(input, decision, CancellationToken.None));
        Assert.Equal("usage_limit_exceeded", scheduler.LastDegradeCode);

        var quiet = new DiagnosisScheduler(new SilentEmptyHarness(), options);
        var quietInput = NewInput(NewChapters(), "ch_10");
        var quietDecision = quiet.Decide(quietInput);
        Assert.Null(await quiet.RunAsync(quietInput, quietDecision, CancellationToken.None));
        Assert.Null(quiet.LastDegradeCode);
    }

    /// <summary>全阶段抛 usage_limit_exceeded 的 stub。</summary>
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

    /// <summary>无异常静默失败：快照无 features + 结果非对象 → 解析均为 null → round null 且无降级码。</summary>
    private sealed class SilentEmptyHarness : IFictionHarnessClient
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
                    Result = JsonSerializer.Deserialize<JsonElement>("\"none\""),
                }
            );

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

    /// <summary>IChapterWriteResult 测试桩。</summary>
    private sealed record ChapterWriteResultStub(string Id, int WordCount) : IChapterWriteResult;

    [Fact]
    public async Task SkipDecision_EmitsSkipSignalThroughHook()
    {
        var store = new MockNovelStore();
        var counts = new Dictionary<string, int>();
        var harness = new RoutingHarness(
            cap => new HarnessResult
            {
                Capability = cap,
                Result = JsonSerializer.Deserialize<JsonElement>(DeepStubJson),
            },
            counts: counts
        );
        var agent = new Agent(new ToolRegistry()) { CurrentNovelId = NovelId };
        var novel = NewNovel();
        await store.SaveNovel(novel);
        store.AddChapters(
            NovelId,
            new Chapter
            {
                Id = "ch_8",
                Title = "第8章",
                Order = 8,
                Content = "林云握紧拳头。悬念四起。",
            },
            new Chapter
            {
                Id = "ch_9",
                Title = "第9章",
                Order = 9,
                Content = "林云握紧拳头。",
            }
        );

        var options = new CloudDiagnosisOptions();
        var scheduler = new DiagnosisScheduler(harness, options);
        var hook = new WritingFlowHook(agent, store, scheduler, null, options);

        // 先跑 ch_9（建立 lastChecked=9 锚点），随后写入 ch_10 且内容与 ch_9 相同：
        // L1-1 距离 1 < 都市下界 3；L1-2/3/4 与 ch_9 相同状态不命中（有钩子？「。」结尾无钩子词→可能命中）
        // 为确定性跳过：让 ch_10 内容含钩子词并保持信息差。
        store.AddChapters(
            NovelId,
            new Chapter
            {
                Id = "ch_10",
                Title = "第10章",
                Order = 10,
                Content = "林云握紧拳头，杀意骤起！",
            }
        );

        var writeDetails = new ChapterWriteResultStub("ch_10", 900);
        var ctx = new ToolHookContext
        {
            ToolName = "write_chapter",
            Result = new AgentToolResult<IChapterWriteResult>
            {
                Content = [new TextContent("ok")],
                Details = writeDetails,
            },
        };

        // 直接调用内部决策路径不可行——通过 Hook 全流程验证跳过说明注入
        await hook.AfterToolCallAsync(ctx, CancellationToken.None);

        // 若 L1/L2 有命中则走云端；此处仅断言「要么跳过说明、要么云端信号」必出现其一，
        // 且若为跳过说明则必须包含锚点文本。
        var injected = (LlmChatMessage)agent.InjectQueue.DequeueAllWithIds()[0].Message;
        Assert.True(
            injected.Content!.Contains("[云端诊断]") || injected.Content.Contains("写作事件信号"),
            "应注入云端信号或本地信号"
        );
    }

    [Fact]
    public async Task DeepDenied_FallsBackToBaseTier_SignalStillProduced()
    {
        var counts = new Dictionary<string, int>();
        var harness = new RoutingHarness(
            cap => new HarnessResult
            {
                Capability = cap,
                Result = JsonSerializer.Deserialize<JsonElement>(ContextJson),
            },
            () => throw new CloudApiException("forbidden", "权限被拒（模拟 403）"),
            counts
        );
        var scheduler = new DiagnosisScheduler(harness, new CloudDiagnosisOptions());
        var chapters = NewChapters();

        var input = NewInput(chapters, "ch_9"); // L2-1 命中 → DeepCheck=true
        var decision = scheduler.Decide(input);
        Assert.True(decision.DeepCheck);

        var round = await scheduler.RunAsync(input, decision, CancellationToken.None);

        // 验收：403 后仅基础档调用且信号仍产出
        Assert.NotNull(round);
        Assert.True(round.DeepDenied);
        Assert.Null(round.StoryCheck);
        Assert.NotNull(round.ContextRetrieve);
        Assert.Equal(1, counts.GetValueOrDefault("story.check")); // 尝试过一次
        Assert.Equal(1, counts.GetValueOrDefault("context.retrieve")); // 基础档照常
        Assert.Equal(1, counts.GetValueOrDefault("base:fiction.scene.plan"));

        // 会话级粘滞：后续 Decide 不再放行深度档。
        // 用 L2-1（伏笔回收章 ch_9）而非 isRewrite：首次 rewrite 本就不构成 L2-3，
        // 原断言无法区分「粘滞降级」与「首次重写未命中 L2」。
        var next = scheduler.Decide(NewInput(chapters, "ch_9"));
        Assert.True(next.ShouldRun);
        Assert.False(next.DeepCheck);
    }

    /// <summary>
    /// #3 修正：输入/契约类 4xx（422 invalid_analysis_input）只作**一次性**降级——
    /// 不得触发会话级粘滞（否则一次参数错误会永久关掉整个会话的深度档）。
    /// 对照：403 forbidden → 粘滞（见 DeepDenied_FallsBackToBaseTier_SignalStillProduced）。
    /// </summary>
    [Fact]
    public async Task InputRejection_DoesNotStickDeepDenial()
    {
        var counts = new Dictionary<string, int>();
        var harness = new RoutingHarness(
            cap => new HarnessResult
            {
                Capability = cap,
                Result = JsonSerializer.Deserialize<JsonElement>(ContextJson),
            },
            () =>
                throw new CloudApiException(
                    "invalid_analysis_input",
                    "Each chapter needs a clientChapterRef and content."
                ),
            counts
        );
        var scheduler = new DiagnosisScheduler(harness, new CloudDiagnosisOptions());
        var chapters = NewChapters();

        var input = NewInput(chapters, "ch_9"); // L2-1 命中 → DeepCheck=true
        var decision = scheduler.Decide(input);
        Assert.True(decision.DeepCheck);
        var round = await scheduler.RunAsync(input, decision, CancellationToken.None);

        Assert.NotNull(round);
        Assert.False(round.DeepDenied); // 并非「权限/凭据问题」
        Assert.Equal("invalid_analysis_input", scheduler.LastDegradeCode); // 降级码可见（一次性）
        Assert.Equal(1, counts.GetValueOrDefault("story.check")); // 尝试过一次

        // 未粘滞：同一 L2 章再次 Decide 仍放行深度档（对照 403 用例的 False）
        var next = scheduler.Decide(NewInput(chapters, "ch_9"));
        Assert.True(next.ShouldRun);
        Assert.True(next.DeepCheck);
    }

    private sealed class ThrowingAllHarness : IFictionHarnessClient
    {
        public Task<HarnessResult> ExecuteAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<AgentContextSnapshot> GetContextAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task ReportSetupFeedbackAsync(
            IReadOnlyList<(string FindingId, string Decision)> items,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    [Fact]
    public void L1_Samples_AIBuzzword_InfoGapDrop_MissingHook()
    {
        // L1-2 AI味超阈（>1.0/千字：短文本塞 2 个关键词）
        var buzz = new DiagnosisInput
        {
            NovelId = NovelId,
            ChapterId = "c1",
            ChapterTitle = "",
            ChapterOrder = 1,
            CurrentContent = "此外，至关重要。",
            IsRewrite = false,
            Chapters =
            [
                new Chapter
                {
                    Id = "c1",
                    Order = 1,
                    Content = "",
                },
            ],
            Novel = NewNovel(),
        };
        Assert.Contains("L1-2", scheduler_DecideHits(buzz));

        // L1-3 信息差泄底疑云（前章 2 个信息差 → 本章 0 个）
        var drop = new DiagnosisInput
        {
            NovelId = NovelId,
            ChapterId = "c2",
            ChapterTitle = "",
            ChapterOrder = 2,
            CurrentContent = "林云握紧拳头，杀意骤起！",
            PreviousContent = "秘密A。秘密B。秘密C。",
            IsRewrite = false,
            Chapters =
            [
                new Chapter
                {
                    Id = "c2",
                    Order = 2,
                    Content = "",
                },
            ],
            Novel = NewNovel(),
        };
        Assert.Contains("L1-3", scheduler_DecideHits(drop));
    }

    [Fact]
    public void TryConsumeDecisionSlot_ExhaustsAfterMax()
    {
        var scheduler = new DiagnosisScheduler(
            new ThrowingAllHarness(),
            new CloudDiagnosisOptions { DecisionMaxPerSession = 2 }
        );

        Assert.True(scheduler.TryConsumeDecisionSlot());
        Assert.True(scheduler.TryConsumeDecisionSlot());
        Assert.False(scheduler.TryConsumeDecisionSlot()); // 超过上限
    }

    [Fact]
    public async Task FreeFeedbackUpgrade_LowScoreAndHighSeverity_ForcesDeepCheck()
    {
        var counts = new Dictionary<string, int>();
        var harness = new RoutingHarness(
            cap => new HarnessResult
            {
                Capability = cap,
                Result = JsonSerializer.Deserialize<JsonElement>(LowScoreContextJson),
            },
            () =>
                new HarnessResult // story.check stub（深度档）
                {
                    Capability = "fiction.story.check",
                    CloudWorkUnits = 5,
                    Result = JsonSerializer.Deserialize<JsonElement>(DeepStubJson),
                },
            counts
        );
        // 覆盖 GetContextAsync 以返回低分快照（模拟基础档评分极低）
        var lowScoreHarness = new LowScoreContextHarness(harness);
        var scheduler = new DiagnosisScheduler(
            lowScoreHarness,
            new CloudDiagnosisOptions { UpgradeScoreThreshold = 50, DeepBudgetPerSession = 1 }
        );
        var chapters = NewChapters();
        var input = NewInput(chapters, "ch_8");
        var decision = scheduler.Decide(input); // 无 L2 命中 → DeepCheck=false

        Assert.False(decision.DeepCheck);

        var round = await scheduler.RunAsync(input, decision, CancellationToken.None);

        // 基础档 overallScore=30 < 50 → 强行升级 story.check
        Assert.NotNull(round);
        Assert.NotNull(round.StoryCheck);
        Assert.Equal(1, counts.GetValueOrDefault("story.check"));
        // 对账透出：深度档实际结算消耗（cloud-work-units）随 round 回传
        Assert.Equal(5, round.DeepUnits);
    }

    /// <summary>
    /// 2026-10-03 默认阈值标定（62，与 pass 线对齐）：实测带内分数（0.59，旧默认 50 下不触发）
    /// + ≥2 条高危信号 → 使用**默认配置**即升级一次深度诊断（防默认值漂移出实测带的回归锚）。
    /// </summary>
    [Fact]
    public async Task FreeFeedbackUpgrade_DefaultThreshold62_RealBandScore_ForcesDeepCheck()
    {
        var counts = new Dictionary<string, int>();
        var harness = new RoutingHarness(
            cap => new HarnessResult
            {
                Capability = cap,
                Result = JsonSerializer.Deserialize<JsonElement>(LowScoreContextJson),
            },
            () =>
                new HarnessResult // story.check stub（深度档）
                {
                    Capability = "fiction.story.check",
                    CloudWorkUnits = 5,
                    Result = JsonSerializer.Deserialize<JsonElement>(DeepStubJson),
                },
            counts
        );
        var scheduler = new DiagnosisScheduler(
            new LowScoreContextHarness(harness, score: 0.59),
            new CloudDiagnosisOptions() // 全默认：DeepBudgetPerSession/Novel = 100，UpgradeScoreThreshold = 62
        );
        var chapters = NewChapters();
        var input = NewInput(chapters, "ch_8");

        var decision = scheduler.Decide(input); // 无 L2 命中 → 仅低分升级应进入深度档
        Assert.False(decision.DeepCheck);

        var round = await scheduler.RunAsync(input, decision, CancellationToken.None);

        Assert.NotNull(round);
        Assert.NotNull(round.StoryCheck);
        Assert.Equal(1, counts.GetValueOrDefault("story.check"));
    }

    private sealed class LowScoreContextHarness(IFictionHarnessClient inner, double score = 0.3)
        : IFictionHarnessClient
    {
        public Task<HarnessResult> ExecuteAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken
        ) => inner.ExecuteAsync(request, idempotencyKey, cancellationToken);

        public Task<AgentContextSnapshot> GetContextAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new AgentContextSnapshot
                {
                    TargetChapterRef = "ch_8",
                    ChapterCount = 3,
                    PreviousChapterCount = 1,
                    ParagraphCount = 10,
                    Features = JsonSerializer.Deserialize<JsonElement>(
                        FormattableString.Invariant(
                            $"{{\"overallScore\":{score},\"wordCount\":1000}}"
                        )
                    ),
                    // §16.1 双重条件（2026-08-28 恢复）：低分 + high severity ≥ 2 才升级深度档。
                    // 中文 severity（"严重"）由客户端 NormalizeSeverity 归一为 high。
                    Signals = JsonSerializer.Deserialize<JsonElement>(
                        """
                        [{"dimension":"代入感","severity":"严重","description":"主角行为不可理喻","impact-score":90},
                         {"dimension":"节奏","severity":"严重","description":"节奏混乱","impact-score":85}]
                        """
                    ),
                }
            );

        public Task ReportSetupFeedbackAsync(
            IReadOnlyList<(string FindingId, string Decision)> items,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    private static readonly string LowScoreContextJson =
        "{\"features\":{\"overallScore\":0.3},\"signals\":["
        + "{\"dimension\":\"代入感\",\"severity\":\"high\",\"description\":\"主角行为不可理喻\",\"impactScore\":90},"
        + "{\"dimension\":\"节奏\",\"severity\":\"high\",\"description\":\"节奏混乱\",\"impactScore\":85}"
        + "]}";

    private static readonly string DeepStubJson =
        "{\"dimensions\":{\"pace\":{\"overallScore\":72}},\"network\":{\"characterPageRank\":{\"topCharacter\":\"林云\"}}}";

    /// <summary>
    /// 深度档 story.check 成功但 network.characterPageRank 缺失（单章数据可为 null，
    /// HARNESS-CLOUD §0.3/Q3）且基础档（context.retrieve + scene.plan）双失败时，
    /// round 不得被丢弃（§3.6 降级语义：任一能力成功即算云端路径成功，
    /// 否则 Hook 误走本地降级丢失深度诊断）。
    /// </summary>
    [Fact]
    public async Task StoryCheckSuccess_WithoutPageRank_BaseTierFailed_KeepsRound()
    {
        var scheduler = new DiagnosisScheduler(
            new BaseTierFailsHarness(),
            new CloudDiagnosisOptions()
        );
        var chapters = NewChapters();
        var input = NewInput(chapters, "ch_9"); // L2-1 命中 → DeepCheck=true

        var decision = scheduler.Decide(input);
        var round = await scheduler.RunAsync(input, decision, CancellationToken.None);

        Assert.NotNull(round); // 不得因基础档失败而整体降级
        Assert.NotNull(round.StoryCheck);
        Assert.Null(round.ContextRetrieve);
        Assert.Null(round.ScenePlan);
    }

    /// <summary>基础档双失败（ExecuteAsync 抛 + GetContextAsync 抛）、深度档 story.check 成功且无 PageRank 的 stub。</summary>
    private sealed class BaseTierFailsHarness : IFictionHarnessClient
    {
        public Task<HarnessResult> ExecuteAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken
        ) =>
            request.Capability == "fiction.story.check"
                ? Task.FromResult(
                    new HarnessResult
                    {
                        Capability = "fiction.story.check",
                        Result = JsonSerializer.Deserialize<JsonElement>(
                            """{"dimensions":{"pace":{"overall-score":72}}}"""
                        ),
                    }
                )
                : throw new CloudApiException("unavailable", "基础档不可用");

        public Task<AgentContextSnapshot> GetContextAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken
        ) => throw new CloudApiException("unavailable", "基础档不可用");

        public Task ReportSetupFeedbackAsync(
            IReadOnlyList<(string FindingId, string Decision)> items,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    /// <summary>Depth 按能力分级（C-6 试点）：story.check=standard，其余=quick；context.retrieve 固定 IncludeFeatures=true。</summary>
    [Fact]
    public async Task FunnelRequests_UseDepthPerCapability_AndContextRetrieveIncludeFeatures()
    {
        var requests = new List<HarnessRequest>();
        var harness = new RecordingRequestHarness(requests);
        var scheduler = new DiagnosisScheduler(harness, new CloudDiagnosisOptions());
        var chapters = NewChapters();
        var input = NewInput(chapters, "ch_9"); // L2-1 命中 → deep

        var decision = scheduler.Decide(input);
        Assert.True(decision.DeepCheck);
        await scheduler.RunAsync(input, decision, CancellationToken.None);

        // 深度档 story.check 升 standard（启用 RST Layer B）；基础档 ctx/plan 保持 quick
        Assert.NotEmpty(requests);
        var check = requests.Single(r => r.Capability == "fiction.story.check");
        Assert.Equal("standard", check.Depth);
        Assert.All(
            requests.Where(r => r.Capability != "fiction.story.check"),
            r => Assert.Equal("quick", r.Depth)
        );
        var ctx = requests.Single(r => r.Capability == "fiction.context.retrieve");
        Assert.True(ctx.Options?.IncludeFeatures);
    }

    /// <summary>
    /// Phase 5.1：scene.plan/story.check 挂设定图（Context.Setup），context.retrieve 保持不挂；
    /// beat 章引用由客户端规范化为 ch_{Order}（云端按章序匹配，客户端 12 位 hex 引用解析不到）。
    /// </summary>
    [Fact]
    public async Task FunnelRequests_AttachSetupDigest_ForScenePlanAndStoryCheckOnly()
    {
        var requests = new List<HarnessRequest>();
        var harness = new RecordingRequestHarness(requests);
        var scheduler = new DiagnosisScheduler(harness, new CloudDiagnosisOptions());
        var chapters = NewChapters();
        var novel = NewNovel();
        var setup = Core.Cloud.SetupDigestBuilder.Build(
            novel,
            characters: [new Character { Id = "c_lin", Name = "林云" }],
            factions: [],
            relationships: [],
            scenes: [],
            plotlines:
            [
                new Plotline
                {
                    Id = "pl_1",
                    Name = "师门血案",
                    PlannedRevealChapterId = "ch_9",
                    Turnpoints = "第9章：揭示杀父真相",
                },
            ],
            chapters: chapters,
            timelineEvents: []
        );
        var input = NewInput(chapters, "ch_9") with { SetupDigest = setup }; // L2-1 命中 → deep

        var decision = scheduler.Decide(input);
        Assert.True(decision.DeepCheck);
        await scheduler.RunAsync(input, decision, CancellationToken.None);

        var plan = requests.Single(r => r.Capability == "fiction.scene.plan");
        Assert.NotNull(plan.Context?.Setup);
        var check = requests.Single(r => r.Capability == "fiction.story.check");
        Assert.NotNull(check.Context?.Setup);

        // beat 章引用：ch_{Order} 规范化（本地章 Id 是 12 位 hex GUID，云端解析不到）
        var beat = check.Context!.Setup!.Plotlines!.Single().Beats!.Single();
        Assert.Equal("ch_9", beat.ChapterId);
        Assert.Equal("ch_9", check.Context.Setup.Plotlines!.Single().PlannedRevealChapterId);

        // 基础档 context.retrieve 保持轻量（不挂设定图）
        var ctx = requests.Single(r => r.Capability == "fiction.context.retrieve");
        Assert.Null(ctx.Context?.Setup);
    }

    /// <summary>
    /// 5.2（2026-10-18）：story.check 挂 target 之前最近 ≤20 章（升序、role=previous）——
    /// 只服务连续性窗口与规则 pass 的跨章上下文；scene.plan / context.retrieve 不挂。
    /// </summary>
    [Fact]
    public async Task FunnelRequests_StoryCheckCarriesUpstreamWindow_Only()
    {
        var requests = new List<HarnessRequest>();
        var harness = new RecordingRequestHarness(requests);
        var scheduler = new DiagnosisScheduler(harness, new CloudDiagnosisOptions());
        var chapters = NewChapters(25, startOrder: 1); // ch_1..ch_25
        var novel = NewNovel();
        novel.Plotlines[0].PlannedRevealChapterId = "ch_25"; // L2-1 命中 → deep
        var input = NewInput(chapters, "ch_25") with { Novel = novel };

        var decision = scheduler.Decide(input);
        Assert.True(decision.DeepCheck);
        await scheduler.RunAsync(input, decision, CancellationToken.None);

        var check = requests.Single(r => r.Capability == "fiction.story.check");
        var upstream = check.Context?.PreviousChapters;
        Assert.NotNull(upstream);
        Assert.Equal(20, upstream!.Count);
        Assert.Equal(5, upstream[0].Order); // 24..5 取最近 20 章后升序
        Assert.Equal(24, upstream[^1].Order);
        Assert.All(upstream, chapter => Assert.True(chapter.Order < 25));
        Assert.All(upstream, chapter => Assert.Equal("previous", chapter.Role));

        Assert.Null(
            requests.Single(r => r.Capability == "fiction.scene.plan").Context?.PreviousChapters
        );
        Assert.Null(
            requests
                .Single(r => r.Capability == "fiction.context.retrieve")
                .Context?.PreviousChapters
        );
    }

    /// <summary>记录全部请求的 stub（BuildRequest 组装断言用）。</summary>
    private sealed class RecordingRequestHarness(List<HarnessRequest> sink) : IFictionHarnessClient
    {
        public Task<HarnessResult> ExecuteAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken ct
        )
        {
            sink.Add(request);
            return Task.FromResult(
                new HarnessResult
                {
                    Capability = request.Capability,
                    Result = JsonSerializer.Deserialize<JsonElement>(DeepStubJson),
                }
            );
        }

        public Task<AgentContextSnapshot> GetContextAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken ct
        )
        {
            sink.Add(request);
            return Task.FromResult(
                new AgentContextSnapshot
                {
                    ChapterCount = 12,
                    Features = JsonSerializer.Deserialize<JsonElement>(
                        """{"overall-score":0.88,"narrative-entropy":1.4}"""
                    ),
                }
            );
        }

        public Task ReportSetupFeedbackAsync(
            IReadOnlyList<(string FindingId, string Decision)> items,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    private static string scheduler_DecideHits(DiagnosisInput input)
    {
        var scheduler = new DiagnosisScheduler(
            new ThrowingAllHarness(),
            new CloudDiagnosisOptions()
        );
        var decision = scheduler.Decide(input);
        return decision.HitSummary;
    }

    /// <summary>
    /// HARNESS-CLOUD §16.5 方案 B/C：会话预算耗尽后，每小说池/银行仍可用时深度档继续放行；
    /// 全耗尽时停止深度调用。验证 budgetStore 接入不影响无 store 的旧语义。
    /// </summary>
    [Fact]
    public async Task NovelBudgetStore_SessionExhausted_ButPoolAllowsDeep()
    {
        var options = new CloudDiagnosisOptions
        {
            DeepBudgetPerSession = 1,
            DeepBudgetPerNovel = 20,
        };
        var counts = new Dictionary<string, int>();
        var harness = new RoutingHarness(
            cap => new HarnessResult
            {
                Capability = cap,
                Result = JsonSerializer.Deserialize<JsonElement>(DeepStubJson),
            },
            () =>
                new HarnessResult
                {
                    Capability = "fiction.story.check",
                    Result = JsonSerializer.Deserialize<JsonElement>(
                        "{\"dimensions\":{\"pace\":{\"overallScore\":50}}}"
                    ),
                },
            counts
        );
        var budgetStore = new NovelBudgetStore();
        var scheduler = new DiagnosisScheduler(harness, options, novelBudgetStore: budgetStore);
        var chapters = NewChapters();

        // 第一章（L2-1 命中 → deep）：会话预算 1 被消耗
        var d1 = scheduler.Decide(NewInput(chapters, "ch_9"));
        Assert.True(d1.ShouldRun && d1.DeepCheck);
        await scheduler.RunAsync(NewInput(chapters, "ch_9"), d1, CancellationToken.None);
        Assert.Equal(1, counts.GetValueOrDefault("story.check"));

        // 第二章（二次重写 → L2-3 deep）：会话预算耗尽，但每小说池（19 剩余）允许深度
        var rewriteInput = NewInput(chapters, "ch_10", isRewrite: true);
        _ = scheduler.Decide(rewriteInput); // 第一次重写计数（IsSecondRewrite 首次 false）
        var d2 = scheduler.Decide(rewriteInput); // 第二次重写 → L2-3 命中 → deep
        Assert.True(d2.ShouldRun);
        Assert.True(d2.DeepCheck, "会话预算耗尽但每小说池剩余 → 深度档应放行");
        await scheduler.RunAsync(rewriteInput, d2, CancellationToken.None);
        Assert.Equal(2, counts.GetValueOrDefault("story.check"));

        // 检查预算状态：池 18、银行 0、会话已用 2
        var (exhausted, _, _) = scheduler.CheckBudget("novel_e2");
        Assert.False(exhausted);
    }

    /// <summary>
    /// 跨仓库缺口 1.5：AgentContextSnapshot 透传 signals[] + dimensionScores（§6.1），
    /// 调度器 ParseContextRetrieveFromSnapshot 将其解析进 ContextRetrieveOutcome——
    /// 基础档 S-1（high severity signals）与 F-1~F-5（16 中文键维度分）的判定输入。
    /// </summary>
    [Fact]
    public async Task SnapshotWithSignalsAndDimensionScores_AreParsedIntoContextOutcome()
    {
        var counts = new Dictionary<string, int>();
        var harness = new SignalSnapshotHarness();
        var options = new CloudDiagnosisOptions { UpgradeScoreThreshold = 50 };
        var scheduler = new DiagnosisScheduler(harness, options);
        var chapters = NewChapters();

        var input = NewInput(chapters, "ch_10");
        var decision = scheduler.Decide(input);
        Assert.True(decision.ShouldRun);
        var round = await scheduler.RunAsync(input, decision, CancellationToken.None);

        Assert.NotNull(round);
        Assert.NotNull(round.ContextRetrieve);
        // 维度分：16 中文键已透传（F-1 节奏 45 < 55 → 命中）
        Assert.True(
            round.ContextRetrieve.DimensionScores.TryGetValue("节奏", out var pace) && pace < 55,
            "dimensionScores 应包含 节奏 键"
        );
        // signals：high severity 信号已透传（S-1 判定输入）
        Assert.Contains(
            round.ContextRetrieve.Signals,
            s => s.Severity == "high" && s.Dimension == "节奏"
        );
    }

    /// <summary>携带 signals + dimensionScores 的快照 stub（§6.1 契约）。</summary>
    private sealed class SignalSnapshotHarness : IFictionHarnessClient
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
                    Result = JsonSerializer.Deserialize<JsonElement>(DeepStubJson),
                }
            );

        public Task<AgentContextSnapshot> GetContextAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken ct
        )
        {
            var features = JsonSerializer.Deserialize<JsonElement>(
                """{"overall-score":0.5,"word-count":1000,"paragraph-count":5,"dialogue-ratio":0.2,"narrative-entropy":1.4}"""
            );
            var signals = JsonSerializer.Deserialize<JsonElement>(
                """[{"dimension":"节奏","severity":"high","description":"节奏混乱","impact-score":85}]"""
            );
            var dimensionScores = JsonSerializer.Deserialize<JsonElement>(
                """{"代入感":70,"爽感":60,"节奏":45,"冲突":65,"描写":80,"对话":50,"伏笔":55,"角色弧":60,"开篇":70,"常识错误":90,"高潮":55,"读者参与":60,"去AI味":75,"文笔质量":70,"章末钩子":50,"信息差":60}"""
            );
            return Task.FromResult(
                new AgentContextSnapshot
                {
                    TargetChapterRef = "ch_x",
                    ChapterCount = 12,
                    PreviousChapterCount = 3,
                    ParagraphCount = 40,
                    Features = features,
                    Signals = signals,
                    DimensionScores = dimensionScores,
                }
            );
        }

        public Task ReportSetupFeedbackAsync(
            IReadOnlyList<(string FindingId, string Decision)> items,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    /// <summary>§16.1 双重条件负例（2026-08-28 恢复后补测）：低分但 high 信号不足 2 条 → 不升级深度档。</summary>
    [Fact]
    public async Task FreeFeedbackUpgrade_LowScoreButFewerThanTwoHighSignals_DoesNotUpgrade()
    {
        var counts = new Dictionary<string, int>();
        var harness = new RoutingHarness(
            cap => new HarnessResult
            {
                Capability = cap,
                Result = JsonSerializer.Deserialize<JsonElement>(LowScoreContextJson),
            },
            () =>
                new HarnessResult
                {
                    Capability = "fiction.story.check",
                    Result = JsonSerializer.Deserialize<JsonElement>(DeepStubJson),
                },
            counts
        );
        var weakHarness = new WeakSignalContextHarness(harness);
        var scheduler = new DiagnosisScheduler(
            weakHarness,
            new CloudDiagnosisOptions { UpgradeScoreThreshold = 50, DeepBudgetPerSession = 1 }
        );
        var input = NewInput(NewChapters(), "ch_8");
        var decision = scheduler.Decide(input);

        var round = await scheduler.RunAsync(input, decision, CancellationToken.None);

        // 仅 1 条 high（低于 ≥2 门槛）→ 不升级深度档
        Assert.NotNull(round);
        Assert.Null(round.StoryCheck);
        Assert.Equal(0, counts.GetValueOrDefault("story.check"));
    }

    /// <summary>低分快照但仅 1 条 high 信号（§16.1 双条件负例桩）。</summary>
    private sealed class WeakSignalContextHarness(IFictionHarnessClient inner)
        : IFictionHarnessClient
    {
        public Task<HarnessResult> ExecuteAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken
        ) => inner.ExecuteAsync(request, idempotencyKey, cancellationToken);

        public Task<AgentContextSnapshot> GetContextAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new AgentContextSnapshot
                {
                    TargetChapterRef = "ch_8",
                    ChapterCount = 3,
                    Features = JsonSerializer.Deserialize<JsonElement>(
                        "{\"overallScore\":0.3,\"wordCount\":1000}"
                    ),
                    Signals = JsonSerializer.Deserialize<JsonElement>(
                        """[{"dimension":"节奏","severity":"严重","description":"节奏混乱","impact-score":85}]"""
                    ),
                }
            );

        public Task ReportSetupFeedbackAsync(
            IReadOnlyList<(string FindingId, string Decision)> items,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }
}

/// <summary>
/// HARNESS-CLOUD §16.5 方案 A（LateNovelForceDeep）边界测试：
/// 后半段（Order ≥ 20 章地板）L2 命中时预算耗尽仍强制深度档（BudgetOverrun 标记），
/// 非后半段预算耗尽则正常拦截。
/// </summary>
[Collection("CloudDiagnosisSchedulerSerial")]
public sealed class LateNovelForceDeepTests
{
    private const string NovelId = "novel_late";

    private static Novel NewNovel(string? plannedRevealChapterId = "ch_20") =>
        new()
        {
            Id = NovelId,
            Title = "后半段测试",
            Genre = "都市",
            AutoDiagnose = true,
            Plotlines =
            [
                new Plotline
                {
                    Id = "pl_1",
                    Name = "师门血案",
                    PlannedRevealChapterId = plannedRevealChapterId,
                },
            ],
        };

    private static List<Chapter> NewChapters(int count, int startOrder = 1)
    {
        var list = new List<Chapter>();
        for (var i = 0; i < count; i++)
            list.Add(
                new Chapter
                {
                    Id = $"ch_{startOrder + i}",
                    Title = $"第{startOrder + i}章",
                    Order = startOrder + i,
                    Content = "林云握紧拳头。",
                }
            );
        return list;
    }

    private static DiagnosisInput NewInput(
        List<Chapter> chapters,
        string chapterId,
        Novel? novel = null
    ) =>
        new()
        {
            NovelId = NovelId,
            ChapterId = chapterId,
            ChapterTitle = "当前章",
            ChapterOrder = chapters.First(c => c.Id == chapterId).Order,
            CurrentContent = chapters.First(c => c.Id == chapterId).Content,
            IsRewrite = false,
            Chapters = chapters,
            Novel = novel ?? NewNovel(),
        };

    private sealed class CountingHarness : IFictionHarnessClient
    {
        public int DeepCalls;

        public Task<HarnessResult> ExecuteAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken ct
        )
        {
            if (request.Capability == "fiction.story.check")
                Interlocked.Increment(ref DeepCalls);
            return Task.FromResult(
                new HarnessResult
                {
                    Capability = request.Capability,
                    Result = JsonSerializer.Deserialize<JsonElement>(
                        request.Capability == "fiction.story.check"
                            ? """{"dimensions":{"pace":{"overall-score":72}}}"""
                            : """{"features":{"overall-score":0.88}}"""
                    ),
                }
            );
        }

        public Task<AgentContextSnapshot> GetContextAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken ct
        ) =>
            Task.FromResult(
                new AgentContextSnapshot
                {
                    ChapterCount = 30,
                    Features = JsonSerializer.Deserialize<JsonElement>(
                        """{"overall-score":0.88,"narrative-entropy":1.4}"""
                    ),
                }
            );

        public Task ReportSetupFeedbackAsync(
            IReadOnlyList<(string FindingId, string Decision)> items,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    /// <summary>30 章小说（阈值 = max(20, ceil(30*0.7)=21) = 21）：maxOrder 30 ≥ 21 → 后半段。</summary>
    [Fact]
    public async Task LateNovel_BudgetExhausted_ForceDeepCheck_OverrunMarked()
    {
        var harness = new CountingHarness();
        var options = new CloudDiagnosisOptions
        {
            DeepBudgetPerSession = 1,
            LateNovelOrderRatio = 0.7,
        };
        var scheduler = new DiagnosisScheduler(harness, options);
        var chapters = NewChapters(30);

        // 第一次（ch_20 伏笔回收 → L2-1 deep）：消耗会话预算 1
        var d1 = scheduler.Decide(NewInput(chapters, "ch_20"));
        Assert.True(d1.DeepCheck);
        await scheduler.RunAsync(NewInput(chapters, "ch_20"), d1, CancellationToken.None);
        Assert.Equal(1, harness.DeepCalls);

        // 第二次（ch_25）：预算耗尽 → Decide 深度档不放行，但 LateNovelForceDeep 置位
        var d2 = scheduler.Decide(NewInput(chapters, "ch_25"));
        Assert.False(d2.DeepCheck);
        Assert.True(d2.LateNovelForceDeep, "30 章 ≥ 21 阈值 → 后半段标记应置位");
        var round2 = await scheduler.RunAsync(
            NewInput(chapters, "ch_25"),
            d2,
            CancellationToken.None
        );
        // LateNovelForceDeep → 即使预算耗尽也强制深度（BudgetOverrun 标记）
        Assert.NotNull(round2);
        Assert.NotNull(round2.StoryCheck);
        Assert.True(round2.BudgetOverrun);
        Assert.Equal(2, harness.DeepCalls);
    }

    /// <summary>短篇（15 章，阈值地板 20）：非后半段，预算耗尽正常拦截深度。</summary>
    [Fact]
    public async Task ShortNovel_DeepBudgetExhausted_DeepBlocked()
    {
        var harness = new CountingHarness();
        var options = new CloudDiagnosisOptions
        {
            DeepBudgetPerSession = 1,
            LateNovelOrderRatio = 0.7,
        };
        var scheduler = new DiagnosisScheduler(harness, options);
        var chapters = NewChapters(15);
        // 短篇小说：plotline 指向 ch_8（存在）→ L2-1 命中 → deep
        var d1 = scheduler.Decide(NewInput(chapters, "ch_8", NewNovel("ch_8")));
        Assert.True(d1.DeepCheck); // 伏笔回收 L2-1
        await scheduler.RunAsync(NewInput(chapters, "ch_8"), d1, CancellationToken.None);
        Assert.Equal(1, harness.DeepCalls);

        var d2 = scheduler.Decide(NewInput(chapters, "ch_15"));
        Assert.False(d2.LateNovelForceDeep);
        Assert.False(d2.DeepCheck);
        var round2 = await scheduler.RunAsync(
            NewInput(chapters, "ch_15"),
            d2,
            CancellationToken.None
        );
        Assert.NotNull(round2);
        Assert.Null(round2.StoryCheck); // 预算耗尽 → 深度被拦截
        Assert.Equal(1, harness.DeepCalls);
    }

    /// <summary>后半段强制深度不阻塞基础档（基础档不受预算限制）。</summary>
    [Fact]
    public async Task LateNovel_BaseTier_StillRuns_WhenDeepForced()
    {
        var harness = new CountingHarness();
        var options = new CloudDiagnosisOptions
        {
            DeepBudgetPerSession = 0, // 无任何深度预算
            LateNovelOrderRatio = 0.7,
        };
        var scheduler = new DiagnosisScheduler(harness, options);
        var chapters = NewChapters(30);

        var d = scheduler.Decide(NewInput(chapters, "ch_25"));
        Assert.True(d.LateNovelForceDeep);
        var round = await scheduler.RunAsync(
            NewInput(chapters, "ch_25"),
            d,
            CancellationToken.None
        );

        Assert.NotNull(round);
        Assert.NotNull(round.ContextRetrieve); // 基础档照常
        Assert.NotNull(round.StoryCheck); // 后半段强制 → 深度也跑（forceOverrun）
        Assert.True(round.BudgetOverrun);
    }
}
