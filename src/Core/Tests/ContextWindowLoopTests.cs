using Core.Agent;
using Core.Stability;
using Core.Tools;
using LlmTornado.Chat;
using LlmTornado.Code;
using Xunit;
using AgentType = Core.Agent.Agent;

namespace Core.Tests;

/// <summary>
/// 上下文接续的请求侧行为：页片段文案、预算提醒（每页一次 + 阈值 clamp）、
/// 换页请求的消费与 run 边界丢弃。
/// </summary>
public sealed class ContextWindowLoopTests
{
    private static ConversationLoop CreateLoop(
        Func<string, CancellationToken, Task<bool>>? onNewContextRequested = null,
        AgentType? agent = null,
        IStabilityMonitor? monitor = null
    ) =>
        new(
            api: new LlmTornado.TornadoApi([
                new ProviderAuthentication(LLmProviders.OpenAi, "test-key"),
            ]),
            modelName: "gpt-test",
            systemPrompt: "system",
            thinkingLevel: "medium",
            toolRegistry: new ToolRegistry(),
            toolExecution: ToolExecutionMode.Parallel,
            agent: agent,
            stabilityMonitor: monitor,
            onNewContextRequested: onNewContextRequested
        );

    // ═══════════════════════════════════════════════════════════
    //  页片段文案
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// prev 为 null 的片段（分支回溯复位到窗口之前的路径）：必须仍给出 history_* 入口，
    /// 因为该会话确实存在更早内容（真首页根本不注入片段，由 ShouldInjectWindowFragment 保证）。
    /// </summary>
    [Fact]
    public void Fragment_NoPrevWindow_StillPointsToHistory()
    {
        var text = ConversationLoop.BuildContextWindowFragment(ContextWindowState.First, 12_345);

        Assert.Contains("首页 ID: w0", text);
        Assert.Contains("当前页 ID: w0", text);
        Assert.Contains("本页剩余 token: 12345", text);
        Assert.Contains("history_search_contents", text);
        Assert.Contains("memory_store", text); // 长期结论落到已有归属（记忆 / 任务清单）
    }

    /// <summary>
    /// 任务锚点：换页不携带历史，但必须让模型知道自己在做什么（否则跨页「继续」无从下手）。
    /// </summary>
    [Fact]
    public void Fragment_WithTaskAnchor_CarriesUnfinishedTask()
    {
        var withAnchor = new ContextWindowState("w2", "w1", "w0", 20, "把第三到第五章改完");
        var text = ConversationLoop.BuildContextWindowFragment(withAnchor, 1_000);

        Assert.Contains("上一页最后一条用户消息", text);
        Assert.Contains("把第三到第五章改完", text);
        Assert.Contains("history_*", text);
        // 三分支必须写明：未完成→直接继续（不等用户）／已完成→待命／用户已给新任务→新任务优先
        Assert.Contains("未完成则直接继续，不必等用户发话", text);
        Assert.Contains("已完成则待命", text);
        Assert.Contains("用户已给出新任务则以新任务为准", text);

        // 无锚点（首页 / 会话恢复 / 读取失败）不出现该行
        var withoutAnchor = ConversationLoop.BuildContextWindowFragment(
            new ContextWindowState("w2", "w1", "w0", 20),
            1_000
        );
        Assert.DoesNotContain("上一页最后一条用户消息", withoutAnchor);
    }

    [Fact]
    public void Fragment_LaterPage_ListsPrevAndHistoryEntryPoints()
    {
        var window = new ContextWindowState("w3", "w2", "w0", 42);
        var text = ConversationLoop.BuildContextWindowFragment(window, -1);

        Assert.Contains("当前页 ID: w3", text);
        Assert.Contains("上一页 ID: w2", text);
        Assert.Contains("history_search_contents", text);
        Assert.Contains("memory_store", text); // 长期结论落到已有归属（记忆 / 任务清单）
        Assert.DoesNotContain("token", text); // remaining 未知时不写剩余行
    }

    // ═══════════════════════════════════════════════════════════
    //  压力提示：模式互斥（compact 指 /compact，接续指 new_context）
    // ═══════════════════════════════════════════════════════════

    /// <summary>三档压力提示都必须指向本模式自己的出口，且绝不出现另一条路径的出口。</summary>
    [Theory]
    [InlineData(ConversationLoop.PressureLevel.Info)]
    [InlineData(ConversationLoop.PressureLevel.Warn)]
    [InlineData(ConversationLoop.PressureLevel.Critical)]
    public void PressureNudge_IsModeExclusive(ConversationLoop.PressureLevel level)
    {
        var compactText = ConversationLoop.BuildPressureNudge(level, 0.9, continuation: false);
        var continuationText = ConversationLoop.BuildPressureNudge(level, 0.9, continuation: true);

        // compact：只指压缩出口（info 档沿用「压缩」措辞，warn/critical 点名 /compact），不提接续工具
        Assert.True(
            compactText.Contains("/compact", StringComparison.Ordinal)
                || compactText.Contains("压缩", StringComparison.Ordinal),
            compactText
        );
        Assert.DoesNotContain("new_context", compactText);
        Assert.DoesNotContain("history_", compactText);

        // 接续：只指 new_context / history_*，绝不提 /compact
        Assert.Contains("new_context", continuationText);
        Assert.DoesNotContain("/compact", continuationText);
    }

    // ═══════════════════════════════════════════════════════════
    //  页片段注入决策：模式互斥 + 首屏不注入
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void WindowFragment_InjectedOnlyInContinuationAfterRoll()
    {
        var loop = CreateLoop();

        // 未装配选项（compact 模式）：即使页状态显示已换页也不注入
        loop.SetContextWindowState(new ContextWindowState("w1", "w0", "w0", 7));
        Assert.False(loop.ShouldInjectWindowFragment());

        // 接续模式 + 换过页：注入
        loop.UpdateContextManagement(
            new ContextManagementOptions(ContextMode.Continuation, 0, false)
        );
        Assert.True(loop.ShouldInjectWindowFragment());

        // 切回 compact：立即停止注入
        loop.UpdateContextManagement(null);
        Assert.False(loop.ShouldInjectWindowFragment());

        // 接续模式但从未换页（首页）：不注入（未换页会话首屏零变化）
        var fresh = CreateLoop();
        fresh.UpdateContextManagement(
            new ContextManagementOptions(ContextMode.Continuation, 0, false)
        );
        fresh.SetContextWindowState(ContextWindowState.First);
        Assert.False(fresh.ShouldInjectWindowFragment());
    }

    // ═══════════════════════════════════════════════════════════
    //  预算提醒：阈值 clamp + 每页一次 + 换页重置
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ShouldClaimBudgetReminder_UsesUsageRatio_NotAbsoluteTokens()
    {
        // usage 比例口径：同一阈值对不同窗口的模型都成立
        Assert.True(ConversationLoop.ShouldClaimBudgetReminder(900, 1_000, 0.85));
        Assert.False(ConversationLoop.ShouldClaimBudgetReminder(800, 1_000, 0.85));
        Assert.True(ConversationLoop.ShouldClaimBudgetReminder(850, 1_000, 0.85));

        // 8K 窗口与 1M 窗口用同一个比例：都不存在「永远不可达 / 一开页就触发」
        Assert.False(ConversationLoop.ShouldClaimBudgetReminder(6_000, 8_000, 0.85));
        Assert.True(ConversationLoop.ShouldClaimBudgetReminder(7_000, 8_000, 0.85));
        Assert.False(ConversationLoop.ShouldClaimBudgetReminder(100_000, 1_000_000, 0.85));
        Assert.True(ConversationLoop.ShouldClaimBudgetReminder(900_000, 1_000_000, 0.85));

        // 无 usage / 无预算 = 不提醒
        Assert.False(ConversationLoop.ShouldClaimBudgetReminder(0, 1_000, 0.5));
        Assert.False(ConversationLoop.ShouldClaimBudgetReminder(100, 0, 0.5));
    }

    [Fact]
    public async Task Reminder_ClaimedOncePerPage_AndResetByWindowChange()
    {
        var loop = CreateLoop();
        loop.UpdateContextManagement(
            new ContextManagementOptions(
                ContextMode.Continuation,
                ReminderPercent: 0,
                VectorRetrieval: false
            )
        );

        // 0 = 用 ModelContextProfile.RollAdvicePercent（按窗口推导）
        var first = loop.TryClaimBudgetReminder(usedTokens: 950_000, budget: 1_000_000);
        Assert.NotNull(first);
        Assert.Contains("new_context", first!);
        Assert.Contains("剩余 50000", first!);

        // 同一页第二次不再提醒
        Assert.Null(loop.TryClaimBudgetReminder(usedTokens: 990_000, budget: 1_000_000));

        // 换页 / 恢复会话后重新可提醒
        loop.SetContextWindowState(new ContextWindowState("w2", "w1", "w0", 30));
        Assert.NotNull(loop.TryClaimBudgetReminder(usedTokens: 960_000, budget: 1_000_000));

        await loop.DisposeAsync();
    }

    /// <summary>模型自写的交接说明优先于裸锚点：新页据此知道「做到哪/下一步」，不必再翻一遍历史。</summary>
    [Fact]
    public void Fragment_RendersHandoffNote_BeforeAnchor()
    {
        var text = ConversationLoop.BuildContextWindowFragment(
            new ContextWindowState(
                "w2",
                "w1",
                "w0",
                20,
                "把第三到第五章改完",
                "model",
                "三~五章已改完；第六章核对时间线未完成，下一步先读 notes 的设定约定"
            ),
            1_000
        );

        Assert.Contains("上一页交接说明：三~五章已改完", text);
        Assert.Contains("上一页最后一条用户消息：把第三到第五章改完", text);
        Assert.Contains("切换原因：模型主动换页", text);
        // 交接说明在锚点之前（更精确的信息先出现）
        Assert.True(
            text.IndexOf("上一页交接说明", StringComparison.Ordinal)
                < text.IndexOf("上一页最后一条用户消息", StringComparison.Ordinal)
        );

        // 只有交接说明（无锚点）时也要渲染，且带三分支指令
        var noteOnly = ConversationLoop.BuildContextWindowFragment(
            new ContextWindowState("w2", "w1", "w0", 20, null, "model", "第六章核对时间线"),
            1_000
        );
        Assert.Contains("上一页交接说明：第六章核对时间线", noteOnly);
        Assert.Contains("未完成则直接继续，不必等用户发话", noteOnly);
        Assert.DoesNotContain("上一页最后一条用户消息", noteOnly);
    }

    /// <summary>片段要说明本页是「被迫切断」还是「计划内切换」——被迫切断时任务多半没做完。</summary>
    [Fact]
    public void Fragment_StatesRollReason()
    {
        var forced = ConversationLoop.BuildContextWindowFragment(
            new ContextWindowState("w2", "w1", "w0", 20, "把第五章改完", "threshold"),
            1_000
        );
        Assert.Contains("切换原因：接近上限（系统换页）", forced);

        var overflow = ConversationLoop.BuildContextWindowFragment(
            new ContextWindowState("w2", "w1", "w0", 20, "把第五章改完", "overflow"),
            1_000
        );
        Assert.Contains("切换原因：溢出恢复（系统换页）", overflow);

        var model = ConversationLoop.BuildContextWindowFragment(
            new ContextWindowState("w2", "w1", "w0", 20, "把第五章改完", "model"),
            1_000
        );
        Assert.Contains("切换原因：模型主动换页", model);
    }

    // ═══════════════════════════════════════════════════════════
    //  自动接续策略：被迫换页才接续，且次数有上限
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void AutoContinue_OnlyAfterForcedRoll_AndBounded()
    {
        // 被迫换页（接近上限 / 溢出恢复）：接续，且最多 MaxAutoContinuesPerUserMessage 次
        Assert.True(ContextRollPolicy.ShouldAutoContinue("threshold", 0));
        Assert.True(ContextRollPolicy.ShouldAutoContinue("overflow", 0));
        Assert.True(
            ContextRollPolicy.ShouldAutoContinue(
                "threshold",
                ContextRollPolicy.MaxAutoContinuesPerUserMessage - 1
            )
        );
        Assert.False(
            ContextRollPolicy.ShouldAutoContinue(
                "threshold",
                ContextRollPolicy.MaxAutoContinuesPerUserMessage
            )
        );

        // 模型主动换页 / 没换页：不代它继续（它自己决定）
        Assert.False(ContextRollPolicy.ShouldAutoContinue("model", 0));
        Assert.False(ContextRollPolicy.ShouldAutoContinue(null, 0));

        Assert.True(ContextRollPolicy.IsForcedRoll("threshold"));
        Assert.True(ContextRollPolicy.IsForcedRoll("overflow"));
        Assert.False(ContextRollPolicy.IsForcedRoll("model"));
        Assert.False(ContextRollPolicy.IsForcedRoll(null));
    }

    /// <summary>提醒必须点名「当前请求」（动态感知任务，模型据此快速收尾），且超长截断。</summary>
    [Fact]
    public async Task Reminder_NamesCurrentRequest_AndTruncatesLongOnes()
    {
        var loop = CreateLoop();
        // 无请求时不加该行
        Assert.DoesNotContain("当前请求", ConversationLoop.BuildBudgetReminder(5_000));

        Assert.Contains(
            "当前请求：把第三到第五章改完",
            ConversationLoop.BuildBudgetReminder(5_000, "把第三到第五章改完")
        );

        var longRequest = new string('长', 500);
        var text = ConversationLoop.BuildBudgetReminder(5_000, longRequest);
        Assert.Contains("…", text);
        Assert.DoesNotContain(longRequest, text); // 已截断，不原样塞进每轮提醒

        await loop.DisposeAsync();
    }

    /// <summary>RunAsync 收到的用户消息会成为提醒里的「当前请求」；换页（页状态变化）后清空。</summary>
    [Fact]
    public async Task Reminder_CurrentRequest_Lifecycle()
    {
        var loop = CreateLoop();
        loop.UpdateContextManagement(
            new ContextManagementOptions(ContextMode.Continuation, 0, false)
        );

        // 注入类系统通知不经 RunAsync 的 userMessages → 不影响「用户亲口说的请求」
        var field = typeof(ConversationLoop).GetField(
            "_lastUserRequest",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
        )!;
        field.SetValue(loop, "把第三到第五章改完");
        Assert.Contains(
            "当前请求：把第三到第五章改完",
            loop.TryClaimBudgetReminder(950_000, 1_000_000)
        );

        // 换页后：提醒不再引用上一页请求（任务陈述由片段锚点承担）
        loop.SetContextWindowState(new ContextWindowState("w2", "w1", "w0", 30));
        Assert.Null(field.GetValue(loop));
        Assert.DoesNotContain("当前请求", loop.TryClaimBudgetReminder(960_000, 1_000_000));

        await loop.DisposeAsync();
    }

    [Fact]
    public async Task Reminder_RespectsConfiguredPercentOverride()
    {
        var loop = CreateLoop();
        // 覆盖为 60%：60% usage 就该提醒（而不是等 profile 的 ~79%）
        loop.UpdateContextManagement(
            new ContextManagementOptions(
                ContextMode.Continuation,
                ReminderPercent: 60,
                VectorRetrieval: false
            )
        );

        Assert.Null(loop.TryClaimBudgetReminder(usedTokens: 550_000, budget: 1_000_000));
        Assert.NotNull(loop.TryClaimBudgetReminder(usedTokens: 610_000, budget: 1_000_000));

        await loop.DisposeAsync();
    }

    [Fact]
    public async Task Reminder_DisabledInCompactMode()
    {
        var loop = CreateLoop(); // 未装配选项 = compact 模式
        Assert.Null(loop.TryClaimBudgetReminder(usedTokens: 999_000, budget: 1_000_000));

        loop.UpdateContextManagement(
            new ContextManagementOptions(
                ContextMode.Compact,
                ReminderPercent: 0,
                VectorRetrieval: false
            )
        );
        Assert.Null(loop.TryClaimBudgetReminder(usedTokens: 999_000, budget: 1_000_000));

        await loop.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════
    //  自适应换页：由 ConversationLoop 自己判定与触发
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task AutoRoll_TriggersWhenUsageCrossesProfileRatio()
    {
        var agent = new AgentType(new ToolRegistry());
        var monitor = new StubStabilityMonitor(budget: 100_000);
        var reasons = new List<string>();
        var loop = CreateLoop(
            onNewContextRequested: (reason, _) =>
            {
                reasons.Add(reason);
                return Task.FromResult(true);
            },
            agent: agent,
            monitor: monitor
        );
        loop.UpdateContextManagement(
            new ContextManagementOptions(ContextMode.Continuation, 0, false)
        );
        // 模型窗口 = 100K：阈值由 profile 推导（模型相对），不是硬编码常量
        var profile = new ModelContextProfile { ContextWindowTokens = 100_000 };
        loop.UpdateModelProfile(profile);
        loop.RestoreFromMessages([new ChatMessage(ChatMessageRoles.User, "本页已有内容")]);
        agent.SetContextWindowState(new ContextWindowState("w1", "w0", "w0", 1));

        var autoRoll = profile.AutoRollPercent;
        monitor.SetTokens((long)(100_000 * (autoRoll - 0.02)));
        Assert.False(loop.ShouldAutoRoll());
        loop.ScheduleContextRollIfNeeded();
        await loop.FlushScheduledContextRollAsync(CancellationToken.None);
        Assert.Empty(reasons); // 未到阈值 → 无登记

        monitor.SetTokens((long)(100_000 * (autoRoll + 0.02)));
        Assert.True(loop.ShouldAutoRoll());
        loop.ScheduleContextRollIfNeeded();
        Assert.Empty(reasons); // 只登记：run 中途不换页（否则会清掉本轮用户消息）
        await loop.FlushScheduledContextRollAsync(CancellationToken.None);
        Assert.Equal(["threshold"], reasons);

        // 每页只尝试一次（防抖）：即使 usage 仍高也不再登记
        loop.ScheduleContextRollIfNeeded();
        await loop.FlushScheduledContextRollAsync(CancellationToken.None);
        Assert.Single(reasons);

        // 换页后（页状态变化）重新可触发
        monitor.SetTokens((long)(100_000 * (autoRoll + 0.02)));
        loop.SetContextWindowState(new ContextWindowState("w2", "w1", "w0", 10));
        loop.ScheduleContextRollIfNeeded();
        await loop.FlushScheduledContextRollAsync(CancellationToken.None);
        Assert.Equal(["threshold", "threshold"], reasons);

        await loop.DisposeAsync();
    }

    [Fact]
    public async Task AutoRoll_SkippedForEmptyPage_FrozenMonitor_OrCompactMode()
    {
        var agent = new AgentType(new ToolRegistry());
        var monitor = new StubStabilityMonitor(budget: 100_000, totalTokens: 99_000);
        var reasons = new List<string>();
        var loop = CreateLoop(
            onNewContextRequested: (reason, _) =>
            {
                reasons.Add(reason);
                return Task.FromResult(true);
            },
            agent: agent,
            monitor: monitor
        );
        loop.UpdateContextManagement(
            new ContextManagementOptions(ContextMode.Continuation, 0, false)
        );
        agent.SetContextWindowState(new ContextWindowState("w1", "w0", "w0", 1));

        // 空页不换（避免连续换空页）
        Assert.False(loop.ShouldAutoRoll());

        loop.RestoreFromMessages([new ChatMessage(ChatMessageRoles.User, "内容")]);
        Assert.True(loop.ShouldAutoRoll());

        // 冻结态不换（由溢出恢复流程处理）
        monitor.Frozen = true;
        Assert.False(loop.ShouldAutoRoll());
        monitor.Frozen = false;

        // compact 模式不换
        loop.UpdateContextManagement(new ContextManagementOptions(ContextMode.Compact, 0, false));
        Assert.False(loop.ShouldAutoRoll());

        Assert.Empty(reasons);
        await loop.DisposeAsync();
    }

    [Fact]
    public async Task AutoRoll_FailureStillClaimsOnce_AndModelRequestWins()
    {
        var agent = new AgentType(new ToolRegistry());
        var monitor = new StubStabilityMonitor(budget: 100_000, totalTokens: 99_000);
        var reasons = new List<string>();
        var loop = CreateLoop(
            onNewContextRequested: (reason, _) =>
            {
                reasons.Add(reason);
                return Task.FromResult(false); // Host 侧落盘失败
            },
            agent: agent,
            monitor: monitor
        );
        loop.UpdateContextManagement(
            new ContextManagementOptions(ContextMode.Continuation, 0, false)
        );
        loop.RestoreFromMessages([new ChatMessage(ChatMessageRoles.User, "内容")]);
        agent.SetContextWindowState(new ContextWindowState("w1", "w0", "w0", 1));

        loop.ScheduleContextRollIfNeeded();
        await loop.FlushScheduledContextRollAsync(CancellationToken.None); // 失败
        loop.ScheduleContextRollIfNeeded();
        await loop.FlushScheduledContextRollAsync(CancellationToken.None); // 不再空转重试
        Assert.Equal(["threshold"], reasons);

        // 模型主动请求优先于自适应阈值（reason=model）
        agent.RequestNewContextWindow("model");
        loop.ScheduleContextRollIfNeeded();
        await loop.FlushScheduledContextRollAsync(CancellationToken.None);
        Assert.Equal(["threshold", "model"], reasons);
        Assert.Null(agent.PendingNewContextReason);

        await loop.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════
    //  换页请求：消费一次 + 无回调时丢弃
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task PendingRequest_InvokesHostCallbackOnce()
    {
        var agent = new AgentType(new ToolRegistry());
        var calls = new List<string>();
        var loop = CreateLoop(
            onNewContextRequested: (reason, _) =>
            {
                calls.Add(reason);
                return Task.FromResult(true);
            },
            agent: agent
        );

        agent.RequestNewContextWindow("model");
        Assert.Equal("model", agent.PendingNewContextReason);

        loop.ScheduleContextRollIfNeeded();
        Assert.Empty(calls); // 只登记，不立刻执行
        await loop.FlushScheduledContextRollAsync(CancellationToken.None);
        await loop.FlushScheduledContextRollAsync(CancellationToken.None); // 幂等：只执行一次

        Assert.Equal(["model"], calls);
        Assert.Null(agent.PendingNewContextReason);

        await loop.DisposeAsync();
    }

    [Fact]
    public async Task PendingRequest_WithoutCallback_IsDiscarded()
    {
        var agent = new AgentType(new ToolRegistry());
        var loop = CreateLoop(agent: agent);

        agent.RequestNewContextWindow("model");
        loop.ScheduleContextRollIfNeeded();
        await loop.FlushScheduledContextRollAsync(CancellationToken.None);

        // 无回调 = 丢弃：不能留到下一次 run（否则会把下一条用户消息连页清出模型上下文）
        Assert.Null(agent.PendingNewContextReason);

        // 重复请求只保留首个原因
        agent.RequestNewContextWindow("model");
        agent.RequestNewContextWindow("overflow");
        Assert.Equal("model", agent.PendingNewContextReason);
        Assert.Equal("model", agent.ConsumeNewContextRequest());
        Assert.Null(agent.ConsumeNewContextRequest());

        await loop.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════
    //  页状态同步：Agent ↔ Loop
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void SetContextWindowState_FlowsThroughAgentAndLoopCreation()
    {
        var agent = new AgentType(new ToolRegistry());
        var window = new ContextWindowState("w5", "w4", "w0", 128);

        agent.SetContextWindowState(window);

        Assert.Equal(window, agent.ContextWindowState);
        Assert.Equal(ContextWindowState.First, GetLoopWindow(agent)); // 循环未创建 → 无副作用

        // 冷启动：循环创建时必须补推页状态与上下文管理选项
        agent.OnNewContextRequested = (_, _) => Task.FromResult(true);
        agent.TornadoApi = new LlmTornado.TornadoApi([
            new ProviderAuthentication(LLmProviders.OpenAi, "test-key"),
        ]);
        agent.SetModelName("gpt-test");
        var loop = agent.EnsureConversationLoop();
        Assert.NotNull(loop);
        Assert.Equal(window, GetLoopWindow(agent));

        agent.ApplyContextManagement(
            new ContextManagementOptions(ContextMode.Continuation, 20_000, false)
        );
        Assert.Equal(ContextMode.Continuation, agent.ContextManagement!.Mode);
    }

    /// <summary>循环未创建时返回 First（通过反射读私有字段，避免为测试放宽可见性）。</summary>
    private static ContextWindowState GetLoopWindow(AgentType agent)
    {
        var loop = agent.ConversationLoop;
        if (loop is null)
            return ContextWindowState.First;
        var field = typeof(ConversationLoop).GetField(
            "_contextWindow",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
        );
        return (ContextWindowState)field!.GetValue(loop)!;
    }
}
