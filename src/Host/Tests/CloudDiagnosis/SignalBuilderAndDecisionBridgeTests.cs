using System.Text.Json;
using CloudDiagnosis;
using Core.Cloud;
using Core.Shared;
using Xunit;

namespace Host.Tests.CloudDiagnosis;

/// <summary>
/// HARNESS-CLOUD §10 P1-G0 验收：SignalBuilder 解析（Q1/Q3 定案契约）与
/// HumanDecisionBridge 四条分支（回答回注 / 超时挂起 / 取消透传 / 触发桥事件）。
/// </summary>
public sealed class SignalBuilderTests
{
    private static HarnessResult ScenePlan(string json) =>
        new()
        {
            Status = "pass",
            Capability = "fiction.scene.plan",
            Result = JsonSerializer.Deserialize<JsonElement>(json),
        };

    [Fact]
    public void HumanDecisionRequired_ParsesDecisionKindAndReason()
    {
        var result = ScenePlan(
            """
            {"capability":"fiction.scene.plan","status":"failed",
             "features":{"overall-score":0.42},
             "repair-intent":{"action":"human_decision_required","requires-human-decision":true,
               "reason-code":"quality-below-threshold",
               "reason":"Overall quality score is below the minimum threshold for automated planning."}}
            """
        );

        var outcome = SignalBuilder.ParseScenePlan(result, passThreshold: 70);

        Assert.NotNull(outcome);
        Assert.Equal(CloudSignalKind.Decision, outcome.Kind);
        Assert.True(outcome.RequiresHumanDecision);
        Assert.Equal("human_decision_required", outcome.RepairAction);
        Assert.Equal("quality-below-threshold", outcome.ReasonCode);
        Assert.Contains("threshold", outcome.Reason);
        Assert.Equal(42, outcome.OverallScore); // 0-1 → 百分制归一化
    }

    [Fact]
    public void ChooseFocus_ParsesFocusAreasAsAction()
    {
        var result = ScenePlan(
            """
            {"capability":"fiction.scene.plan","status":"warning",
             "features":{"overall-score":0.65},
             "repair-intent":{"action":"choose_focus","focus-areas":["代入感","节奏"],
               "requires-human-decision":false}}
            """
        );

        var outcome = SignalBuilder.ParseScenePlan(result, passThreshold: 70);

        Assert.NotNull(outcome);
        Assert.Equal(CloudSignalKind.Action, outcome.Kind);
        Assert.False(outcome.RequiresHumanDecision);
        Assert.Equal(2, outcome.FocusAreas.Count);
        Assert.Contains("代入感", outcome.FocusAreas);
    }

    [Fact]
    public void HighScoreWithoutRepairIntent_IsPass()
    {
        var result = ScenePlan(
            """{"capability":"fiction.scene.plan","status":"pass","features":{"overall-score":0.85}}"""
        );

        var outcome = SignalBuilder.ParseScenePlan(result, passThreshold: 70);

        Assert.NotNull(outcome);
        Assert.Equal(CloudSignalKind.Pass, outcome.Kind);
        Assert.False(outcome.RequiresHumanDecision);
        Assert.Null(outcome.RepairAction);
    }

    [Fact]
    public void NonJsonResult_ReturnsNull()
    {
        var result = new HarnessResult { Capability = "fiction.scene.plan", Result = "not-json" };

        Assert.Null(SignalBuilder.ParseScenePlan(result, 70));
    }

    [Fact]
    public void PassMessage_UsesDocumentedPrefix()
    {
        var outcome = new ScenePlanOutcome(CloudSignalKind.Pass, 85, null, false, null, []);

        var text = SignalBuilder.BuildPassMessage(outcome);

        Assert.StartsWith("[云端诊断] status=pass", text);
    }

    /// <summary>层 1 采样门槛：insufficient_data（样本不可测）不得解析为人工裁决。</summary>
    [Fact]
    public void InsufficientData_ParsesAsNonDecisionAction()
    {
        var result = ScenePlan(
            """
            {"capability":"fiction.scene.plan","status":"warning",
             "features":{},
             "repair-intent":{"action":"insufficient_data","requires-human-decision":false,
               "reason-code":"insufficient-evidence",
               "reason":"Target chapter lacks sufficient substantive content for a reliable scene-plan verdict."}}
            """
        );

        var outcome = SignalBuilder.ParseScenePlan(result, passThreshold: 70);

        Assert.NotNull(outcome);
        Assert.False(outcome.RequiresHumanDecision);
        Assert.Equal("insufficient_data", outcome.RepairAction);
        Assert.Equal("insufficient-evidence", outcome.ReasonCode);
        Assert.Equal(0, outcome.OverallScore); // 无评分（不作为质量判定消费）
    }

    /// <summary>request_goal（§5.1 触发源表：request_goal 属规划分支，不触发人工裁决）。</summary>
    [Fact]
    public void RequestGoal_DoesNotTriggerHumanDecision()
    {
        var result = ScenePlan(
            """
            {"capability":"fiction.scene.plan","status":"warning",
             "features":{"overall-score":0.55},
             "repair-intent":{"action":"request_goal","requires-human-decision":false}}
            """
        );

        var outcome = SignalBuilder.ParseScenePlan(result, passThreshold: 70);
        Assert.NotNull(outcome);
        Assert.False(outcome.RequiresHumanDecision);
        Assert.Equal("request_goal", outcome.RepairAction);
        Assert.Equal(CloudSignalKind.Action, outcome.Kind); // 非 DECISION
    }
}

/// <summary>
/// HumanDecisionBridge 分支测试。超时用例将 DecisionTimeoutSeconds 压到 1s；
/// 用户取消用例断言 OCE 透传（§12 红线 5）。
/// </summary>
public sealed class HumanDecisionBridgeTests
{
    private static readonly ScenePlanOutcome Decision = new(
        CloudSignalKind.Decision,
        38,
        "human_decision_required",
        true,
        "Overall quality score is below the minimum threshold for automated planning.",
        ["代入感"]
    );

    [Fact]
    public async Task Decide_CreatesPendingQuestion_AndFiresTriggerBridgeEvent()
    {
        var store = new UserQuestionStore();
        IReadOnlyList<PendingQuestion>? notified = null;
        store.PendingQuestionsCreated += q => notified = q;
        var bridge = new HumanDecisionBridge(store, decisionTimeoutSeconds: 120);

        var task = bridge.DecideAsync(Decision, CancellationToken.None);

        // 等待问题创建（DecideAsync 在 CreateQuestions 后阻塞等待回答）
        for (var i = 0; i < 50 && store.GetPending().Count == 0; i++)
            await Task.Delay(10);

        var pending = store.GetPending();
        Assert.Single(pending);
        Assert.Equal("创作决策", pending[0].Header);
        // Q1 定案：reason 转述为 Question；focusAreas 映射为 Options
        Assert.Contains("低于自动推进阈值", pending[0].Question);
        Assert.Equal(["聚焦 代入感 修复"], pending[0].Options);
        // §5.4 触发桥：宿主经 store 事件收到通知
        Assert.NotNull(notified);
        Assert.NotEmpty(notified);

        store.Answer(pending[0].Id, ["聚焦 代入感 修复"]);
        var verdict = await task;

        // 验收②：回答后注入文本带 [人工裁决] 前缀
        Assert.StartsWith("[人工裁决]", verdict);
        Assert.Contains("聚焦 代入感 修复", verdict);
    }

    [Fact]
    public async Task Timeout_LeavesQuestionPending_AndReturnsConservativeSignal()
    {
        var store = new UserQuestionStore();
        var bridge = new HumanDecisionBridge(store, decisionTimeoutSeconds: 1);

        var verdict = await bridge.DecideAsync(Decision, CancellationToken.None);

        // 验收③：超时后问题留存 store + 挂起轻信号
        Assert.Single(store.GetPending());
        Assert.DoesNotContain("[人工裁决]", verdict);
        Assert.Contains("待决问题", verdict);
        Assert.Contains("保守路径", verdict);
    }

    [Fact]
    public async Task UserCancellation_PropagatesOperationCanceledException()
    {
        var store = new UserQuestionStore();
        var bridge = new HumanDecisionBridge(store, decisionTimeoutSeconds: 120);
        using var cts = new CancellationTokenSource();

        var task = bridge.DecideAsync(Decision, cts.Token);
        // 等问题创建后再取消，确保取消发生在 WaitForAnswerAsync 挂起期间
        for (var i = 0; i < 50 && store.GetPending().Count == 0; i++)
            await Task.Delay(10);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task UnknownReason_UsesFallbackTemplate_AndNoFocusAreas_DefaultOptions()
    {
        var store = new UserQuestionStore();
        var bridge = new HumanDecisionBridge(store, decisionTimeoutSeconds: 120);
        var outcome = Decision with { Reason = "some unknown english reason", FocusAreas = [] };

        var task = bridge.DecideAsync(outcome, CancellationToken.None);
        for (var i = 0; i < 50 && store.GetPending().Count == 0; i++)
            await Task.Delay(10);

        var pending = store.GetPending()[0];
        // 兜底模板：未知 reason 附原文；无 focusAreas → 默认两项选项
        Assert.Contains("unknown english reason", pending.Question);
        Assert.Equal(["按建议继续自动迭代", "我来指定方向"], pending.Options);

        store.Answer(pending.Id, pending.Options);
        await task;
    }

    [Fact]
    public async Task KnownReasonCode_PreferredOverReasonText()
    {
        // U-1：reason-code 命中时优先本地化，英文 reason 文本改写/未收录不影响文案。
        var store = new UserQuestionStore();
        var bridge = new HumanDecisionBridge(store, decisionTimeoutSeconds: 120);
        var outcome = Decision with
        {
            Reason = "some unknown english reason",
            ReasonCode = "quality-below-threshold",
            FocusAreas = [],
        };

        var task = bridge.DecideAsync(outcome, CancellationToken.None);
        for (var i = 0; i < 50 && store.GetPending().Count == 0; i++)
            await Task.Delay(10);

        var pending = store.GetPending()[0];
        Assert.Contains("低于自动推进阈值", pending.Question);
        Assert.DoesNotContain("unknown english reason", pending.Question);

        store.Answer(pending.Id, pending.Options);
        await task;
    }

    [Fact]
    public void ShouldSkipDecision_ThreeCharsAndOrderFive_DoesNotSkip()
    {
        var outcome = new ScenePlanOutcome(
            CloudSignalKind.Decision,
            38,
            "human_decision_required",
            true,
            "reason",
            []
        );

        var skip = HumanDecisionBridge.ShouldSkipDecision(outcome, 3, 5);
        Assert.Null(skip);
    }

    [Fact]
    public void ShouldSkipDecision_CustomThresholds_AreHonored()
    {
        var outcome = new ScenePlanOutcome(
            CloudSignalKind.Decision,
            38,
            "human_decision_required",
            true,
            "reason",
            []
        );

        // 默认阈值（3 角色 / 第 5 章）：5 角色 + 第 6 章 → 不跳过
        Assert.Null(HumanDecisionBridge.ShouldSkipDecision(outcome, 5, 6));
        // 提高角色阈值 → 命中角色完整性条件
        var charSkip = HumanDecisionBridge.ShouldSkipDecision(
            outcome,
            5,
            6,
            null,
            minCharacters: 6
        );
        Assert.NotNull(charSkip);
        Assert.Contains("5 个结构化角色", charSkip);
        // 提高章序阈值 → 命中小说早期条件（文案带实际生效阈值）
        var orderSkip = HumanDecisionBridge.ShouldSkipDecision(
            outcome,
            5,
            6,
            null,
            minChapterOrder: 10
        );
        Assert.NotNull(orderSkip);
        Assert.Contains("前 10 章", orderSkip);
    }

    [Fact]
    public void ShouldSkipDecision_TwoChars_Skips()
    {
        var outcome = new ScenePlanOutcome(
            CloudSignalKind.Decision,
            38,
            "human_decision_required",
            true,
            "reason",
            []
        );

        var skip = HumanDecisionBridge.ShouldSkipDecision(outcome, 2, 10);
        Assert.NotNull(skip);
        Assert.Contains("2", skip);
    }

    [Fact]
    public void ShouldSkipDecision_OrderFour_Skips()
    {
        var outcome = new ScenePlanOutcome(
            CloudSignalKind.Decision,
            38,
            "human_decision_required",
            true,
            "reason",
            []
        );

        var skip = HumanDecisionBridge.ShouldSkipDecision(outcome, 5, 4);
        Assert.NotNull(skip);
        Assert.Contains("前 5 章", skip);
    }

    [Fact]
    public async Task DecideAsync_DecisionOutcome_CreatesQuestion()
    {
        var questions = new UserQuestionStore();
        var bridge = new HumanDecisionBridge(questions, decisionTimeoutSeconds: 120);

        var outcome = new ScenePlanOutcome(
            CloudSignalKind.Decision,
            38,
            "human_decision_required",
            true,
            "Overall quality score is below the minimum threshold for automated planning.",
            []
        );

        var task = bridge.DecideAsync(outcome, CancellationToken.None);

        // 等待问题创建
        for (var i = 0; i < 100 && questions.GetPending().Count == 0; i++)
            await Task.Delay(10);

        var pending = questions.GetPending();
        Assert.Single(pending);
        Assert.Equal("创作决策", pending[0].Header);

        questions.Answer(pending[0].Id, ["按建议继续自动迭代"]);
        var result = await task;
        Assert.StartsWith("[人工裁决]", result);
    }

    // ── HARNESS-CLOUD §16.2 DecisionHistory 持久化 + 决策模式统计 ──

    [Fact]
    public async Task DecideAsync_StructuredAnswerKind_TracksOptionIdentity()
    {
        // 无 focusAreas → 默认模板（首项 = 采纳建议）→ FollowSuggestion
        var store = new UserQuestionStore();
        var bridge = new HumanDecisionBridge(store, decisionTimeoutSeconds: 120);
        var task = bridge.DecideAsync(Decision with { FocusAreas = [] }, CancellationToken.None);
        for (var i = 0; i < 50 && store.GetPending().Count == 0; i++)
            await Task.Delay(10);
        store.Answer(store.GetPending()[0].Id, ["按建议继续自动迭代"]);
        await task;

        Assert.Equal(DecisionAnswerKind.FollowSuggestion, bridge.DecisionHistory[0].AnswerKind);
        Assert.Equal(DecisionProfileKind.Trusting, bridge.DecisionProfile);

        // 有 focusAreas → 聚焦选项 = 自定方向 → OwnDirection
        var store2 = new UserQuestionStore();
        var bridge2 = new HumanDecisionBridge(store2, decisionTimeoutSeconds: 120);
        var task2 = bridge2.DecideAsync(Decision, CancellationToken.None);
        for (var i = 0; i < 50 && store2.GetPending().Count == 0; i++)
            await Task.Delay(10);
        store2.Answer(store2.GetPending()[0].Id, ["聚焦 代入感 修复"]);
        await task2;

        Assert.Equal(DecisionAnswerKind.OwnDirection, bridge2.DecisionHistory[0].AnswerKind);
        Assert.Equal(DecisionProfileKind.Controlling, bridge2.DecisionProfile);
    }

    [Fact]
    public void RecordDecision_PersistsAnswerCode_AndMigratesLegacyTable()
    {
        var db = TempDb();
        try
        {
            // 旧库旧表（无 answer_code 列）+ 旧行：打开即补列，旧行按 Answer 文本回退归类
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}"))
            {
                conn.Open();
                using var create = conn.CreateCommand();
                create.CommandText = """
                    CREATE TABLE decision_history (
                        seq INTEGER PRIMARY KEY AUTOINCREMENT,
                        novel_id TEXT NOT NULL,
                        at_utc TEXT NOT NULL,
                        question TEXT NOT NULL,
                        answer TEXT NOT NULL,
                        scene_plan_score REAL NOT NULL,
                        was_skipped INTEGER NOT NULL DEFAULT 0
                    )
                    """;
                _ = create.ExecuteNonQuery();
                using var insert = conn.CreateCommand();
                insert.CommandText = """
                    INSERT INTO decision_history (novel_id, at_utc, question, answer, scene_plan_score, was_skipped)
                    VALUES ('n', '2026-01-01T00:00:00.0000000+00:00', 'q', '按建议继续自动迭代', 0.6, 0)
                    """;
                _ = insert.ExecuteNonQuery();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            var bridge = new HumanDecisionBridge(new UserQuestionStore(), persistenceDbPath: db);
            Assert.Single(bridge.DecisionHistory);
            Assert.Equal(DecisionAnswerKind.Unknown, bridge.DecisionHistory[0].AnswerKind);
            Assert.Equal(
                DecisionAnswerKind.FollowSuggestion,
                bridge.DecisionHistory[0].EffectiveAnswerKind
            );

            // 新记录写入显式 code → 新实例回源保持一致
            bridge.RecordDecision(
                "n",
                "q2",
                "我来指定方向",
                0.4,
                answerKind: DecisionAnswerKind.OwnDirection
            );
            var reopened = new HumanDecisionBridge(new UserQuestionStore(), persistenceDbPath: db);
            Assert.Equal(2, reopened.DecisionHistory.Count);
            Assert.Equal(DecisionAnswerKind.OwnDirection, reopened.DecisionHistory[1].AnswerKind);
            Assert.Equal(
                DecisionAnswerKind.FollowSuggestion,
                reopened.DecisionHistory[0].EffectiveAnswerKind
            );
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(db);
        }
    }

    private static string TempDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"decision_history_{Guid.NewGuid():N}.db");
        return path;
    }

    [Fact]
    public void RecordDecision_AddsToHistory_AndPersistsAcrossInstances()
    {
        var db = TempDb();
        try
        {
            var store = new UserQuestionStore();
            var bridge1 = new HumanDecisionBridge(store, persistenceDbPath: db);
            bridge1.RecordDecision("novel_a", "问题1", "按建议继续自动迭代", 0.55);
            bridge1.RecordDecision("novel_a", "问题2", "我来指定方向", 0.42);

            Assert.Equal(2, bridge1.DecisionHistory.Count);
            Assert.Equal("novel_a", bridge1.DecisionHistory[0].NovelId);

            // 新实例（模拟新会话）从 SQLite 回源
            var bridge2 = new HumanDecisionBridge(new UserQuestionStore(), persistenceDbPath: db);
            Assert.Equal(2, bridge2.DecisionHistory.Count);
            Assert.Equal("按建议继续自动迭代", bridge2.DecisionHistory[0].Answer);
            Assert.Equal(0.55, bridge2.DecisionHistory[0].ScenePlanScore);
        }
        finally
        {
            // Microsoft.Data.Sqlite 连接池持有物理句柄：先清池再删文件，否则 Windows 上 IOException
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(db);
        }
    }

    [Fact]
    public void DecisionProfile_Trusting_WhenMostlyFollowSuggestions()
    {
        var history = new List<DecisionHistoryEntry>
        {
            new("n", DateTimeOffset.UtcNow, "q", "按建议继续自动迭代", 60, false),
            new("n", DateTimeOffset.UtcNow, "q", "按建议继续自动迭代", 70, false),
            new("n", DateTimeOffset.UtcNow, "q", "我来指定方向", 0.5, false),
        };

        Assert.Equal(DecisionProfileKind.Trusting, HumanDecisionBridge.ComputeProfile(history));
    }

    [Fact]
    public void DecisionProfile_Controlling_WhenMostlyCustomAnswers()
    {
        var history = new List<DecisionHistoryEntry>
        {
            new("n", DateTimeOffset.UtcNow, "q", "聚焦 节奏 修复", 0.6, false),
            new("n", DateTimeOffset.UtcNow, "q", "我来指定方向", 0.7, false),
            new("n", DateTimeOffset.UtcNow, "q", "按建议继续自动迭代", 0.5, false),
        };

        Assert.Equal(DecisionProfileKind.Controlling, HumanDecisionBridge.ComputeProfile(history));
    }

    [Fact]
    public void DecisionProfile_Unknown_WhenEmptyOrTie()
    {
        Assert.Equal(DecisionProfileKind.Unknown, HumanDecisionBridge.ComputeProfile([]));

        var tie = new List<DecisionHistoryEntry>
        {
            new("n", DateTimeOffset.UtcNow, "q", "按建议继续自动迭代", 60, false),
            new("n", DateTimeOffset.UtcNow, "q", "聚焦 节奏 修复", 0.5, false),
        };
        Assert.Equal(DecisionProfileKind.Unknown, HumanDecisionBridge.ComputeProfile(tie));
    }

    [Fact]
    public async Task DecideAsync_RecordsDecisionWithNovelId()
    {
        var store = new UserQuestionStore();
        var bridge = new HumanDecisionBridge(store, decisionTimeoutSeconds: 120);

        var task = bridge.DecideAsync(Decision, CancellationToken.None, novelId: "novel_x");
        for (var i = 0; i < 50 && store.GetPending().Count == 0; i++)
            await Task.Delay(10);
        store.Answer(store.GetPending()[0].Id, ["聚焦 代入感 修复"]);
        await task;

        var history = bridge.DecisionHistory;
        Assert.Single(history);
        Assert.Equal("novel_x", history[0].NovelId);
        Assert.Equal(38, history[0].ScenePlanScore);
    }
}

/// <summary>
/// HumanDecisionBridge 边界补充：skipAnswerInjection 双通道去重 / ShouldSkipDecision
/// 历史稳定性三条件 / 信任型 0.35 下沿 / DecideAsync 超时留题记录 wasSkipped 不落库（超时不 RecordDecision）。
/// </summary>
public sealed class HumanDecisionBridgeEdgeTests
{
    private static readonly ScenePlanOutcome Decision = new(
        CloudSignalKind.Decision,
        38,
        "human_decision_required",
        true,
        "Overall quality score is below the minimum threshold for automated planning.",
        ["代入感"]
    );

    [Fact]
    public async Task SkipAnswerInjection_ReturnsEmptyString_NoInjectQueueContent()
    {
        // Desktop 宿主复用 ask_user 渲染链路自动追加答案 → bridge 不再重复注入（P2 双通道去重）
        var store = new UserQuestionStore();
        var bridge = new HumanDecisionBridge(
            store,
            decisionTimeoutSeconds: 120,
            skipAnswerInjection: true
        );

        var task = bridge.DecideAsync(Decision, CancellationToken.None);
        for (var i = 0; i < 50 && store.GetPending().Count == 0; i++)
            await Task.Delay(10);
        store.Answer(store.GetPending()[0].Id, ["聚焦 代入感 修复"]);

        var verdict = await task;

        Assert.Equal("", verdict);
        // 但 DECISION 历史仍记录（宿主侧渲染不替代决策历史）
        Assert.Single(bridge.DecisionHistory);
    }

    [Fact]
    public void ShouldSkipDecision_ThreeStableScores_InCriticalZone_Skips()
    {
        // 历史：连续 3 次 score ≥ 0.6 且未跳过；当前 0.45（临界区 [0.4, 0.55)）→ 跳过
        var history = new List<DecisionHistoryEntry>
        {
            new("n", DateTimeOffset.UtcNow, "q", "按建议继续自动迭代", 70, false),
            new("n", DateTimeOffset.UtcNow, "q", "按建议继续自动迭代", 65, false),
            new("n", DateTimeOffset.UtcNow, "q", "按建议继续自动迭代", 60, false),
        };
        var outcome = new ScenePlanOutcome(
            CloudSignalKind.Decision,
            45,
            "human_decision_required",
            true,
            "r",
            []
        );

        var skip = HumanDecisionBridge.ShouldSkipDecision(outcome, 5, 10, history);

        Assert.NotNull(skip);
        Assert.Contains("临界区", skip);
    }

    [Fact]
    public void ShouldSkipDecision_StableHistory_ScoreBelowCriticalZone_NotSkipped()
    {
        // 控制型历史（多数自定义方向）→ 临界区下沿 0.4；当前 0.35 < 0.4 → 不跳过
        var history = new List<DecisionHistoryEntry>
        {
            new("n", DateTimeOffset.UtcNow, "q", "聚焦 节奏 修复", 70, false),
            new("n", DateTimeOffset.UtcNow, "q", "我来指定方向", 65, false),
            new("n", DateTimeOffset.UtcNow, "q", "按建议继续自动迭代", 60, false),
        };
        var outcome = new ScenePlanOutcome(
            CloudSignalKind.Decision,
            35,
            "human_decision_required",
            true,
            "r",
            []
        );

        Assert.Null(HumanDecisionBridge.ShouldSkipDecision(outcome, 5, 10, history));
    }

    [Fact]
    public void ShouldSkipDecision_TrustingProfile_LowersThresholdTo035()
    {
        // 信任型历史（多数按建议继续）+ 当前 0.38（[0.35, 0.55)）→ 跳过（0.35 下沿）
        var history = new List<DecisionHistoryEntry>
        {
            new("n", DateTimeOffset.UtcNow, "q", "按建议继续自动迭代", 70, false),
            new("n", DateTimeOffset.UtcNow, "q", "按建议继续自动迭代", 65, false),
            new("n", DateTimeOffset.UtcNow, "q", "按建议继续自动迭代", 60, false),
        };
        var outcome = new ScenePlanOutcome(
            CloudSignalKind.Decision,
            38,
            "human_decision_required",
            true,
            "r",
            []
        );

        var skip = HumanDecisionBridge.ShouldSkipDecision(outcome, 5, 10, history);

        Assert.NotNull(skip);
        Assert.Contains("临界区", skip ?? "");
    }

    [Fact]
    public void ShouldSkipDecision_HistoryTooShort_DoesNotApplyStabilityFilter()
    {
        // 仅 2 条历史 → 稳定性条件不生效；角色/早期条件未命中 → 不跳过
        var history = new List<DecisionHistoryEntry>
        {
            new("n", DateTimeOffset.UtcNow, "q", "按建议继续自动迭代", 70, false),
            new("n", DateTimeOffset.UtcNow, "q", "按建议继续自动迭代", 65, false),
        };
        var outcome = new ScenePlanOutcome(
            CloudSignalKind.Decision,
            45,
            "human_decision_required",
            true,
            "r",
            []
        );

        Assert.Null(HumanDecisionBridge.ShouldSkipDecision(outcome, 5, 10, history));
    }

    [Fact]
    public void ShouldSkipDecision_RecentSkippedBreaksStabilityChain()
    {
        // 最近一次 WasSkipped=true → 稳定性链断裂（信任条件不成立）→ 不跳过
        var history = new List<DecisionHistoryEntry>
        {
            new("n", DateTimeOffset.UtcNow, "q", "按建议继续自动迭代", 70, false),
            new("n", DateTimeOffset.UtcNow, "q", "按建议继续自动迭代", 65, false),
            new("n", DateTimeOffset.UtcNow, "q", "按建议继续自动迭代", 60, true),
        };
        var outcome = new ScenePlanOutcome(
            CloudSignalKind.Decision,
            45,
            "human_decision_required",
            true,
            "r",
            []
        );

        Assert.Null(HumanDecisionBridge.ShouldSkipDecision(outcome, 5, 10, history));
    }
}
