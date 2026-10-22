using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Core.Evolution;
using Core.Inference;
using Core.Infrastructure;
using Core.Prompts;
using Core.Shared;
using Core.Stability;
using Core.Tools;
using LlmTornado;
using LlmTornado.Chat;
using LlmTornado.Chat.Vendors.Anthropic;
using LlmTornado.ChatFunctions;
using LlmTornado.Code;
using LlmTornado.Common;
using Microsoft.Extensions.Logging;

namespace Core.Agent;

/// <summary>循环以可恢复原因停止的记录（非异常路径）。Code 取值：empty_turn / tool_calls_not_continued / stability_tripped。</summary>
public sealed record LoopStop(string Code, string Message);

/// <summary>
/// 基于 LlmTornado Conversation 的 Agent Loop 实现。
///
/// 核心设计原则：
/// 1. Conversation 是一等公民 — 一个 session 对应一个 Conversation
/// 2. Tool 执行走 FunctionCallHandler — 在回调中执行 AgentTool
/// 3. Agent Loop = 多次 StreamResponseRich
/// 4. 直接使用 ChatMessage，无需转换
///
/// 负责管理 LLM 对话循环、工具调用、流式输出和消息持久化。
/// </summary>
public sealed class ConversationLoop(
    TornadoApi api,
    string modelName,
    string systemPrompt,
    string thinkingLevel,
    ToolRegistry toolRegistry,
    ToolExecutionMode toolExecution,
    IToolHook? beforeToolCall = null,
    IToolHook? afterToolCall = null,
    Agent? agent = null,
    ILogger? logger = null,
    IStabilityMonitor? stabilityMonitor = null,
    IHolographicEngine? holographicEngine = null,
    IMessageScorer? messageScorer = null,
    IGuardRail? guardRail = null,
    ISystemOneClient? systemOne = null,
    ToolInjector? toolInjector = null,
    Func<
        ChatMessage,
        IReadOnlyList<ChatMessage>,
        List<ChatMessage>,
        CancellationToken,
        Task<bool>
    >? shouldStopAfterTurn = null,
    Func<ChatMessage, string?>? entryIdProvider = null,
    Func<IReadOnlyList<ChatMessage>, CancellationToken, Task>? onMessagesReady = null,
    Func<string, CancellationToken, Task<bool>>? onNewContextRequested = null
) : IAsyncDisposable
{
    private readonly TornadoApi _api = api ?? throw new ArgumentNullException(nameof(api));
    private readonly string _modelName =
        modelName ?? throw new ArgumentNullException(nameof(modelName));
    private string _systemPrompt = systemPrompt; // 可变：UpdateSystemPrompt 需要更新
    private readonly ToolRegistry _toolRegistry =
        toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));

    /// <summary>本地判定模型客户端（可选；`system-one.loop.enabled=false` 时零请求、零影响）。</summary>
    private readonly ISystemOneClient? _systemOne = systemOne;

    /// <summary>本 run 已注入「未完成，继续」提醒的次数（每 run 重置）。</summary>
    private int _stopContinuations;

    /// <summary>本 run 已推翻稳定性断路的次数（每 run 至多一次，防止无限续跑）。</summary>
    private int _stabilityOverrides;

    private string? _lastToolsSignature;

    // Conversation 是 runtime 的 source of truth
    private Conversation? _conversation;

    // 持久化用的消息历史（从 Conversation 同步生成）
    private readonly List<ChatMessage> _persistedMessages = [];

    // 当前迭代的新消息（用于返回值）
    private List<ChatMessage>? _loopNewMessages;

    // 循环控制：是否需要继续 stream
    // volatile: 跨 async 边界读写，需要内存屏障保证可见性
    private volatile bool _continueLoop;

    // A tool can finish the current run while waiting for an explicit user decision.
    private volatile bool _stopAfterTool;

    // 外部控制（shouldStopAfterTurn）终止本轮——区别于异常/未知中断（Fix B 检测用）
    private bool _stoppedByExternalControl;

    // circuit breaker 历史
    private ToolDispatcher.CircularBuffer<ToolDispatcher.ToolCallRecord>? _callHistory;
    private int _turnId;

    // StabilityHarness 组件（通过 Agent 属性注入）
    private int _driftCheckCounter; // 每 N 轮检查一次上下文漂移

    // 本轮 API 调用的完整上下文 token 数（OnUsageReceived 最后一次回调值），
    // 流式结束后写入该轮最后一条 assistant 消息的 Tokens，持久化为 ContextTokens 锚点。
    private int? _lastContextTokens;

    // 上次 API 调用的 PromptTokens。用于计算稳定性监控的本轮增量消耗：
    // 每次调用的 PromptTokens 都包含全部历史上下文，直接累加会把同一段上下文重复计数
    // （实测 8 轮对话累计 201K 撞破 200K 预算，而实际上下文仅 26.5K）。
    // 模型切换/compact 后 prompt 可能缩小，增量允许为负，使 TotalTokens 自然回落。
    private int? _lastPromptTokens;

    // ── B3: ref 稳定（EntryId 锚定）──
    // entryIdProvider 从消息提取稳定身份（TuiApp 提供：LlmChatMessage.NativeObject 存 EntryId）。
    // _refByEntryId 映射 EntryId → ref 编号，_nextMsgRef 全局递增不回收。
    // 压缩/恢复后 EntryId 不变 → ref 跨压缩稳定；无 provider 时退回按序编号（每轮从 0）。
    private readonly Dictionary<string, int> _refByEntryId = [];

    // 每轮消息就绪时的持久化回调（TuiApp 注册，用于及时写入 FileSessionStore，不等 Agent loop 停止）
    private int _nextMsgRef = 1;

    /// <summary>
    /// 模型上下文能力 profile — 所有稳定器动态阈值的来源。
    /// 在模型切换时通过 UpdateModelProfile 更新。
    /// </summary>
    private ModelContextProfile _modelProfile = new();

    /// <summary>工具使用统计（调用次数、batch 占比、断路器触发次数）。</summary>
    public ToolUsageStats UsageStats { get; } = new();

    /// <summary>最近一次运行的循环停止原因（可恢复非异常停止）；null = 自然收尾或循环未创建。</summary>
    public LoopStop? LastStop { get; private set; }

    /// <summary>获取持久化用的消息历史。</summary>
    public IReadOnlyList<ChatMessage> PersistedMessages => _persistedMessages;

    /// <summary>获取底层 Conversation 对象（runtime source of truth）。</summary>
    public Conversation? Conversation => _conversation;

    // ── 上下文接续（页 + 预算提醒）──
    /// <summary>当前上下文页（首页 = ContextWindowState.First）。</summary>
    private ContextWindowState _contextWindow = ContextWindowState.First;

    /// <summary>本页是否已注入过预算提醒（换页 / 恢复会话时重置）。</summary>
    private bool _windowReminderClaimed;

    /// <summary>
    /// 本页是否已自动换过页（防抖：换页失败或监控器未及时回落时不至于连续换空页）。
    /// 换页 / 恢复会话时重置。
    /// </summary>
    private bool _autoRollClaimed;

    /// <summary>
    /// 会话是否已发生过换页 / 恢复过页状态（决定是否注入页片段）。
    /// 一旦为 true 就保持——分支回溯复位到首页时仍要注入片段，让模型知道「本页没有更早的消息，
    /// 需要用 history_* 检索」。
    /// </summary>
    private bool _windowFragmentActive;

    /// <summary>上下文管理选项（null = compact 模式：不注入页片段与预算提醒）。</summary>
    private ContextManagementOptions? _contextManagement;

    /// <summary>本 run 登记的换页原因（"model" | "threshold"）；run 边界执行，跨 run 不残留。</summary>
    private string? _rollAfterRun;

    /// <summary>
    /// 本页最近一次「用户亲口说的请求」（只由 RunAsync 的 userMessages 更新；注入/steer 系统通知不算）。
    /// 预算提醒要「动态感知任务」，靠的就是它——不靠扫消息列表猜（注入通知同样是 user 角色）。
    /// </summary>
    private string? _lastUserRequest;

    /// <summary>
    /// Host 在换页 / 恢复会话后调用：更新页状态、重置提醒并重置 prompt 基线。
    /// </summary>
    public void SetContextWindowState(ContextWindowState state)
    {
        _contextWindow = state ?? ContextWindowState.First;
        _windowReminderClaimed = false;
        _autoRollClaimed = false;
        _lastUserRequest = null; // 新页/恢复：任务陈述由片段锚点承担，提醒不再引用上一页的请求
        _windowFragmentActive =
            _windowFragmentActive
            || _contextWindow.StartOrder > 0
            || _contextWindow.PrevWindowId is not null;
        ResetPromptBaseline();
    }

    /// <summary>
    /// 是否注入页片段：模式必须是接续，且会话确实换过页 / 恢复过页状态
    /// （首屏 w0 不注入——保持未换页会话的既有行为）。
    /// </summary>
    internal bool ShouldInjectWindowFragment() =>
        _contextManagement is { Mode: ContextMode.Continuation } && _windowFragmentActive;

    /// <summary>更新上下文管理选项（compact 模式传 null）。模式变化时同步刷新 system 骨架段。</summary>
    public void UpdateContextManagement(ContextManagementOptions? options)
    {
        var wasContinuation = _contextManagement is { Mode: ContextMode.Continuation };
        _contextManagement = options;
        if (wasContinuation != (options is { Mode: ContextMode.Continuation }))
            _conversation?.SetSystemMessage(BuildSystemMessageWithDirectory(_systemPrompt));
    }

    /// <summary>本页剩余 token；无法判定（无监控器 / 未知预算）时返回 -1。</summary>
    private long RemainingTokens()
    {
        if (stabilityMonitor is not { IsFrozen: false })
            return -1;
        var budget = stabilityMonitor.GetTokenBudget();
        return budget > 0 ? budget - stabilityMonitor.TotalTokens : -1;
    }

    /// <summary>
    /// 页片段：告知模型自己在哪一页、更早内容需要检索（Codex context_window 片段的对应物）。
    /// 运行时注入、发送后还原，不写入 JSONL。
    /// </summary>
    internal static string BuildContextWindowFragment(
        ContextWindowState window,
        long remainingTokens
    )
    {
        var prev = window.PrevWindowId;
        var sb = new StringBuilder(256);
        sb.Append("[上下文页]\n");
        sb.Append($"首页 ID: {window.FirstWindowId}    当前页 ID: {window.WindowId}");
        if (prev is not null)
            sb.Append($"    上一页 ID: {prev}");
        sb.Append('\n');

        if (remainingTokens >= 0)
            sb.Append($"本页剩余 token: {remainingTokens}\n");

        // ★ 任务锚点：让模型知道自己在做什么（换页不携带历史，但任务陈述必须跟着走）。
        // 只陈述事实 + 明确优先级：用户已给出新任务时以新任务为准，避免模型被过期的锚点拽回去做旧任务。
        var rollReasonText = window.RollReason switch
        {
            "threshold" => "接近上限（系统换页）",
            "overflow" => "溢出恢复（系统换页）",
            "model" => "模型主动换页",
            _ => null,
        };
        // ★ 模型自写的交接说明优先：它比裸锚点精确得多（做到哪 / 下一步 / 未完成项）
        if (!string.IsNullOrWhiteSpace(window.HandoffNote))
            sb.Append($"上一页交接说明：{window.HandoffNote}");
        if (!string.IsNullOrWhiteSpace(window.TaskAnchor))
            sb.Append(
                (string.IsNullOrWhiteSpace(window.HandoffNote) ? "" : "\n")
                    + $"上一页最后一条用户消息：{window.TaskAnchor}"
            );
        if (
            !string.IsNullOrWhiteSpace(window.HandoffNote)
            || !string.IsNullOrWhiteSpace(window.TaskAnchor)
        )
        {
            if (rollReasonText is not null)
                sb.Append($"（切换原因：{rollReasonText}）");
            sb.Append('\n');
            sb.Append(
                "（未完成则直接继续，不必等用户发话——先看上一页交接说明与 history_* 确认做到哪；"
                    + "已完成则待命；用户已给出新任务则以新任务为准。）\n"
            );
        }

        // prev == null 只出现在「分支回溯复位到窗口之前」这一种运行时路径
        // （真首页 _windowFragmentActive == false，根本不注入片段）——此时会话确实存在更早内容，
        // 所以这里不能写「这是第一页」，必须给出 history_* 检索入口。
        sb.Append(
            "本页没有更早的消息。需要更早的对话内容或工具输出时，用 history_search_contents / history_read_item 检索；\n"
                + "跨页要长期保留的结论请落库（memory_store 记忆 / todo_write 任务清单），不要只留在对话里。"
        );
        // 机制对用户完全隐藏：这些标识/剩余量只服务模型自己的调度，不得出现在回复里
        sb.Append('\n').Append(HiddenNotice);
        return sb.ToString();
    }

    /// <summary>预算提醒里引用的当前请求最大长度（超出截断，避免提醒本身变贵）。</summary>
    private const int ReminderRequestMaxChars = 120;

    /// <summary>
    /// 预算提醒文案（每页至多一次）：**提前告知**本页将满，点名当前请求，要求尽快收尾后换页。
    /// </summary>
    /// <param name="currentRequest">本页最近一次用户请求（动态感知任务）；null = 不引用。</param>
    internal static string BuildBudgetReminder(long remainingTokens, string? currentRequest = null)
    {
        var taskLine = string.IsNullOrWhiteSpace(currentRequest)
            ? ""
            : $"当前请求：{(currentRequest.Length <= ReminderRequestMaxChars ? currentRequest : currentRequest[..ReminderRequestMaxChars] + "…")}\n";

        return $"[上下文预算] 本页剩余 {remainingTokens} token。{taskLine}"
            + "请尽快收尾当前任务——不要再开启新的长任务或大范围探索；"
            + "收尾 = 用 todo_write 更新未完成项、把交接说明写进 new_context(note:)（不要假装完成）；"
            + "然后调用 new_context 开启新页继续——新页会自动接着做剩余部分，不需要等用户发话。"
            + HiddenNotice;
    }

    /// <summary>
    /// 预算提醒判定：**usage 比例**口径（不是绝对 token），阈值默认由模型窗口推导。
    /// </summary>
    /// <param name="usedTokens">当前 usage。</param>
    /// <param name="budget">模型预算（上下文窗口）。</param>
    /// <param name="advicePercent">覆盖的 usage 比例（0/NaN = 用 ModelContextProfile 默认）。</param>
    internal static bool ShouldClaimBudgetReminder(
        long usedTokens,
        long budget,
        double advicePercent
    )
    {
        if (usedTokens <= 0 || budget <= 0)
            return false;
        var ratio = (double)usedTokens / budget;
        return ratio >= advicePercent;
    }

    /// <summary>隐藏提示：注入的机制文本只服务模型调度，不得出现在回复里。</summary>
    internal const string HiddenNotice = "（系统内部状态：不要向用户提及或解释。）";

    /// <summary>上下文压力档位（nudge 三档：info / warn / critical）。</summary>
    public enum PressureLevel
    {
        Info,
        Warn,
        Critical,
    }

    /// <summary>
    /// 上下文压力提示文案。**模式互斥**：compact 指向 /compact，接续指向 new_context，两套文案不交叉。
    /// </summary>
    internal static string BuildPressureNudge(
        PressureLevel level,
        double percent,
        bool continuation
    ) =>
        (level, continuation) switch
        {
            (PressureLevel.Critical, true) =>
                $"🚨 上下文使用率 >{percent:P0}，接近上限。本轮必须完成当前阶段并立即调用 new_context 开启新页，不得开启新的长任务。{HiddenNotice}",
            (PressureLevel.Critical, false) =>
                $"🚨 上下文使用率 >{percent:P0}，接近上限。本轮必须完成当前阶段并立即建议用户执行 /compact，不得开启新的长任务。",
            (PressureLevel.Warn, true) =>
                $"⚠️ 上下文使用率 >{percent:P0}。请完成当前阶段后调用 new_context 开启新页继续；新页不携带历史，需要时用 history_* 检索。{HiddenNotice}",
            (PressureLevel.Warn, false) =>
                $"⚠️ 上下文使用率 >{percent:P0}。请完成当前阶段后，向用户建议执行 /compact 压缩已完成内容。",
            (PressureLevel.Info, true) =>
                $"📋 上下文使用率 >{percent:P0}，建议调用 new_context 开启新页继续。{HiddenNotice}",
            _ => $"📋 上下文使用率 >{percent:P0}，建议压缩已消耗内容。",
        };

    /// <summary>建议换页阈值（usage 比例）：配置覆盖优先，否则用模型 profile 推导值。</summary>
    private double RollAdviceRatio =>
        _contextManagement is { ReminderPercent: > 0 and <= 99 } configured
            ? configured.ReminderPercent / 100.0
            : _modelProfile.RollAdvicePercent;

    /// <summary>
    /// 预算提醒（每页一次）：命中即置位 claim 并返回文案；未命中返回 null。
    /// 文案里的剩余量按实际 budget − usage 计算，因此对任意窗口都准确。
    /// </summary>
    internal string? TryClaimBudgetReminder(long usedTokens, long budget)
    {
        if (_contextManagement is not { Mode: ContextMode.Continuation } || _windowReminderClaimed)
            return null;
        if (!ShouldClaimBudgetReminder(usedTokens, budget, RollAdviceRatio))
            return null;

        _windowReminderClaimed = true;
        return BuildBudgetReminder(Math.Max(budget - usedTokens, 0), _lastUserRequest);
    }

    /// <summary>
    /// 自适应换页判定：usage 到 AutoRollPercent（模型相对）后由循环自己触发换页，
    /// 先于 pre-flight 溢出；每页至多一次（防抖）。
    /// </summary>
    internal bool ShouldAutoRoll()
    {
        if (_contextManagement is not { Mode: ContextMode.Continuation })
            return false;
        if (onNewContextRequested is null || _autoRollClaimed)
            return false;
        if (stabilityMonitor is not { IsFrozen: false } || _persistedMessages.Count == 0)
            return false;

        var budget = stabilityMonitor.GetTokenBudget();
        return ShouldClaimBudgetReminder(
            stabilityMonitor.TotalTokens,
            budget,
            _modelProfile.AutoRollPercent
        );
    }

    /// <summary>
    /// 换页触发登记（迭代最顶部）：模型请求优先，其次自适应阈值。
    ///
    /// **只登记，不在 run 中途执行**——中途执行会 `ReplaceMessages([])` 把本轮刚收到的用户消息
    /// 与本轮工具结果一起清掉：模型在新页上既看不到任务，也没有可检索的线索。
    /// 实际换页由 <see cref="FlushScheduledContextRollAsync"/> 在 run 正常结束时执行，
    /// 于是当前任务在旧页正常做完、新页从下一条用户消息开始（Codex 同款语义）。
    /// Host 只负责「写 window 条目 + 清空模型侧 + 记账」。
    /// </summary>
    internal void ScheduleContextRollIfNeeded()
    {
        // 模型请求先取走：无论能否执行都不留到下一个 run（否则下一次 prompt 的用户消息会被误换页）
        var pending = agent?.PendingNewContextReason;
        if (pending is not null)
            agent!.ConsumeNewContextRequest();

        if (_rollAfterRun is not null)
            return; // 本 run 已登记，先到先得

        if (pending is not null)
        {
            if (onNewContextRequested is not null)
                _rollAfterRun = pending;
            return;
        }

        if (ShouldAutoRoll())
        {
            _autoRollClaimed = true; // 每页只尝试一次（防抖），无论执行成败
            _rollAfterRun = "threshold";
        }
    }

    /// <summary>
    /// run 边界执行换页：只在 run 正常结束时调用（取消/异常路径不执行，登记在下一个 run 开头清掉）。
    /// 失败只记日志——换页是增强能力，不能影响已经完成的 run。
    /// </summary>
    internal async Task FlushScheduledContextRollAsync(CancellationToken ct)
    {
        var reason = _rollAfterRun;
        _rollAfterRun = null;
        if (reason is null || onNewContextRequested is null)
            return;

        try
        {
            await onNewContextRequested(reason, ct);
        }
        catch (Exception ex)
        {
            CoreLogger.ContextRollFlushFailed(logger.Safe(), reason, ex.Message);
        }
    }

    /// <summary>
    /// 重置 prompt token 基线（compact 后调用）。
    ///
    /// B6 修复：compact 后 prompt 大幅缩小，但 _lastPromptTokens 仍是压缩前旧值，
    /// 下一轮 ComputeTurnIncrement = prompt_new − prompt_old 会算出大幅负增量，
    /// 把 stabilityMonitor.TotalTokens 拉低到错误水平，导致 pre-flight 低估漏检。
    /// 置 null 后下一轮增量走 seeded 分支（增量 = completion 或 full），从新基线起步。
    /// </summary>
    public void ResetPromptBaseline() => _lastPromptTokens = null;

    /// <summary>
    /// 更新模型上下文 profile 并同步所有稳定器阈值。
    /// 在模型切换、上下文窗口大小变更、thinking 级别变更时调用。
    /// 同时将 profile 传播到所有注入的稳定器组件，确保 pre-flight 检查的 budget
    /// 和 monitor 的 budget 始终基于同一个 ContextWindowTokens 值。
    /// </summary>
    public void UpdateModelProfile(ModelContextProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _modelProfile = profile;

        // 同步所有稳定器组件，确保 budget/阈值一致
        stabilityMonitor?.ApplyProfile(profile);
        holographicEngine?.ApplyProfile(profile);
        messageScorer?.ApplyProfile(profile);
    }

    /// <summary>
    /// 从已有消息恢复 Conversation 状态（用于会话恢复场景）。
    /// 恢复完整 reasoning Content（LLM 需要参考之前的推理过程），不恢复 Signature。
    /// Unknown 角色消息不进 Conversation（防御性处理，正常数据不应出现 Unknown 角色）。
    /// </summary>
    /// <param name="historicalMessages">历史消息列表。</param>
    public void RestoreFromMessages(IReadOnlyList<ChatMessage> historicalMessages)
    {
        ArgumentNullException.ThrowIfNull(historicalMessages);

        _persistedMessages.Clear();
        _persistedMessages.AddRange(historicalMessages);

        _conversation = CreateConversation();

        foreach (var msg in historicalMessages)
        {
            switch (msg.Role)
            {
                case ChatMessageRoles.Unknown:
                    // Unknown 角色不进 Conversation（防御性处理，正常数据不应出现 Unknown 角色）
                    continue;
                case ChatMessageRoles.Assistant:
                {
                    // 构建进入 Conversation 的消息
                    var convMsg = new ChatMessage(
                        msg.Role ?? ChatMessageRoles.Assistant,
                        msg.Content ?? ""
                    )
                    {
                        ToolCalls = msg.ToolCalls,
                        Name = msg.Name,
                        ToolCallId = msg.ToolCallId,
                        ToolInvocationSucceeded = msg.ToolInvocationSucceeded,
                    };

                    // 恢复 reasoning content（完整文本，发给 API）
                    if (!string.IsNullOrEmpty(msg.ReasoningContent))
                    {
                        convMsg.ReasoningContent = msg.ReasoningContent;
                    }

                    // 恢复 Parts：保留 Reasoning parts 的 Content，不恢复 Signature
                    // （Signature round-trip 代价高且收益有限，仅 Anthropic extended thinking 多轮连续性需要）
                    if (msg.Parts is not null && msg.Parts.Count > 0)
                    {
                        var restoredParts = new List<ChatMessagePart>(msg.Parts.Count);
                        foreach (var part in msg.Parts)
                        {
                            if (part is { Type: ChatMessageTypes.Reasoning, Reasoning: not null })
                            {
                                restoredParts.Add(
                                    new ChatMessagePart(ChatMessageTypes.Reasoning)
                                    {
                                        Reasoning = new ChatMessageReasoningData
                                        {
                                            Content = part.Reasoning.Content,
                                            // Signature = null ← 不恢复
                                        },
                                    }
                                );
                            }
                            else
                            {
                                restoredParts.Add(part);
                            }
                        }
                        convMsg.Parts = restoredParts;
                    }

                    _conversation.AddMessage(convMsg);
                    break;
                }
                default:
                    _conversation.AddMessage(msg);
                    break;
            }
        }

        // ★ 会话恢复后预估历史消息的 token 占用，注入 StabilityMonitor。
        // 避免恢复后 TotalTokens=0 导致 pre-flight 检查跳过，
        // 首次 LLM 调用可能因上下文溢出返回 400 错误。
        // 估算方式：文本长度 / 4（1 token ≈ 4 字符的粗粒度启发式），
        // 含 ReasoningContent 和 ToolCall 参数。
        // 仅在 TotalTokens=0 时生效（避免重复调用时覆盖已有的精确计数）。
        if (stabilityMonitor is not null && historicalMessages.Count > 0)
        {
            var estimatedTokens = EstimateMessagesTokens(historicalMessages);
            stabilityMonitor.SeedTokenCount(estimatedTokens);
        }
    }

    /// <summary>
    /// 通知外部持久化层：当前已就绪（可安全落盘）的消息。
    /// 在用户消息入队后以及每个 LLM/tool turn 结束后调用，
    /// 避免必须等整个 Agent loop 停止才写入 FileSessionStore。
    /// 传入的是完整持久化消息列表，由外部按自身增量游标跳过已写部分。
    /// </summary>
    private async Task NotifyMessagesReadyAsync(CancellationToken ct)
    {
        if (onMessagesReady is not null)
            await onMessagesReady(_persistedMessages, ct);
    }

    /// <summary>
    /// 异常路径下安全落盘：将 _persistedMessages 中尚未写入的消息及时持久化。
    /// 使用 CancellationToken.None（原 ct 可能已取消），
    /// try/catch 防止持久化失败掩盖原始异常。
    /// </summary>
    private async Task SafelyPersistAsync()
    {
        try
        {
            await NotifyMessagesReadyAsync(CancellationToken.None);
        }
        catch
        {
            // 尽力而为：持久化失败不掩盖原始异常
        }
    }

    /// <summary>
    /// 执行 Agent Loop：追加用户消息，循环调用 LLM 直到获得最终回复。
    /// </summary>
    /// <param name="userMessages">用户消息列表。</param>
    /// <param name="emit">事件发射委托。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>本次运行产生的新消息数组。</returns>
    public async Task<ChatMessage[]> RunAsync(
        IReadOnlyList<ChatMessage> userMessages,
        Func<AgentEvent, CancellationToken, Task> emit,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(userMessages);

        _conversation ??= CreateConversation();

        var newMessages = new List<ChatMessage>(userMessages.Count + 8);

        await emit(new AgentStart(), ct);

        // ★ P0d: 先静默注入 InjectQueue 残留消息（中断前未消费的系统通知，不触发 UI 事件）
        var residualInject = DequeueInjectMessages();
        foreach (var inject in residualInject)
        {
            _conversation.AddMessage(inject);
            _persistedMessages.Add(inject);
        }

        // ★ 再投递 SteeringQueue 中的残留消息（中断前未消费的）
        var residualSteer = DequeueSteeringMessages();
        foreach (var (msg, steerId) in residualSteer)
        {
            await emit(new SteerPromoted(steerId ?? "", msg), ct);
            _conversation.AddMessage(msg);
            _persistedMessages.Add(msg);
            newMessages.Add(msg);
            await emit(new MessageStart(msg), ct);
            await emit(new MessageEnd(msg), ct);
        }

        // ★ GuardRail 输入检查：在用户消息加入 Conversation 之前
        if (guardRail is not null)
        {
            foreach (var msg in userMessages)
            {
                if (msg.Role == ChatMessageRoles.User)
                {
                    var inputResult = await guardRail.CheckInputAsync(msg.Content, ct);
                    if (!inputResult.Passed)
                    {
                        if (inputResult.Tripwire)
                        {
                            CoreLogger.GuardRailTriggered(
                                logger.Safe(),
                                "input",
                                inputResult.Reason
                            );
                            await emit(
                                new AgentError(
                                    $"输入 GuardRail 触发：{inputResult.Reason}",
                                    null,
                                    IsContextOverflow: false,
                                    Recoverable: false
                                ),
                                CancellationToken.None
                            );
                            await emit(new AgentEnd([]), ct);
                            return [];
                        }
                        // 非 tripwire：仅记录警告
                        CoreLogger.GuardRailWarning(logger.Safe(), "input", inputResult.Reason);
                    }
                }
            }
        }

        // 追加用户消息到 Conversation
        foreach (var msg in userMessages)
        {
            _conversation.AddMessage(msg);
            _persistedMessages.Add(msg);
            newMessages.Add(msg);
            if (msg.Role == ChatMessageRoles.User && !string.IsNullOrWhiteSpace(msg.Content))
                _lastUserRequest = msg.Content;
            await emit(new MessageStart(msg), ct);
            await emit(new MessageEnd(msg), ct);
        }

        // ★ 及时落盘：用户消息入队后立即持久化（含 RunAsync 开头恢复的 residual inject/steer），
        // 不等 Agent loop 停止，避免后续 LLM/工具调用失败时丢失用户输入。
        await NotifyMessagesReadyAsync(ct);

        // 进入 ReAct 循环
        await RunLoopAsync(newMessages, emit, ct);

        await emit(new AgentEnd(newMessages.ToArray()), ct);

        // ★ run 边界换页：本轮已完整结束（消息已落盘、UI 已收尾），换页只影响下一页的模型上下文
        await FlushScheduledContextRollAsync(ct);
        return newMessages.ToArray();
    }

    /// <summary>
    /// 继续循环（不追加用户消息，仅重入循环）。
    /// </summary>
    /// <param name="emit">事件发射委托。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>本次运行产生的新消息数组。</returns>
    public async Task<ChatMessage[]> ContinueAsync(
        Func<AgentEvent, CancellationToken, Task> emit,
        CancellationToken ct
    )
    {
        _conversation ??= CreateConversation();

        var newMessages = new List<ChatMessage>(8);
        await emit(new AgentStart(), ct);

        await RunLoopAsync(newMessages, emit, ct);

        await emit(new AgentEnd(newMessages.ToArray()), ct);

        // ★ run 边界换页（与 RunAsync 同语义）
        await FlushScheduledContextRollAsync(ct);
        return newMessages.ToArray();
    }

    /// <summary>
    /// 核心 ReAct 循环：迭代调用 LLM，处理工具调用，直到获得最终回复。
    /// </summary>
    /// <param name="newMessages">新消息列表（用于收集本次运行产生的消息）。</param>
    /// <param name="emit">事件发射委托。</param>
    /// <param name="ct">取消令牌。</param>
    private async Task RunLoopAsync(
        List<ChatMessage> newMessages,
        Func<AgentEvent, CancellationToken, Task> emit,
        CancellationToken ct
    )
    {
        var iteration = 0;
        LastStop = null; // 新任务不继承上次运行的停止原因

        // ★ 循环判定计数只在产生它的 run 内有效（新任务 = 新的复核预算）
        _stopContinuations = 0;
        _stabilityOverrides = 0;

        // ★ 换页登记只在产生它的 run 内有效：上一次 run 被中断/异常时未执行，
        // 残留会在下一次 prompt 的用户消息之前换页，故此处清掉。
        agent?.ConsumeNewContextRequest();
        agent?.ConsumeNewContextNote();
        _rollAfterRun = null;
        _callHistory = new ToolDispatcher.CircularBuffer<ToolDispatcher.ToolCallRecord>(
            _modelProfile.ScaledCallHistoryCapacity
        );

        // ★ 新任务边界重置稳定器断路状态：一次 Tripped 若不重置会永久毒化后续每次运行
        // （RecordStep 返回 Tripped 不抛异常 → 每轮后 CheckStability 都 Tripped → 每轮只跑 1 次就静默中断）。
        // RunAsync/ContinueAsync 共用此入口 → 一个用户任务 = 一个升级检测窗口。
        (stabilityMonitor as IGrowthRatioGuard)?.ResetEscalation();

        // 循环无迭代次数上限：唯一退出路径为显式 break（自然停止 / 空回合 /
        // 工具未继续 / 稳定性触发）与取消（上方 ThrowIfCancellationRequested）。
        // 失控由稳定性监控、断路器、pre-flight 预算检查（compact + retry，
        // TuiApp maxOverflowRetries=3）与用户中断兜底。
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            iteration++;

            // ★ 换页触发点：模型主动 new_context 优先，其次是 usage 到 AutoRollPercent 的自适应换页。
            // 此处只登记（见 ScheduleContextRollIfNeeded 的注释），run 正常结束时才真正换页。
            ScheduleContextRollIfNeeded();

            // ★ Tool 热刷新：注入面签名变化（插件加载/卸载或 tool_search 展开新工具）时
            // 重建 Conversation，使工具列表（常驻 + 已展开 + 非延迟）与最新状态一致。
            // BuildRequestTools 只在 CreateConversation 时执行一次，此处检测签名
            // 变化后重建——否则展开的工具永远不会进入下一轮请求（Bug A 修复）。
            if (toolInjector is not null && toolInjector.ToolsSignature != _lastToolsSignature)
            {
                // 重建 Conversation（旧实例交给 GC，LlmTornado 无原生资源）
                _conversation = CreateConversation();
                // ★ 回放已持久化消息，保持 LLM 上下文连续（Bug B 修复）
                foreach (var msg in _persistedMessages)
                    _conversation.AddMessage(msg);
                _lastToolsSignature = toolInjector.ToolsSignature;
                CoreLogger.LoopToolSetRefreshed(logger.Safe(), _toolRegistry.Version);
            }

            // ★ P0d: 静默注入系统通知（CRUD/Hook/审计），不触发 MessageStart/End
            var injectMessages = DequeueInjectMessages();
            foreach (var inject in injectMessages)
            {
                _conversation!.AddMessage(inject);
                _persistedMessages.Add(inject);
            }

            // ★ Steer 投递点：上一个 Turn 已结束，下一个 Turn 还没开始
            var steerMessages = DequeueSteeringMessages();
            foreach (var (msg, steerId) in steerMessages)
            {
                // ★ 先发 SteerPromoted → TUI 删 waiting 占位
                await emit(new SteerPromoted(steerId ?? "", msg), ct);

                _conversation!.AddMessage(msg);
                _persistedMessages.Add(msg);
                newMessages.Add(msg);

                // ★ 再发 MessageStart/End → TUI 画 user 气泡
                await emit(new MessageStart(msg), ct);
                await emit(new MessageEnd(msg), ct);
            }

            // ★ inject/steer 消息入 _persistedMessages 后立即落盘，
            // 防止后续 pre-flight 或 StreamOneTurnAsync 异常时丢失这些系统通知。
            if (injectMessages.Count > 0 || steerMessages.Count > 0)
                await NotifyMessagesReadyAsync(ct);

            CoreLogger.LoopIteration(logger.Safe(), iteration);
            await emit(new TurnStart(), ct);

            // 保存循环级状态
            _loopNewMessages = newMessages;
            _continueLoop = false;
            _stopAfterTool = false;
            _stoppedByExternalControl = false;

            // ★ Pre-flight budget check：如果 token 已接近预算上限，提前触发 compact
            // 避免在 StreamOneTurnAsync 中途耗尽预算（此时工具结果已塞入 Conversation，
            // compact 代价更高）。阈值通过 ModelContextProfile 动态计算，
            // 给当轮 LLM 调用留 1.5× TypicalTurnTokens 余量。
            // 1M → 95%, 128K → 85%, 8K → 75%。
            // IsFrozen 守卫：冻结态由溢出恢复流程（Unfreeze）处理，pre-flight 不应在冻结态
            // 反复拦截——否则监控器冻结后（TotalTokens 停留在旧值）所有请求被无限拦截。
            if (
                stabilityMonitor is not null
                && !stabilityMonitor.IsFrozen
                && stabilityMonitor.TotalTokens > 0
            )
            {
                var budgetLimit = stabilityMonitor.GetTokenBudget();
                var preflightPercent = _modelProfile.PreflightThresholdPercent;
                if (
                    budgetLimit > 0
                    && stabilityMonitor.TotalTokens >= budgetLimit * preflightPercent
                )
                {
                    // ★ 接续模式不在 run 中途换页（会连带清掉本轮用户消息），统一走溢出恢复：
                    // Host 捕获后换页并重试同一段文本——重试把用户消息重新投进新页，无信息丢失。
                    var preflightMsg =
                        $"Token 预算接近上限（{stabilityMonitor.TotalTokens:N0}/{budgetLimit:N0}），需要压缩上下文";
                    // ★ Pre-flight 溢出前落盘：inject/steer 消息已在上方持久化，
                    // 此处再次确认所有 _persistedMessages 已写入（防御性）
                    await SafelyPersistAsync();
                    await emit(
                        new AgentError(
                            preflightMsg,
                            null,
                            IsContextOverflow: true,
                            Recoverable: true
                        ),
                        CancellationToken.None
                    );
                    throw new ContextOverflowException(preflightMsg);
                }
            }

            var outcome = await StreamOneTurnAsync(emit, ct);

            // 取消后不再继续循环
            if (ct.IsCancellationRequested)
            {
                break;
            }

            // ★ 空回合检测：流无文本、无工具调用、Conversation 无新增消息——真实异常（模型侧空响应）
            if (outcome is { NewMessageCount: 0, HadText: false, ToolCallCount: 0 })
            {
                CoreLogger.LoopEmptyTurn(logger.Safe(), iteration);
                await emit(
                    new AgentError(
                        "本轮模型未产生任何输出（空回合），已停止。",
                        null,
                        IsContextOverflow: false,
                        Recoverable: true
                    ),
                    CancellationToken.None
                );
                LastStop = new LoopStop("empty_turn", "本轮模型未产生任何输出（空回合），已停止。");
                break;
            }

            // ★ 工具调用未继续检测：本轮执行了工具但循环不续跑，且非 Terminate 工具、非外部控制——
            // 捕获"工具执行了但 AfterFunctionCallsResolvedHandler 未触发"的隐蔽路径
            if (
                outcome.ToolCallCount > 0
                && !_continueLoop
                && !_stopAfterTool
                && !_stoppedByExternalControl
            )
            {
                CoreLogger.LoopToolCallsNotContinued(
                    logger.Safe(),
                    outcome.ToolCallCount,
                    iteration
                );
                await emit(
                    new AgentError(
                        $"本轮模型发起了 {outcome.ToolCallCount} 个工具调用，但循环未能继续执行，已停止。",
                        null,
                        IsContextOverflow: false,
                        Recoverable: true
                    ),
                    CancellationToken.None
                );
                LastStop = new LoopStop(
                    "tool_calls_not_continued",
                    $"本轮模型发起了 {outcome.ToolCallCount} 个工具调用，但循环未能继续执行，已停止。"
                );
                break;
            }

            // 取消后不再继续循环
            if (ct.IsCancellationRequested)
            {
                break;
            }

            // 稳定性检查（GrowthRatioGuard / LyapunovMonitor）
            if (stabilityMonitor is not null)
            {
                var status = stabilityMonitor.CheckStability();
                if (status is StabilityStatus.Tripped or StabilityStatus.Frozen)
                {
                    // ★ 循环判定复核：仅 Tripped（Frozen=预算耗尽，永不复核）且本轮确有工具调用时生效——
                    // 先问一句「最近这些调用是否仍在实质推进」，判定推翻则本轮继续（每 run 至多一次）。
                    if (
                        status == StabilityStatus.Tripped
                        && outcome.ToolCallCount > 0
                        && await JudgeStabilityOverrideAsync(_callHistory, ct)
                    )
                    {
                        _continueLoop = true; // 断路已被复核推翻：本轮不终止（升级计数已重置）
                    }
                    else
                    {
                        var reason =
                            status == StabilityStatus.Frozen
                                ? "token 预算已耗尽"
                                : "稳定性监控器已触发（检测到异常增长模式）";
                        var energySummary =
                            stabilityMonitor.EnergyHistory.Count > 0
                                ? string.Join(
                                    ",",
                                    stabilityMonitor
                                        .EnergyHistory.TakeLast(6)
                                        .Select(e => e.ToString("F0"))
                                )
                                : "n/a";
                        CoreLogger.StabilityMonitorTriggered(
                            logger.Safe(),
                            $"{reason} | tokens={stabilityMonitor.TotalTokens} steps={stabilityMonitor.TotalSteps} energy=[{energySummary}]"
                        );

                        var monitorMsg = CreateStabilityMessage(
                            $"{reason}（累计 {stabilityMonitor.TotalTokens:N0} token，{stabilityMonitor.TotalSteps} 轮，能量轨迹: [{energySummary}]）"
                        );
                        _conversation!.AddMessage(monitorMsg);
                        _persistedMessages.Add(monitorMsg);
                        newMessages.Add(monitorMsg);
                        // ★ 稳定性中断：monitorMsg 已入 _persistedMessages，及时落盘
                        await NotifyMessagesReadyAsync(ct);
                        // ★ 可见性：稳定器中断不再静默——emit AgentError 让 TUI 面板显示停止原因。
                        // monitorMsg 只进 Conversation/_persistedMessages（无 MessageStart/End），
                        // AgentEventAdapter 的 AgentEnd 不渲染消息，UI 此前看不到任何提示。
                        await emit(
                            new AgentError(
                                $"稳定性监控器触发：{reason}。本轮已停止，发送新消息后监控状态将重置。",
                                null,
                                IsContextOverflow: false,
                                Recoverable: true
                            ),
                            CancellationToken.None
                        );
                        LastStop = new LoopStop(
                            "stability_tripped",
                            $"稳定性监控器触发：{reason}。本轮已停止，发送新消息后监控状态将重置。"
                        );
                        break;
                    }
                }
            }

            // ── VSA 上下文漂移检测（频率和阈值通过 HolographicEngine 动态配置） ──
            // 从 HolographicEngine 读取配置，确保 VSAConfig 的手动覆盖值优先于 ModelContextProfile 计算
            if (
                holographicEngine is not null
                && ++_driftCheckCounter % holographicEngine.EffectiveDriftCheckInterval == 0
            )
            {
                try
                {
                    // ★ 结构闸门 1（防误报）：只有模型仍在执行任务时才可能"跑题"——
                    // 本轮回调用了工具、或有待处理 steer/inject 说明方向尚未交回用户。
                    // 模型刚给出纯文本回答、控制权已交回用户（等待下一条指令）时，
                    // 下一轮方向由用户新消息重置，此时度量/注入提醒既无意义也会污染对话
                    // （上一轮真实案例：模型向用户澄清"你指的是哪两章？"——合法等待输入，
                    // 其问句文本与系统提示词向量天然正交，被误判为偏离并注入突兀的系统提醒）。
                    if (_continueLoop || steerMessages.Count > 0 || injectMessages.Count > 0)
                    {
                        // ★ 结构闸门 2（采样窗口 = 当前任务段）：只取最近一条用户消息之后的
                        // 助手内容，上限 EffectiveDriftContextWindow 条。早于最近用户消息的
                        // 助手内容（如已被用户回答过的澄清问题、上一段汇报）是过期片段，
                        // 方向已被新指令重置，不应参与漂移度量。
                        var recentAssistant = SampleCurrentSegmentAssistantText(
                            _persistedMessages,
                            holographicEngine.EffectiveDriftContextWindow
                        );
                        if (recentAssistant.Count > 0)
                        {
                            var contextText = string.Concat(recentAssistant);
                            var contextVec = holographicEngine.EncodeText(contextText);
                            var drift = holographicEngine.CheckDrift("system_prompt", contextVec);
                            // VSA 漂移检测：cosine similarity 低于阈值表示当前上下文与系统提示词正交（漂移）。
                            // 阈值来源：VSAConfig.EffectiveDriftThreshold → 优先手动设置，否则 ModelContextProfile.ScaledDriftThreshold。
                            // 10K 维 VSA 空间: 3/√10000 ≈ 0.03 (3σ 置信度)。
                            // 之前硬编码 0.3 几乎不可达（≈30σ），漂移检测从未真正生效。
                            var driftThreshold = holographicEngine.EffectiveDriftThreshold;
                            if (drift >= -driftThreshold && drift < driftThreshold)
                            {
                                // ★ 循环判定复核：判定仍在角色/任务范围内时，本次漂移按误报压制
                                // （不告警、不注入提醒——随机投影 cosine 在该量级误报率高）。
                                if (await JudgeDriftVetoAsync(drift, contextText, ct))
                                {
                                    // 压制路径：日志见 JudgeDriftVetoAsync
                                }
                                else
                                {
                                    CoreLogger.StabilityMonitorTriggered(
                                        logger.Safe(),
                                        $"VSA 上下文漂移警告: similarity={drift:F4}（低于 ±{driftThreshold:F4} 阈值）| steps={stabilityMonitor?.TotalSteps ?? 0}"
                                    );

                                    // ★ 严重漂移时注入 system-reminder，引导模型回归系统提示词方向。
                                    // |similarity| < threshold/3 视为严重漂移（≈1σ 以内，几乎正交），
                                    // 此时注入提醒比纯日志更有效——LLM 的 recency bias 会使最近的消息
                                    // 影响后续输出方向，system-reminder 利用这一特性拉回对话。
                                    var severeThreshold = driftThreshold / 3.0;
                                    if (Math.Abs(drift) < severeThreshold)
                                    {
                                        var reminderMsg = new ChatMessage(
                                            ChatMessageRoles.Assistant,
                                            [
                                                new ChatMessagePart
                                                {
                                                    Type = ChatMessageTypes.Text,
                                                    Text =
                                                        "[系统提醒] 检测到对话方向偏离初始设定，请回顾系统提示词中的角色定义和写作流程，确保后续输出与设定一致。",
                                                },
                                            ]
                                        );
                                        _conversation!.AddMessage(reminderMsg);
                                        _persistedMessages.Add(reminderMsg);
                                        newMessages.Add(reminderMsg);
                                        // ★ VSA 漂移提醒：及时落盘（低频，不影响热路径）
                                        await NotifyMessagesReadyAsync(ct);
                                    }
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // VSA 漂移检测失败不阻断对话
                }
            }

            // ★ 有未处理的 steer/inject 消息时继续循环（即使 LLM 在本轮未调用工具）
            if (!_continueLoop && steerMessages.Count == 0 && injectMessages.Count == 0)
            {
                // ★ 循环判定复核：一轮只有文本、没有工具调用的收尾，可能是「声明了下一步却没动手」。
                // 判定「未完成」时注入一条提醒并继续一轮（外部控制停止 / Terminate 工具一律不推翻）。
                if (
                    !_stoppedByExternalControl
                    && !_stopAfterTool
                    && outcome is { HadText: true, ToolCallCount: 0 }
                    && await JudgeContinueAsync(
                        LastAssistantText(_persistedMessages) ?? "",
                        newMessages,
                        ct
                    )
                )
                {
                    continue; // 提醒已注入并计数；下一轮继续（每 run 至多 StopCheck.MaxPerRun 次）
                }

                CoreLogger.LoopExited(
                    logger.Safe(),
                    "natural",
                    iteration,
                    outcome.ToolCallCount,
                    outcome.HadText
                );
                break;
            }
        }
    }

    // ── 循环判定（停止 / VSA 漂移 / 稳定性断路的启发式复核）──
    // 失败即未知：禁用、未启用子开关、输入为空、AskAsync 返回 null、缺答案、
    // 选项不符、无概率、低于阈值 → 一律 false / null，调用方保持既有行为。
    // 每处判定各发一次请求（一题），失败不重试（ISystemOneClient 契约）。

    /// <summary>系统提示词摘要上限（角色/任务方向）。</summary>
    internal const int JudgeRoleChars = 600;

    /// <summary>最近用户请求上限。</summary>
    internal const int JudgeTaskChars = 300;

    /// <summary>单次工具调用参数摘要上限。</summary>
    internal const int JudgeCallArgsChars = 160;

    /// <summary>问题键（每次判定只发一题，键名固定）。</summary>
    internal const string JudgeQuestionKey = "verdict";

    /// <summary>停止复核的「未完成」选项名。</summary>
    internal const string JudgeIncompleteOption = "incomplete";

    /// <summary>截断到 maxChars（null/空 → 空串）。</summary>
    internal static string JudgeTruncate(string? text, int maxChars) =>
        string.IsNullOrEmpty(text) ? ""
        : text.Length <= maxChars ? text
        : text[..maxChars];

    /// <summary>最后一条有非空 Content 的助手消息；无则 null。</summary>
    internal static string? LastAssistantText(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var msg = messages[i];
            if (msg.Role == ChatMessageRoles.Assistant && !string.IsNullOrWhiteSpace(msg.Content))
                return msg.Content;
        }
        return null;
    }

    /// <summary>工具调用摘要（输入须已按「最近在前」排序，取前 maxCalls 条）："ToolName 参数截断"。</summary>
    internal static List<string> BuildToolDigest(
        IReadOnlyList<ToolDispatcher.ToolCallRecord> recent,
        int maxCalls
    )
    {
        if (recent.Count == 0 || maxCalls <= 0)
            return [];

        var take = Math.Min(recent.Count, maxCalls);
        var lines = new List<string>(take);
        for (var i = 0; i < take; i++)
        {
            var record = recent[i];
            var args = JudgeTruncate(record.Arguments, JudgeCallArgsChars);
            lines.Add(args.Length == 0 ? record.ToolName : $"{record.ToolName} {args}");
        }
        return lines;
    }

    /// <summary>true = 判定「未完成，应继续」且 p ≥ loop.stop-check.min-probability。</summary>
    internal static async Task<bool> ShouldContinueAsync(
        ISystemOneClient? client,
        string? reply,
        CancellationToken ct
    )
    {
        var loop = client?.Options.Loop;
        if (
            client is not { Enabled: true }
            || loop is not { Enabled: true, StopCheck.Enabled: true }
        )
            return false;

        var text = JudgeTruncate(reply, loop.TextChars);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var questions = new Dictionary<string, SystemOneQuestion>
        {
            [JudgeQuestionKey] = new ChoiceQuestion
            {
                Instructions = "reply 中的助手回复属于哪种情况？",
                Criteria = new Dictionary<string, string?>
                {
                    [JudgeIncompleteOption] =
                        "回复声明了接下来要做的事（例如“接下来我会…”）但本轮没有执行任何工具，也没有向用户提问；任务尚未完成",
                    ["awaiting-user"] = "回复已给出结论/交付物，或正在等待用户答复或确认",
                },
            },
        };

        var response = await client.AskAsync(new { reply = text }, questions, ct);
        return ChoiceProbability(response, JudgeIncompleteOption) is { } p
            && p >= loop.StopCheck.MinProbability;
    }

    /// <summary>
    /// true = 判定「仍在角色/任务范围内」（用于压制 VSA 误报提醒），
    /// 且 p ≥ loop.drift-check.min-probability。
    /// </summary>
    internal static async Task<bool> DriftLooksOnTaskAsync(
        ISystemOneClient? client,
        string? systemPrompt,
        string? recentText,
        CancellationToken ct
    )
    {
        var loop = client?.Options.Loop;
        if (
            client is not { Enabled: true }
            || loop is not { Enabled: true, DriftCheck.Enabled: true }
        )
            return false;

        var role = JudgeTruncate(systemPrompt, JudgeRoleChars);
        var recent = JudgeTruncate(recentText, loop.TextChars);
        if (string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(recent))
            return false;

        var questions = new Dictionary<string, SystemOneQuestion>
        {
            [JudgeQuestionKey] = new NoulQuestion
            {
                Instructions =
                    "根据 role 定义的角色与任务方向，recent 中的助手输出是否仍在范围内（没有跑题、没有偏离写作流程）？",
                Criteria = new NoulCriteria
                {
                    Yes = "仍在角色与任务范围内",
                    No = "已经跑题或明显偏离",
                },
            },
        };

        var response = await client.AskAsync(new { role, recent }, questions, ct);
        return response?.Noul(JudgeQuestionKey) is { } p && p >= loop.DriftCheck.MinProbability;
    }

    /// <summary>
    /// 稳定性复核的命中概率（null = 未启用 / 未知 / 低于阈值）；
    /// 调用方据其非空即「推翻断路」，并把概率写进日志。
    /// </summary>
    internal static async Task<double?> ProgressHitAsync(
        ISystemOneClient? client,
        string? userRequest,
        IReadOnlyList<string> toolCalls,
        CancellationToken ct
    )
    {
        var loop = client?.Options.Loop;
        if (
            client is not { Enabled: true }
            || loop is not { Enabled: true, StabilityCheck.Enabled: true }
            || toolCalls.Count == 0
        )
            return null;

        var questions = new Dictionary<string, SystemOneQuestion>
        {
            [JudgeQuestionKey] = new NoulQuestion
            {
                Instructions = "task 的最近这些工具调用是否仍在实质推进任务（每次都有新增进展）？",
                Criteria = new NoulCriteria
                {
                    Yes = "仍在实质推进",
                    No = "在原地重复同一动作或无效尝试",
                },
            },
        };

        var state = new { task = JudgeTruncate(userRequest, JudgeTaskChars), calls = toolCalls };
        var response = await client.AskAsync(state, questions, ct);
        return response?.Noul(JudgeQuestionKey) is { } p && p >= loop.StabilityCheck.MinProbability
            ? p
            : null;
    }

    /// <summary>choice 命中概率：选项不符 / 无概率 / 非法值一律 null（"未知"）。</summary>
    private static double? ChoiceProbability(SystemOneResponse? response, string option)
    {
        if (
            response is null
            || !response.Answers.TryGetValue(JudgeQuestionKey, out var answer)
            || !string.Equals(answer.Choice, option, StringComparison.Ordinal)
        )
            return null;

        var p =
            answer.Probabilities is { } map && map.TryGetValue(option, out var value)
                ? value
                : answer.Confidence;
        return p is { } v && double.IsFinite(v) && v is >= 0 and <= 1 ? v : null;
    }

    /// <summary>停止复核命中时注入的提醒（internal 测试缝：断言注入内容用）。</summary>
    internal const string JudgeContinueNotice =
        "[系统提醒] 你上一条回复声明了后续动作但本轮没有执行。若任务尚未完成，请立即用工具继续；若已结束，请简要收尾。";

    /// <summary>
    /// 自然停止复核：判定「上一条回复未完成，应继续」时注入一条提醒并记数，返回 true（调用方 continue）。
    /// 禁用 / 未启用 / 输入为空 / 超出本 run 次数 / 服务失败或低于阈值，一律 false —— 与既有收尾行为逐字节一致。
    /// </summary>
    internal async Task<bool> JudgeContinueAsync(
        string reply,
        List<ChatMessage> newMessages,
        CancellationToken ct
    )
    {
        var loop = _systemOne?.Options.Loop;
        if (
            _systemOne is not { Enabled: true }
            || loop is not { Enabled: true, StopCheck.Enabled: true }
            || string.IsNullOrWhiteSpace(reply)
            || _stopContinuations >= Math.Max(loop.StopCheck.MaxPerRun, 0)
        )
            return false;

        bool hit;
        try
        {
            hit = await ShouldContinueAsync(_systemOne, reply, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false; // 判定请求超时/内部取消：按未知处理（取消用户请求时重抛）
        }

        if (!hit)
            return false;

        _stopContinuations++;
        var reminderMsg = new ChatMessage(
            ChatMessageRoles.Assistant,
            [new ChatMessagePart { Type = ChatMessageTypes.Text, Text = JudgeContinueNotice }]
        );
        _conversation!.AddMessage(reminderMsg);
        _persistedMessages.Add(reminderMsg);
        newMessages.Add(reminderMsg);
        // ★ 判定提醒：及时落盘（与 VSA 漂移提醒同路径）
        await NotifyMessagesReadyAsync(ct);
        CoreLogger.LoopJudgeContinued(logger.Safe(), _stopContinuations);
        return true;
    }

    /// <summary>
    /// VSA 漂移复核：判定仍在角色/任务范围内（p ≥ `loop.drift-check.min-probability`）时返回 true，
    /// 调用方据此压制本次漂移告警与提醒（误报）。失败/未启用一律 false → 保留既有 VSA 行为。
    /// </summary>
    internal async Task<bool> JudgeDriftVetoAsync(
        double drift,
        string recentText,
        CancellationToken ct
    )
    {
        var loop = _systemOne?.Options.Loop;
        if (
            _systemOne is not { Enabled: true }
            || loop is not { Enabled: true, DriftCheck.Enabled: true }
        )
            return false;

        try
        {
            if (!await DriftLooksOnTaskAsync(_systemOne, _systemPrompt, recentText, ct))
                return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }

        CoreLogger.LoopJudgeDriftVetoed(logger.Safe(), drift);
        return true;
    }

    /// <summary>
    /// 稳定性断路复核：判定「最近工具调用仍在实质推进」（p ≥ `loop.stability-check.min-probability`）时，
    /// 重置断路器升级计数并返回 true（调用方本轮继续，不终止）。每 run 至多一次；
    /// 历史为空 / 未启用 / 服务失败一律 false → 保留既有终止行为。
    /// </summary>
    internal async Task<bool> JudgeStabilityOverrideAsync(
        ToolDispatcher.CircularBuffer<ToolDispatcher.ToolCallRecord>? history,
        CancellationToken ct
    )
    {
        var loop = _systemOne?.Options.Loop;
        if (
            _systemOne is not { Enabled: true }
            || loop is not { Enabled: true, StabilityCheck.Enabled: true }
            || _stabilityOverrides > 0
            || history is null
        )
            return false;

        var maxCalls = Math.Max(loop.StabilityCheck.MaxCalls, 1);
        var calls = BuildToolDigest(history.GetRecent(maxCalls), maxCalls);
        if (calls.Count == 0)
            return false;

        double? probability;
        try
        {
            probability = await ProgressHitAsync(_systemOne, _lastUserRequest, calls, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }

        if (probability is not { } p)
            return false;

        _stabilityOverrides++;
        (stabilityMonitor as IGrowthRatioGuard)?.ResetEscalation();
        CoreLogger.LoopJudgeStabilityOverridden(logger.Safe(), p);
        return true;
    }

    /// <summary>
    /// 执行一次完整的流式 LLM 调用。
    /// 处理流式输出、工具调用、reasoning 内容同步等。
    /// 返回回合摘要供循环检测空回合 / 工具调用未继续等异常。
    /// </summary>
    private async Task<TurnOutcome> StreamOneTurnAsync(
        Func<AgentEvent, CancellationToken, Task> emit,
        CancellationToken ct
    )
    {
        var fullText = new StringBuilder(2048);
        var reasoningText = new StringBuilder(512);
        var toolCallsCollected = new List<(string Id, string Name, string Arguments)>(4);

        // 记录调用前的消息数量，用于后续同步新增消息（必须在 nudge 注入之前）
        var messagesBeforeCall = _conversation?.Messages.Count ?? 0;

        // ★ ref 标签注入（运行时注入，不持久化污染 JSONL）
        // Conversation.Messages 返回副本列表但元素是引用——直接改 msg.Content 属性
        // 会作用于内部列表对象（EditMessageContent 同机制），发送序列化时生效。
        // 返回 (消息, 原内容) 供 StreamResponseRich 完成后还原，避免标签写入 JSONL。
        var injectedRefTags = _conversation is null
            ? []
            : InjectRefTags(
                _conversation.Messages,
                entryIdProvider,
                _refByEntryId,
                ref _nextMsgRef
            );

        // ★ 上下文页片段（仅 continuation 模式 + 已换页 / 已恢复页状态时注入；运行时注入不持久化）
        ChatMessage? windowMsg = null;
        if (ShouldInjectWindowFragment())
        {
            windowMsg = new ChatMessage(
                ChatMessageRoles.User,
                BuildContextWindowFragment(_contextWindow, RemainingTokens())
            );
            _conversation?.AddMessage(windowMsg);
        }

        // ★ 预算提醒（每页一次）：usage 比例口径（阈值 = 配置覆盖 或 模型 profile 的 RollAdvicePercent）
        ChatMessage? budgetMsg = null;
        var budgetForReminder = stabilityMonitor is { IsFrozen: false }
            ? stabilityMonitor.GetTokenBudget()
            : 0;
        var usedForReminder = stabilityMonitor is { IsFrozen: false }
            ? stabilityMonitor.TotalTokens
            : 0;
        if (TryClaimBudgetReminder(usedForReminder, budgetForReminder) is { } reminderText)
        {
            budgetMsg = new ChatMessage(ChatMessageRoles.User, reminderText);
            _conversation?.AddMessage(budgetMsg);
        }

        // ★ nudge 检查（≤15 行，无独立类，热路径直接读 StabilityMonitor）
        // # ponytail: 线性检查，无字典重建，无抽象层，若频率>10Hz 再优化
        // AddMessage 在 messagesBeforeCall 之后 → 属本轮新增；发送后 RemoveMessage
        // → 不被 SyncNewMessagesFromConversation 捕获，不持久化、不堆积。
        ChatMessage? nudgeMsg = null;
        if (stabilityMonitor is { IsFrozen: false, TotalTokens: > 0 })
        {
            var budget = stabilityMonitor.GetTokenBudget();
            if (budget > 0)
            {
                var ratio = (double)stabilityMonitor.TotalTokens / budget;
                // P0：阈值从 ModelContextProfile 动态推导，保证 info < warn < critical < preflight，
                // 修复硬编码 70/85/92% 与 pre-flight 动态阈值层级矛盾（8K 模型 92% 为死代码）。
                var critical = _modelProfile.NudgeCriticalPercent;
                var warn = _modelProfile.NudgeWarnPercent;
                var info = _modelProfile.NudgeInfoPercent;
                // ★ 模式互斥：提示文案不得交叉（compact → /compact；接续 → new_context）
                var continuation = _contextManagement is { Mode: ContextMode.Continuation };
                nudgeMsg =
                    ratio >= critical
                        ? new ChatMessage(
                            ChatMessageRoles.User,
                            BuildPressureNudge(PressureLevel.Critical, critical, continuation)
                        )
                    : ratio >= warn
                        ? new ChatMessage(
                            ChatMessageRoles.User,
                            BuildPressureNudge(PressureLevel.Warn, warn, continuation)
                        )
                    : ratio >= info
                        ? new ChatMessage(
                            ChatMessageRoles.User,
                            BuildPressureNudge(PressureLevel.Info, info, continuation)
                        )
                    : null;
                if (nudgeMsg is not null)
                    _conversation?.AddMessage(nudgeMsg);
            }
        }

        var handler = new ChatStreamEventHandler
        {
            // 收集 reasoning tokens
            ReasoningTokenHandler = async reasoning =>
            {
                ct.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(reasoning.Content))
                {
                    reasoningText.Append(reasoning.Content);
                    await emit(
                        new StreamChunk(
                            reasoning.Content,
                            reasoningText.ToString(),
                            StreamContentType.Thinking
                        ),
                        ct
                    );
                }
            },

            // 收集 text tokens
            MessageTokenHandler = async token =>
            {
                ct.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(token))
                {
                    // 首个 text token 表示 thinking 结束
                    if (fullText.Length == 0 && reasoningText.Length > 0)
                    {
                        await emit(new StreamChunk("", "", StreamContentType.ThinkingEnd), ct);
                    }
                    fullText.Append(token);
                    await emit(
                        new StreamChunk(token, fullText.ToString(), StreamContentType.Text),
                        ct
                    );
                }
            },

            // 处理 Function Calls（工具调用）
            FunctionCallHandler = async calls =>
            {
                ct.ThrowIfCancellationRequested();
                foreach (var call in calls)
                {
                    var id = call.ToolCall?.Id ?? Guid.NewGuid().ToString("N")[..12];
                    var name = call.Name;
                    var args = call.Arguments ?? "{}";
                    toolCallsCollected.Add((id, name, args));
                }

                // 根据工具执行模式选择并行或串行执行。
                // 全局 Parallel + 多调用 → 混合调度：ExecutionMode=Sequential 的写入类工具
                // 先逐个串行执行（避免并发读改写同一文件），其余 Parallel 工具再并发执行。
                if (toolExecution == ToolExecutionMode.Parallel && calls.Count > 1)
                {
                    var (sequential, parallel) = PartitionByExecutionMode(
                        calls,
                        call => _toolRegistry.TryGet(call.Name)
                    );

                    foreach (var call in sequential)
                        await ExecuteFunctionCallAsync(call, emit, ct);

                    if (parallel.Count > 0)
                    {
                        var tasks = parallel
                            .Select(call => ExecuteFunctionCallAsync(call, emit, ct))
                            .ToArray();
                        await Task.WhenAll(tasks);
                    }
                }
                else
                {
                    foreach (var call in calls)
                    {
                        await ExecuteFunctionCallAsync(call, emit, ct);
                    }
                }
            },

            // 所有 tool 执行完毕后
            AfterFunctionCallsResolvedHandler = async (resolved, _) =>
            {
                // ★ 取消守卫：如果已取消，抛出 OperationCanceledException 让 StreamResponseRich 终止
                ct.ThrowIfCancellationRequested();

                // 检查 circuit breaker
                CheckCircuitBreaker(toolCallsCollected, emit, ct);

                // 检查 ShouldStopAfterTurn（外部控制循环终止）
                if (shouldStopAfterTurn is not null && _loopNewMessages is not null)
                {
                    var lastAssistant = resolved.AssistantMessage;

                    var toolResultMessages = resolved
                        .ToolResults.Select(tr => tr.ToolMessage)
                        .ToList();

                    var shouldStop = await shouldStopAfterTurn(
                        lastAssistant,
                        toolResultMessages,
                        _loopNewMessages,
                        ct
                    );
                    CoreLogger.ShouldStopAfterTurnResult(logger.Safe(), shouldStop, 0);
                    if (shouldStop)
                    {
                        _stoppedByExternalControl = true;
                        return;
                    }
                }

                _continueLoop = !_stopAfterTool;
            },

            // Token 用量追踪（thinking-aware）
            OnUsageReceived = usage =>
            {
                if (ct.IsCancellationRequested)
                    return ValueTask.CompletedTask;
                // 提取 thinking tokens（如果可用）：
                // OpenAI/Google 通过 CompletionTokensDetails.ReasoningTokens 暴露，
                // Anthropic 将 thinking 计入 OutputTokens 但不单独拆分 ReasoningTokens。
                var thinkingTokens = usage.CompletionTokensDetails?.ReasoningTokens ?? 0;
                _lastContextTokens = usage.PromptTokens + usage.CompletionTokens;

                // ★ 稳定性监控使用"本轮增量"而非全量 prompt+completion：
                // 全量会重复累计（每轮都含全部历史），导致 TotalTokens 虚高撞破预算。
                // 增量 = 上下文增长 + 本轮输出，TotalTokens = seed + Σ增量 ≈ 当前上下文占用。
                var incrementalTokens = ComputeTurnIncrement(
                    _lastPromptTokens,
                    usage.PromptTokens,
                    usage.CompletionTokens,
                    seeded: stabilityMonitor is not null && stabilityMonitor.TotalTokens > 0
                );
                _lastPromptTokens = usage.PromptTokens;
                stabilityMonitor?.RecordStep(incrementalTokens, thinkingTokens);
                _ = emit(new TokenUsageEvent(usage.PromptTokens, usage.CompletionTokens), ct);
                return ValueTask.CompletedTask;
            },
        };

        // 发送 MessageStart 事件（助手消息占位）
        var assistantPlaceholder = new ChatMessage(ChatMessageRoles.Assistant);
        await emit(new MessageStart(assistantPlaceholder), ct);

        try
        {
            // 调用 StreamResponseRich 进行流式 LLM 调用。
            // 有界瞬态重试（429/5xx/网络瞬断）：仅当本轮尚未产出任何文本/推理/工具调用
            // （幂等保护，避免重复输出/重复工具调用）且未超过总窗口时重试；指数退避 + jitter
            // （LlmTornado 异常不带响应头 → 拿不到 Retry-After，用 LlmRetryDelay 近似）。
            // ★ finally 包裹整个 while：无论成功/重试耗尽/异常，还原 ref 标签 + 移除 nudge
            // 都发生在 SyncNewMessagesFromConversation 之前，标签与 nudge 不会进入持久化消息。
            try
            {
                var retryWatch = Stopwatch.StartNew();
                var llmAttempt = 0;
                while (true)
                {
                    llmAttempt++;
                    try
                    {
                        await _conversation!.StreamResponseRich(handler, ct);
                        break; // 流式成功 → 退出重试循环
                    }
                    catch (Exception ex)
                        when (!ct.IsCancellationRequested
                            && llmAttempt < MaxLlmTurnRetryAttempts
                            && retryWatch.Elapsed < LlmTurnRetryBudget
                            && CanRetryLlmTurn(
                                ex,
                                llmAttempt,
                                MaxLlmTurnRetryAttempts,
                                fullText.Length > 0
                                    || reasoningText.Length > 0
                                    || toolCallsCollected.Count > 0
                            )
                        )
                    {
                        var delay = LlmRetryDelay(llmAttempt);
                        var (httpStatusCode, _, _) = ExtractLlmError(ex);
                        CoreLogger.LlmTurnTransientRetry(
                            logger.Safe(),
                            llmAttempt,
                            httpStatusCode,
                            delay.TotalMilliseconds
                        );
                        await Task.Delay(delay, ct);
                    }
                }
            }
            finally
            {
                foreach (var (msg, original) in injectedRefTags)
                    msg.Content = original;
                if (nudgeMsg is not null)
                    _conversation?.RemoveMessage(nudgeMsg);
                if (windowMsg is not null)
                    _conversation?.RemoveMessage(windowMsg);
                if (budgetMsg is not null)
                    _conversation?.RemoveMessage(budgetMsg);
            }

            // 从 Conversation.Messages 同步新增消息
            var newMessages = SyncNewMessagesFromConversation(messagesBeforeCall);

            // 修复 LlmTornado bug: 当 FinishReason==ToolCalls 时，
            // delta.Content 被设为 API 的 object 字段值（如 "chat.completion"），
            // 这不是有效文本内容，会污染下一轮 API 请求
            foreach (var msg in newMessages)
            {
                if (msg is { Role: ChatMessageRoles.Assistant, ToolCalls.Count: > 0 })
                {
                    msg.Content = null;
                }
            }

            // 修复关键 bug：LlmTornado 的 Conversation.Messages 内部构建的 ChatMessage
            // 不携带 ReasoningContent——reasoning 只通过 ReasoningTokenHandler 回调收集
            // 到 reasoningText StringBuilder 中。必须手动补回，否则持久化到 DB 后
            // 历史记录加载时 thinking 内容丢失。
            //
            // 典型场景：用户提问 → LLM 先 thinking → 调用 tool → 再 thinking → 输出文本
            // 第一个 thinking 在 Conversation 的 assistant message 里完全不存在，
            // 导致 SaveSessionMessages 时 ReasoningContent 为 null，加载后丢失。
            PatchReasoningContent(newMessages, reasoningText.ToString());

            // 写入本轮上下文 token 锚点：锚点值 = 本轮 API 调用的完整上下文 token 数。
            // 持久化时由 FileSessionStore 写入 JsonlMessageEntry.ContextTokens，
            // 会话恢复时作为精确锚点（锚点之前的消息不再启发式估算）。
            if (_lastContextTokens is > 0)
            {
                var lastAssistant = newMessages.LastOrDefault(m =>
                    m.Role == ChatMessageRoles.Assistant
                );

                // ★ 统一锚点语义：只保留本轮最后一条 assistant 的完整 usage（prompt+completion）。
                // LlmTornado 流式过程中会给 user 消息 stamp PromptTokens、assistant 消息 stamp
                // CompletionTokens（Conversation.cs），混入持久化后恢复估算可能把单次 completion
                // 误当上下文锚点。清掉本轮其他消息的零散 stamp。
                foreach (var msg in newMessages)
                    msg.Tokens = null;

                lastAssistant?.Tokens = _lastContextTokens;
                _lastContextTokens = null;
            }

            // 修复 LLM 流式输出截断导致的 ToolCall.Arguments JSON 损坏。
            // 如果不修复，损坏的 arguments 会被发回 LLM API，导致 500 错误。
            JsonRepairUtil.RepairToolCallArguments(newMessages, logger);

            // 将新消息添加到持久化列表和返回值列表
            foreach (var msg in newMessages)
            {
                _persistedMessages.Add(msg);
                _loopNewMessages?.Add(msg);
            }

            // ★ 及时落盘：这一轮（含工具调用轮）的消息已经完整归一化（reasoning/args/tokens），
            // 立即写入 FileSessionStore，不等整个 Agent loop 停止。
            await NotifyMessagesReadyAsync(ct);

            // 发射 MessageEnd 和 TurnEnd 事件
            var finalMessage =
                newMessages.LastOrDefault(m => m.Role == ChatMessageRoles.Assistant)
                ?? BuildAssistantMessage(fullText, reasoningText, toolCallsCollected);

            // ★ GuardRail 输出检查：在 MessageEnd 之前
            if (guardRail is not null && !string.IsNullOrEmpty(finalMessage.Content))
            {
                var outputResult = await guardRail.CheckOutputAsync(finalMessage.Content, ct);
                if (!outputResult.Passed)
                {
                    if (outputResult.Tripwire)
                    {
                        CoreLogger.GuardRailTriggered(logger.Safe(), "output", outputResult.Reason);
                        // tripwire：不发射 MessageEnd，直接中断循环
                        throw new GuardRailTriggeredException(
                            $"输出 GuardRail 触发：{outputResult.Reason}"
                        );
                    }
                    // 非 tripwire：仅记录警告
                    CoreLogger.GuardRailWarning(logger.Safe(), "output", outputResult.Reason);
                }
            }

            return new TurnOutcome(
                toolCallsCollected.Count,
                fullText.Length > 0,
                newMessages.Count
            );
        }
        catch (OperationCanceledException)
        {
            // ★ 用户中断：尽力落盘 _persistedMessages 中尚未写入的消息，
            // 避免中断前已完成的工具调用结果丢失
            await SafelyPersistAsync();
            throw;
        }
        catch (IOException ex) when (ct.IsCancellationRequested)
        {
            // ★ 取消导致的流关闭：同 OperationCanceledException 处理
            await SafelyPersistAsync();
            throw new OperationCanceledException(ex.Message, ex, ct);
        }
        catch (BudgetExhaustedException ex)
        {
            // Token 预算耗尽：与 ContextOverflow 同等处理——触发 auto-compact + retry。
            // BudgetExhaustedException 来自 StabilityMonitor.RecordStep（OnUsageReceived 回调），
            // 不是 LLM API 返回的 400 错误，但本质相同：上下文太长，需要压缩。
            await SafelyPersistAsync();
            await emit(
                new AgentError(ex.Message, null, IsContextOverflow: true, Recoverable: true),
                CancellationToken.None
            );
            throw new ContextOverflowException(ex.Message, ex);
        }
        catch (ContextOverflowException)
        {
            // ★ 上下文溢出：落盘后传递，TuiApp 负责 compact + retry
            await SafelyPersistAsync();
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // LLM 调用错误：提取 HTTP 状态码和原始消息，发射 AgentError 事件。
            // ★ 含 IOException（网络断连/远端强制关闭连接）：用户未取消时的 IO 异常
            //   不能静默逃逸——必须 emit AgentError 让 UI 停止 spinner 并展示错误，
            //   否则用户看到无限转圈。网络类错误视为可恢复（recoverable=true）。
            var (httpStatusCode, rawMessage, isContextOverflow) = ExtractLlmError(ex);
            var recoverable =
                isContextOverflow
                || httpStatusCode is >= 500 and < 600
                || httpStatusCode == 429
                || IsTransientNetworkError(ex);

            // ★ 错误路径：尽力落盘已就绪消息（含本轮之前 inject/steer + 上一轮 turn），
            // 避免进程崩溃或重试时丢失已确认的数据
            await SafelyPersistAsync();

            // 发射错误事件（ct 可能已部分取消，用 CancellationToken.None 确保事件送达）
            await emit(
                new AgentError(rawMessage, httpStatusCode, isContextOverflow, recoverable),
                CancellationToken.None
            );

            // Context overflow 包装为专用异常，供 TuiApp 触发 auto-compact + retry
            if (isContextOverflow)
                throw new ContextOverflowException(rawMessage, ex, httpStatusCode);

            throw;
        }
    }

    /// <summary>
    /// 从 Conversation.Messages 同步新增消息。
    /// 通过比较调用前后的消息数量来确定新增消息。
    /// </summary>
    /// <param name="messagesBeforeCall">调用前的消息数量。</param>
    /// <returns>新增的消息列表。</returns>
    private List<ChatMessage> SyncNewMessagesFromConversation(int messagesBeforeCall)
    {
        var newMessages = new List<ChatMessage>();
        var conversationMessages = _conversation?.Messages;

        if (conversationMessages is null)
            return newMessages;

        for (var i = messagesBeforeCall; i < conversationMessages.Count; i++)
        {
            newMessages.Add(conversationMessages[i]);
        }

        return newMessages;
    }

    /// <summary>
    /// 将 ReasoningTokenHandler 收集的 reasoning 内容补回 Conversation 返回的 assistant 消息。
    ///
    /// LlmTornado 的 Conversation.StreamResponseRich 内部构建的 ChatMessage 不携带
    /// ReasoningContent（reasoning 只通过 ReasoningTokenHandler 回调单独收集）。
    /// 如果不补回，持久化到 DB 后历史加载时 thinking 内容丢失。
    ///
    /// 策略：找到本次 turn 中第一个缺少 ReasoningContent 的 assistant 消息，
    /// 将 reasoningText 整体赋值给它。这覆盖了最常见场景：
    /// - thinking → tool call（一条 assistant msg，无 ReasoningContent）
    /// - thinking → text（一条 assistant msg，无 ReasoningContent）
    /// </summary>
    /// <param name="newMessages">新增的消息列表。</param>
    /// <param name="reasoningText">收集到的 reasoning 文本。</param>
    private static void PatchReasoningContent(List<ChatMessage> newMessages, string reasoningText)
    {
        if (string.IsNullOrEmpty(reasoningText))
            return;

        foreach (var msg in newMessages)
        {
            if (
                msg.Role == ChatMessageRoles.Assistant
                && string.IsNullOrEmpty(msg.ReasoningContent)
            )
            {
                // 优先设置 ReasoningContent（简单消息格式）
                msg.ReasoningContent = reasoningText;

                if (msg.Parts is not null && msg.Parts.Count > 0)
                {
                    // 检查是否已有 Reasoning Part（Conversation 自动构建的可能已包含带 Signature 的版本）
                    var hasReasoningPart = msg.Parts.Any(p => p.Type == ChatMessageTypes.Reasoning);

                    if (!hasReasoningPart)
                    {
                        // 只在没有时才插入新的 Reasoning Part
                        msg.Parts.Insert(
                            0,
                            new ChatMessagePart
                            {
                                Type = ChatMessageTypes.Reasoning,
                                Reasoning = new ChatMessageReasoningData
                                {
                                    Content = reasoningText,
                                },
                            }
                        );
                    }
                    // 如果已有 Reasoning Part，不覆盖——保留 Conversation 构建的带 Signature 版本
                }

                // 只补第一个缺少 reasoning 的 assistant 消息
                // （一次 StreamResponseRich 通常只产生一个 assistant message）
                return;
            }
        }
    }

    /// <summary>
    /// 按工具执行模式将一轮调用划分为串行组（Sequential）与并行组（Parallel）。
    /// 串行组保持原调用顺序；未注册/未知工具按 Parallel 处理（读取类工具默认并行）。
    /// </summary>
    internal static (List<T> Sequential, List<T> Parallel) PartitionByExecutionMode<T>(
        IEnumerable<T> calls,
        Func<T, AgentTool?> toolOf
    )
    {
        var sequential = new List<T>();
        var parallel = new List<T>();
        foreach (var call in calls)
        {
            var tool = toolOf(call);
            if (tool is not null && tool.ExecutionMode == ToolExecutionMode.Sequential)
                sequential.Add(call);
            else
                parallel.Add(call);
        }
        return (sequential, parallel);
    }

    /// <summary>
    /// 执行单个 AgentTool（工具调用）。
    /// 包括参数解析、novel_id 注入、钩子调用和结果处理。
    /// </summary>
    private async Task ExecuteFunctionCallAsync(
        FunctionCall call,
        Func<AgentEvent, CancellationToken, Task> emit,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();

        var toolCallId = call.ToolCall?.Id ?? Guid.NewGuid().ToString("N")[..12];
        var toolName = call.Name;
        var arguments = call.Arguments ?? "{}";

        var tool = _toolRegistry.TryGet(toolName);

        if (tool is null)
        {
            CoreLogger.ToolNotFound(logger.Safe(), toolName);
            call.Result = new FunctionResult(
                call,
                [new FunctionResultBlockText($"错误：未找到工具「{toolName}」")],
                invocationSucceeded: false
            );
            await emit(new ToolExecutionEnd(toolCallId, true, $"未找到工具「{toolName}」"), ct);
            return;
        }

        CoreLogger.ToolExecutionStart(logger.Safe(), toolName, toolCallId);
        await emit(new ToolExecutionStart(toolCallId, toolName, arguments), ct);

        ct.ThrowIfCancellationRequested();

        // 解析工具参数
        JsonElement args;
        try
        {
            args = JsonSerializer.Deserialize<JsonElement>(arguments);
        }
        catch
        {
            // JSON 解析失败 — 可能是 LLM 流式输出被截断，尝试修复
            CoreLogger.ToolCallArgsParseFailed(logger.Safe(), toolName);
            var repaired = JsonRepairUtil.TryRepairJson(arguments);
            try
            {
                args = JsonSerializer.Deserialize<JsonElement>(repaired);
            }
            catch
            {
                args = JsonSerializer.Deserialize<JsonElement>("{}");
            }
        }

        // 自动注入当前 novel_id：如果 tool 参数里有 novel_id 字段且为空，
        // 用 Agent.CurrentNovelId 填充
        if (agent?.CurrentNovelId is not null)
        {
            args = InjectNovelId(args, agent.CurrentNovelId);
        }

        // Before hook（工具调用前钩子）
        if (beforeToolCall is not null)
        {
            ct.ThrowIfCancellationRequested();

            var hookContext = new ToolHookContext
            {
                ToolCallId = toolCallId,
                ToolName = toolName,
                Arguments = arguments,
                Agent = agent,
            };
            var beforeResult = await beforeToolCall.BeforeToolCallAsync(hookContext, ct);
            if (beforeResult.Block)
            {
                CoreLogger.ToolBlockedByHook(
                    logger.Safe(),
                    toolName,
                    beforeResult.Reason ?? "blocked"
                );
                call.Result = new FunctionResult(
                    call,
                    [
                        new FunctionResultBlockText(
                            $"工具执行被阻止：{beforeResult.Reason ?? "未指定原因"}"
                        ),
                    ],
                    invocationSucceeded: false
                );

                await emit(
                    new ToolExecutionEnd(toolCallId, true, beforeResult.Reason ?? "blocked"),
                    ct
                );
                return;
            }
        }

        ct.ThrowIfCancellationRequested();

        // 执行工具
        AgentToolResult? toolResult;
        try
        {
            var progress = new Progress<AgentToolResult>(update =>
            {
                _ = emit(
                    new ToolExecutionUpdate(toolCallId, ToolDispatcher.ExtractText(update.Content)),
                    ct
                );
            });

            using (
                var writeScope = OntologyWriteContext.Begin(
                    Core.Data.AuditSource.Ai,
                    toolName,
                    agent?.SessionId
                )
            )
            {
                toolResult = await tool.ExecuteAsync(toolCallId, args, ct, progress);
            }
            if (toolResult.Terminate)
                _stopAfterTool = true; // any tool in a parallel batch can stop the loop

            // After hook（工具调用后钩子）
            if (afterToolCall is not null)
            {
                ct.ThrowIfCancellationRequested();

                var hookContext = new ToolHookContext
                {
                    ToolCallId = toolCallId,
                    ToolName = toolName,
                    Arguments = arguments,
                    Result = toolResult,
                    Agent = agent,
                };
                await afterToolCall.AfterToolCallAsync(hookContext, ct);
            }

            await emit(
                new ToolExecutionEnd(
                    toolCallId,
                    false,
                    ToolDispatcher.ExtractText(toolResult.Content),
                    toolResult.Details
                ),
                ct
            );
        }
        catch (OperationCanceledException)
        {
            throw; // 让取消信号向上传播，不要吞噬
        }
        catch (Exception ex)
        {
            CoreLogger.ToolExecutionFailed(logger.Safe(), toolName, ex.Message);
            toolResult = new AgentToolResult
            {
                Content = [new TextContent($"工具执行失败：{ex.Message}")],
                IsError = true,
            };
            await emit(new ToolExecutionEnd(toolCallId, true, ex.Message), ct);
        }

        // 设置 FunctionCall.Result，供 LlmTornado 后续处理
        // ★ 统一截断点：resultText 进 Conversation（内存）→ 落库 → 重建 三路径同源，
        // 字节一致保证 provider 前缀缓存稳定；超长结果（章节全文/关系图）不整段占上下文。
        // P1：阈值随模型上下文窗口动态化（ScaledToolResultMaxChars），超大结果写缓存文件。
        var resultText = await PrepareToolResultForContextAsync(
            ToolDispatcher.ExtractText(toolResult.Content),
            ct
        );
        call.Result = new FunctionResult(
            call,
            [new FunctionResultBlockText(resultText)],
            invocationSucceeded: !toolResult.IsError
        );
    }

    /// <summary>
    /// 工具结果进入上下文前的统一截断（P1）：超长结果保留头尾 + 规模标记，完整原文写入缓存文件。
    /// 阈值随模型上下文窗口动态化（ScaledToolResultMaxChars），修复 8K/1M 模型下 10,000 硬编码失衡。
    /// 在 resultText 生成处截断 → 内存/落库/重建三路径字节一致（provider 前缀缓存稳定）。
    /// 超大结果写缓存文件（内容寻址，确定性路径），模型可 read 恢复，避免硬截断丢失细节。
    /// </summary>
    private async Task<string> PrepareToolResultForContextAsync(string text, CancellationToken ct)
    {
        var maxChars = _modelProfile.ScaledToolResultMaxChars;
        if (text.Length <= maxChars)
            return text;

        string? cachePath = null;
        try
        {
            cachePath = await WriteToolResultCacheAsync(text, ct);
        }
        catch
        {
            // 写缓存失败不阻断工具结果返回（降级为纯截断）
        }

        var truncated = TruncateHeadTail(text, maxChars);
        return cachePath is null
            ? truncated
            : $"完整内容已写入: {cachePath}（{text.Length:N0} 字符）\n{truncated}";
    }

    /// <summary>
    /// 头尾截断纯函数（P1）：保留头尾各 maxChars*2/5，中间省略标记 + 行数。
    /// 独立静态以便单测；maxChars 动态化后核心逻辑不变（maxChars=10,000 时头尾各 4,000，兼容旧行为）。
    /// </summary>
    internal static string TruncateHeadTail(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;

        var headTailChars = Math.Max(1, maxChars * 2 / 5);
        var lineCount = 1 + text.Count(ch => ch == '\n');

        var head = text[..headTailChars];
        var tail = text[^headTailChars..];
        var omitted = text.Length - headTailChars * 2;
        return $"Total output lines: {lineCount}\n{head}\n…[省略 {omitted} 字符]…\n{tail}";
    }

    /// <summary>写超大工具结果到缓存文件（内容寻址：SHA256 前缀，确定性路径，幂等覆盖）。</summary>
    private static async Task<string> WriteToolResultCacheAsync(string text, CancellationToken ct)
    {
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache",
            "mythology",
            "tool-results"
        );
        Directory.CreateDirectory(cacheDir);

        var hash = Convert
            .ToHexString(
                System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))
            )[..12]
            .ToLowerInvariant();
        var filePath = Path.Combine(cacheDir, $"tool-{hash}.txt");
        await File.WriteAllTextAsync(filePath, text, ct);
        return filePath;
    }

    /// <summary>
    /// 从 LLM 异常中提取结构化错误信息（委托到共享的 <see cref="LlmErrorHelper"/>）。
    /// </summary>
    private static (int? httpStatusCode, string rawMessage, bool isContextOverflow) ExtractLlmError(
        Exception ex
    ) => LlmErrorHelper.Extract(ex);

    /// <summary>
    /// 判断异常是否为瞬态网络错误（可恢复，符合重试语义）。
    /// 覆盖：IOException / HttpIOException / 传输层 HttpRequestException
    /// （HTTP 断连、远端强制关闭连接、TLS 重置等，顶层或内层链上的 IO 异常，
    /// 以及无 HTTP 状态码的 HttpRequestException = 未收到响应，而非业务拒绝）。
    /// </summary>
    private static bool IsTransientNetworkError(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case IOException:
                case HttpRequestException { StatusCode: null }:
                    return true;
            }
        }
        return false;
    }

    /// <summary>单轮 LLM 调用最大尝试次数（1 次原始 + 2 次重试）。</summary>
    private const int MaxLlmTurnRetryAttempts = 3;

    /// <summary>LLM 瞬态重试总窗口（含退避等待），超时即放弃继续重试。</summary>
    private static readonly TimeSpan LlmTurnRetryBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 判定本次 LLM 调用失败是否值得重试。
    /// 幂等保护：仅当本轮尚未产出任何内容/推理/工具调用（<paramref name="anythingEmitted"/> 为 false）
    /// 且未超尝试上限时才重试；context overflow 属 400 语义（走 compact+retry），不在此重试。
    /// </summary>
    internal static bool CanRetryLlmTurn(
        Exception ex,
        int attempt,
        int maxAttempts,
        bool anythingEmitted
    )
    {
        if (attempt >= maxAttempts || anythingEmitted)
            return false;

        var (httpStatusCode, _, isContextOverflow) = ExtractLlmError(ex);
        if (isContextOverflow)
            return false;

        return httpStatusCode == 429
            || (httpStatusCode is >= 500 and < 600)
            || IsTransientNetworkError(ex);
    }

    /// <summary>LLM 重试延迟：1s 起指数退避（封顶 8s）+ 20% jitter。LlmTornado 异常不带 Retry-After → 无头可读。</summary>
    internal static TimeSpan LlmRetryDelay(int attempt)
    {
        var baseMs = Math.Min(1000d * Math.Pow(2, Math.Max(attempt - 1, 0)), 8000d);
        var jitterMs = Random.Shared.NextDouble() * baseMs * 0.2;
        return TimeSpan.FromMilliseconds(baseMs + jitterMs);
    }

    /// <summary>
    /// 检查 circuit breaker（断路器）。
    /// 如果检测到重复调用同一工具，则触发断路器并返回 true。
    /// </summary>
    private bool CheckCircuitBreaker(
        List<(string Id, string Name, string Arguments)> toolCalls,
        Func<AgentEvent, CancellationToken, Task> emit,
        CancellationToken ct
    )
    {
        if (_callHistory is null)
            return false;

        // 批量 tool calls 共享同一个 turnId，同一轮并行调用不算高频
        var currentTurnId = ++_turnId;
        foreach (var tc in toolCalls)
        {
            _callHistory.Add(
                new ToolDispatcher.ToolCallRecord(tc.Name, tc.Arguments, currentTurnId)
            );
            UsageStats.RecordCall(tc.Name);
        }

        var breakerResult = CircuitBreaker.Check(
            _callHistory,
            _modelProfile.ScaledCircuitBreakerThreshold
        );
        if (breakerResult is not null)
        {
            UsageStats.RecordBreakerTrigger();
            var loggerSafe = logger.Safe();
            CoreLogger.CircuitBreakerTriggered(
                loggerSafe,
                breakerResult.ToolName,
                breakerResult.RepeatCount,
                breakerResult.Reason
            );

            _ = emit(breakerResult, ct);
            var breakerMsg = CreateBreakerMessage(breakerResult.Reason);
            _conversation!.AddMessage(breakerMsg);
            // 不直接 Add 到 _persistedMessages / _loopNewMessages：
            // breakerMsg 已进 Conversation，SyncNewMessagesFromConversation 会拾取，
            // 随后归一化 + NotifyMessagesReadyAsync 统一落盘。直接 Add 会导致重复。
            return true;
        }

        return false;
    }

    /// <summary>
    /// 构建 assistant 消息。
    /// 根据文本内容、reasoning 内容和工具调用构建完整的助手消息。
    /// </summary>
    private static ChatMessage BuildAssistantMessage(
        StringBuilder fullText,
        StringBuilder reasoningText,
        List<(string Id, string Name, string Arguments)> toolCalls
    )
    {
        var parts = new List<ChatMessagePart>();

        if (reasoningText.Length > 0)
        {
            parts.Add(
                new ChatMessagePart
                {
                    Type = ChatMessageTypes.Reasoning,
                    Reasoning = new ChatMessageReasoningData { Content = reasoningText.ToString() },
                }
            );
        }

        if (fullText.Length > 0)
        {
            parts.Add(
                new ChatMessagePart { Type = ChatMessageTypes.Text, Text = fullText.ToString() }
            );
        }

        if (toolCalls.Count > 0)
        {
            var llmToolCalls = toolCalls
                .Select(tc => new ToolCall
                {
                    Id = tc.Id,
                    Type = "function",
                    FunctionCall = new FunctionCall { Name = tc.Name, Arguments = tc.Arguments },
                })
                .ToList();

            var msg = new ChatMessage(ChatMessageRoles.Assistant, parts)
            {
                ToolCalls = llmToolCalls,
            };
            return msg;
        }

        return new ChatMessage(ChatMessageRoles.Assistant, parts);
    }

    /// <summary>创建稳定性监控触发消息。</summary>
    private static ChatMessage CreateStabilityMessage(string reason)
    {
        return new ChatMessage(
            ChatMessageRoles.Assistant,
            [
                new ChatMessagePart
                {
                    Type = ChatMessageTypes.Text,
                    Text = $"[系统] 稳定性监控触发：{reason}",
                },
            ]
        );
    }

    /// <summary>
    /// 采样当前任务段的助手文本：取最近一条用户消息之后的助手内容，最多 maxCount 条。
    /// 早于最近用户消息的助手内容（如已被用户回答过的澄清问题、上一段汇报）是过期片段——
    /// 方向已被新指令重置，不应参与 VSA 漂移度量。
    /// 返回按时间正序排列（最新在最后），与下游 string.Concat 语义一致。
    /// </summary>
    internal static List<string> SampleCurrentSegmentAssistantText(
        IReadOnlyList<ChatMessage> messages,
        int maxCount
    )
    {
        var lastUserIndex = -1;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == ChatMessageRoles.User)
            {
                lastUserIndex = i;
                break;
            }
        }

        var result = new List<string>(Math.Min(maxCount, messages.Count));
        for (var i = messages.Count - 1; i > lastUserIndex && result.Count < maxCount; i--)
        {
            var m = messages[i];
            if (m.Role == ChatMessageRoles.Assistant && !string.IsNullOrEmpty(m.Content))
                result.Add(m.Content!);
        }
        result.Reverse();
        return result;
    }

    /// <summary>创建断路器触发消息。</summary>
    private static ChatMessage CreateBreakerMessage(string reason)
    {
        return new ChatMessage(
            ChatMessageRoles.Assistant,
            [
                new ChatMessagePart
                {
                    Type = ChatMessageTypes.Text,
                    Text = $"[系统] Circuit breaker 触发：{reason}",
                },
            ]
        );
    }

    /// <summary>
    /// 计算稳定性监控的本轮增量 token 消耗。
    /// 全量 prompt 会重复累计（每轮都含全部历史），增量 = 上下文增长 + 本轮输出。
    /// - 首次调用且有 seed（会话恢复）：seed 已代表调用前上下文，增量 = 本轮输出
    /// - 首次调用无 seed（新会话）：增量 = 完整 prompt + 输出（从 0 建立基线）
    /// - 后续调用：增量 = prompt 差 + 输出；compact/模型切换后 prompt 缩小，
    ///   增量为负，TotalTokens 自然回落（增长检测对负值安全，不会误触发）。
    /// </summary>
    internal static int ComputeTurnIncrement(
        int? lastPromptTokens,
        int promptTokens,
        int completionTokens,
        bool seeded
    )
    {
        if (lastPromptTokens is null)
            return seeded ? completionTokens : promptTokens + completionTokens;
        return promptTokens - lastPromptTokens.Value + completionTokens;
    }

    /// <summary>
    /// 为消息列表注入 [mNNNN] ref 标签（B3）。纯函数：修改消息 Content 并记录还原信息。
    ///
    /// 两种模式：
    /// - entryIdProvider 非 null（B3 稳定）：EntryId → refNo 映射 + nextMsgRef 全局递增不回收。
    ///   EntryId 持久化不变 → 压缩/恢复后保留消息的 ref 稳定；新消息分配新号；无 EntryId
    ///   （运行时新消息/摘要）跳过。
    /// - entryIdProvider null（默认）：按序编号，每轮从 0 重新分配（无稳定身份，退化行为）。
    ///
    /// 跳过规则：非 user 消息、空内容、已注入标签（[m 前缀）、压缩摘要消息（[上下文摘要 前缀）。
    /// </summary>
    /// <returns>(消息, 原内容) 列表，调用方在发送后还原 Content。</returns>
    internal static List<(ChatMessage Msg, string? Original)> InjectRefTags(
        IEnumerable<ChatMessage> messages,
        Func<ChatMessage, string?>? entryIdProvider,
        Dictionary<string, int> refByEntryId,
        ref int nextMsgRef
    )
    {
        var injected = new List<(ChatMessage Msg, string? Original)>(16);
        var refNo = 0; // 无 provider 模式的按序编号（每轮从 0）

        foreach (var msg in messages)
        {
            if (msg.Role != ChatMessageRoles.User || string.IsNullOrEmpty(msg.Content))
                continue;
            if (msg.Content.StartsWith("[m", StringComparison.Ordinal))
                continue; // 已注入
            if (msg.Content.StartsWith("[上下文摘要", StringComparison.Ordinal))
                continue; // 压缩摘要消息（用 blockId 定位，不占 ref）

            if (entryIdProvider is not null)
            {
                var entryId = entryIdProvider(msg);
                if (string.IsNullOrEmpty(entryId))
                    continue; // 运行时新消息（尚未持久化，无 EntryId）不注入
                if (!refByEntryId.TryGetValue(entryId, out refNo))
                {
                    refNo = nextMsgRef++;
                    refByEntryId[entryId] = refNo;
                }
            }

            injected.Add((msg, msg.Content));
            msg.Content = $"[m{refNo++:D4}] {msg.Content}";
        }

        return injected;
    }

    /// <summary>
    /// 估算消息列表的 token 占用，锚点优先 + 中文感知。
    ///
    /// 锚点优先（对齐 pi / Claude Code 的混合策略）：
    /// 从后往前找最后一条携带 Tokens（持久化的真实 usage）的 assistant 消息，
    /// 锚点之前的上下文用精确的 usage 值，只有锚点之后的尾部消息才做启发式估算。
    /// 无锚点时（旧会话数据）退化为全量估算。
    ///
    /// 中文感知：CJK 字符 ≈ 1 token/字，ASCII ≈ 4 字符/token。
    /// 修正 chars/4 对中文严重低估的问题（中文 100 字 ≈ 100 token，而非 25）。
    /// </summary>
    /// <remarks>
    /// B6 估算口径统一：此方法作为唯一估算入口，TuiApp 的 EstimateContextTokens
    /// 已改调此处（锚点优先 + CJK 感知），不再使用 chars×2/3 粗估算，避免双轨漂移。
    /// </remarks>
    public static long EstimateMessagesTokens(IReadOnlyList<ChatMessage> messages)
    {
        long trailing = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var msg = messages[i];
            if (msg.Tokens is > 0)
                return msg.Tokens.Value + trailing; // 锚点：精确上下文 + 已累计的尾部估算
            trailing += EstimateSingleMessageTokens(msg);
        }
        return trailing;
    }

    /// <summary>估算单段文本的 token 占用（CJK ≈ 1 token/字，ASCII ≈ 4 字符/token）。</summary>
    public static long EstimateTextTokens(string? text)
    {
        long cjk = 0,
            ascii = 0;
        CountChars(text, ref cjk, ref ascii);
        return cjk + (long)Math.Ceiling(ascii / 4.0);
    }

    /// <summary>估算单条消息的 token 占用（含 Content、ReasoningContent、ToolCall 参数）。</summary>
    public static long EstimateSingleMessageTokens(ChatMessage msg)
    {
        var tokens = EstimateTextTokens(msg.Content);
        tokens += EstimateTextTokens(msg.ReasoningContent);
        if (msg.ToolCalls is not null)
        {
            tokens += msg.ToolCalls.Sum(tc => EstimateTextTokens(tc.FunctionCall?.Arguments));
        }
        return tokens;
    }

    /// <summary>
    /// 统计字符串中的 CJK 与 ASCII 字符数。
    /// CJK 判定：U+2E80 起（含 CJK 部首/汉字/假名/韩文），空白不计数。
    /// </summary>
    private static void CountChars(string? text, ref long cjk, ref long ascii)
    {
        if (string.IsNullOrEmpty(text))
            return;
        foreach (var ch in text)
        {
            if (ch >= 0x2E80)
                cjk++;
            else if (!char.IsWhiteSpace(ch))
                ascii++;
        }
    }

    /// <summary>
    /// 动态更新 system prompt（不重建 Conversation）。
    /// 替换 Conversation 中的 system message，保留对话历史。
    /// </summary>
    /// <param name="systemPrompt">新的系统提示词。</param>
    public void UpdateSystemPrompt(string systemPrompt)
    {
        _systemPrompt = systemPrompt;

        _conversation?.SetSystemMessage(BuildSystemMessageWithDirectory(_systemPrompt));
    }

    /// <summary>
    /// 拼接完整 system message：骨架 + 用户自定义 + 动态工具目录。
    /// 工具目录由 ToolInjector 实时生成（只列未注入能力），
    /// 插件装卸/工具展开后重建 Conversation 时自动更新。
    /// </summary>
    private string BuildSystemMessageWithDirectory(string systemPrompt)
    {
        var skeleton = _contextManagement is { Mode: ContextMode.Continuation }
            ? PromptSkeletonContinuation
            : PromptSkeleton;
        var baseMessage = string.IsNullOrEmpty(systemPrompt)
            ? skeleton
            : string.Concat(skeleton, "\n\n", systemPrompt);

        var directory = toolInjector?.BuildToolDirectory();
        return string.IsNullOrWhiteSpace(directory)
            ? baseMessage
            : string.Concat(baseMessage, "\n\n", directory);
    }

    /// <summary>
    /// 创建 Conversation 并配置 ChatRequest。
    /// System prompt = 预计算骨架 + 用户自定义/小说上下文。
    /// </summary>
    private Conversation CreateConversation()
    {
        var request = BuildChatRequest();
        var conversation = _api.Chat.CreateConversation(request);
        if (toolInjector is not null)
            _lastToolsSignature = toolInjector.ToolsSignature;

        // System prompt = 预计算骨架 + 用户自定义/小说上下文 + 动态工具目录
        // 骨架（角色定义→交互规范→写作流程→去AI味）只计算一次
        // 用户自定义放最后（LLM recency bias，上下文信息更有效）
        conversation.AddSystemMessage(BuildSystemMessageWithDirectory(_systemPrompt));

        // ── 注册系统提示词为 VSA 不变量（上下文漂移检测） ──
        if (holographicEngine is not null)
        {
            try
            {
                var key = holographicEngine.EncodeText("system_prompt");
                var val = holographicEngine.EncodeText(
                    BuildSystemMessageWithDirectory(_systemPrompt)
                );
                holographicEngine.RegisterInvariant("system_prompt", key, val);
            }
            catch
            {
                // VSA 注册失败不阻断对话
            }
        }

        return conversation;
    }

    /// <summary>
    /// 预计算的 Prompt 骨架：由 PromptAssembly 按序装配所有段。
    /// 段清单见 PromptAssembly.SegmentKeys；可在组装器按 Key 排除（灰度/回退）。
    /// </summary>
    private static readonly string PromptSkeleton = PromptAssembly.Build(null);

    /// <summary>
    /// 接续模式的 Prompt 骨架：与 compact 骨架只差 writing 段第 16 条
    /// （compact 建议 /compact；接续直接调用 new_context，且不得向用户描述机制）。
    /// </summary>
    private static readonly string PromptSkeletonContinuation = PromptAssembly.Build(
        null,
        continuation: true
    );

    /// <summary>构建 ChatRequest，配置模型、工具、思考级别等。</summary>
    private ChatRequest BuildChatRequest()
    {
        var request = new ChatRequest
        {
            Model = _modelName,
            ReasoningFormat = ChatReasoningFormats.Parsed,
        };

        // 注册工具：ToolInjector 优先（按需注入），否则全量注入（向后兼容）
        var tools =
            toolInjector?.BuildRequestTools()
            ?? _toolRegistry.GetAll().OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();
        if (tools.Count > 0)
        {
            var llmTools = new List<Tool>(tools.Count);
            foreach (var tool in tools)
            {
                if (tool.Parameters.HasValue)
                {
                    var schemaJson = tool.Parameters!.Value.GetRawText();
                    llmTools.Add(
                        new Tool(new ToolFunction(tool.Name, tool.Description, schemaJson))
                    );
                }
            }

            if (llmTools.Count > 0)
                request.Tools = llmTools;
        }

        // 配置思考级别
        if (!string.IsNullOrEmpty(thinkingLevel) && thinkingLevel != "off")
        {
            var tl = ThinkingLevelBudgets.FromString(thinkingLevel);
            var effortStr = ThinkingLevelBudgets.GetReasoningEffort(tl);

            request.ReasoningEffort = effortStr switch
            {
                "low" => ChatReasoningEfforts.Low,
                "high" => ChatReasoningEfforts.High,
                _ => ChatReasoningEfforts.Medium,
            };

            // Adaptive-only thinking 模型（Opus 4.7+, Claude 5）不接受 manual budget_tokens，
            // LlmTornado 会自动将 Enabled → Adaptive 并清除 BudgetTokens。
            // 这里为 manual thinking 模型计算 budget；adaptive-only 模型设为 0
            // （LlmTornado 会忽略此值，仅影响 ModelContextProfile 的语义正确性）。
            var budgetTokens = _modelProfile.IsAdaptiveThinking
                ? 0
                : ThinkingLevelBudgets.GetBudgetTokens(tl, _modelProfile.ContextWindowTokens);

            request.VendorExtensions = new ChatRequestVendorExtensions(
                new ChatRequestVendorAnthropicExtensions
                {
                    Thinking = new AnthropicThinkingSettings
                    {
                        BudgetTokens = budgetTokens,
                        Enabled = true,
                    },
                }
            );
        }

        return request;
    }

    /// <summary>
    /// 从 Agent.SteeringQueue 取出待投递的 steer 消息，应用配额和聚合。
    /// 每次调用会清空队列中的消息。
    /// 配额逻辑：≤ScaledMaxSteerPerTurn 条直接投递；超过则聚合为一条消息（避免上下文爆炸）。
    /// 聚合时 SteerId 置空（无法对应单一 waiting 占位）。
    /// </summary>
    private List<(ChatMessage Message, string? SteerId)> DequeueSteeringMessages()
    {
        if (agent is null)
            return [];

        var messages = agent.SteeringQueue.DequeueAllWithIds();
        if (messages.Count == 0)
            return [];

        // 配额限制：单次最多投递 ScaledMaxSteerPerTurn 条
        if (messages.Count <= _modelProfile.ScaledMaxSteerPerTurn)
            return [.. messages];

        // 超配额：聚合为一条，避免上下文爆炸。聚合消息无 SteerId。
        var aggregated = string.Join(
            "\n\n---\n\n",
            messages.Select((m, i) => $"[{i + 1}] {m.Message.Content ?? ""}")
        );
        return [(new ChatMessage(ChatMessageRoles.User, aggregated), null)];
    }

    /// <summary>
    /// 从 Agent.InjectQueue 取出待注入的系统通知消息（P0d）。
    /// 每次调用会清空队列。系统通知静默进入 LLM 上下文，不触发 UI 事件。
    /// </summary>
    private List<ChatMessage> DequeueInjectMessages()
    {
        if (agent is null)
            return [];

        return [.. agent.InjectQueue.DequeueAllWithIds().Select(m => m.Message)];
    }

    /// <summary>异步释放资源。</summary>
    public async ValueTask DisposeAsync()
    {
        _persistedMessages.Clear();
        await Task.CompletedTask;
    }

    /// <summary>
    /// 自动注入 novel_id：如果 tool 参数里有 novel_id 字段且为空/null，
    /// 用当前选择的 novel id 填充。
    /// </summary>
    /// <param name="args">工具参数。</param>
    /// <param name="novelId">当前小说 ID。</param>
    /// <returns>注入后的参数。</returns>
    private static JsonElement InjectNovelId(JsonElement args, string novelId)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return args;

        // 检查 novel_id 字段是否存在且为空
        if (!args.TryGetProperty("novel-id", out var prop))
            return args; // 没有 novel_id 字段，不注入

        if (
            prop.ValueKind == JsonValueKind.Null
            || (prop.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(prop.GetString()))
        )
        {
            var node = JsonNode.Parse(args.GetRawText())!;
            node["novel-id"] = novelId;
            return JsonSerializer.Deserialize<JsonElement>(node.ToJsonString());
        }

        return args; // novel_id 已有值，不覆盖
    }

    /// <summary>单个 LLM 回合的摘要，供 RunLoopAsync 检测异常停止路径。</summary>
    private sealed record TurnOutcome(int ToolCallCount, bool HadText, int NewMessageCount);
}
