using System.Globalization;
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
/// HARNESS-CLOUD §10 P1-H3 验收：缓存刷新调用计数 / stale≥5 章触发刷新 /
/// ChapterCount 失效 / narrativeEntropy 存在性 / 熵差计算 / 刷新失败沿用旧值 /
/// A-4 熵方向处方分支。
/// </summary>
public sealed class ContextSnapshotCacheTests
{
    /// <summary>可配置 features 的 GetContextAsync stub（FeaturesJson 可变以测熵差；Break 模拟故障）。</summary>
    private sealed class SnapshotHarness(string featuresJson) : IFictionHarnessClient
    {
        public int ContextCalls;
        public string FeaturesJson { get; set; } = featuresJson;
        public bool Broken { get; set; }

        public Task<HarnessResult> ExecuteAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<AgentContextSnapshot> GetContextAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken
        )
        {
            if (Broken)
                throw new CloudApiException("analysis_failed", "broken");
            Interlocked.Increment(ref ContextCalls);

            // 直接构造快照（对齐 HttpFictionHarnessClient.ToSnapshot 的产物形态：
            // context 计数 + features 原始 JsonElement）
            var features = JsonSerializer.Deserialize<JsonElement>(FeaturesJson);
            return Task.FromResult(
                new AgentContextSnapshot
                {
                    TargetChapterRef = "ch_x",
                    ChapterCount = 12,
                    PreviousChapterCount = 3,
                    ParagraphCount = 40,
                    Features = features,
                }
            );
        }

        public Task ReportSetupFeedbackAsync(
            IReadOnlyList<(string FindingId, string Decision)> items,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    private static HarnessRequest NewRequest() =>
        new()
        {
            Capability = "fiction.context.retrieve",
            Options = new AnalysisOptionsInput(IncludeFeatures: true),
        };

    [Fact]
    public async Task Persistence_RoundTripsAcrossInstances()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"snap_test_{Guid.NewGuid():N}.db");
        try
        {
            var harness = new SnapshotHarness("""{"overall-score":0.9,"narrative-entropy":1.7}""");
            var first = new ContextSnapshotCache(harness, dbPath);
            await first.RefreshAsync("n1", NewRequest(), chapterOrder: 12, CancellationToken.None);

            // 新实例（模拟重启）：内存为空，从 SQLite 回源
            var second = new ContextSnapshotCache(harness, dbPath);
            var restored = second.TryGet("n1");

            Assert.NotNull(restored);
            Assert.Equal(12, restored.FetchedAtChapterOrder);
            Assert.Equal(1.7, restored.NarrativeEntropy);
            Assert.Equal(12, restored.Snapshot.ChapterCount); // 快照 JSON 往返
            Assert.False(second.IsStale("n1", currentMaxOrder: 12, localChapterCount: 12));
        }
        finally
        {
            // Microsoft.Data.Sqlite 连接池持有物理句柄：先清池再删文件，否则 Windows 上 IOException
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task Refresh_StoresEntry_ParsesEntropy_AndComputesDeltaOnSecondRefresh()
    {
        var harness = new SnapshotHarness(
            """{"overall-score":0.88,"narrative-entropy":1.4,"mattr":0.7}"""
        );
        var cache = new ContextSnapshotCache(harness);

        var first = await cache.RefreshAsync(
            "n1",
            NewRequest(),
            chapterOrder: 5,
            CancellationToken.None
        );
        Assert.NotNull(first);
        Assert.Equal(1.4, first.NarrativeEntropy); // 验收：narrativeEntropy 存在性
        Assert.Null(first.NarrativeEntropyDelta); // 首次无前值

        harness.FeaturesJson = """{"overall-score":0.9,"narrative-entropy":1.8}""";
        var second = await cache.RefreshAsync(
            "n1",
            NewRequest(),
            chapterOrder: 6,
            CancellationToken.None
        );

        Assert.NotNull(second);
        Assert.Equal(1.8, second.NarrativeEntropy);
        Assert.Equal(0.4, second.NarrativeEntropyDelta); // 熵差 = H(t) − H(t−1)
        Assert.Equal(2, harness.ContextCalls); // §11 缓存行：刷新调用计数
    }

    /// <summary>
    /// C-1：复用调用方已取得的快照更新缓存——不发起任何网络调用；
    /// 熵差仍基于上一缓存值（首个 RefreshAsync 为种子）计算。
    /// </summary>
    [Fact]
    public async Task UpdateFromSnapshot_StoresEntry_ComputesDelta_WithoutFetch()
    {
        var harness = new SnapshotHarness("""{"overall-score":0.9,"narrative-entropy":1.4}""");
        var cache = new ContextSnapshotCache(harness);
        await cache.RefreshAsync("n1", NewRequest(), chapterOrder: 5, CancellationToken.None);
        var callsBefore = harness.ContextCalls; // 种子：唯一一次真实拉取

        var entry = cache.UpdateFromSnapshot(
            "n1",
            new AgentContextSnapshot
            {
                ChapterCount = 13,
                Features = JsonSerializer.Deserialize<JsonElement>(
                    """{"overall-score":0.9,"narrative-entropy":1.8}"""
                ),
            },
            chapterOrder: 6
        );

        Assert.NotNull(entry);
        Assert.Equal(callsBefore, harness.ContextCalls); // 无二次云端调用
        Assert.Equal(6, entry!.FetchedAtChapterOrder);
        Assert.Equal(1.8, entry.NarrativeEntropy);
        Assert.Equal(0.4, entry.NarrativeEntropyDelta); // 熵差 = H(t) − H(t−1)
        Assert.Equal(6, cache.TryGet("n1")!.FetchedAtChapterOrder);
    }

    /// <summary>§3.4 单请求超时（RequestTimeoutSeconds=15）：链接 CTS 超时 → 静默降级返回 null，不抛异常。</summary>
    [Fact]
    public async Task Refresh_Timeout_DegradesSilently_ReturnsNull()
    {
        var cache = new ContextSnapshotCache(new HangingHarness(), requestTimeoutSeconds: 1);

        var result = await cache.RefreshAsync("n1", NewRequest(), 5, CancellationToken.None);

        Assert.Null(result); // 超时降级（不抛异常，§3.6 降级链）
    }

    /// <summary>用户取消（ct）→ OCE 向上抛出（§12 红线 5）。</summary>
    [Fact]
    public async Task Refresh_UserCancellation_Propagates()
    {
        var cache = new ContextSnapshotCache(new HangingHarness());
        using var cts = new CancellationTokenSource();
        var task = cache.RefreshAsync("n1", NewRequest(), 5, cts.Token);
        await Task.Delay(50);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    /// <summary>GetContextAsync 挂起直到外部取消（模拟慢请求）。</summary>
    private sealed class HangingHarness : IFictionHarnessClient
    {
        public Task<HarnessResult> ExecuteAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken ct
        ) => Task.FromResult(new HarnessResult());

        public async Task<AgentContextSnapshot> GetContextAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken ct
        )
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new AgentContextSnapshot();
        }

        public Task ReportSetupFeedbackAsync(
            IReadOnlyList<(string FindingId, string Decision)> items,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    [Fact]
    public void IsStale_NoEntry_ReturnsTrue()
    {
        var cache = new ContextSnapshotCache(new SnapshotHarness("{}"));
        Assert.True(cache.IsStale("unknown", currentMaxOrder: 10, localChapterCount: 10));
    }

    [Fact]
    public async Task IsStale_OrderDriftAtLeastFive_Triggers()
    {
        var harness = new SnapshotHarness("""{"overall-score":0.8}""");
        var cache = new ContextSnapshotCache(harness);
        await cache.RefreshAsync("n1", NewRequest(), chapterOrder: 10, CancellationToken.None);

        Assert.False(cache.IsStale("n1", currentMaxOrder: 14, localChapterCount: 12)); // 差 4 < 5
        Assert.True(cache.IsStale("n1", currentMaxOrder: 15, localChapterCount: 12)); // L2-4：差 ≥ 5
    }

    [Fact]
    public async Task IsStale_ChapterCountMismatch_Triggers()
    {
        var harness = new SnapshotHarness("""{"overall-score":0.8}""");
        var cache = new ContextSnapshotCache(harness);
        await cache.RefreshAsync("n1", NewRequest(), chapterOrder: 10, CancellationToken.None);

        // 快照 ChapterCount=12，本地 15 → 失效
        Assert.True(cache.IsStale("n1", currentMaxOrder: 10, localChapterCount: 15));
        Assert.False(cache.IsStale("n1", currentMaxOrder: 10, localChapterCount: 12));
    }

    [Fact]
    public async Task RefreshFailure_RetainsOldEntry_AndReturnsNull()
    {
        var harness = new SnapshotHarness("""{"overall-score":0.8,"narrative-entropy":1.2}""");
        var cache = new ContextSnapshotCache(harness);
        await cache.RefreshAsync("n1", NewRequest(), 5, CancellationToken.None);
        var before = cache.TryGet("n1");

        harness.Broken = true; // 下一次 GetContextAsync 抛异常
        var result = await cache.RefreshAsync("n1", NewRequest(), 11, CancellationToken.None);

        Assert.Null(result); // 静默降级
        Assert.NotNull(cache.TryGet("n1")); // 旧值保留
        Assert.Equal(before!.FetchedAtChapterOrder, cache.TryGet("n1")!.FetchedAtChapterOrder);
    }

    // ── A-4 熵方向处方（§6.3）──

    [Fact]
    public void A4_EntropyRising_PrescribesPlant()
    {
        var facts = new LocalFacts { OpenPlotlines = [], EntropyDelta = 0.25 };
        var action = SignalRules
            .Evaluate(null, null, null, facts)
            .Actions.Single(a => a.Code == "A-4");
        Assert.Contains("埋新线", action.Prescription);
        Assert.Contains("熵差", action.Evidence);
        Assert.Contains("减法近似", action.Boundary);
    }

    /// <summary>MIN-4 修复（2026-08-28）：刷新幂等键基于内容哈希（弃用 cts.GetHashCode）。</summary>
    [Fact]
    public async Task Refresh_UsesContentStableIdempotencyKey()
    {
        var recorder = new KeyRecordingHarness("""{"overall-score":0.8,"narrative-entropy":1.2}""");
        var cache = new ContextSnapshotCache(recorder);
        var req = NewRequest() with
        {
            Chapters = [new ChapterInput("c1", 3, "第一章", "target", "内容甲")],
        };

        await cache.RefreshAsync("n1", req, 3, CancellationToken.None);
        await cache.RefreshAsync("n1", req, 3, CancellationToken.None);

        // 同章同内容 → 幂等键稳定（内容哈希入键）
        Assert.Equal(2, recorder.Keys.Count);
        Assert.Equal(recorder.Keys[0], recorder.Keys[1]);

        // 重写后内容变化 → 键变化（对齐 CHANGE_06 §11.3 缓存失效哲学）
        var rewritten = req with
        {
            Chapters = [new ChapterInput("c1", 3, "第一章", "target", "内容乙")],
        };
        await cache.RefreshAsync("n1", rewritten, 3, CancellationToken.None);
        Assert.NotEqual(recorder.Keys[0], recorder.Keys[2]);
    }

    /// <summary>
    /// C-2：幂等键纳入章节集指纹——前文（previous）改写、target 未动 → 键变化；
    /// 同请求重试 → 键稳定；键长受服务端 200 字符上限约束（GUID 形状 id 最坏情况）。
    /// </summary>
    [Fact]
    public async Task Refresh_IdempotencyKey_TracksPreviousChapters_AndStaysBounded()
    {
        var recorder = new KeyRecordingHarness("""{"overall-score":0.8,"narrative-entropy":1.2}""");
        var cache = new ContextSnapshotCache(recorder);
        var novelId = Guid.NewGuid().ToString();
        var previousId = Guid.NewGuid().ToString();
        var targetId = Guid.NewGuid().ToString();
        var req = NewRequest() with
        {
            Chapters =
            [
                new ChapterInput(previousId, 2, "第一章", "previous", "前文甲"),
                new ChapterInput(targetId, 3, "第二章", "target", "内容甲"),
            ],
        };

        await cache.RefreshAsync(novelId, req, 3, CancellationToken.None);
        await cache.RefreshAsync(novelId, req, 3, CancellationToken.None); // 同请求 → 键稳定
        await cache.RefreshAsync(
            novelId,
            req with
            {
                Chapters =
                [
                    new ChapterInput(previousId, 2, "第一章", "previous", "前文乙"),
                    new ChapterInput(targetId, 3, "第二章", "target", "内容甲"),
                ],
            },
            3,
            CancellationToken.None
        );

        Assert.Equal(3, recorder.Keys.Count);
        Assert.Equal(recorder.Keys[0], recorder.Keys[1]); // 同输入重试 → 幂等命中
        Assert.NotEqual(recorder.Keys[0], recorder.Keys[2]); // 前文改写（target 未动）→ 新键
        Assert.All(recorder.Keys, key => Assert.True(key.Length <= 200, $"幂等键超长：{key}"));
    }

    /// <summary>R-4：持久化时间解析固定 InvariantCulture + RoundtripKind——非公历默认文化（ar-SA）下往返不丢。</summary>
    [Fact]
    public void Persistence_RoundTrips_UnderNonInvariantCulture()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"snap_culture_{Guid.NewGuid():N}.db");
        var original = CultureInfo.CurrentCulture;
        try
        {
            var harness = new SnapshotHarness("""{"overall-score":0.9,"narrative-entropy":1.7}""");
            var cache = new ContextSnapshotCache(harness, dbPath);
            cache.UpdateFromSnapshot(
                "n1",
                new AgentContextSnapshot
                {
                    ChapterCount = 12,
                    Features = JsonSerializer.Deserialize<JsonElement>(
                        """{"overall-score":0.9,"narrative-entropy":1.7}"""
                    ),
                },
                chapterOrder: 12
            );

            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            var second = new ContextSnapshotCache(harness, dbPath);
            var restored = second.TryGet("n1");

            Assert.NotNull(restored); // 非公历文化下仍可回源
            Assert.Equal(12, restored!.FetchedAtChapterOrder);
            Assert.Equal(1.7, restored.NarrativeEntropy);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                if (File.Exists(dbPath + suffix))
                    File.Delete(dbPath + suffix);
            }
        }
    }

    /// <summary>R-5：旧结构（裸快照 JSON，无版本封装）的行直接丢弃——由下一次刷新重建。</summary>
    [Fact]
    public void Persistence_LegacyUnversionedRow_IsDiscarded()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"snap_legacy_{Guid.NewGuid():N}.db");
        try
        {
            using (var conn = CloudVectorsDb.Open(dbPath))
            {
                using var create = conn.CreateCommand();
                create.CommandText = """
                    CREATE TABLE IF NOT EXISTS context_snapshots (
                        novel_id TEXT PRIMARY KEY,
                        snapshot_json TEXT NOT NULL,
                        fetched_at_chapter_order INTEGER NOT NULL,
                        fetched_at_utc TEXT NOT NULL,
                        narrative_entropy REAL,
                        narrative_entropy_delta REAL
                    )
                    """;
                _ = create.ExecuteNonQuery();
                using var insert = conn.CreateCommand();
                insert.CommandText = """
                    INSERT INTO context_snapshots (novel_id, snapshot_json, fetched_at_chapter_order, fetched_at_utc, narrative_entropy, narrative_entropy_delta)
                    VALUES ('n1', '{"chapterCount":12}', 12, '2026-10-03T07:55:26.9395510+00:00', 1.7, NULL)
                    """;
                _ = insert.ExecuteNonQuery();
            }

            var cache = new ContextSnapshotCache(new SnapshotHarness("{}"), dbPath);

            Assert.Null(cache.TryGet("n1")); // 旧结构 → 丢弃（由下一次刷新重建）
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                if (File.Exists(dbPath + suffix))
                    File.Delete(dbPath + suffix);
            }
        }
    }

    private sealed class KeyRecordingHarness(string featuresJson) : IFictionHarnessClient
    {
        public List<string> Keys { get; } = [];

        public Task<HarnessResult> ExecuteAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<AgentContextSnapshot> GetContextAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken
        )
        {
            Keys.Add(idempotencyKey);
            return Task.FromResult(
                new AgentContextSnapshot
                {
                    ChapterCount = 12,
                    Features = JsonSerializer.Deserialize<JsonElement>(featuresJson),
                }
            );
        }

        public Task ReportSetupFeedbackAsync(
            IReadOnlyList<(string FindingId, string Decision)> items,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }
}

/// <summary>L2-4 经 Hook 触发刷新的集成验收：stale≥5 章 → GetContextAsync 计数增加。</summary>
public sealed class SnapshotL2_4IntegrationTests
{
    [Fact]
    public async Task StaleSnapshot_L2_4_TriggersRefreshCallThroughHook()
    {
        const string NovelId = "novel_l24";
        var store = new MockNovelStore();
        var harness = new CountingContextHarness();
        var agent = new Agent(new ToolRegistry()) { CurrentNovelId = NovelId };
        var novel = new Novel
        {
            Id = NovelId,
            Title = "测试",
            AutoDiagnose = true,
            Genre = "都市",
        };
        await store.SaveNovel(novel);
        // 8~10 三章 + 新写 ch_11（距缓存锚点 9 → 差 2 <5？——用 ch_16 使差 ≥5）
        store.AddChapters(
            NovelId,
            new Chapter
            {
                Id = "ch_8",
                Order = 8,
                Content = "正文八",
            },
            new Chapter
            {
                Id = "ch_9",
                Order = 9,
                Content = "正文九",
            },
            new Chapter
            {
                Id = "ch_10",
                Order = 10,
                Content = "正文十",
            },
            new Chapter
            {
                Id = "ch_16",
                Order = 16,
                Content = "正文十六",
            }
        );

        var options = new CloudDiagnosisOptions();
        var scheduler = new DiagnosisScheduler(harness, options);
        var cache = new ContextSnapshotCache(harness, null);

        // 预置缓存锚点：FetchedAtChapterOrder=9（模拟早前刷新）
        await cache.RefreshAsync(NovelId, NewSnapshotRequest(novel), 9, CancellationToken.None);
        var callsBefore = harness.ContextCalls;

        var hook = new WritingFlowHook(agent, store, scheduler, null, options, cache);
        var ctx = new ToolHookContext
        {
            ToolName = "write_chapter",
            Result = new AgentToolResult<IChapterWriteResult>
            {
                Content = [new TextContent("ok")],
                Details = new ChapterWriteStub("ch_16", 800),
            },
        };
        await hook.AfterToolCallAsync(ctx, CancellationToken.None);

        // 验收：stale≥5（16-9=7≥5 → L2-4 命中）→ 云端被调 → 成功后顺手刷新（context.retrieve 计数增加）
        Assert.True(harness.ContextCalls > callsBefore, "L2-4 应触发快照刷新");
        Assert.True(agent.InjectQueue.HasPending);
    }

    /// <summary>R-3：刷新失败静默沿用旧值时，云端信号边界声明快照数据年龄（§6.2）。</summary>
    [Fact]
    public async Task StaleSnapshot_RefreshFails_AgeMarkerAppearsInCloudSignal()
    {
        const string NovelId = "novel_l24_age";
        var store = new MockNovelStore();
        var harness = new FailingContextHarness();
        var agent = new Agent(new ToolRegistry()) { CurrentNovelId = NovelId };
        var novel = new Novel
        {
            Id = NovelId,
            Title = "测试",
            AutoDiagnose = true,
            Genre = "都市",
        };
        await store.SaveNovel(novel);
        store.AddChapters(
            NovelId,
            new Chapter
            {
                Id = "ch_8",
                Order = 8,
                Content = "正文八",
            },
            new Chapter
            {
                Id = "ch_9",
                Order = 9,
                Content = "正文九",
            },
            new Chapter
            {
                Id = "ch_10",
                Order = 10,
                Content = "正文十",
            },
            new Chapter
            {
                Id = "ch_16",
                Order = 16,
                Content = "正文十六",
            }
        );

        var options = new CloudDiagnosisOptions();
        var scheduler = new DiagnosisScheduler(harness, options);
        var cache = new ContextSnapshotCache(harness, null);
        // 预置缓存锚点：第 5 章（距 ch_16 差 11 章 ≥ 阈值；刷新失败 → 沿用旧值）
        cache.UpdateFromSnapshot(
            NovelId,
            new AgentContextSnapshot { ChapterCount = 10 },
            chapterOrder: 5
        );

        var hook = new WritingFlowHook(agent, store, scheduler, null, options, cache);
        var ctx = new ToolHookContext
        {
            ToolName = "write_chapter",
            Result = new AgentToolResult<IChapterWriteResult>
            {
                Content = [new TextContent("ok")],
                Details = new ChapterWriteStub("ch_16", 800),
            },
        };
        await hook.AfterToolCallAsync(ctx, CancellationToken.None);

        var messages = agent
            .InjectQueue.DequeueAllWithIds()
            .Select(item => ((LlmChatMessage)item.Message).Content ?? "")
            .ToList();
        Assert.Contains(messages, m => m.Contains("快照数据年龄 11 章"));
    }

    private static HarnessRequest NewSnapshotRequest(Novel novel) =>
        new()
        {
            Capability = "fiction.context.retrieve",
            Depth = "quick",
            Options = new AnalysisOptionsInput(IncludeFeatures: true),
            Context = HarnessContextBuilder.Build(novel),
        };

    private sealed class CountingContextHarness : IFictionHarnessClient
    {
        public int ContextCalls;

        public Task<HarnessResult> ExecuteAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken ct
        )
        {
            // scene.plan / story.check 统一返回 PASS 形状（本用例只关注 context.retrieve 计数）
            return Task.FromResult(
                new HarnessResult
                {
                    Capability = request.Capability,
                    Result = JsonSerializer.Deserialize<JsonElement>(
                        """{"capability":"x","status":"pass","features":{"overall-score":0.85}}"""
                    ),
                }
            );
        }

        public Task<AgentContextSnapshot> GetContextAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken ct
        )
        {
            Interlocked.Increment(ref ContextCalls);
            return Task.FromResult(
                new AgentContextSnapshot
                {
                    TargetChapterRef = "ch_16",
                    ChapterCount = 17,
                    Features = JsonSerializer.Deserialize<JsonElement>(
                        """{"overall-score":0.86,"narrative-entropy":1.5}"""
                    ),
                }
            );
        }

        public Task ReportSetupFeedbackAsync(
            IReadOnlyList<(string FindingId, string Decision)> items,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    /// <summary>context.retrieve 恒失败（模拟刷新失败沿用旧值）；scene.plan 正常返回 PASS。</summary>
    private sealed class FailingContextHarness : IFictionHarnessClient
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
                        """{"capability":"x","status":"pass","features":{"overall-score":0.85}}"""
                    ),
                }
            );

        public Task<AgentContextSnapshot> GetContextAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken ct
        ) => throw new CloudApiException("unavailable", "context retrieve down");

        public Task ReportSetupFeedbackAsync(
            IReadOnlyList<(string FindingId, string Decision)> items,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    private sealed record ChapterWriteStub(string Id, int WordCount) : IChapterWriteResult;
}
