using System.Text.Json;
using ClientPlatform;
using Core.Agent;
using Core.Cloud;
using Core.Data;
using Core.Evolution;
using Core.Shared;
using Core.Tools;
using LlmTornado.Code;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace CloudDiagnosis;

/// <summary>
/// AfterToolCall 钩子：当 write_chapter / rewrite_selection 成功后，
/// 自动计算章节特征并通过 InjectQueue 注入信号，
/// 引导 LLM 自主判断是否需要诊断/增强。
/// P9/P10: 注入章末钩子状态 + 活跃信息差数（通过 WritingProfile 轻量扫描）。
///
/// HARNESS-CLOUD P1-G0：新增云端路径（基础档 fiction.scene.plan）——
/// DiagnosisScheduler 调度 → SignalBuilder 翻译 → PASS/ACTION 直接注入；
/// DECISION 路由 HumanDecisionBridge（同步阻塞式人工裁决，§5.3）。
/// 云端任何失败静默降级到既有本地信号路径（§0.2-4）；用户取消 OCE 上抛（§12 红线 5）。
/// </summary>
public sealed class WritingFlowHook(
    Agent agent,
    INovelStore store,
    DiagnosisScheduler? scheduler = null,
    HumanDecisionBridge? decisionBridge = null,
    CloudDiagnosisOptions? options = null,
    ContextSnapshotCache? snapshotCache = null,
    MonotonyWatcher? monotonyWatcher = null,
    UserQuestionStore? questionStore = null,
    UsageReconciler? usageReconciler = null,
    TacticLedger? ledger = null,
    KnowledgeRestClient? knowledge = null
) : IToolHook
{
    /// <summary>触发写作流程信号的写入类工具名称集合</summary>
    private static readonly HashSet<string> WriteTools = ["write_chapter", "rewrite_selection"];

    private readonly DiagnosisScheduler? _scheduler = scheduler;
    private readonly HumanDecisionBridge? _decisionBridge = decisionBridge;
    private readonly CloudDiagnosisOptions? _options = options;
    private readonly ContextSnapshotCache? _snapshotCache = snapshotCache;
    private readonly MonotonyWatcher? _monotony = monotonyWatcher;
    private readonly UserQuestionStore? _questionStore = questionStore;
    private readonly UsageReconciler? _usageReconciler = usageReconciler;

    /// <summary>双速自进化账本（单账本折叠；null = 未装配，行为与接线前逐字节一致）。</summary>
    private readonly TacticLedger? _ledger = ledger;

    /// <summary>素材提示客户端（null = 未配置云端 Key / 未注入；零行为变化）。</summary>
    private readonly KnowledgeRestClient? _knowledge = knowledge;

    /// <summary>本会话已尝试过素材预取的章节（novelId#order；同章只扣一次费）。</summary>
    private readonly HashSet<string> _hintCheckedChapters = new(StringComparer.Ordinal);

    /// <summary>本会话已注入过素材提示的章节（预取或 harness 捎带，二者互斥）。</summary>
    private string? _hintInjectedChapter;

    /// <summary>
    /// 双速自进化：最近一次注入战术块的 novelId#chapter（同章重写不重复注入）。
    /// 单写者（同 <see cref="_scheduler"/> 的会话假设），无需锁。
    /// </summary>
    private string? _lastInjectChapterKey;

    /// <summary>双速自进化：会话注入计数（≤ MaxInjectPerSession；单写者，无需锁）。</summary>
    private int _sessionInjects;

    /// <summary>C-4：余额预警是否已注入（每会话 1 条）。</summary>
    private bool _usageLowWarned;

    /// <summary>C-5：额度耗尽提示是否已注入（每会话 1 条）。</summary>
    private bool _usageLimitWarned;

    /// <summary>小说存储（实例方法体内直接可用主构造参数，辅助方法需字段）。</summary>
    private readonly INovelStore _store = store;

    /// <summary>
    /// 工具调用前钩子（直接允许执行）。
    /// </summary>
    public Task<ToolCallBeforeResult> BeforeToolCallAsync(
        ToolHookContext context,
        CancellationToken ct
    ) => Task.FromResult(ToolCallBeforeResult.Allow);

    /// <summary>
    /// 工具调用后钩子：写入类工具成功后注入写作流程信号。
    /// </summary>
    public async Task AfterToolCallAsync(ToolHookContext context, CancellationToken ct)
    {
        if (context.Result is null || context.Result.IsError)
            return;

        // §7.1 M-1：ask_user 属人工介入，重置单调计数（DECISION 由 bridge 内部重置）
        if (context.ToolName == "ask_user")
            _monotony?.NotifyIntervention();

        if (!WriteTools.Contains(context.ToolName))
            return;

        var novelId = agent.CurrentNovelId;
        if (string.IsNullOrEmpty(novelId))
            return;

        // §16.5 方案 C（2026-08-28 产品决策）：会话边界 = 宿主进程生命周期。
        // 小说切换不结算（多本小说间切换是同一创作会话的常态，不应重置配额/刷额度）；
        // 结算由宿主退出（Host/Program、Desktop Shutdown）与跨会话自愈（GetOrCreate 回源）承担。
        try
        {
            var novel = await _store.GetNovel(novelId, ct);
            if (novel is null)
                return;

            var profile = WritingProfile.FromNovel(novel);
            if (!profile.AutoDiagnose)
                return;

            // 通过 IChapterWriteResult 接口强类型提取，无需反射
            if (context.Result.Details is not IChapterWriteResult chapterResult)
                return;

            // 从 store 获取章节标题和内容
            var chapters = await _store.GetChapters(novelId, ct);
            var chapter = chapters.FirstOrDefault(c => c.Id == chapterResult.Id);
            var title = chapter?.Title ?? "";

            // 本地轻量扫描提前（供 L1 判定 / I4 平坦记录 / 本地降级信号三处共用）
            double aiBuzzwordDensity = 0;
            string? hookType = null;
            var activeInfoGaps = 0;
            if (chapter is not null)
            {
                aiBuzzwordDensity = WritingProfile.ScanAiBuzzwordDensity(chapter.Content);
                hookType = WritingProfile.ScanChapterHookType(
                    chapter.Content ?? "",
                    profile.GenreProfile
                );
                activeInfoGaps = WritingProfile.ScanInfoAsymmetryCount(chapter.Content ?? "");
            }

            // ── HARNESS-CLOUD I4 组件 E：单调检测记录（每次写入必记）──
            _monotony?.RecordChapter(hookType is null && activeInfoGaps == 0);

            // ── HARNESS-CLOUD E2：三层调度漏斗（L1 五指标 → L2 三事件 → 基础档 → 深度档）──
            if (_scheduler is not null && _options is { Enabled: true } && chapter is not null)
            {
                var previous = chapters
                    .Where(c => c.Order == chapter.Order - 1)
                    .Select(c => c.Content)
                    .FirstOrDefault();
                var priorAppeared = await LoadPriorAppearedIdsAsync(
                    novelId,
                    chapters,
                    chapter.Order,
                    ct
                );
                var input = new DiagnosisInput
                {
                    NovelId = novelId,
                    ChapterId = chapterResult.Id,
                    ChapterTitle = title,
                    ChapterOrder = chapter.Order,
                    CurrentContent = chapter.Content ?? "",
                    IsRewrite = context.ToolName == "rewrite_selection",
                    Chapters = chapters,
                    Novel = novel,
                    PreviousContent = previous,
                    PriorAppearedCharacterIds = priorAppeared,
                    SnapshotStale =
                        _snapshotCache?.IsStale(novelId, chapters.Max(c => c.Order), chapters.Count)
                        ?? false,
                    // P3 §16.4：SceneGoal 自动填充
                    SceneGoal = ResolveSceneGoal(novel, chapter),
                };

                // 本体 harness：硬偏差（Constraint 规则/死亡角色）优先本地修正，本轮不花云端积分。
                if (_options is { OntologyHarnessEnabled: true })
                {
                    var harness = await OntologyHarnessEvaluator.EvaluateAsync(
                        _store,
                        novel,
                        chapter,
                        _options,
                        ct
                    );
                    if (harness.BlocksCloud && harness.Render.Length > 0)
                    {
                        agent.InjectQueue.Enqueue(
                            new LlmChatMessage(ChatMessageRoles.User, harness.Render)
                        );
                        return;
                    }
                }

                // 主动提示：写完一章先取一次素材参考（同章只扣一次费；超时静默跳过）。
                // 必须在 input 构造之后、scheduler 调用之前，注入才能赶在同一轮 LLM 调用前。
                var hintKey = $"{novelId}#{chapter.Order}";
                if (
                    _options is { MaterialHintsEnabled: true }
                    && _knowledge is not null
                    && _hintCheckedChapters.Add(hintKey)
                )
                {
                    var prompt = await TryFetchMaterialHintsAsync(input, chapter, ct);
                    if (!string.IsNullOrWhiteSpace(prompt) && _hintInjectedChapter != hintKey)
                    {
                        agent.InjectQueue.Enqueue(
                            new LlmChatMessage(
                                ChatMessageRoles.User,
                                $"{SignalBuilder.Prefix} 素材参考（可改写，可忽略）\n{prompt}"
                            )
                        );
                        _hintInjectedChapter = hintKey;
                    }
                }

                var decision = _scheduler.Decide(input);
                if (!decision.ShouldRun)
                {
                    // §3.1：两层未命中 → 注入一行跳过说明，让 LLM 在信息充分下自主决策
                    agent.InjectQueue.Enqueue(
                        new LlmChatMessage(
                            ChatMessageRoles.User,
                            $"{SignalBuilder.Prefix} {decision.SkipReason}"
                        )
                    );
                    // 跳过轮 = 自然停顿点，可触发灵感（§7.2）
                    await TryInspireAsync(novelId, novel, chapters, ct: ct);
                    return;
                }

                // Phase 5.1：调度命中才构图（本地集合读取；本地 SQLite，成本可接受）——
                // scene.plan/story.check 的计划符合度/连续性/设定接地依赖它；跳过轮不构建。
                input = input with
                {
                    SetupDigest = await BuildSetupDigestAsync(novel, novelId, ct),
                };

                var round = await _scheduler.RunAsync(input, decision, ct);

                // §3.4 预算告警（IMP-1 修复）：深度预算真正耗尽（会话配额 + 每小说池/银行均无可续）
                // 时注入一次性提示（每会话 1 条），其余状态沿用降级链语义。
                var budgetExhausted =
                    _scheduler.CheckBudget(input.NovelId).Exhausted
                    && _scheduler.TryConsumeBudgetWarning();

                // C-4：低频用量对账（每 N 章；预算告警当轮强制刷新一次）——为预算告警附服务端余额、
                // 余额低于阈值时一次性预警。失败静默（null），不影响本轮诊断。
                var usageSnapshot = _usageReconciler is not null
                    ? await _usageReconciler.TryFetchAsync(
                        input.ChapterOrder,
                        force: budgetExhausted,
                        ct
                    )
                    : null;

                if (budgetExhausted)
                {
                    agent.InjectQueue.Enqueue(
                        new LlmChatMessage(
                            ChatMessageRoles.User,
                            $"{SignalBuilder.Prefix} 深度诊断本地预算已耗尽（会话内 "
                                + $"{_options?.DeepBudgetPerSession ?? 100} 次且小说预算池/银行均无余量）；"
                                + "云端积分未变，重置本地预算后恢复深度诊断。"
                                + (
                                    usageSnapshot is { } balance
                                        ? $"（服务端本月剩余 {balance.RemainingUnits} 单位）"
                                        : ""
                                )
                        )
                    );
                }

                // C-5：额度耗尽与服务不可用分流——本轮出现 402 usage_limit_exceeded 时一次性可见提示
                if (!_usageLimitWarned && _scheduler.LastDegradeCode == "usage_limit_exceeded")
                {
                    _usageLimitWarned = true;
                    _usageLowWarned = true; // 额度耗尽本轮不再重复余额预警
                    agent.InjectQueue.Enqueue(
                        new LlmChatMessage(
                            ChatMessageRoles.User,
                            $"{SignalBuilder.Prefix} 云端积分已耗尽（本月 Cloud Writing Credits 用尽），"
                                + "所有云端调用（基础与深度）暂停；本地写作与本地信号不受影响，新账期或充值后自动恢复。"
                        )
                    );
                }

                // C-4：余额预警（每会话 1 条）：服务端剩余额度 ≤ 阈值时提示（基础档与深度档共用额度）
                if (
                    !_usageLowWarned
                    && usageSnapshot is { } usageNow
                    && usageNow.RemainingUnits <= (_options?.UsageLowRemainingThreshold ?? 100)
                )
                {
                    _usageLowWarned = true;
                    agent.InjectQueue.Enqueue(
                        new LlmChatMessage(
                            ChatMessageRoles.User,
                            $"{SignalBuilder.Prefix} 云端额度预警：本月剩余 {usageNow.RemainingUnits}/"
                                + $"{usageNow.IncludedUnits} 单位（已用 {usageNow.UsedUnits}，在途 {usageNow.ReservedUnits}）；"
                                + "额度耗尽后所有云端调用（基础与深度）都会暂停。"
                        )
                    );
                }

                if (round is not null)
                {
                    // 规则评估提前到本轮注入前一次完成：既有诊断注入与双速自进化
                    // （结算/记录/战术注入）共用同一结论，避免二次评估（BuildLocalFacts 含章节扫描）。
                    var ruleOutcome = SignalRules.Evaluate(
                        round.ScenePlan,
                        round.ContextRetrieve,
                        round.StoryCheck,
                        BuildLocalFacts(round, input)
                    );

                    var hadActionOrDecision = await InjectCloudRoundAsync(
                        round,
                        input,
                        ruleOutcome,
                        ct
                    );

                    // ── 双速自进化（单账本折叠）：结算 → 记录 → 战术注入（默认关闭，失败静默）──
                    // 置于主诊断注入之后：DECISION 等待中被取消（OCE 上抛）时跳过进化写入。
                    await TryApplyEvolutionAsync(
                        novelId,
                        input.ChapterOrder,
                        profile.Genre,
                        round,
                        ruleOutcome,
                        ct
                    );

                    if (!hadActionOrDecision)
                    {
                        // PASS 轮自然停顿：优先用云端 features.absentCharacters（Q13）作灵感候选
                        await TryInspireAsync(
                            novelId,
                            novel,
                            chapters,
                            round.ContextRetrieve?.AbsentCharacters,
                            ct
                        );
                    }
                    return; // 云端成功 → 不重复注入本地信号（§4.1）
                }
            } // 云端 if 块闭合：round == null → 落入下方本地降级路径

            // ── 本地降级路径（HARNESS-CLOUD §0.2-4：云端不可用时 = 原行为）──
            // 复用上方已计算的 aiBuzzwordDensity / hookType / activeInfoGaps
            var signal = profile.BuildSignalMessage(
                title,
                chapterResult.WordCount,
                "",
                aiBuzzwordDensity,
                hookType,
                activeInfoGaps
            );
            if (!string.IsNullOrEmpty(signal))
            {
                // ★ P0d: Hook 信号改走 InjectQueue（系统生成，非用户意图，静默注入不渲染为 👤 气泡）
                agent.InjectQueue.Enqueue(new LlmChatMessage(ChatMessageRoles.User, signal));
            }

            // 本地降级轮也是自然停顿点（无 ACTION 时）
            await TryInspireAsync(novelId, novel, chapters, ct: ct);
        }
        catch (OperationCanceledException)
        {
            // §12 红线 5：用户取消必须立即生效，向上传播给 Loop 的中断路径
            throw;
        }
        catch
        {
            // 信号注入失败不应阻塞工具调用流程
        }
    }

    /// <summary>
    /// Phase 5.1：构建设定图摘要（与 check_setup 工具同款的集合加载）——scene.plan/story.check
    /// 请求的 Context.Setup 来源。本地读取失败 → null（宁可不挂：云端按缺数据处理并静默降级，
    /// 不阻断本轮诊断）；用户取消按 §12 红线 5 上抛。
    /// </summary>
    private async Task<SetupCheckInput?> BuildSetupDigestAsync(
        Novel novel,
        string novelId,
        CancellationToken ct
    )
    {
        try
        {
            var characters = await _store.GetCharacters(novelId, ct);
            var factions = await _store.GetFactions(novelId, ct);
            var relationships = await _store.GetRelationshipsByNovel(novelId, ct);
            var scenes = await _store.GetScenesByNovel(novelId, ct);
            var plotlines = await _store.GetPlotlines(novelId, ct);
            var chapters = await _store.GetChapters(novelId, ct);
            var timelineEvents = await _store.GetTimelineEvents(novelId, ct);
            var locations = await _store.GetLocations(novelId, ct);
            var items = await _store.GetItems(novelId, ct);
            var worldRules = await _store.GetWorldRules(novelId, ct);
            return SetupDigestBuilder.Build(
                novel,
                characters,
                factions,
                relationships,
                scenes,
                plotlines,
                chapters,
                timelineEvents,
                locations,
                items,
                worldRules
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// P3 §16.4：SceneGoal 自动填充（优先级递减）：
    /// 1. 当前章是某 plotline 的计划回收章 → 「回收伏笔：{Name}」
    /// 2. LLM 上一轮输出（最近 Assistant 消息）中提取「下章/接下来…」目标
    /// 3. 用户输入（最近 User 消息）中提取同类目标
    /// 4. null（兜底，走云端 run.Goal）
    /// </summary>
    private string? ResolveSceneGoal(Novel novel, Chapter chapter)
    {
        // 优先级1：当前章是某 plotline 的计划回收章
        var matchingPlotline = novel.Plotlines.FirstOrDefault(p =>
            p.ResolvedAtChapterId is null
            && p.PlannedRevealChapterId is not null
            && p.PlannedRevealChapterId == chapter.Id
        );
        if (matchingPlotline is not null)
            return $"回收伏笔：{matchingPlotline.Name}";

        // 优先级2：LLM 上一轮输出提取（最近 Assistant 消息，排除 tool 结果）
        var assistantGoal = agent
            .Messages.LastOrDefault(m =>
                m.Role == ChatMessageRoles.Assistant && !string.IsNullOrWhiteSpace(m.Content)
            )
            ?.Content;
        if (ExtractSceneGoal(assistantGoal) is { } fromAssistant)
            return fromAssistant;

        // 优先级3：用户输入指令提取（最近 User 消息）
        var userGoal = agent
            .Messages.LastOrDefault(m =>
                m.Role == ChatMessageRoles.User && !string.IsNullOrWhiteSpace(m.Content)
            )
            ?.Content;
        if (ExtractSceneGoal(userGoal) is { } fromUser)
            return fromUser;

        return null;
    }

    /// <summary>
    /// 从自由文本中提取「下章/接下来…」形式的创作目标（§16.4 优先级 2/3）。
    /// 启发式：定位目标标记词后的短句（截到句号/换行，限 60 字）；无标记返回 null。
    /// </summary>
    internal static string? ExtractSceneGoal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var markers = new[] { "下章", "下一章", "接下来" };
        foreach (var marker in markers)
        {
            var idx = text.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0)
                continue;
            var slice = text[idx..];
            var end = slice.IndexOfAny(['。', '！', '？', '；', '\n', '\r']);
            var sentence = end > 0 ? slice[..end] : slice;
            if (sentence.Length > 60)
                sentence = sentence[..60];
            if (sentence.Length >= 6)
                return sentence.Trim();
        }
        return null;
    }

    /// <summary>
    /// 主动提示：调 knowledge.hint 取 prompt-block。超时/失败/非 ok 全部静默返回 null；
    /// 外层用户取消仍上抛（§12 红线 5）。不记录正文，只依赖服务端长度日志。
    /// </summary>
    private async Task<string?> TryFetchMaterialHintsAsync(
        DiagnosisInput input,
        Chapter chapter,
        CancellationToken ct
    )
    {
        if (_knowledge is null || _options is not { MaterialHintsEnabled: true })
        {
            return null;
        }
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.MaterialHintTimeoutMs);
            var content = chapter.Content ?? "";
            if (content.Length > 2000)
            {
                content = content[^2000..];
            }
            var json = await _knowledge.HintAsync(
                new
                {
                    sceneGoal = input.SceneGoal ?? chapter.Title,
                    currentText = content,
                    genre = WritingProfile.FromNovel(input.Novel).Genre,
                    limit = 5,
                    maxChars = _options.MaterialHintMaxChars,
                },
                cts.Token
            );
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var status = root.TryGetProperty("status", out var statusEl)
                ? statusEl.GetString()
                : null;
            var prompt = root.TryGetProperty("prompt-block", out var promptEl)
                ? promptEl.GetString()
                : null;
            return string.Equals(status, "ok", StringComparison.Ordinal) ? prompt : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 云端一轮结果分发（§4.1）。返回 true = 本轮含 ACTION 或 DECISION 处理
    /// （I4 灵感互斥依据）；false = PASS 轮。DECISION 介入后重置单调计数。
    /// </summary>
    private async Task<bool> InjectCloudRoundAsync(
        CloudRound round,
        DiagnosisInput input,
        RuleOutcome ruleOutcome,
        CancellationToken ct
    )
    {
        if (
            round.ScenePlan is { RequiresHumanDecision: true } decisionOutcome
            && _decisionBridge is { } bridge
        )
        {
            // P3 DECISION 前置过滤（§16.2）：角色完整性 + 小说早期（阈值可配，U-3）
            var skipReason = HumanDecisionBridge.ShouldSkipDecision(
                decisionOutcome,
                input.Novel.Characters.Count,
                input.ChapterOrder,
                bridge.DecisionHistory,
                _options?.DecisionMinCharacters ?? 3,
                _options?.DecisionMinChapterOrder ?? 5
            );
            if (skipReason is not null)
            {
                // 频率上限由调度器 TryConsumeDecisionSlot() 在 Decide 前控制
                agent.InjectQueue.Enqueue(
                    new LlmChatMessage(
                        ChatMessageRoles.User,
                        $"{SignalBuilder.Prefix} 场景规划建议继续自动迭代（{skipReason}）"
                    )
                );
                _monotony?.NotifyIntervention();
                return true; // 视为已处理（避免叠加灵感）
            }

            // P3 §16.2 频率红线：每会话最多 DecisionMaxPerSession 次 DECISION
            if (_scheduler is not null && !_scheduler.TryConsumeDecisionSlot())
            {
                agent.InjectQueue.Enqueue(
                    new LlmChatMessage(
                        ChatMessageRoles.User,
                        $"{SignalBuilder.Prefix} 本轮DECISION已达会话上限（{_scheduler.GetType().Name}），按建议继续自动迭代"
                    )
                );
                _monotony?.NotifyIntervention();
                return true;
            }

            var verdict = await bridge.DecideAsync(decisionOutcome, ct, input.NovelId);
            if (!string.IsNullOrEmpty(verdict))
                agent.InjectQueue.Enqueue(new LlmChatMessage(ChatMessageRoles.User, verdict));
            _monotony?.NotifyIntervention();
            return true;
        }

        if (round.ScenePlan is { RequiresHumanDecision: true })
            return true; // 无 bridge 可用：保守视为已介入，不触发灵感

        // 层 1 采样门槛（跨仓库）：引擎/能力层判定样本不可测（实质字符不足）→ 不产出评分，
        // 不触发修复/人工裁决；注入一行跳过说明，避免落入 PASS 文案（“综合=0 可按计划推进”）。
        if (round.ScenePlan is { RepairAction: CloudContractLiterals.RepairInsufficientData })
        {
            agent.InjectQueue.Enqueue(
                new LlmChatMessage(
                    ChatMessageRoles.User,
                    $"{SignalBuilder.Prefix} 本轮跳过（当前章实质性内容不足，未产出评分；继续写作后下一轮自动恢复诊断）"
                )
            );
            return false; // 无 ACTION/DECISION → 不阻断灵感互斥语义
        }

        // I4 M-3（P2 云端增强）：喂入「爽感」维度分供单调递减判定
        if (_monotony is not null && round.ContextRetrieve is not null)
        {
            var has = round.ContextRetrieve.DimensionScores.TryGetValue(
                "爽感",
                out var satisfaction
            );
            _monotony.RecordSatisfactionScore(has ? satisfaction : null);
        }

        var hasAction = ruleOutcome.Actions.Count > 0;
        var text = hasAction
            ? SignalBuilder.FormatAction(round.OverallScore, ruleOutcome)
            : $"{SignalBuilder.Prefix} status=pass 综合={round.OverallScore} 可按计划推进（本轮免重复诊断）";

        if (round.DeepDenied)
            text += "\n备注: 深度诊断被拒（凭据/权限问题），本轮仅基础信号";
        if (round.BudgetOverrun)
            text += "\n备注: 预算超额——后半段强制深度检查，本次超出会话预算配额（§16.5 方案 A）";
        if (round.DeepUnits > 0)
            text += $"\n云端消耗: {round.DeepUnits} units（story.check 实际结算）";

        // R-3：快照数据年龄可见化——刷新失败静默沿用旧值时，信号边界声明数据年龄（§6.2）
        if (
            _snapshotCache?.TryGet(input.NovelId) is { } snapshotEntry
            && input.ChapterOrder - snapshotEntry.FetchedAtChapterOrder
                >= ContextSnapshotCache.StaleOrderThreshold
        )
        {
            var age = input.ChapterOrder - snapshotEntry.FetchedAtChapterOrder;
            text += $"\n边界: 快照数据年龄 {age} 章（上次刷新未成功，沿用旧值）";
        }

        // harness 捎带：预取未成功时，context.retrieve 的 material-hints 兜底注入（同章只一次）。
        var hintKey = $"{input.NovelId}#{input.ChapterOrder}";
        if (
            _hintInjectedChapter != hintKey
            && round.ContextRetrieve?.MaterialHints is { Status: "ok" } materialHints
            && !string.IsNullOrWhiteSpace(materialHints.PromptBlock)
        )
        {
            text +=
                "\n\n"
                + SignalBuilder.Prefix
                + " 素材参考（可改写，可忽略）\n"
                + materialHints.PromptBlock;
            _hintInjectedChapter = hintKey;
        }

        agent.InjectQueue.Enqueue(new LlmChatMessage(ChatMessageRoles.User, text));
        return hasAction;
    }

    /// <summary>历史验证战术块字符预算（含首行；至少保留 1 条战术行）。</summary>
    private const int TacticBlockCharBudget = 1200;

    /// <summary>
    /// 双速自进化（单账本折叠架构）：一次调用完成 结算（本小说早于本章的待结算注入）→
    /// 记录（本章 diagnose）→ 注入（历史验证战术块）。
    /// 门控（短路即返回）：账本缺失 / 未启用 / 停机开关任一即零写入与零注入；
    /// 注入另有「本章已注入过」「会话注入计数达上限」「无候选」三个短路。
    /// 任何异常静默（对齐本文件既有降级红线：信号注入失败不应阻塞工具调用流程）；
    /// 用户取消按 §12 红线 5 上抛。
    /// </summary>
    private async Task TryApplyEvolutionAsync(
        string novelId,
        int chapterOrder,
        string genre,
        CloudRound round,
        RuleOutcome ruleOutcome,
        CancellationToken ct
    )
    {
        var ledger = _ledger;
        if (ledger is null || _options is not { EvolutionEnabled: true } || ledger.StopRequested)
            return;

        try
        {
            // verdict 优先级：Decision > Action > Pass；insufficient_data 轮无判定（不写）。
            string? verdict =
                round.ScenePlan is { RequiresHumanDecision: true } ? "decision"
                : round.ScenePlan is { RepairAction: CloudContractLiterals.RepairInsufficientData }
                    ? null
                : ruleOutcome.Actions.Count > 0 ? "action"
                : "pass";

            // ── 1. 种子：首次见到的行动项写入 learn（处方取首次观测文本，后续不再写） ──
            foreach (var action in ruleOutcome.Actions)
            {
                var tacticId = TacticLedger.IdFor(action.Code, genre);
                if (ledger.Find(tacticId) is not null)
                    continue;
                await ledger.LearnAsync(
                    new TacticStat(
                        tacticId,
                        action.Code,
                        genre,
                        action.Prescription,
                        action.Boundary
                    ),
                    ct
                );
            }

            // ── 2. 结算：本小说此前注入、且早于本章的待结算项一次结清 ──
            // 无论结果如何都出队——晚到章节不再回溯归因，避免错误归因（断更缺口 abstain）。
            foreach (
                var pending in ledger
                    .PendingInjections.Where(p => p.NovelId == novelId && p.Chapter < chapterOrder)
                    .ToList()
            )
            {
                if (ledger.Find(pending.TacticId) is { } tactic)
                {
                    if (ruleOutcome.Actions.Any(a => a.Code == pending.Trigger))
                        await ledger.VerdictAsync(novelId, chapterOrder, tactic, pass: false, ct);
                    else if (verdict == "pass")
                        await ledger.VerdictAsync(novelId, chapterOrder, tactic, pass: true, ct);
                }
                ledger.RemovePending(pending.TacticId);
            }

            // ── 3. 记录本章：diagnose 事件（同章重写会再写一条，折叠与课程 CLI 均按后写覆盖） ──
            if (verdict is not null)
            {
                await ledger.DiagnoseAsync(
                    novelId,
                    chapterOrder,
                    genre,
                    verdict,
                    ruleOutcome.Actions.Select(a => a.Code).ToList(),
                    CollectDimensions(round),
                    ct
                );
            }

            // ── 4. 注入：历史验证战术块（每章至多一次、会话封顶、字符预算） ──
            if (_lastInjectChapterKey == $"{novelId}#{chapterOrder}")
                return;
            if (_sessionInjects >= (_options?.MaxInjectPerSession ?? 20))
                return;
            var picks = ledger.Select(ruleOutcome.Actions.Select(a => a.Code), genre, novelId);
            if (picks.Count == 0)
                return;

            var (block, included) = BuildTacticBlock(picks);
            agent.InjectQueue.Enqueue(new LlmChatMessage(ChatMessageRoles.User, block));
            foreach (var pick in included)
                await ledger.InjectAsync(novelId, chapterOrder, pick, ct);
            _lastInjectChapterKey = $"{novelId}#{chapterOrder}";
            _sessionInjects++;
        }
        catch (OperationCanceledException)
        {
            // §12 红线 5：用户取消必须立即生效，向上传播给 Loop 的中断路径
            throw;
        }
        catch
        {
            // 双速自进化失败静默：不得阻塞诊断主流程（对齐本文件既有降级红线）
        }
    }

    /// <summary>
    /// diagnose 事件的维度集合：context.retrieve 高危信号维度 ∪ story.check 规则发现维度，
    /// 去重取前 3（两者均为 CloudRound 上现成数据；缺失写空表）。
    /// </summary>
    private static List<string> CollectDimensions(CloudRound round)
    {
        var dims = new List<string>();
        if (round.ContextRetrieve is { } contextRetrieve)
            dims.AddRange(
                contextRetrieve
                    .Signals.Where(s =>
                        SignalRules.NormalizeSeverity(s.Severity)
                        == CloudContractLiterals.SeverityHigh
                    )
                    .Select(s => s.Dimension)
            );
        if (round.StoryCheck is { } storyCheck)
            dims.AddRange(storyCheck.RuleFindings.Select(f => f.Dimension));
        return dims.Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToList();
    }

    /// <summary>
    /// 历史验证战术块：首行固定「[写作战术 · 历史验证]」，逐行累加至字符预算
    /// （至少保留 1 条，避免全空块）。返回块文本与最终入选的战术（供 inject 记账）。
    /// </summary>
    private static (string Text, List<TacticStat> Included) BuildTacticBlock(
        IReadOnlyList<TacticStat> picks
    )
    {
        var lines = new List<string> { "[写作战术 · 历史验证]" };
        var included = new List<TacticStat>();
        var total = lines[0].Length;
        foreach (var pick in picks)
        {
            var line =
                $"- [{pick.Trigger} · 胜率 {pick.WinRate * 100:F0}% · {pick.Uses} 次] {pick.Prescription}";
            if (included.Count > 0 && total + line.Length + 1 > TacticBlockCharBudget)
                break;
            lines.Add(line);
            included.Add(pick);
            total += line.Length + 1;
        }
        return (string.Join("\n", lines), included);
    }

    /// <summary>
    /// I4 灵感供给（§7.2）：自然停顿点 + 双信号到期 + 配额未耗尽 + 无待决 DECISION →
    /// 注入低强度 [风格观察] 灵感信号。候选来自本地配角缺席扫描与最沉睡暗线。
    /// </summary>
    private async Task TryInspireAsync(
        string novelId,
        Novel novel,
        IReadOnlyList<Chapter> chapters,
        IReadOnlyList<string>? cloudAbsent = null,
        CancellationToken ct = default
    )
    {
        if (_monotony is null)
            return;
        // 证据快照必须在 TryBeginInspiration（成功即重置计数）之前取，
        // 否则 M-1/平坦窗口恒为 0（§7.2 模板要求携带真实计数）。
        var (autoAdvances, flatCount) = _monotony.SnapshotCounters();
        if (!_monotony.TryBeginInspiration())
            return;

        // §7.2：store 有待决 DECISION 问题时不叠加（注意力预算唯一）
        if (
            _questionStore is not null
            && _questionStore.GetPending().Any(q => q.Header == "创作决策")
        )
            return;

        // 候选③：最沉睡暗线（未解决中计划揭示最早的一条；无计划则取首个）
        string? dormant = null;
        var openWithPlan = novel
            .Plotlines.Where(p => p.ResolvedAtChapterId is null)
            .OrderBy(p => orderOfPlanned(p))
            .FirstOrDefault();
        if (openWithPlan is not null)
            dormant = openWithPlan.Name;

        // §7.2/MED-1 修复：方向①缺席配角候选——优先使用云端 features.absentCharacters
        // （Q13：云端由 novel_features character_appearances 全书序列推导），缺失时回退
        // 本地权威出场记录（ENTITY-EVOLUTION §5 精确版），无记录数据再回退最近 K 章正文
        // 出场计数（纯字符串包含轻量启发式，§3.2 允许级别）。
        var absentSupporting = cloudAbsent is { Count: > 0 }
            ? cloudAbsent
            : await FindAbsentSupportingFromRecordsAsync(novel, chapters, ct)
                ?? FindAbsentSupportingCandidates(
                    novel,
                    chapters,
                    Math.Max(1, _options?.MonotonyFlatWindow ?? 5)
                );

        agent.InjectQueue.Enqueue(
            new LlmChatMessage(
                ChatMessageRoles.User,
                _monotony.BuildInspirationMessage(
                    autoAdvances,
                    flatCount,
                    absentSupporting,
                    dormant
                )
            )
        );

        int? orderOfPlanned(Plotline p) =>
            p.PlannedRevealChapterId is not null
            && chapters.FirstOrDefault(c => c.Id == p.PlannedRevealChapterId) is { } ch
                ? ch.Order
                : null;
    }

    /// <summary>从输入组装规则引擎本地事实（§4.2 A-3/A-4/A-5 数据源）。internal 供熵差接线测试。</summary>
    internal LocalFacts BuildLocalFacts(CloudRound round, DiagnosisInput input)
    {
        var protagonist =
            input.Novel.Characters.FirstOrDefault(c => c.Role == CharacterRole.Protagonist)?.Name
            ?? "";
        var orderById = input.Chapters.ToDictionary(c => c.Id, c => c.Order);
        var orderedChapters = input.Chapters.OrderBy(c => c.Order).ToList();
        return new LocalFacts
        {
            ProtagonistName = protagonist,
            CurrentOrder = input.ChapterOrder,
            MaxChapterOrder = input.Chapters.Count == 0 ? 0 : input.Chapters.Max(c => c.Order),
            OpenPlotlines = input
                .Novel.Plotlines.Where(p => p.ResolvedAtChapterId is null)
                .Select(p => new PlotlineFact(
                    p.Name,
                    p.PlannedRevealChapterId is not null
                    && orderById.TryGetValue(p.PlannedRevealChapterId, out var order)
                        ? order
                        : null,
                    // §6.3 埋设章龄锚点：伏笔名在正文首次出现的章序（无计划揭示章时
                    // 供 A-3「埋设超龄」判定；字符串扫描近似，仅作触发输入，非诊断结论）。
                    FirstAppearOrder: FindFirstAppearOrder(p.Name, orderedChapters)
                ))
                .ToList(),
            ActiveInfoGaps = WritingProfile.ScanInfoAsymmetryCount(input.CurrentContent),
            // IMP-4 修复：熵差来自快照缓存（§6.2 H(t) − H(t−1) 纯减法近似），
            // 供 A-4 暗线插入时机分支（熵升埋线 / 熵降收线）消费。
            EntropyDelta = _snapshotCache?.TryGet(input.NovelId)?.NarrativeEntropyDelta,
        };
    }

    /// <summary>
    /// 伏笔名在已排序章节正文中首次出现的章 Order（A-3 埋设章龄锚点；§6.3）。
    /// 字符串包含扫描（§3.2 允许的轻量启发式级别），null = 伏笔名未在任何章节出现。
    /// </summary>
    internal static int? FindFirstAppearOrder(string? plotlineName, IReadOnlyList<Chapter> chapters)
    {
        if (string.IsNullOrWhiteSpace(plotlineName))
            return null;
        foreach (var chapter in chapters)
        {
            if (chapter.Content?.Contains(plotlineName, StringComparison.Ordinal) == true)
                return chapter.Order;
        }
        return null;
    }

    /// <summary>
    /// §7.2 灵感方向①候选（MED-1 修复）：最近 K 章未出场的配角名（≤2）。
    /// 纯最近 K 章正文出场计数（§3.2 允许的轻量启发式），只供灵感文本引用，
    /// 不产出「感知衰减」类诊断结论（§12-12 红线：本地禁止近似深度诊断）。
    /// internal 供灵感候选接线测试。
    /// </summary>
    internal static IReadOnlyList<string> FindAbsentSupportingCandidates(
        Novel novel,
        IReadOnlyList<Chapter> chapters,
        int window
    )
    {
        if (window <= 0 || chapters.Count == 0)
            return [];

        var ordered = chapters.OrderBy(c => c.Order).ToList();
        var recent = ordered.TakeLast(window);
        var recentText = string.Join("\n", recent.Select(c => c.Content ?? ""));
        var earlierText = string.Join(
            "\n",
            ordered.Take(Math.Max(0, ordered.Count - window)).Select(c => c.Content ?? "")
        );

        return novel
            .Characters.Where(c =>
                c.Role == CharacterRole.Supporting && !string.IsNullOrWhiteSpace(c.Name)
            )
            .Where(c =>
                earlierText.Contains(c.Name!, StringComparison.Ordinal)
                && !recentText.Contains(c.Name!, StringComparison.Ordinal)
            )
            .Take(2)
            .Select(c => c.Name!)
            .ToList();
    }

    /// <summary>
    /// 从权威出场记录加载「当前章之前已出场角色 ID 集」（ENTITY-EVOLUTION §5.2）。
    /// 返回 null = 无记录数据（未启用 L0 打底）→ 调用方回退正文扫描。
    /// </summary>
    private async Task<IReadOnlySet<string>?> LoadPriorAppearedIdsAsync(
        string novelId,
        IReadOnlyList<Chapter> chapters,
        int currentChapterOrder,
        CancellationToken ct
    )
    {
        try
        {
            var view = await EntityTimelineView.BuildAsync(_store, novelId, ct);
            if (view.AllAppearances.Count == 0)
                return null;

            var orderById = chapters.Where(c => c.Order > 0).ToDictionary(c => c.Id, c => c.Order);
            if (orderById.Count == 0)
                return null;

            var prior = view
                .AllAppearances.Where(r =>
                    r.EntityType == EntityType.Character
                    && orderById.TryGetValue(r.ChapterId, out var o)
                    && o < currentChapterOrder
                )
                .Select(r => r.EntityId)
                .ToHashSet();
            return prior;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null; // 记录查询失败 → 回退扫描（不阻断诊断主流程）
        }
    }

    /// <summary>
    /// 从本地权威出场记录查近窗缺席配角（ENTITY-EVOLUTION §5.2 精确版）。
    /// 返回 null 表示小说尚无出场记录数据（未启用 L0 打底）→ 调用方回退字符串扫描。
    /// </summary>
    private async Task<IReadOnlyList<string>?> FindAbsentSupportingFromRecordsAsync(
        Novel novel,
        IReadOnlyList<Chapter> chapters,
        CancellationToken ct
    )
    {
        try
        {
            var view = await EntityTimelineView.BuildAsync(_store, novel.Id, ct);
            if (view.AllAppearances.Count == 0)
                return null; // 无 L0/AI 记录 → 回退扫描

            var chapterOrderMap = chapters
                .Where(c => c.Order > 0)
                .ToDictionary(c => c.Id, c => c.Order);
            if (chapterOrderMap.Count == 0)
                return null;

            var supporting = novel
                .Characters.Where(c => c.Role == CharacterRole.Supporting)
                .ToList();
            var latestOrder = chapterOrderMap.Values.Max();

            return view.FindAbsentSupporting(
                supporting,
                chapterOrderMap,
                latestOrder,
                Math.Max(1, _options?.MonotonyFlatWindow ?? 5)
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null; // 记录查询失败 → 回退扫描（信号注入不阻断主流程）
        }
    }
}
