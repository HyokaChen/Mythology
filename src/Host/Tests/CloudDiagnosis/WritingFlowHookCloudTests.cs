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
/// HARNESS-CLOUD §10 P1-G0 WritingFlowHook 集成验收：
/// ① scene.plan human_decision_required → GetPending 出现问题；
/// ② 回答后 InjectQueue 出现 [人工裁决] 前缀；
/// ⑤ ThrowingHarness（CloudApiException）→ 降级本地信号，Loop 不中断；
/// 幂等：同章二次写入仅调一次 harness；取消：OCE 透传出 Hook。
/// </summary>
public sealed class WritingFlowHookCloudTests
{
    private const string NovelId = "novel_test";

    private static readonly string DecisionJson = """
        {"capability":"fiction.scene.plan","status":"failed",
         "features":{"overall-score":0.38},
         "repair-intent":{"action":"human_decision_required","requires-human-decision":true,
           "reason":"Overall quality score is below the minimum threshold for automated planning."}}
        """;

    private static readonly string PassJson =
        """{"capability":"fiction.scene.plan","status":"pass","features":{"overall-score":0.88}}""";

    /// <summary>层 1 采样门槛：样本不可测（无评分、无人工裁决）。</summary>
    private static readonly string InsufficientJson = """
        {"capability":"fiction.scene.plan","status":"warning","features":{},
         "repair-intent":{"action":"insufficient_data","requires-human-decision":false,
           "reason-code":"insufficient-evidence",
           "reason":"Target chapter lacks sufficient substantive content for a reliable scene-plan verdict."}}
        """;

    private static Agent NewAgent()
    {
        var agent = new Agent(new ToolRegistry());
        agent.CurrentNovelId = NovelId;
        return agent;
    }

    private static async Task<WritingFlowHook> NewHookAsync(
        MockNovelStore store,
        IFictionHarnessClient harness,
        UserQuestionStore? questionStore = null,
        int decisionTimeoutSeconds = 120,
        CloudDiagnosisOptions? options = null,
        KnowledgeRestClient? knowledge = null,
        Agent? agent = null
    )
    {
        var novel = new Novel
        {
            Id = NovelId,
            Title = "测试小说",
            AutoDiagnose = true,
        };
        await store.SaveNovel(novel);
        store.AddChapters(
            NovelId,
            new Chapter
            {
                Id = "ch_1",
                Title = "第一章",
                Order = 1,
                Content = "少年在山巅睁开眼。",
            }
        );
        var effectiveOptions =
            options ?? new CloudDiagnosisOptions { OntologyHarnessEnabled = false };
        return new WritingFlowHook(
            agent ?? NewAgent(),
            store,
            new DiagnosisScheduler(harness, effectiveOptions),
            questionStore is null
                ? null
                : new HumanDecisionBridge(questionStore, decisionTimeoutSeconds),
            effectiveOptions,
            knowledge: knowledge
        );
    }

    private static ToolHookContext WriteContext(string chapterId = "ch_1") =>
        new()
        {
            ToolName = "write_chapter",
            Result = new AgentToolResult<ChapterWriteDetails>
            {
                Content = [new Core.Shared.TextContent("ok")],
                Details = new ChapterWriteDetails(chapterId, 1200),
            },
        };

    /// <summary>IChapterWriteResult 测试桩（对齐 write_chapter 的真实返回形状）。</summary>
    private sealed record ChapterWriteDetails(string Id, int WordCount) : IChapterWriteResult;

    [Fact]
    public async Task DecisionOutcome_CreatesQuestion_AndAnswerFlowsToInjectQueue()
    {
        var store = new MockNovelStore();
        var questions = new UserQuestionStore();
        var agent = NewAgent();
        var calls = 0;
        var harness = new StubHarness(_ =>
        {
            Interlocked.Increment(ref calls);
            return new HarnessResult
            {
                Capability = "fiction.scene.plan",
                Result = JsonSerializer.Deserialize<JsonElement>(DecisionJson),
            };
        });
        var novel = new Novel
        {
            Id = NovelId,
            Title = "测试小说",
            AutoDiagnose = true,
            Characters =
            [
                new Character { Name = "主角", Role = CharacterRole.Protagonist },
                new Character { Name = "配角甲", Role = CharacterRole.Supporting },
                new Character { Name = "配角乙", Role = CharacterRole.Supporting },
            ],
        };
        await store.SaveNovel(novel);
        store.AddChapters(
            NovelId,
            new Chapter
            {
                Id = "ch_1",
                Title = "第五章",
                Order = 5,
                Content = "少年在山巅睁开眼。",
            }
        );
        var hook = new WritingFlowHook(
            agent,
            store,
            new DiagnosisScheduler(harness, new CloudDiagnosisOptions()),
            new HumanDecisionBridge(questions, decisionTimeoutSeconds: 120),
            new CloudDiagnosisOptions()
        );

        // Hook 在 DECISION 路径上阻塞等待回答——先启动再从测试线程回答
        var hookTask = hook.AfterToolCallAsync(WriteContext(), CancellationToken.None);
        for (var i = 0; i < 100 && questions.GetPending().Count == 0; i++)
            await Task.Delay(10);

        // 验收①：scene.plan human_decision_required → GetPending 出现问题
        var pending = questions.GetPending();
        Assert.Single(pending);

        questions.Answer(pending[0].Id, ["按建议继续自动迭代"]);
        await hookTask;

        // 验收②：InjectQueue 出现 [人工裁决] 前缀消息；云端成功不注入本地信号
        Assert.True(agent.InjectQueue.HasPending);
        var injected = (LlmChatMessage)agent.InjectQueue.DequeueAllWithIds()[0].Message;
        Assert.StartsWith("[人工裁决]", injected.Content);
        Assert.Equal(1, calls); // 每章幂等 ≤1 次
    }

    [Fact]
    public async Task PassOutcome_InjectsCloudPassSignal_SkipsLocalSignal()
    {
        var store = new MockNovelStore();
        var harness = new StubHarness(_ => new HarnessResult
        {
            Capability = "fiction.scene.plan",
            Result = JsonSerializer.Deserialize<JsonElement>(PassJson),
        });
        var agent = NewAgent();
        var novel = new Novel
        {
            Id = NovelId,
            Title = "测试小说",
            AutoDiagnose = true,
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
        store.AddChapters(
            NovelId,
            new Chapter
            {
                Id = "ch_1",
                Title = "第一章",
                Order = 4,
                Content = "他隐瞒了秘密，众人都以为他是凡人。",
            }
        );
        var hook = new WritingFlowHook(
            agent,
            store,
            new DiagnosisScheduler(harness, new CloudDiagnosisOptions()),
            null,
            new CloudDiagnosisOptions()
        );

        await hook.AfterToolCallAsync(WriteContext(), CancellationToken.None);

        Assert.True(agent.InjectQueue.HasPending);
        var injected = agent.InjectQueue.DequeueAllWithIds()[0].Message;
        Assert.StartsWith("[云端诊断] status=pass", ((LlmChatMessage)injected).Content);
    }

    [Fact]
    public async Task ConstraintRuleViolation_BlocksCloudAndInjectsOntologyHarness()
    {
        var store = new MockNovelStore();
        var calls = 0;
        var harness = new StubHarness(_ =>
        {
            Interlocked.Increment(ref calls);
            return new HarnessResult
            {
                Capability = "fiction.scene.plan",
                Result = JsonSerializer.Deserialize<JsonElement>(PassJson),
            };
        });
        var agent = NewAgent();
        await store.SaveNovel(
            new Novel
            {
                Id = NovelId,
                Title = "测试小说",
                AutoDiagnose = true,
                Characters =
                [
                    new Character
                    {
                        Id = "c1",
                        NovelId = NovelId,
                        Name = "林长歌",
                    },
                ],
            }
        );
        store.AddChapters(
            NovelId,
            new Chapter
            {
                Id = "ch_1",
                Title = "第一章",
                Order = 1,
                Content = "林长歌御剑而起。",
            }
        );
        await store.SaveScene(
            new SceneDefinition
            {
                Id = "sc1",
                NovelId = NovelId,
                ChapterId = "ch_1",
                Title = "场景",
                Order = 1,
                ParticipantIds = ["c1"],
                PlannedDeltas =
                [
                    new PlannedDelta
                    {
                        Kind = PlannedDeltaKind.Action,
                        TargetEntityType = EntityType.Character,
                        TargetId = "c1",
                        ActionType = "御剑",
                        Summary = "计划御剑",
                    },
                ],
            }
        );
        await store.SaveWorldRule(
            NovelId,
            new WorldRule
            {
                Id = "r1",
                NovelId = NovelId,
                EntryKind = WorldRuleEntryKind.Rule,
                RuleKind = RuleKind.Forbids,
                SubjectType = EntityType.Character,
                ForbiddenAction = "御剑",
                Description = "外门弟子不得御剑",
                Severity = RuleSeverity.Constraint,
                Effect = new PlannedDelta { Summary = "被执法堂处罚" },
            }
        );
        var options = new CloudDiagnosisOptions { OntologyHarnessEnabled = true };
        var hook = new WritingFlowHook(
            agent,
            store,
            new DiagnosisScheduler(harness, options),
            null,
            options
        );

        await hook.AfterToolCallAsync(WriteContext(), CancellationToken.None);

        Assert.Equal(0, calls);
        Assert.True(agent.InjectQueue.HasPending);
        var injected = (LlmChatMessage)agent.InjectQueue.DequeueAllWithIds()[0].Message;
        Assert.StartsWith("[本体 harness]", injected.Content);
        Assert.Contains("风险：", injected.Content);
    }

    /// <summary>层 1 采样门槛（跨仓库）：scene.plan 返回 insufficient_data → 注入一行跳过说明，
    /// 不得落入 PASS 文案（“综合=0 可按计划推进”），也不触发人工裁决/修复规则。
    /// </summary>
    [Fact]
    public async Task InsufficientData_InjectsSkipLine_NotPassMessage()
    {
        var store = new MockNovelStore();
        var harness = new StubHarness(_ => new HarnessResult
        {
            Capability = "fiction.scene.plan",
            Result = JsonSerializer.Deserialize<JsonElement>(InsufficientJson),
        });
        var agent = NewAgent();
        await store.SaveNovel(
            new Novel
            {
                Id = NovelId,
                Title = "测试小说",
                AutoDiagnose = true,
            }
        );
        store.AddChapters(
            NovelId,
            new Chapter
            {
                Id = "ch_1",
                Title = "第一章",
                Order = 1,
                Content = "少年在山巅睁开眼。",
            }
        );
        var hook = new WritingFlowHook(
            agent,
            store,
            new DiagnosisScheduler(harness, new CloudDiagnosisOptions()),
            null,
            new CloudDiagnosisOptions()
        );

        await hook.AfterToolCallAsync(WriteContext(), CancellationToken.None);

        Assert.True(agent.InjectQueue.HasPending);
        var injected = (LlmChatMessage)agent.InjectQueue.DequeueAllWithIds()[0].Message;
        Assert.Contains("本轮跳过", injected.Content);
        Assert.DoesNotContain("status=pass", injected.Content);
    }

    [Fact]
    public async Task ThrowingHarness_FallsBackToLocalSignal_DoesNotThrow()
    {
        var store = new MockNovelStore();
        var harness = new ThrowingHarness(new CloudApiException("unauthorized", "未登录"));
        var agent = NewAgent();
        var novel = new Novel
        {
            Id = NovelId,
            Title = "测试小说",
            AutoDiagnose = true,
        };
        await store.SaveNovel(novel);
        store.AddChapters(
            NovelId,
            new Chapter
            {
                Id = "ch_1",
                Title = "第一章",
                Order = 1,
                Content = "正文内容足够长。",
            }
        );
        var hook = new WritingFlowHook(
            agent,
            store,
            new DiagnosisScheduler(harness, new CloudDiagnosisOptions()),
            null,
            new CloudDiagnosisOptions()
        );

        // 验收⑤：不抛异常（Loop 不中断）
        await hook.AfterToolCallAsync(WriteContext(), CancellationToken.None);

        // 降级到本地信号：InjectQueue 有消息且不含云端诊断前缀
        Assert.True(agent.InjectQueue.HasPending);
        var injected = (LlmChatMessage)agent.InjectQueue.DequeueAllWithIds()[0].Message;
        Assert.DoesNotContain("[云端诊断]", injected.Content);
    }

    /// <summary>
    /// E2 L2-3 新语义：同章首次写入与首次重写各允许一次基础档；
    /// 第二次 rewrite 触发「同章二次重写」事件 → 解除幂等再调一次（共 2 次）。
    /// </summary>
    [Fact]
    public async Task SameChapterRewrite_SecondCall_BypassesIdempotency()
    {
        var store = new MockNovelStore();
        var calls = 0;
        var harness = new StubHarness(request =>
        {
            if (request.Capability == "fiction.scene.plan")
                Interlocked.Increment(ref calls);
            return new HarnessResult
            {
                Capability = request.Capability,
                Result = JsonSerializer.Deserialize<JsonElement>(PassJson),
            };
        });
        var agent = NewAgent();
        var novel = new Novel
        {
            Id = NovelId,
            Title = "测试小说",
            AutoDiagnose = true,
            Characters =
            [
                new Character { Name = "主角", Role = CharacterRole.Protagonist },
                new Character { Name = "配角甲", Role = CharacterRole.Supporting },
                new Character { Name = "配角乙", Role = CharacterRole.Supporting },
            ],
        };
        await store.SaveNovel(novel);
        store.AddChapters(
            NovelId,
            new Chapter
            {
                Id = "ch_1",
                Title = "第五章",
                Order = 5,
                Content = "少年在山巅睁开眼。",
            }
        );
        var hook = new WritingFlowHook(
            agent,
            store,
            new DiagnosisScheduler(harness, new CloudDiagnosisOptions()),
            null,
            new CloudDiagnosisOptions()
        );

        await hook.AfterToolCallAsync(WriteContext(), CancellationToken.None); // 首次写入
        await hook.AfterToolCallAsync(RewriteContext(), CancellationToken.None); // 第一次重写 → 幂等跳过
        await hook.AfterToolCallAsync(RewriteContext(), CancellationToken.None); // 第二次重写 → L2-3 强制体检

        Assert.Equal(2, calls);
    }

    private static ToolHookContext RewriteContext() =>
        new()
        {
            ToolName = "rewrite_selection",
            Result = new AgentToolResult<ChapterWriteDetails>
            {
                Content = [new TextContent("ok")],
                Details = new ChapterWriteDetails("ch_1", 1100),
            },
        };

    [Fact]
    public async Task UserCancellation_PropagatesOutOfHook()
    {
        var store = new MockNovelStore();
        using var cts = new CancellationTokenSource();
        var harness = new CancelingHarness(cts.Token);
        var agent = NewAgent();
        var novel = new Novel
        {
            Id = NovelId,
            Title = "测试小说",
            AutoDiagnose = true,
            Characters =
            [
                new Character { Name = "主角", Role = CharacterRole.Protagonist },
                new Character { Name = "配角甲", Role = CharacterRole.Supporting },
                new Character { Name = "配角乙", Role = CharacterRole.Supporting },
            ],
        };
        await store.SaveNovel(novel);
        store.AddChapters(
            NovelId,
            new Chapter
            {
                Id = "ch_1",
                Title = "第五章",
                Order = 5,
                Content = "少年在山巅睁开眼。",
            }
        );
        var hook = new WritingFlowHook(
            agent,
            store,
            new DiagnosisScheduler(harness, new CloudDiagnosisOptions()),
            null,
            new CloudDiagnosisOptions()
        );

        // 验收④：OCE 透传出 Hook（不被 catch-all 吞掉）。
        // 先让 Hook 挂起在云端调用内，再触发取消。
        var hookTask = hook.AfterToolCallAsync(WriteContext(), cts.Token);
        await Task.Delay(50); // 确保 RunScenePlanAsync 已进入 ExecuteAsync 等待
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hookTask);
    }

    [Fact]
    public async Task SceneGoal_PlannedRevealChapter_SetsSceneGoalInInput()
    {
        var store = new MockNovelStore();
        var harness = new StubHarness(_ => new HarnessResult
        {
            Capability = "fiction.scene.plan",
            Result = JsonSerializer.Deserialize<JsonElement>(PassJson),
        });
        var agent = NewAgent();
        var novel = new Novel
        {
            Id = NovelId,
            Title = "测试小说",
            AutoDiagnose = true,
            Plotlines =
            [
                new Plotline
                {
                    Id = "pl_1",
                    Name = "师门血案",
                    PlannedRevealChapterId = "ch_2",
                },
            ],
        };
        await store.SaveNovel(novel);
        store.AddChapters(
            NovelId,
            new Chapter
            {
                Id = "ch_1",
                Title = "第一章",
                Order = 1,
                Content = "少年在山巅睁开眼。",
            }
        );
        var hook = new WritingFlowHook(
            agent,
            store,
            new DiagnosisScheduler(harness, new CloudDiagnosisOptions()),
            null,
            new CloudDiagnosisOptions()
        );

        await hook.AfterToolCallAsync(WriteContext(), CancellationToken.None);

        Assert.Equal(1, harness.CallCount);
    }

    [Fact]
    public async Task SceneGoal_NoMatchingPlotline_LeavesSceneGoalNull()
    {
        var store = new MockNovelStore();
        var harness = new StubHarness(_ => new HarnessResult
        {
            Capability = "fiction.scene.plan",
            Result = JsonSerializer.Deserialize<JsonElement>(PassJson),
        });
        var agent = NewAgent();
        var novel = new Novel
        {
            Id = NovelId,
            Title = "测试小说",
            AutoDiagnose = true,
            Plotlines =
            [
                new Plotline
                {
                    Id = "pl_1",
                    Name = "师门血案",
                    PlannedRevealChapterId = "ch_99",
                },
            ],
        };
        await store.SaveNovel(novel);
        store.AddChapters(
            NovelId,
            new Chapter
            {
                Id = "ch_1",
                Title = "第一章",
                Order = 1,
                Content = "少年在山巅睁开眼。",
            }
        );
        var hook = new WritingFlowHook(
            agent,
            store,
            new DiagnosisScheduler(harness, new CloudDiagnosisOptions()),
            null,
            new CloudDiagnosisOptions()
        );

        await hook.AfterToolCallAsync(WriteContext(), CancellationToken.None);

        Assert.Equal(1, harness.CallCount);
    }

    /// <summary>
    /// §12-6 队列语义红线：Hook 产生的全部信号（云端 PASS/ACTION/DECISION 回注/本地降级/灵感）
    /// 只走 InjectQueue；SteeringQueue 必须保持零消息（Steer 专属于用户真实输入）。
    /// </summary>
    [Fact]
    public async Task HookSignals_GoToInjectQueue_SteeringQueueStaysEmpty()
    {
        var store = new MockNovelStore();
        var harness = new StubHarness(_ => new HarnessResult
        {
            Capability = "fiction.scene.plan",
            Result = JsonSerializer.Deserialize<JsonElement>(PassJson),
        });
        var agent = NewAgent();
        var novel = new Novel
        {
            Id = NovelId,
            Title = "测试小说",
            AutoDiagnose = true,
            Characters =
            [
                new Character { Name = "主角", Role = CharacterRole.Protagonist },
                new Character { Name = "配角甲", Role = CharacterRole.Supporting },
                new Character { Name = "配角乙", Role = CharacterRole.Supporting },
            ],
        };
        await store.SaveNovel(novel);
        store.AddChapters(
            NovelId,
            new Chapter
            {
                Id = "ch_1",
                Title = "第五章",
                Order = 5,
                Content = "少年在山巅睁开眼。",
            }
        );
        var hook = new WritingFlowHook(
            agent,
            store,
            new DiagnosisScheduler(harness, new CloudDiagnosisOptions()),
            null,
            new CloudDiagnosisOptions()
        );

        await hook.AfterToolCallAsync(WriteContext(), CancellationToken.None);

        Assert.True(agent.InjectQueue.HasPending); // 系统信号已注入
        Assert.False(agent.SteeringQueue.HasPending); // Steer 零调用（红线 6）
    }

    /// <summary>
    /// Phase 5.1：设定图只在调度命中后构建（成本门控）——命中轮 scene.plan/story.check 请求
    /// 携带 Context.Setup（context.retrieve 不挂）；同章同内容二次写入被幂等拦截 → 不再读设定集合。
    /// </summary>
    [Fact]
    public async Task SetupDigest_BuiltOnlyWhenScheduled_AndAttachedToPlanRequests()
    {
        var store = new MockNovelStore();
        var requests = new List<HarnessRequest>();
        var harness = new StubHarness(request =>
        {
            requests.Add(request);
            return new HarnessResult
            {
                Capability = request.Capability,
                Result = JsonSerializer.Deserialize<JsonElement>(PassJson),
            };
        });
        var hook = await NewHookAsync(store, harness);

        await hook.AfterToolCallAsync(WriteContext(), CancellationToken.None);

        Assert.Equal(1, store.SetupDigestLoads); // 命中轮构图一次
        Assert.Contains(requests, r => r.Capability == "fiction.scene.plan");
        Assert.All(
            requests.Where(r => r.Capability is "fiction.scene.plan" or "fiction.story.check"),
            r => Assert.NotNull(r.Context?.Setup)
        );
        Assert.All(
            requests.Where(r => r.Capability == "fiction.context.retrieve"),
            r => Assert.Null(r.Context?.Setup)
        );

        // 同章同内容二次写入 → 幂等跳过 → 不再构图
        await hook.AfterToolCallAsync(WriteContext(), CancellationToken.None);
        Assert.Equal(1, store.SetupDigestLoads);
    }

    /// <summary>story.check 带 review 三段（continuity conflict + 规则 finding + 计划符合度）。</summary>
    private static readonly string ReviewCheckJson = """
        {"capability":"fiction.story.check","status":"warning",
         "dimensions":{"pace":{"overall-score":50}},
         "rule-findings":[
           {"dimension":"narrative.state-entropy","severity":"high",
            "description":"纯灌水章节：叙事熵 0.21 低于阈值",
            "suggestion":"删除灌水段，压缩为事件推进","impact-score":88.5}],
         "continuity-audit":{
           "findings":[
             {"kind":"conflict","rule":"continuity.setup-drift","severity":"medium",
              "chapters":["ch_1"],"description":"主角在第 1 章被杀，但设定状态仍为 active",
              "evidence":"第 1 章正文","suggestion":"修订第 1 章死亡描写或同步设定状态",
              "impact-score":60}],
           "revision-candidates":[{"chapter":"ch_1","reasons":["continuity.setup-drift"],"impact-score":60}]},
         "plan-conformance":{"planned-beat-count":2,"missed-beat-count":1,"findings":[]}}
        """;

    /// <summary>
    /// review 段消费端到端（2026-10-18）：story.check 顶层 continuity-audit / rule-findings /
    /// plan-conformance 必须经「注入队列」到达 LLM 可见文本（三段式证据/处方/边界）——
    /// 这是「服务端新键 → 用户可见信号」这条链路的锁（此前新键只在服务端与单元解析里存在）。
    /// </summary>
    [Fact]
    public async Task StoryCheckReviewSections_ReachInjectedSignal()
    {
        var store = new MockNovelStore();
        var harness = new StubHarness(request => new HarnessResult
        {
            Capability = request.Capability,
            Result = JsonSerializer.Deserialize<JsonElement>(
                request.Capability == "fiction.story.check" ? ReviewCheckJson : PassJson
            ),
        });
        var agent = NewAgent();
        await store.SaveNovel(
            new Novel
            {
                Id = NovelId,
                Title = "测试小说",
                AutoDiagnose = true,
                Characters = [new Character { Name = "主角", Role = CharacterRole.Protagonist }],
                // 计划揭示章 = 目标章 → L2-1 命中 → 深度档 story.check 放行
                Plotlines =
                [
                    new Plotline
                    {
                        Id = "pl_1",
                        Name = "师门血案",
                        PlannedRevealChapterId = "ch_1",
                    },
                ],
            }
        );
        store.AddChapters(
            NovelId,
            new Chapter
            {
                Id = "ch_1",
                Title = "第五章",
                Order = 5,
                Content = "少年在山巅睁开眼。",
            }
        );
        var hook = new WritingFlowHook(
            agent,
            store,
            new DiagnosisScheduler(harness, new CloudDiagnosisOptions()),
            null,
            new CloudDiagnosisOptions()
        );

        await hook.AfterToolCallAsync(WriteContext(), CancellationToken.None);

        Assert.True(agent.InjectQueue.HasPending);
        var injected = (LlmChatMessage)agent.InjectQueue.DequeueAllWithIds()[0].Message;
        Assert.StartsWith("[云端诊断] status=action", injected.Content);
        Assert.Contains("证据[C-1(continuity.setup-drift)]", injected.Content);
        Assert.Contains("候选修订章：ch_1", injected.Content);
        Assert.Contains("处方:", injected.Content);
    }

    // ══════════════════════════════════════════════════════════
    //  双速自进化（单账本折叠架构）— Step 2 接线验收
    // ══════════════════════════════════════════════════════════

    /// <summary>含 high severity signal 的假云端响应 → 规则引擎产出 S-1(节奏) 行动项。</summary>
    private static readonly string EvolutionSignalsJson = """
        [{"dimension":"节奏","severity":"high","description":"节奏拖沓",
          "suggestion":"建议引入对抗或加速事件"}]
        """;

    /// <summary>
    /// 双速自进化专用桩：基础档 context.retrieve 走 GetContextAsync（快照路径，
    /// 携带 high severity signal + overall-score），其余能力走 ExecuteAsync（scene.plan → pass）。
    /// </summary>
    private sealed class EvolutionHarness : IFictionHarnessClient
    {
        public int ExecuteCalls;

        public Task<HarnessResult> ExecuteAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken
        )
        {
            Interlocked.Increment(ref ExecuteCalls);
            return Task.FromResult(
                new HarnessResult
                {
                    Capability = request.Capability,
                    Result = JsonSerializer.Deserialize<JsonElement>(PassJson),
                }
            );
        }

        public Task<AgentContextSnapshot> GetContextAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new AgentContextSnapshot
                {
                    Features = JsonSerializer.Deserialize<JsonElement>("""{"overall-score":0.7}"""),
                    Signals = JsonSerializer.Deserialize<JsonElement>(EvolutionSignalsJson),
                }
            );

        public Task ReportSetupFeedbackAsync(
            IReadOnlyList<(string FindingId, string Decision)> items,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    private static string NewEvolutionDir() =>
        Directory
            .CreateDirectory(
                Path.Combine(
                    Path.GetTempPath(),
                    "WritingFlowEvolution_" + Guid.NewGuid().ToString("N")
                )
            )
            .FullName;

    private static string LedgerPathOf(string dir) =>
        Path.Combine(dir, "evolution", "ledger.jsonl");

    /// <summary>读账本行为独立 JsonElement（Clone 脱离 JsonDocument 生命周期）。</summary>
    private static List<JsonElement> ReadLedgerRows(string dir) =>
        File.ReadLines(LedgerPathOf(dir))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l =>
            {
                using var doc = JsonDocument.Parse(l);
                return doc.RootElement.Clone();
            })
            .ToList();

    private static string KindOf(JsonElement row) => row.GetProperty("kind").GetString() ?? "";

    [Fact]
    public async Task Evolution_Disabled_WritesNothingAndInjectsNoTactics()
    {
        var store = new MockNovelStore();
        var agent = NewAgent();
        var dir = NewEvolutionDir();
        try
        {
            await store.SaveNovel(
                new Novel
                {
                    Id = NovelId,
                    Title = "测试小说",
                    AutoDiagnose = true,
                    Genre = "都市爽文",
                }
            );
            store.AddChapters(
                NovelId,
                new Chapter
                {
                    Id = "ch_1",
                    Title = "第一章",
                    Order = 1,
                    Content = "少年在山巅睁开眼。",
                }
            );
            var hook = new WritingFlowHook(
                agent,
                store,
                new DiagnosisScheduler(new EvolutionHarness(), new CloudDiagnosisOptions()),
                null,
                new CloudDiagnosisOptions(), // EvolutionEnabled 默认 false
                ledger: new TacticLedger(dir)
            );

            await hook.AfterToolCallAsync(WriteContext(), CancellationToken.None);

            var messages = agent
                .InjectQueue.DequeueAllWithIds()
                .Select(m => ((LlmChatMessage)m.Message).Content)
                .ToList();
            Assert.Contains(messages, m => m.StartsWith("[云端诊断] status=action"));
            Assert.DoesNotContain(messages, m => m.Contains("[写作战术"));
            Assert.False(File.Exists(LedgerPathOf(dir))); // 默认关 ⇒ 零账本写入
        }
        finally
        {
            AtomicFile.ForgetPath(LedgerPathOf(dir));
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Evolution_ActionRound_InjectsTactics_AndNextChapterSettlesFail()
    {
        var store = new MockNovelStore();
        var agent = NewAgent();
        var dir = NewEvolutionDir();
        var options = new CloudDiagnosisOptions { EvolutionEnabled = true };
        try
        {
            await store.SaveNovel(
                new Novel
                {
                    Id = NovelId,
                    Title = "测试小说",
                    AutoDiagnose = true,
                    Genre = "都市爽文",
                }
            );
            store.AddChapters(
                NovelId,
                new Chapter
                {
                    Id = "ch_1",
                    Title = "第一章",
                    Order = 1,
                    Content = "少年在山巅睁开眼。",
                },
                new Chapter
                {
                    Id = "ch_2",
                    Title = "第二章",
                    Order = 2,
                    Content = "他走进房间，关上门。",
                }
            );
            var hook = new WritingFlowHook(
                agent,
                store,
                new DiagnosisScheduler(new EvolutionHarness(), options),
                null,
                options,
                ledger: new TacticLedger(dir)
            );

            // ── 第一章（action 轮）：注入战术块 + learn/diagnose/inject 落账 ──
            await hook.AfterToolCallAsync(WriteContext("ch_1"), CancellationToken.None);

            var firstMessages = agent
                .InjectQueue.DequeueAllWithIds()
                .Select(m => ((LlmChatMessage)m.Message).Content)
                .ToList();
            var block = Assert.Single(firstMessages, m => m.StartsWith("[写作战术 · 历史验证]"));
            Assert.Contains("S-1(节奏)", block);
            Assert.Contains("胜率 50% · 0 次", block); // 首次观测：Laplace 0.5 / 0 次使用

            var firstRows = ReadLedgerRows(dir);
            Assert.Contains(
                firstRows,
                r => KindOf(r) == "diagnose" && r.GetProperty("verdict").GetString() == "action"
            );
            Assert.Contains(firstRows, r => KindOf(r) == "learn");
            // A-4（本地事实）与 S-1(节奏)（云端高危信号）各注入一条
            var injectRows = firstRows.Where(r => KindOf(r) == "inject").ToList();
            Assert.Equal(2, injectRows.Count);
            Assert.All(injectRows, r => Assert.Equal(1, r.GetProperty("chapter").GetInt32()));
            Assert.Contains(injectRows, r => r.GetProperty("trigger").GetString() == "S-1(节奏)");
            var diagnoseRow = firstRows.First(r => KindOf(r) == "diagnose");
            Assert.Equal("都市爽文", diagnoseRow.GetProperty("genre").GetString());
            Assert.Equal("节奏", diagnoseRow.GetProperty("dims")[0].GetString());
            Assert.Contains(
                "S-1(节奏)",
                diagnoseRow.GetProperty("codes").EnumerateArray().Select(c => c.GetString() ?? "")
            );

            // ── 第二章：复现同 code → 结算 verdict:fail（跨章归因） ──
            await hook.AfterToolCallAsync(WriteContext("ch_2"), CancellationToken.None);

            var secondRows = ReadLedgerRows(dir);
            // A-4 与 S-1(节奏) 的待结算项均一次结清为 fail（第二章复现同 code）
            var verdictRows = secondRows.Where(r => KindOf(r) == "verdict").ToList();
            Assert.Equal(2, verdictRows.Count);
            Assert.All(
                verdictRows,
                r => Assert.Equal("fail", r.GetProperty("outcome").GetString())
            );
            Assert.All(verdictRows, r => Assert.Equal(2, r.GetProperty("chapter").GetInt32()));
            Assert.Equal(2, secondRows.Count(r => KindOf(r) == "diagnose")); // 新章继续记录
            Assert.Contains(
                secondRows,
                r =>
                    KindOf(r) == "diagnose"
                    && r.GetProperty("chapter").GetInt32() == 2
                    && r.GetProperty("verdict").GetString() == "action"
            );

            // 第二章再次注入战术块（新章未命中「本章已注入过」门控）
            var secondMessages = agent
                .InjectQueue.DequeueAllWithIds()
                .Select(m => ((LlmChatMessage)m.Message).Content)
                .ToList();
            Assert.Contains(secondMessages, m => m.StartsWith("[写作战术 · 历史验证]"));
        }
        finally
        {
            AtomicFile.ForgetPath(LedgerPathOf(dir));
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task MaterialHintsEnabled_Prefetch_InjectsOncePerChapter()
    {
        var store = new MockNovelStore();
        var agent = NewAgent();
        var calls = 0;
        var handler = new StubKnowledgeHandler(_ =>
        {
            Interlocked.Increment(ref calls);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"status":"ok","prompt-block":"素材参考A","hints":[],"vector-used":false}""",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        });
        var knowledge = new KnowledgeRestClient(
            "http://localhost",
            () => "mk_test_public-secret",
            http: new HttpClient(handler)
        );
        var hook = await NewHookAsync(
            store,
            new StubHarness(_ => new HarnessResult
            {
                Status = "completed",
                Capability = "fiction.scene.plan",
                Result = JsonDocument.Parse(PassJson).RootElement,
            }),
            options: new CloudDiagnosisOptions
            {
                MaterialHintsEnabled = true,
                MaterialHintTimeoutMs = 2000,
            },
            knowledge: knowledge,
            agent: agent
        );

        await hook.AfterToolCallAsync(WriteContext(), TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        var messages = agent.InjectQueue.DequeueAllWithIds().Select(item => item.Message).ToList();
        Assert.Contains(
            messages,
            message => message.Content?.Contains("素材参考A", StringComparison.Ordinal) == true
        );

        // 同章第二次写入：已尝试过预取 → 不再扣费/注入。
        await hook.AfterToolCallAsync(WriteContext(), TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
    }

    private sealed class StubKnowledgeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(respond(request));
    }

    private sealed class StubHarness(Func<HarnessRequest, HarnessResult> respond)
        : IFictionHarnessClient
    {
        public int CallCount;

        public Task<HarnessResult> ExecuteAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken
        )
        {
            Interlocked.Increment(ref CallCount);
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

    private sealed class ThrowingHarness(CloudApiException exception) : IFictionHarnessClient
    {
        public Task<HarnessResult> ExecuteAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken
        ) => Task.FromException<HarnessResult>(exception);

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

    /// <summary>在 ExecuteAsync 内等待外部 token 取消——模拟用户中断发生在云端调用期间。</summary>
    private sealed class CancelingHarness(CancellationToken trigger) : IFictionHarnessClient
    {
        public async Task<HarnessResult> ExecuteAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken
        )
        {
            var combined = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                trigger
            );
            await Task.Delay(Timeout.Infinite, combined.Token);
            return null!;
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

    // ── §16.4 SceneGoal 自动填充（优先级 2/3 提取逻辑）──

    [Fact]
    public void ExtractSceneGoal_FromAssistantPlan_ReturnsGoal()
    {
        var goal = WritingFlowHook.ExtractSceneGoal(
            "好的，我来写这一章。下章要引入反派，制造冲突。"
        );
        Assert.NotNull(goal);
        Assert.Contains("下章要引入反派", goal);
    }

    [Fact]
    public void ExtractSceneGoal_WithoutMarker_ReturnsNull()
    {
        var goal = WritingFlowHook.ExtractSceneGoal("本章已写完，整体节奏平稳。");
        Assert.Null(goal);
    }

    // ── §6.3 A-3 埋设章龄锚点（2026-08-28 增强）：伏笔名正文首现章序扫描 ──

    [Fact]
    public void FindFirstAppearOrder_ReturnsFirstChapterContainingName()
    {
        var chapters = new List<Chapter>
        {
            new()
            {
                Id = "c1",
                Order = 3,
                Content = "本章无伏笔。",
            },
            new()
            {
                Id = "c2",
                Order = 7,
                Content = "师门血案的真相浮现。",
            },
            new()
            {
                Id = "c3",
                Order = 9,
                Content = "师门血案再次提及。",
            },
        };

        Assert.Equal(7, WritingFlowHook.FindFirstAppearOrder("师门血案", chapters));
    }

    [Fact]
    public void FindFirstAppearOrder_NotFound_ReturnsNull()
    {
        var chapters = new List<Chapter>
        {
            new()
            {
                Id = "c1",
                Order = 3,
                Content = "本章无伏笔。",
            },
        };

        Assert.Null(WritingFlowHook.FindFirstAppearOrder("不存在的伏笔", chapters));
        Assert.Null(WritingFlowHook.FindFirstAppearOrder("", chapters));
    }

    // ── §16.5 方案 C 产品决策（2026-08-28）：会话边界 = 宿主进程，小说切换不结算 ──

    /// <summary>
    /// 小说 A（伏笔回收章 → L2-1 deep → 深度消耗）写一次后切换到小说 B 再写，
    /// 断言 A 的 SessionUsed 未被切换清零、Bank 未凭空增加（总量守恒、不刷额度）。
    /// </summary>
    [Fact]
    public async Task NovelSwitch_DoesNotSettlePreviousNovel()
    {
        var store = new MockNovelStore();
        var agent = new Agent(new ToolRegistry()) { CurrentNovelId = "novel_a" };
        var novelA = new Novel
        {
            Id = "novel_a",
            Title = "A",
            AutoDiagnose = true,
            Plotlines =
            [
                new Plotline
                {
                    Id = "pl_1",
                    Name = "谜底",
                    PlannedRevealChapterId = "ch_1",
                },
            ],
        };
        var novelB = new Novel
        {
            Id = "novel_b",
            Title = "B",
            AutoDiagnose = true,
        };
        await store.SaveNovel(novelA);
        await store.SaveNovel(novelB);
        store.AddChapters(
            "novel_a",
            new Chapter
            {
                Id = "ch_1",
                Title = "第一章",
                Order = 1,
                Content = "少年在山巅睁开眼。",
            }
        );
        store.AddChapters(
            "novel_b",
            new Chapter
            {
                Id = "ch_1",
                Title = "第一章",
                Order = 1,
                Content = "另一部小说的开篇。",
            }
        );

        var budgetStore = new NovelBudgetStore();
        var options = new CloudDiagnosisOptions
        {
            DeepBudgetPerSession = 20,
            DeepBudgetPerNovel = 20,
        };
        var harness = new SwitchHarness();
        var scheduler = new DiagnosisScheduler(harness, options, novelBudgetStore: budgetStore);
        var hook = new WritingFlowHook(agent, store, scheduler, null, options);

        // 写 A 的伏笔回收章（L2-1 → deep → 深度 1 次）
        await hook.AfterToolCallAsync(WriteContext(), CancellationToken.None);
        var stateA = budgetStore.GetOrCreate("novel_a", 20);
        Assert.Equal(1, stateA.SessionUsed);
        Assert.Equal(0, stateA.BankBalance);
        Assert.Equal(19, stateA.PoolRemaining);

        // 对账透出：深度档实际结算消耗随诊断信号进对话流（DeepUnits > 0 → 追加云端消耗行）
        var injectedA = (LlmChatMessage)agent.InjectQueue.DequeueAllWithIds()[0].Message;
        Assert.Contains("云端消耗: 4 units", injectedA.Content);

        // 切换到 B 再写：不触发 A 的结算（会话边界 = 宿主进程，非小说切换）
        agent.CurrentNovelId = "novel_b";
        await hook.AfterToolCallAsync(WriteContext(), CancellationToken.None);

        var afterSwitch = budgetStore.GetOrCreate("novel_a", 20);
        Assert.Equal(1, afterSwitch.SessionUsed); // 未被切换清零
        Assert.Equal(0, afterSwitch.BankBalance); // 未凭空入银行
        Assert.Equal(19, afterSwitch.PoolRemaining); // 未重复扣减
        Assert.Equal(19, afterSwitch.TotalRemaining); // 总量守恒 = 20 - 已用 1
    }

    /// <summary>支持快照 + scene.plan/story.check 响应的 harness（切换不结算场景）。</summary>
    private sealed class SwitchHarness : IFictionHarnessClient
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
                    CloudWorkUnits = request.Capability == "fiction.story.check" ? 4 : 0,
                    Result = JsonSerializer.Deserialize<JsonElement>(
                        request.Capability == "fiction.story.check"
                            ? """{"dimensions":{"pace":{"overall-score":72}}}"""
                            : """{"capability":"fiction.scene.plan","status":"pass","features":{"overall-score":0.88}}"""
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
}
