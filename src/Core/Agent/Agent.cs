using System.Runtime.CompilerServices;
using Core.Inference;
using Core.Infrastructure;
using Core.Stability;
using Core.Tools;
using Core.Vector;
using LlmTornado.Chat;
using LlmTornado.Code;
using Microsoft.Extensions.Logging;

namespace Core.Agent;

/// <summary>
/// Agent 模块入口文件。
/// 定义 Agent 状态枚举和 Agent 核心类，负责协调 LLM 对话循环、工具调用、事件发布和会话管理。
/// </summary>
/// <summary>Agent 运行状态枚举。</summary>
public enum AgentStateEnum
{
    /// <summary>空闲状态，等待用户输入。</summary>
    Idle,

    /// <summary>正在运行中，处理用户请求。</summary>
    Running,
}

/// <summary>
/// Agent 核心类：状态机 + 事件发布。
/// 直接使用 LlmTornado.ChatMessage，无需转换。
/// 负责管理对话生命周期、工具注册、消息队列和会话持久化。
/// </summary>
public sealed class Agent : IAsyncDisposable
{
    private readonly ToolRegistry _toolRegistry; // 工具注册表
    private readonly List<ChatMessage> _messages = []; // 消息历史列表

    /// <summary>
    /// 当前会话消息历史（只读快照）。供 WritingFlowHook 的 SceneGoal 自动填充
    /// （HARNESS-CLOUD §16.4 优先级 2/3：从最近 Assistant/User 消息提取创作目标）读取。
    /// </summary>
    public IReadOnlyList<ChatMessage> Messages => _messages;
    private readonly ILogger? _logger; // 日志记录器
    private CancellationTokenSource _internalCts = new(); // 内部取消令牌源
    private CancellationHttpHandler? _httpHandler; // 追踪活跃 HTTP 响应的 handler
    private string _systemPrompt = ""; // 完整的系统提示词
    private string _userPrompt = ""; // 用户自定义提示词（来自配置文件，不变）
    private string _novelContext = ""; // 小说上下文（每次发消息前注入，可变）
    private string _modelName = ""; // 模型名称
    private string _thinkingLevel = "medium"; // 思考深度级别
    private List<AgentTool> _tools; // 当前激活的工具列表
    private bool _isStreaming; // 是否正在流式输出
    private ChatMessage? _streamMessage; // 当前流式消息
    private HashSet<string> _pendingToolCalls = []; // 待处理的工具调用集合
    private string? _error; // 错误信息

    // ConversationLoop 实例，管理对话循环
    private ConversationLoop? _conversationLoop;

    // ── 配置属性 ──

    /// <summary>工具执行模式，默认并行执行。</summary>
    public ToolExecutionMode ToolExecution { get; set; } = ToolExecutionMode.Parallel;

    /// <summary>LlmTornado API 实例，必须设置才能运行。</summary>
    public LlmTornado.TornadoApi? TornadoApi { get; set; }

    /// <summary>工具调用前钩子，可在执行前拦截或修改。</summary>
    public IToolHook? BeforeToolCall { get; set; }

    /// <summary>工具调用后钩子，可在执行后处理结果。</summary>
    public IToolHook? AfterToolCall { get; set; }

    /// <summary>Agent 记忆存储（自动钩子用）。</summary>
    public AgentMemoryStore? MemoryStore { get; set; }

    /// <summary>引导消息队列，用于注入引导性指令。</summary>
    public SteeringQueue SteeringQueue { get; } = new();

    /// <summary>
    /// 系统通知队列（P0d）— CRUD/Hook/审计等系统生成消息走此队列，
    /// 静默注入 LLM 上下文，不触发 MessageStart/End 等 UI 事件（不显示转正气泡）。
    /// </summary>
    public SteeringQueue InjectQueue { get; } = new();

    /// <summary>
    /// 每个 turn 结束后调用。如果返回 true，循环优雅终止。
    /// 参数：最后一条助手消息、工具结果消息列表、新消息列表、取消令牌。
    /// </summary>
    public Func<
        ChatMessage,
        IReadOnlyList<ChatMessage>,
        List<ChatMessage>,
        CancellationToken,
        Task<bool>
    >? ShouldStopAfterTurn { get; set; }

    /// <summary>
    /// 每次 Run/Continue 开始前调用（可向 InjectQueue 写入零成本系统提示）。
    /// 钩子异常只记日志，不阻断本轮对话。
    /// </summary>
    public Func<CancellationToken, Task>? BeforeRun { get; set; }

    public string? CurrentNovelId
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                // 会话切换（不同小说）时清空 tool_search 展开缓存，避免旧小说的工具子集残留
                ToolInjector?.ClearExpanded();
                // 同时重置 tool_search 搜索频率熔断计数
                if (_toolRegistry.TryGet("tool_search") is ToolSearchTool searchTool)
                    searchTool.ResetSearchTrip();
            }
        }
    }

    /// <summary>获取底层 ConversationLoop 实例（用于模型切换时同步 ModelContextProfile）。</summary>
    internal ConversationLoop? ConversationLoop => _conversationLoop;

    /// <summary>
    /// 预热底层循环（Host 在首次 prompt 前需要循环已装配好接续回调与页状态；测试亦用）。
    /// 与 Prompt 路径共用同一创建入口，保证「创建时补推选项与页状态」这条不变量只有一份实现。
    /// </summary>
    internal ConversationLoop EnsureConversationLoop() => GetOrCreateConversationLoop();

    /// <summary>
    /// 重置 prompt token 基线（compact 后调用）。
    /// 委托到 ConversationLoop.ResetPromptBaseline：使下一轮增量计算从新基线起步，
    /// 避免 compact 后旧 _lastPromptTokens 导致大幅负增量压低监控器 TotalTokens。
    /// </summary>
    public void ResetPromptBaseline() => _conversationLoop?.ResetPromptBaseline();

    /// <summary>工具使用统计（从 ConversationLoop 透传）。为 null 表示循环尚未创建。</summary>
    public ToolUsageStats? UsageStats => _conversationLoop?.UsageStats;

    /// <summary>最近一次运行的循环停止原因（可恢复非异常停止）；null = 自然收尾或循环未创建。</summary>
    public LoopStop? LastLoopStop => _conversationLoop?.LastStop;

    /// <summary>当前会话 ID，贯穿所有持久化操作。</summary>
    public string? SessionId { get; set; }

    /// <summary>稳定性监控器，用于检测异常增长模式。</summary>
    public IStabilityMonitor? StabilityMonitor { get; set; }

    /// <summary>VSA 全息引擎，用于检测上下文漂移。</summary>
    public IHolographicEngine? HolographicEngine { get; set; }

    /// <summary>消息评分器，用于智能压缩时评估信息密度。</summary>
    public IMessageScorer? MessageScorer { get; set; }

    /// <summary>
    /// B3 ref 稳定：从消息提取稳定身份（EntryId）的委托。
    /// TuiApp 装配时提供（用 LlmChatMessage.NativeObject 存 EntryId）；
    /// null 时 ConversationLoop 退回按序编号（每轮从 0）。
    /// </summary>
    public Func<ChatMessage, string?>? EntryIdProvider { get; set; }

    /// <summary>
    /// 每轮消息就绪时回调（用户消息入队后、每个 LLM/tool turn 结束后）。
    /// 外部持久化层（如 TuiApp/FileSessionStore）用它及时增量落盘，
    /// 不必等整个 Agent loop 停止。
    /// </summary>
    public Func<IReadOnlyList<ChatMessage>, CancellationToken, Task>? OnMessagesReady { get; set; }

    /// <summary>压缩状态（最小版：已压缩消息 ID 映射，支持可逆恢复）。</summary>
    /// <remarks>ponytail: 无字典索引，线性扫描 O(n)，消息量小足够。</remarks>
    public List<(
        string BlockId,
        List<string> MessageIds,
        string Summary
    )> CompressionBlocks { get; set; } = [];

    /// <summary>模型请求开启新页（由 new_context 工具置位，循环顶部交给 Host 执行）。</summary>
    public string? PendingNewContextReason { get; private set; }

    /// <summary>
    /// 模型自写的交接说明（可选）：一句话写清做到哪、下一步、未完成项。
    /// 换页时由 Host 取走写进页状态 → 出现在新页片段开头（新页不带对话历史，这是唯一的交接通道）。
    /// </summary>
    public string? PendingNewContextNote { get; private set; }

    /// <summary>请求开启新页（接续）。重复请求只保留首个原因 / 首条交接说明。</summary>
    public void RequestNewContextWindow(string reason, string? note = null)
    {
        PendingNewContextReason ??= reason;
        if (!string.IsNullOrWhiteSpace(note))
            PendingNewContextNote ??= note.Trim();
    }

    /// <summary>取走并清空待处理换页请求；无请求返回 null。</summary>
    public string? ConsumeNewContextRequest()
    {
        var reason = PendingNewContextReason;
        PendingNewContextReason = null;
        return reason;
    }

    /// <summary>取走并清空模型自写的交接说明；无则返回 null。</summary>
    public string? ConsumeNewContextNote()
    {
        var note = PendingNewContextNote;
        PendingNewContextNote = null;
        return note;
    }

    /// <summary>当前上下文页（Host 在换页 / 恢复会话时设置）。</summary>
    public ContextWindowState ContextWindowState { get; private set; } = ContextWindowState.First;

    /// <summary>设置页状态并同步到循环（循环会重置提醒与 prompt 基线）。</summary>
    public void SetContextWindowState(ContextWindowState state)
    {
        ContextWindowState = state ?? ContextWindowState.First;
        _conversationLoop?.SetContextWindowState(ContextWindowState);
    }

    /// <summary>
    /// 换页执行回调（Host 装配：写 window 条目 + 清空模型侧消息 + 记账）。
    /// 返回 false = 未换页（无会话 / 落盘失败），循环据此回退到溢出兜底路径。
    /// 触发时机由 ConversationLoop 决定（模型请求 / 自适应阈值 / pre-flight 前）。
    /// </summary>
    public Func<string, CancellationToken, Task<bool>>? OnNewContextRequested { get; set; }

    /// <summary>上下文管理选项（Host 从 config.yaml 装配；null = compact 模式）。</summary>
    public ContextManagementOptions? ContextManagement { get; private set; }

    /// <summary>应用上下文管理选项（compact 模式传 null）。</summary>
    public void ApplyContextManagement(ContextManagementOptions? options)
    {
        ContextManagement = options;
        _conversationLoop?.UpdateContextManagement(options);
    }

    /// <summary>
    /// GuardRail 语义检查器，用于在 LLM 调用前后检查输入/输出是否满足约束。
    /// null 表示不启用 GuardRail 检查。
    /// </summary>
    public IGuardRail? GuardRail { get; set; }

    /// <summary>
    /// 本地判定模型客户端（可选；`system-one.enabled=false` 时零请求）。
    /// 循环判定（停止/漂移/稳定性复核）走它；失败一律保持既有行为。
    /// </summary>
    public ISystemOneClient? SystemOne { get; set; }

    /// <summary>
    /// 工具注入器，控制每次 LLM 请求注入的工具子集。
    /// null 时使用全量注入（向后兼容）。
    /// </summary>
    public ToolInjector? ToolInjector { get; set; }

    /// <summary>
    /// 模型上下文能力 profile — 稳定器动态阈值的来源。
    /// 在模型切换时更新，传递给 ConversationLoop 使所有阈值随上下文窗口和 thinking 级别自适应。
    /// </summary>
    public ModelContextProfile? ModelProfile { get; set; }

    /// <summary>
    /// 应用 ModelContextProfile 到所有稳定器组件。
    /// 在模型切换、上下文窗口大小变更、thinking 级别变更时调用。
    /// 统一同步入口，避免调用方需要了解 StabilityHarness 内部类型。
    /// ConversationLoop.UpdateModelProfile 会将 profile 传播到所有注入的稳定器组件。
    /// </summary>
    public void ApplyModelProfile(ModelContextProfile profile)
    {
        ModelProfile = profile;

        // 同步 ConversationLoop（内部会传播到 stabilityMonitor/holographicEngine/messageScorer）
        _conversationLoop?.UpdateModelProfile(profile);

        // 同步 Agent 层的稳定器组件（可能与 ConversationLoop 的不同实例）
        StabilityMonitor?.ApplyProfile(profile);
        HolographicEngine?.ApplyProfile(profile);
        MessageScorer?.ApplyProfile(profile);
    }

    // ── 事件 ──

    /// <summary>Agent 事件回调，用于向外部（如 TUI）广播生命周期事件。</summary>
    public event Func<AgentEvent, CancellationToken, Task>? OnEvent;

    // ── 状态快照 ──

    /// <summary>当前 Agent 运行状态。</summary>
    public AgentStateEnum AgentStateEnum { get; private set; } = AgentStateEnum.Idle;

    /// <summary>是否正在运行中。</summary>
    public bool IsRunning => AgentStateEnum == AgentStateEnum.Running;

    /// <summary>获取当前 Agent 状态的完整快照。</summary>
    public AgentState State =>
        new()
        {
            SystemPrompt = _systemPrompt,
            UserPrompt = _userPrompt,
            NovelContext = _novelContext,
            ModelName = _modelName,
            ThinkingLevel = _thinkingLevel,
            Tools = _tools.ToArray(),
            Messages = _messages.ToArray(),
            IsStreaming = _isStreaming,
            StreamMessage = _streamMessage,
            PendingToolCalls = new HashSet<string>(_pendingToolCalls, StringComparer.Ordinal),
            Error = _error,
        };

    // ── 构造函数 ──

    /// <summary>
    /// 创建 Agent 实例。
    /// </summary>
    /// <param name="toolRegistry">工具注册表。</param>
    /// <param name="tornadoApi">LlmTornado API 实例（可选）。</param>
    /// <param name="logger">日志记录器（可选）。</param>
    public Agent(
        ToolRegistry toolRegistry,
        LlmTornado.TornadoApi? tornadoApi = null,
        ILogger? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(toolRegistry);
        _toolRegistry = toolRegistry;
        TornadoApi = tornadoApi;
        _tools = [.. toolRegistry.GetAll()];
        _logger = logger;
    }

    // ── 状态管理 API ──

    /// <summary>设置用户自定义的系统提示词。</summary>
    public void SetSystemPrompt(string prompt)
    {
        _userPrompt = prompt;
        _systemPrompt = BuildSystemPrompt();
    }

    /// <summary>
    /// 更新小说上下文（不重建 ConversationLoop，只更新 system prompt 中的上下文部分）。
    /// </summary>
    /// <param name="context">小说上下文文本。</param>
    public void SetNovelContext(string context)
    {
        _novelContext = context;
        _systemPrompt = BuildSystemPrompt();
        _conversationLoop?.UpdateSystemPrompt(_systemPrompt);
    }

    /// <summary>
    /// 构建完整的 system prompt：用户自定义 + 小说上下文。
    /// Prompt 骨架（角色定义/交互规范/写作流程/去AI味）由 ConversationLoop 负责，不在这里拼接。
    /// </summary>
    private string BuildSystemPrompt()
    {
        if (string.IsNullOrEmpty(_userPrompt) && string.IsNullOrEmpty(_novelContext))
            return "";
        if (string.IsNullOrEmpty(_userPrompt))
            return _novelContext;
        return string.IsNullOrEmpty(_novelContext)
            ? _userPrompt
            : string.Concat(_userPrompt, "\n\n", _novelContext);
    }

    /// <summary>设置模型名称。</summary>
    public void SetModelName(string modelName) => _modelName = modelName;

    /// <summary>设置思考深度级别。</summary>
    public void SetThinkingLevel(string level) => _thinkingLevel = level;

    /// <summary>设置当前激活的工具列表。</summary>
    public void SetTools(IEnumerable<AgentTool> tools) => _tools = [.. tools];

    /// <summary>从 ToolRegistry 刷新工具快照。</summary>
    public void RefreshTools() => _tools = [.. _toolRegistry.GetAll()];

    /// <summary>通过委托更新 Agent 状态。</summary>
    public void UpdateState(Action<AgentState> update)
    {
        var state = State;
        update(state);
        _userPrompt = state.UserPrompt;
        _novelContext = state.NovelContext;
        _systemPrompt = BuildSystemPrompt();
        _modelName = state.ModelName;
        _thinkingLevel = state.ThinkingLevel;
        _tools = [.. state.Tools];
        _isStreaming = state.IsStreaming;
        _streamMessage = state.StreamMessage;
        _pendingToolCalls = [.. state.PendingToolCalls];
        _error = state.Error;
    }

    /// <summary>追加单条消息到历史记录。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendMessage(ChatMessage message) => _messages.Add(message);

    /// <summary>替换所有消息并同步到 ConversationLoop。</summary>
    public void ReplaceMessages(IEnumerable<ChatMessage> messages)
    {
        _messages.Clear();
        _messages.AddRange(messages);
        _conversationLoop?.RestoreFromMessages(_messages);
    }

    /// <summary>清空所有消息并释放 ConversationLoop。</summary>
    public void ClearMessages()
    {
        _messages.Clear();
        _conversationLoop = null;
    }

    // ── 入口方法 ──

    /// <summary>发送文本消息给 Agent。</summary>
    public async Task Prompt(string text, CancellationToken ct = default)
    {
        var message = new ChatMessage(ChatMessageRoles.User, text);
        await Prompt(message, ct);
    }

    /// <summary>发送单条消息给 Agent。</summary>
    public async Task<ChatMessage[]> Prompt(ChatMessage message, CancellationToken ct = default)
    {
        return await Prompt([message], ct);
    }

    /// <summary>发送多条消息给 Agent。</summary>
    public async Task<ChatMessage[]> Prompt(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken ct = default
    )
    {
        return await RunInternalAsync(
            loop => loop.RunAsync(messages, EmitEventAsync, _internalCts.Token),
            ct
        );
    }

    /// <summary>继续当前对话（不追加新消息）。</summary>
    public async Task<ChatMessage[]> Continue(CancellationToken ct = default)
    {
        return await RunInternalAsync(
            loop => loop.ContinueAsync(EmitEventAsync, _internalCts.Token),
            ct
        );
    }

    // ── 核心运行逻辑 ──

    /// <summary>
    /// 内部运行方法：设置状态、创建取消令牌、执行对话循环、处理异常。
    /// </summary>
    private async Task<ChatMessage[]> RunInternalAsync(
        Func<ConversationLoop, Task<ChatMessage[]>> runLoop,
        CancellationToken ct
    )
    {
        // 模型切换（InvalidateConversationLoop）可能在运行中置空字段；同步必须用本次实际运行的
        // loop 实例，否则中断/异常路径跳过同步丢失该轮消息。
        ConversationLoop? loop = null;
        ThrowIfRunning();
        AgentStateEnum = AgentStateEnum.Running;
        var newCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var oldCts = Interlocked.Exchange(ref _internalCts, newCts);

        try
        {
            CoreLogger.AgentPromptStart(_logger.Safe());

            loop = GetOrCreateConversationLoop();
            if (BeforeRun is not null)
            {
                try
                {
                    await BeforeRun(newCts.Token);
                }
                catch (OperationCanceledException) when (newCts.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "BeforeRun hook 执行失败（已忽略）");
                }
            }
            var newMessages = await runLoop(loop);

            // 同步 _messages 从 ConversationLoop 的持久化消息
            _messages.Clear();
            _messages.AddRange(loop.PersistedMessages);

            CoreLogger.AgentPromptComplete(_logger.Safe(), newMessages.Length);

            AgentStateEnum = AgentStateEnum.Idle;
            return newMessages;
        }
        catch (OperationCanceledException)
        {
            // 取消时从 ConversationLoop 同步消息（不再含 Unknown 中断标记，中断信息通过事件传递）
            if (loop is not null)
            {
                _messages.Clear();
                _messages.AddRange(loop.PersistedMessages);
            }

            // 发射中断事件（ct 已取消，必须用 CancellationToken.None）
            await EmitEventAsync(new AgentInterrupted(), CancellationToken.None);

            AgentStateEnum = AgentStateEnum.Idle;
            throw;
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException and not ContextOverflowException)
        {
            // 错误时从 ConversationLoop 同步消息（与取消路径一致）
            if (loop is not null)
            {
                _messages.Clear();
                _messages.AddRange(loop.PersistedMessages);
            }

            // 记录错误信息并恢复状态
            _error = ex.InnerException?.Message ?? ex.Message;
            AgentStateEnum = AgentStateEnum.Idle;
            throw;
        }
        catch (ContextOverflowException)
        {
            // Context overflow：同步消息后直接传递，TuiApp 负责自动恢复
            if (loop is not null)
            {
                _messages.Clear();
                _messages.AddRange(loop.PersistedMessages);
            }

            _error = "上下文长度超出限制";
            AgentStateEnum = AgentStateEnum.Idle;
            throw;
        }
        catch (GuardRailTriggeredException ex)
        {
            // GuardRail tripwire：语义约束违反，不重试
            if (loop is not null)
            {
                _messages.Clear();
                _messages.AddRange(loop.PersistedMessages);
            }

            _error = ex.Message;
            AgentStateEnum = AgentStateEnum.Idle;
            throw;
        }
        finally
        {
            // 清理流式状态
            _isStreaming = false;
            _streamMessage = null;
            _pendingToolCalls.Clear();

            // 清理旧的 CTS（如果有被替换的）
            oldCts.Dispose();
        }
    }

    // ── 控制方法 ──

    /// <summary>取消当前运行中的操作。</summary>
    public void Abort()
    {
        // 读取当前 CTS 的快照并取消，避免竞态：
        // 如果 RunInternalAsync 正在创建新 CTS，我们拿到的是旧引用；
        // 但新 CTS 的 linked token 也会传播取消。
        Volatile.Read(ref _internalCts).Cancel();
        // 强制关闭活跃的流式 HTTP 响应，使阻塞在 ReadAsync 上的 SSE 读取立即中断。
        // 这是必要的，因为 .NET 的 NetworkStream.ReadAsync 不响应 CancellationToken。
        _httpHandler?.CancelActiveRequests();
    }

    /// <summary>设置用于追踪和强制关闭活跃 HTTP 响应的 handler。</summary>
    public void SetHttpHandler(CancellationHttpHandler handler) => _httpHandler = handler;

    /// <summary>
    /// 废弃 ConversationLoop，保留 _messages（对话历史）。
    /// 下次 Prompt() 时会用新 TornadoApi/ModelName 重建 loop 并从 _messages 恢复。
    /// </summary>
    public void InvalidateConversationLoop()
    {
        _conversationLoop = null;
    }

    /// <summary>重置 Agent 到初始状态，清空所有消息和队列。</summary>
    public void Reset()
    {
        _messages.Clear();
        _conversationLoop = null;
        SteeringQueue.Clear();
        InjectQueue.Clear();
        _pendingToolCalls.Clear();
        _error = null;
        _isStreaming = false;
        _streamMessage = null;
    }

    // ── 队列管理 ──

    /// <summary>向引导队列添加消息。</summary>
    /// <param name="message">要加入的消息。</param>
    /// <param name="steerId">Steer 标识符，用于匹配 Waiting 占位气泡。null 表示未关联占位气泡。</param>
    public void Steer(ChatMessage message, string? steerId = null) =>
        SteeringQueue.Enqueue(message, steerId);

    /// <summary>清空引导队列。</summary>
    public void ClearSteeringQueue() => SteeringQueue.Clear();

    /// <summary>清空所有队列（含系统通知队列）。</summary>
    public void ClearAllQueues()
    {
        SteeringQueue.Clear();
        InjectQueue.Clear();
    }

    /// <summary>设置引导队列的投递模式。</summary>
    public void SetSteeringMode(QueueDeliveryMode mode) => SteeringQueue.Mode = mode;

    // ── 内部方法 ──

    /// <summary>如果 Agent 正在运行则抛出异常。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfRunning()
    {
        if (AgentStateEnum == AgentStateEnum.Running)
        {
            CoreLogger.AgentAlreadyRunning(_logger.Safe());
            throw new InvalidOperationException("Agent 正在运行中，请等待当前操作完成。");
        }
    }

    /// <summary>异步发射 Agent 事件。</summary>
    private async Task EmitEventAsync(AgentEvent evt, CancellationToken ct)
    {
        if (OnEvent is not null)
        {
            await OnEvent.Invoke(evt, ct);
        }
    }

    /// <summary>
    /// 获取或创建 ConversationLoop。
    /// ConversationLoop 是有状态的，保持 Conversation 对象跨迭代复用。
    /// </summary>
    /// <returns>ConversationLoop 实例。</returns>
    private ConversationLoop GetOrCreateConversationLoop()
    {
        if (_conversationLoop is not null)
            return _conversationLoop;

        if (TornadoApi is null)
            throw new InvalidOperationException(
                "Agent 需要 TornadoApi 才能运行。请设置 Agent.TornadoApi。"
            );

        _conversationLoop = new ConversationLoop(
            api: TornadoApi,
            modelName: _modelName,
            systemPrompt: _systemPrompt,
            thinkingLevel: _thinkingLevel,
            toolRegistry: _toolRegistry,
            toolExecution: ToolExecution,
            beforeToolCall: BeforeToolCall,
            afterToolCall: AfterToolCall,
            agent: this,
            logger: _logger,
            stabilityMonitor: StabilityMonitor,
            holographicEngine: HolographicEngine,
            messageScorer: MessageScorer,
            guardRail: GuardRail,
            systemOne: SystemOne,
            toolInjector: ToolInjector,
            shouldStopAfterTurn: ShouldStopAfterTurn,
            entryIdProvider: EntryIdProvider,
            onMessagesReady: OnMessagesReady,
            onNewContextRequested: OnNewContextRequested
        );

        // 同步 ModelContextProfile 到 ConversationLoop
        if (ModelProfile is not null)
            _conversationLoop.UpdateModelProfile(ModelProfile);

        // ★ 冷启动补推：循环是惰性创建的，创建时必须把接续选项与页状态带给它，
        // 否则会话恢复 / 换页后的第一次 prompt 不会注入页片段（模型不知道自己在哪一页）。
        _conversationLoop.UpdateContextManagement(ContextManagement);
        _conversationLoop.SetContextWindowState(ContextWindowState);

        if (_messages.Count > 0)
        {
            _conversationLoop.RestoreFromMessages(_messages);
        }

        return _conversationLoop;
    }

    /// <summary>异步释放资源。</summary>
    public async ValueTask DisposeAsync()
    {
        var cts = Volatile.Read(ref _internalCts);
        await cts.CancelAsync();
        cts.Dispose();
        _messages.Clear();
        if (_conversationLoop is not null)
        {
            await _conversationLoop.DisposeAsync();
            _conversationLoop = null;
        }
        await Task.CompletedTask;
    }
}
