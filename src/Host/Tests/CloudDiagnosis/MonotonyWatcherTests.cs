using System.Text.Json;
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
/// HARNESS-CLOUD §10 P1-I4 验收：M-1/M-2 双信号到期、每会话 ≤2 次硬限、
/// NotifyIntervention 重置、灵感文本 [风格观察] 锚点与方向候选。
/// </summary>
public sealed class MonotonyWatcherTests
{
    private static CloudDiagnosisOptions Opts(int threshold = 8, int window = 5) =>
        new() { MonotonyChapterThreshold = threshold, MonotonyFlatWindow = window };

    [Fact]
    public void M1_AutoAdvances_ReachesThreshold_AtChapter8()
    {
        var watcher = new MonotonyWatcher(Opts(threshold: 8));
        for (var i = 0; i < 7; i++)
            watcher.RecordChapter(isFlat: false);
        Assert.False(watcher.IsInspirationDue());

        watcher.RecordChapter(false); // 第 8 章
        Assert.True(watcher.IsInspirationDue());
    }

    [Fact]
    public void M2_FlatWindowFull_TriggersBeforeM1()
    {
        // 窗口 3：连续 3 章平坦 → 第 3 章即命中（早于 M-1 的 8）
        var watcher = new MonotonyWatcher(Opts(threshold: 8, window: 3));
        watcher.RecordChapter(true);
        watcher.RecordChapter(true);
        Assert.False(watcher.IsInspirationDue());
        watcher.RecordChapter(true);
        Assert.True(watcher.IsInspirationDue());
    }

    [Fact]
    public void M2_NonFlatChapter_BreaksWindow()
    {
        var watcher = new MonotonyWatcher(Opts(window: 3));
        watcher.RecordChapter(true);
        watcher.RecordChapter(false); // 打破窗口
        watcher.RecordChapter(true);
        watcher.RecordChapter(true);
        Assert.False(watcher.IsInspirationDue()); // 窗口内含 false → 未满
    }

    [Fact]
    public void Quota_HardLimitTwoPerSession()
    {
        var watcher = new MonotonyWatcher(Opts(threshold: 1)); // 每章都命中

        // 第 1 轮：积累 → 触发 → 重置
        watcher.RecordChapter(true);
        Assert.True(watcher.TryBeginInspiration());

        // 第 2 轮：重新积累一章 → 再次触发（配额第 2 次）
        watcher.RecordChapter(true);
        Assert.True(watcher.TryBeginInspiration());

        // 配额耗尽：即使再次积累也不再触发（§7.2 频率红线）
        watcher.RecordChapter(true);
        // 配额耗尽：IsInspirationDue 含配额检查 → false；TryBegin 也拒绝
        Assert.False(watcher.IsInspirationDue());
        Assert.False(watcher.TryBeginInspiration());
    }

    [Fact]
    public void NotifyIntervention_ResetsAutoAdvances()
    {
        var watcher = new MonotonyWatcher(Opts(threshold: 3));
        for (var i = 0; i < 2; i++)
            watcher.RecordChapter(false);

        watcher.NotifyIntervention(); // 人工介入重置

        watcher.RecordChapter(false);
        Assert.False(watcher.IsInspirationDue()); // 计数从 1 重新开始，未到 3
    }

    /// <summary>P2 M-3 爽感单调递减（§7.1 增强）：窗口内 ≥3 个非空且严格递减 → 灵感到期。</summary>
    [Fact]
    public void M3_SatisfactionMonotonicDeclining_TriggersInspiration()
    {
        var watcher = new MonotonyWatcher(Opts(threshold: 99, window: 5)); // M-1/M-2 不触发
        for (var i = 0; i < 4; i++)
            watcher.RecordChapter(false);

        watcher.RecordSatisfactionScore(80);
        watcher.RecordSatisfactionScore(70);
        watcher.RecordSatisfactionScore(55);
        Assert.True(watcher.IsInspirationDue(), "≥3 个非空且严格递减应触发 M-3");
    }

    /// <summary>M-3 非递减（含回升）不触发；不足 3 个非空不触发。</summary>
    [Fact]
    public void M3_NotMonotonic_OrTooFewScores_DoesNotTrigger()
    {
        var watcher = new MonotonyWatcher(Opts(threshold: 99, window: 5));
        for (var i = 0; i < 4; i++)
            watcher.RecordChapter(false);

        // 回升：80 → 90 → 70（非严格递减）
        watcher.RecordSatisfactionScore(80);
        watcher.RecordSatisfactionScore(90);
        watcher.RecordSatisfactionScore(70);
        Assert.False(watcher.IsInspirationDue());

        // 仅 2 个非空（第三个为 null 占位）
        var watcher2 = new MonotonyWatcher(Opts(threshold: 99, window: 5));
        watcher2.RecordSatisfactionScore(80);
        watcher2.RecordSatisfactionScore(70);
        watcher2.RecordSatisfactionScore(null);
        Assert.False(watcher2.IsInspirationDue());
    }

    [Fact]
    public void BuildInspiration_ContainsAnchor_Evidence_AndCandidates()
    {
        var watcher = new MonotonyWatcher(Opts(threshold: 8, window: 5));

        var text = watcher.BuildInspirationMessage(
            autoAdvances: 8,
            flatCount: 4,
            absentSupporting: ["苏若雪", "老王"],
            dormantPlotline: "师门血案"
        );

        Assert.StartsWith("[风格观察]", text);
        Assert.Contains("M-1 计数=8", text);
        Assert.Contains("苏若雪", text);
        Assert.Contains("师门血案", text);
        Assert.Contains("忽略本条", text);
    }

    /// <summary>MED-1 修复（2026-08-28）：灵感方向①候选 = 最近 K 章未出场的配角（纯出场计数）。</summary>
    [Fact]
    public void FindAbsentSupportingCandidates_ExcludesRecentWindowAppearances()
    {
        var novel = new Novel
        {
            Characters =
            [
                new Character { Name = "主角", Role = CharacterRole.Protagonist },
                new Character { Name = "冷月", Role = CharacterRole.Supporting },
                new Character { Name = "青禾", Role = CharacterRole.Supporting },
            ],
        };
        var chapters = new List<Chapter>
        {
            new()
            {
                Id = "c1",
                Order = 1,
                Content = "冷月提剑而立。",
            },
            new()
            {
                Id = "c2",
                Order = 2,
                Content = "青禾远远望了一眼。",
            },
            new()
            {
                Id = "c3",
                Order = 3,
                Content = "主角独行于长街。",
            },
            new()
            {
                Id = "c4",
                Order = 4,
                Content = "主角继续赶路。",
            },
            new()
            {
                Id = "c5",
                Order = 5,
                Content = "冷月再度现身。",
            },
        };

        var absent = WritingFlowHook.FindAbsentSupportingCandidates(novel, chapters, window: 3);

        // 最近 3 章（c3/c4/c5）中 冷月 出现过 → 不缺席；青禾 近 3 章未出场 → 缺席
        Assert.DoesNotContain("冷月", absent);
        Assert.Contains("青禾", absent);
    }

    /// <summary>MED-1：缺席候选最多取 2 名。</summary>
    [Fact]
    public void FindAbsentSupportingCandidates_CapsAtTwo()
    {
        var novel = new Novel
        {
            Characters =
            [
                new Character { Name = "主角", Role = CharacterRole.Protagonist },
                new Character { Name = "配角甲", Role = CharacterRole.Supporting },
                new Character { Name = "配角乙", Role = CharacterRole.Supporting },
                new Character { Name = "配角丙", Role = CharacterRole.Supporting },
            ],
        };
        var chapters = new List<Chapter>
        {
            new()
            {
                Id = "c1",
                Order = 1,
                Content = "配角甲与配角乙、配角丙都在第一卷登场。",
            },
            new()
            {
                Id = "c2",
                Order = 2,
                Content = "主角独行。",
            },
        };

        var absent = WritingFlowHook.FindAbsentSupportingCandidates(novel, chapters, window: 1);

        Assert.Equal(2, absent.Count);
    }
}

/// <summary>
/// Hook 集成：连续平坦章写入 → 灵感信号注入 InjectQueue；配额 ≤2；
/// 与 ACTION 同轮互斥。stub harness 对 scene.plan 返回高 PASS。
/// </summary>
public sealed class MonotonyHookIntegrationTests
{
    private const string NovelId = "novel_i4";

    private static readonly string PassJson =
        """{"capability":"fiction.scene.plan","status":"pass","features":{"overall-score":0.9}}""";

    /// <summary>写入一章（内容平坦：无钩子词、无信息差词）。</summary>
    private static ToolHookContext WriteFlat(int order) =>
        new()
        {
            ToolName = "write_chapter",
            Result = new AgentToolResult<IChapterWriteResult>
            {
                Content = [new TextContent("ok")],
                Details = new FlatChapterStub($"ch_{order}", order * 100),
            },
        };

    private sealed record FlatChapterStub(string Id, int WordCount) : IChapterWriteResult;

    [Fact]
    public async Task EightFlatChapters_InspireTwice_ThenNever()
    {
        var store = new MockNovelStore();
        var agent = new Agent(new ToolRegistry()) { CurrentNovelId = NovelId };
        var novel = new Novel
        {
            Id = NovelId,
            Title = "I4",
            AutoDiagnose = true,
            // 2 条开放暗线（健康区）→ A-4 不触发；内容含信息差词 → A-5 不触发 → 恒为 PASS 轮
            Plotlines =
            {
                new Plotline
                {
                    Id = "pl_1",
                    Name = "师门血案",
                    PlannedRevealChapterId = "ch_99",
                },
                new Plotline { Id = "pl_2", Name = "身世之谜" },
            },
        };
        await store.SaveNovel(novel);

        var harness = new StubPassHarness();
        var options = new CloudDiagnosisOptions
        {
            MonotonyChapterThreshold = 8,
            MonotonyFlatWindow = 5,
        };
        var scheduler = new DiagnosisScheduler(harness, options);
        var cache = new ContextSnapshotCache(harness);
        var watcher = new MonotonyWatcher(options);
        var hook = new WritingFlowHook(agent, store, scheduler, null, options, cache, watcher);

        string? firstInspireText = null;
        var inspireCount = 0;
        for (var order = 1; order <= 20; order++)
        {
            store.AddChapters(
                NovelId,
                new Chapter
                {
                    Id = $"ch_{order}",
                    Order = order,
                    Content = "林云握紧拳头，藏着秘密，众人以为他只是个普通少年。",
                }
            );
            var ctx = new ToolHookContext
            {
                ToolName = "write_chapter",
                Result = new AgentToolResult<IChapterWriteResult>
                {
                    Content = [new TextContent("ok")],
                    Details = new FlatChapterStub($"ch_{order}", order * 100),
                },
            };
            var before = agent.InjectQueue.HasPending ? 1 : 0;
            await hook.AfterToolCallAsync(ctx, CancellationToken.None);
            var after = agent.InjectQueue.DequeueAllWithIds();

            // 找 [风格观察] 注入
            foreach (var m in after)
                if (((LlmChatMessage)m.Message).Content?.Contains("[风格观察]") == true)
                {
                    inspireCount++;
                    firstInspireText ??= ((LlmChatMessage)m.Message).Content;
                }
            _ = before;
        }

        // 验收：恰好 2 次（频率红线）；首次文本含锚点与真实计数证据段
        // （2026-08-30 修复：证据快照必须在 TryBeginInspiration 重置前取，否则恒为 0）
        Assert.NotNull(firstInspireText);
        Assert.StartsWith("[风格观察]", firstInspireText);
        Assert.Contains("M-1 计数=8", firstInspireText);
        Assert.Contains("平坦窗口=", firstInspireText);
    }

    [Fact]
    public async Task Inspiration_MutuallyExclusive_WithAction()
    {
        // 场景：L2-1 命中伏笔回收章 → ACTION/DECISION 轮，即使 M-1 到期也不出灵感
        var store = new MockNovelStore();
        var agent = new Agent(new ToolRegistry()) { CurrentNovelId = NovelId };
        var novel = new Novel
        {
            Id = NovelId,
            Title = "I4",
            AutoDiagnose = true,
            Plotlines =
            {
                new Plotline
                {
                    Id = "pl_1",
                    Name = "师门血案",
                    PlannedRevealChapterId = "ch_16",
                },
            },
        };
        await store.SaveNovel(novel);
        store.AddChapters(
            NovelId,
            new Chapter
            {
                Id = "ch_15",
                Order = 15,
                Content = "正文十五",
            },
            new Chapter
            {
                Id = "ch_16",
                Order = 16,
                Content = "真相大白的一刻。",
            }
        );

        var harness = new StubActionHarness(); // scene.plan choose_focus + focusAreas → A-8 ACTION
        var options = new CloudDiagnosisOptions { MonotonyChapterThreshold = 1 };
        var scheduler = new DiagnosisScheduler(harness, options);
        var watcher = new MonotonyWatcher(options);
        var hook = new WritingFlowHook(agent, store, scheduler, null, options, null, watcher);
        // 先推满 M-1 阈值（阈值=1，一次即到）
        await hook.AfterToolCallAsync(
            new ToolHookContext
            {
                ToolName = "write_chapter",
                Result = new AgentToolResult<IChapterWriteResult>
                {
                    Content = [new TextContent("ok")],
                    Details = new FlatChapterStub("ch_16", 800),
                },
            },
            CancellationToken.None
        );

        // L2-1/ACTION 轮：不应出现灵感信号
        var drained = agent.InjectQueue.DequeueAllWithIds();
        Assert.DoesNotContain(
            drained,
            m => ((LlmChatMessage)m.Message).Content?.Contains("[风格观察]") == true
        );
    }

    private sealed class StubPassHarness : IFictionHarnessClient
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
                    Result = JsonSerializer.Deserialize<JsonElement>(PassJson),
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
                    ChapterCount = 99,
                    Features = JsonSerializer.Deserialize<JsonElement>(
                        """{"overall-score":0.9,"narrative-entropy":1.6}"""
                    ),
                }
            );

        public Task ReportSetupFeedbackAsync(
            IReadOnlyList<(string FindingId, string Decision)> items,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    /// <summary>scene.plan 走 choose_focus + focusAreas → A-8 ACTION；context.retrieve 带 signals。</summary>
    private sealed class StubActionHarness : IFictionHarnessClient
    {
        public Task<HarnessResult> ExecuteAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken ct
        )
        {
            var json =
                request.Capability == "fiction.scene.plan"
                    ? """{"features":{"overall-score":0.6},"repair-intent":{"action":"choose_focus","focus-areas":["冲突"],"requires-human-decision":false}}"""
                    : """{"features":{"overall-score":0.6},"signals":[]}""";
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
            CancellationToken ct
        ) => Task.FromResult(new AgentContextSnapshot());

        public Task ReportSetupFeedbackAsync(
            IReadOnlyList<(string FindingId, string Decision)> items,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }
}
