using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClientPlatform;
using Core.Agent;
using Core.Data;
using Core.Notes;
using Core.Shared;
using Core.Stability;
using Core.Tools;
using Core.Vector;
using LlmTornado.Chat;
using LlmTornado.ChatFunctions;
using LlmTornado.Code;
using Microsoft.Extensions.Logging;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace Desktop.Services;

/// <summary>会话持久化故障（写入失败，继续生成会造成无法恢复的历史缺口）。</summary>
internal sealed class SessionPersistenceException(string message, Exception innerException)
    : Exception(message, innerException) { }

/// <summary>压缩结果（引擎内部）。</summary>
/// <remarks>
/// 两种「被拒绝」必须分开：<see cref="RejectedInProgress"/> 是 B4 防重入（稍后重试可能成功）；
/// <see cref="RejectedWrongMode"/> 是模式互斥兜底（接续模式不产生 compact 条目，重试永远无用）。
/// </remarks>
internal enum CompactResult
{
    Success,
    RejectedInProgress,
    RejectedWrongMode,
    Failed,
}

/// <summary>压缩对外结果（供 /compact 命令呈现）。</summary>
public enum CompactOutcomeKind
{
    Done,
    Rejected,
    Failed,
}

public sealed record CompactOutcome(CompactOutcomeKind Kind, int Before, int After, string? Error);

/// <summary>
/// Desktop 会话引擎：把 TUI（TuiApp.Session.cs + TuiApp.Compact.cs）的会话生命周期、
/// JSONL 增量持久化、四级上下文压缩、分支导航、变体持久化逐段移植为 Desktop 本地实现。
///
/// 每对话一个实例（Transient），共享同一个单例 Agent：Agent 上下文随 <see cref="SwitchToAsync"/>/
/// <see cref="NewSession"/> 重置，<see cref="Activate"/> 把 ConversationLoop 的每轮持久化回调
/// 重指向本引擎。所有持久化直接走 Core 的 FileSessionStore，与 TUI/Host 完全同源。
/// </summary>
public sealed class SessionEngine(
    DesktopAgentBridge bridge,
    FileSessionStore store,
    INovelStore novelStore,
    TodoStore todoStore,
    ContextTokenCalibrator calibrator,
    ConfigStore configStore,
    ILogger log,
    RetrievalKernel? retrievalKernel = null
)
{
    private readonly Agent agent = bridge.Agent;

    // ── 会话 ID 与增量持久化追踪 ──
    private string _sessionId = "";
    private bool _sessionPersistenceFaulted;

    /// <summary>会话持久化是否故障（变体采用前的守卫，对齐 TUI _sessionPersistenceFaulted）。</summary>
    public bool IsPersistenceFaulted => _sessionPersistenceFaulted;

    private string? _lastParentEntryId;
    private int _persistedMessageCount;

    /// <summary>随手记捕获扫描游标：已扫描到 Agent 消息列表的哪一位（压缩/换页/切会话后回退自愈）。</summary>
    private int _noteScanIndex;
    private int _nextOrder;
    private volatile bool _isCompacting;
    private readonly List<string> _inputHistory = [];
    private ExploreDetails? _lastExploreDetails;

    /// <summary>当前会话 ID。</summary>
    public string SessionId => _sessionId;

    /// <summary>是否有压缩进行中。</summary>
    public bool IsCompacting => _isCompacting;

    /// <summary>最近的变体组（会话恢复/重新打开用）。</summary>
    public ExploreDetails? LastVariantSet => _lastExploreDetails;

    /// <summary>用户输入历史（上下键翻阅）。</summary>
    public IReadOnlyList<string> InputHistory => _inputHistory;

    /// <summary>系统通知（压缩/中断/持久化故障文案，替代 TUI ToastService）。</summary>
    public event Action<string>? OnSystemNotice;

    /// <summary>压缩开始。</summary>
    public event Action? OnCompactStart;

    /// <summary>压缩结束（参数：结果文案）。</summary>
    public event Action<string>? OnCompactEnd;

    // ══════════════════════════════════════════════════════════
    //  增量持久化状态机
    // ══════════════════════════════════════════════════════════

    /// <summary>DEBUG 条件断言：验证 _persistedMessageCount ≤ _nextOrder 不变量。</summary>
    [Conditional("DEBUG")]
    private void AssertOrderInvariant()
    {
        Debug.Assert(
            _persistedMessageCount <= _nextOrder,
            $"Order invariant violated: _persistedMessageCount={_persistedMessageCount} > _nextOrder={_nextOrder}. "
                + "Non-message entries consume order bits but should not exceed message count."
        );
    }

    private void HandleSessionPersistenceFailure(Exception ex)
    {
        _sessionPersistenceFaulted = true;
        log.LogWarning(ex, "persist session messages failed");
        OnSystemNotice?.Invoke(
            "会话保存失败，已停止生成以保护历史记录。请检查磁盘和会话文件后重启。"
        );
    }

    /// <summary>
    /// 持久化新增消息（失败路由 HandleSessionPersistenceFailure）。
    /// 变体采用（AppendMessage 后落盘）等 UI 直写路径使用；返回 false 表示持久化已故障。
    /// </summary>
    public async Task<bool> TryPersistNewMessagesAsync()
    {
        try
        {
            await PersistNewMessagesAsync();
            return true;
        }
        catch (SessionPersistenceException ex)
        {
            HandleSessionPersistenceFailure(ex);
            return false;
        }
    }

    private async Task PersistNewMessagesAsync() => await PersistBatchAsync(agent.State.Messages);

    /// <summary>
    /// 会话随手记捕获：把本轮新用户消息里带触发词的句子落成候选笔记（旁路，失败不影响对话）。
    /// </summary>
    private async Task CaptureNotesAsync(CancellationToken ct)
    {
        var novelId = agent.CurrentNovelId;
        if (string.IsNullOrEmpty(novelId))
            return;

        try
        {
            var messages = agent.State.Messages;
            if (messages.Count < _noteScanIndex)
                _noteScanIndex = 0; // 压缩/换页/切会话后自愈
            var capturedOrder = NoteCaptureScanner.MaxWrittenOrderOf(
                await novelStore.GetStructure(novelId, ct)
            );
            await new NoteCaptureScanner(novelStore, retrievalKernel).ScanAsync(
                novelId,
                _sessionId,
                messages,
                _noteScanIndex,
                capturedOrder,
                ct
            );
            _noteScanIndex = messages.Count;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "capture notes from session");
        }
    }

    /// <summary>ConversationLoop 每轮消息就绪回调：即时写入 JSONL，不等整体停止。</summary>
    private Task PersistReadyMessagesAsync(
        IReadOnlyList<LlmChatMessage> readyMessages,
        CancellationToken _
    ) => PersistBatchAsync(readyMessages);

    /// <summary>将指定消息集合从 _persistedMessageCount 起增量写入 JSONL 的公共实现。</summary>
    private async Task PersistBatchAsync(IReadOnlyList<LlmChatMessage> messages)
    {
        try
        {
            var novelId = agent.CurrentNovelId ?? "";
            var newCount = messages.Count;
            if (newCount <= _persistedMessageCount)
                return;

            var newMessages = new LlmChatMessage[newCount - _persistedMessageCount];
            for (var i = 0; i < newMessages.Length; i++)
                newMessages[i] = messages[_persistedMessageCount + i];
            var lastEntryId = await store.SaveSessionMessages(
                _sessionId,
                novelId,
                newMessages,
                lastParentEntryId: _lastParentEntryId,
                startOrder: _nextOrder
            );
            _persistedMessageCount = newCount;
            _nextOrder += newMessages.Length;
            AssertOrderInvariant();
            if (lastEntryId is not null)
                _lastParentEntryId = lastEntryId;
        }
        catch (Exception ex)
        {
            throw new SessionPersistenceException(
                "Session message persistence failed (session="
                    + _sessionId
                    + ", messageCount="
                    + messages.Count
                    + ")",
                ex
            );
        }
    }

    /// <summary>循环以可恢复原因停止时，将停止原因写入会话 JSONL。</summary>
    private async Task PersistLoopStopEventAsync()
    {
        var stop = agent.LastLoopStop;
        if (stop is null)
            return;
        try
        {
            await store.SaveSessionEvent(
                _sessionId,
                agent.CurrentNovelId ?? "",
                new SessionEvent
                {
                    EventType = SessionEventType.Error,
                    ParentEntryId = _lastParentEntryId,
                    Order = _nextOrder,
                    ErrorCode = stop.Code,
                    Content = stop.Message,
                }
            );
            _nextOrder++;
        }
        catch (Exception ex)
        {
            HandleSessionPersistenceFailure(ex);
        }
    }

    // ══════════════════════════════════════════════════════════
    //  恢复
    // ══════════════════════════════════════════════════════════

    /// <summary>从 JSONL 恢复会话全部消息（UI 显示用，含已 compact 历史）。</summary>
    public async Task<List<Core.Data.ChatMessage>> GetUiMessagesAsync()
    {
        try
        {
            return await store.GetActiveMessages(_sessionId);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "restore session messages failed");
            return [];
        }
    }

    /// <summary>从 compact 后的上下文恢复 LLM 消息（仅保留 compact 点之后的消息）。</summary>
    private async Task RestoreContextFromDbAsync(string sessionId)
    {
        try
        {
            var boundary = await store.GetWindowBoundaryAsync(sessionId);

            var dbMessages = await store.GetContextMessages(sessionId);
            if (dbMessages.Count == 0)
            {
                agent.ReplaceMessages([]);
                // ★ 两个分支都要同步页状态（否则空页 / 全被压缩的会话恢复后页状态丢失）
                agent.SetContextWindowState(boundary);
                return;
            }
            agent.ReplaceMessages(dbMessages.Select(MapDbMessageToChatMessage).ToArray());
            agent.SetContextWindowState(boundary);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "restore session context failed");
            agent.ReplaceMessages([]);
        }
    }

    /// <summary>将 JSONL 消息记录映射为 LlmChatMessage（透传 EntryId 锚点、恢复 ToolCalls）。</summary>
    private static LlmChatMessage MapDbMessageToChatMessage(Core.Data.ChatMessage m)
    {
        var role = MessageRoleMapping.ToChatMessageRoles(m.Role);
        var msg = new LlmChatMessage(role, m.Content)
        {
            Name = m.ToolName,
            ToolCallId = m.ToolCallId,
            ToolInvocationSucceeded = !m.IsError,
            Tokens = m.Tokens, // 透传上下文 token 锚点（恢复时作精确锚点）
            NativeObject = m.EntryId, // B3 ref 稳定：EntryId 透传，跨压缩保留
        };

        if (m.Role == MessageRole.Assistant && !string.IsNullOrEmpty(m.ReasoningContent))
            msg.ReasoningContent = m.ReasoningContent;

        if (m.Role == MessageRole.Assistant && !string.IsNullOrEmpty(m.ToolCallsJson))
            RestoreToolCallsFromJson(m.ToolCallsJson, msg);

        return msg;
    }

    private static void RestoreToolCallsFromJson(string json, LlmChatMessage msg)
    {
        var items = ToolCallItemDto.DeserializeList(json);
        if (items is null or { Count: 0 })
            return;

        msg.ToolCalls = items
            .Select(tc => new ToolCall
            {
                Id = tc.Id ?? "",
                Type = "function",
                FunctionCall = new FunctionCall
                {
                    Name = tc.Name ?? "",
                    Arguments = tc.Arguments ?? "",
                },
            })
            .ToList();

        if (string.IsNullOrWhiteSpace(msg.Content))
            msg.Content = null;
    }

    // ══════════════════════════════════════════════════════════
    //  会话生命周期
    // ══════════════════════════════════════════════════════════

    /// <summary>新建会话：中止旧 Agent → 落盘旧会话任务 → 重置上下文与追踪器。</summary>
    public void NewSession()
    {
        SaveCurrentSessionTodos();
        _sessionId = Guid.NewGuid().ToString("N")[..12];
        agent.SessionId = _sessionId;
        agent.ReplaceMessages([]);
        _lastParentEntryId = null;
        _persistedMessageCount = 0;
        _sessionPersistenceFaulted = false;
        _nextOrder = 0;
        AssertOrderInvariant();
        todoStore.Clear();
        _inputHistory.Clear();
    }

    /// <summary>切换到已有会话：恢复 UI 消息/LLM 上下文/增量状态/输入历史/任务/变体组。</summary>
    public async Task SwitchToAsync(string sessionId, string novelId)
    {
        SaveCurrentSessionTodos();

        var session = await store.GetSession(sessionId);
        if (session is null)
        {
            // 会话不存在 → 退化为新会话
            NewSession();
            agent.CurrentNovelId = novelId;
            Activate();
            return;
        }

        _sessionId = session.Id;
        agent.SessionId = _sessionId;
        agent.CurrentNovelId = novelId;
        _sessionPersistenceFaulted = false;

        // LLM 仅加载 compact 后的上下文消息
        await RestoreContextFromDbAsync(_sessionId);

        // 恢复增量追踪状态：从 JSONL 轻量元数据推导
        var (nextOrder, lastEntryId) = await store.GetSessionMetadataAsync(_sessionId);
        _nextOrder = nextOrder;
        _persistedMessageCount = agent.State.Messages.Count;
        AssertOrderInvariant();
        _lastParentEntryId = lastEntryId;

        // 恢复输入历史
        try
        {
            var dbHistory = await store.GetInputHistory(_sessionId, limit: 100);
            dbHistory.Reverse();
            _inputHistory.Clear();
            _inputHistory.AddRange(dbHistory);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "load session input history failed");
        }

        LoadSessionTodos(_sessionId);
        await LoadLastVariantSetAsync();
        Activate();
    }

    /// <summary>把 ConversationLoop 的每轮持久化回调指向本引擎（切换对话后必须调用）。</summary>
    public void Activate()
    {
        agent.OnMessagesReady = PersistReadyMessagesAsync;

        // ★ 上下文接续：换页请求交给 Host 执行（写 window 条目 + 清空模型侧 + 记账）。
        // 必须在首次 prompt 之前装配（循环首次创建时取走回调与选项）。
        // compact 模式下 agent.ContextManagement == null，走原压缩路径。
        agent.OnNewContextRequested = (reason, _) => RollContextWindowAsync(reason);
    }

    // ══════════════════════════════════════════════════════════
    //  编排：RunPromptAsync
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 发送用户消息：主动压缩预检 → 更新 token 显示 → bridge.SendAsync → 持久化。
    /// 处理取消（interrupt 事件）、上下文溢出（compact + retry 最多 3 次）、通用异常。
    /// </summary>
    public async Task RunPromptAsync(string text, CancellationToken ct, int overflowRetryCount = 0)
    {
        if (_sessionPersistenceFaulted)
        {
            OnSystemNotice?.Invoke("会话保存故障尚未恢复，已拒绝继续生成以防止历史记录缺失。");
            return;
        }

        try
        {
            await InjectNovelContextAsync();

            // 主动压缩预检：接近动态阈值时先压缩再发送，阈值与显示分母一致（ModelProfile 窗口）
            var estimatedTokens = calibrator.Project(
                ConversationLoop.EstimateMessagesTokens(agent.State.Messages)
            );
            var compactPercent = agent.ModelProfile?.CompactThresholdPercent ?? 0.85;
            var contextWindow =
                agent.ModelProfile?.ContextWindowTokens
                ?? configStore.Load().Providers.ContextWindowSize;
            var compactThreshold = (long)(contextWindow * compactPercent);
            var keepRecent = agent.ModelProfile?.ScaledKeepRecent ?? 20;
            // ★ 模式互斥：接续模式在发送前只换页（不压缩）；compact 模式保持原压缩路径。
            if (estimatedTokens > compactThreshold)
            {
                if (_contextManagement is { Mode: ContextMode.Continuation })
                    await RollContextWindowAsync("threshold");
                else
                    await AutoCompactAsync(keepRecent);
            }

            // ★ 新用户消息 = 新的接续预算（自动接续次数清零，上一轮的换页原因作废）
            _autoContinueCount = 0;
            _lastRollReason = null;

            await bridge.SendAsync(text, ct);
            await PersistNewMessagesAsync();
            await CaptureNotesAsync(ct);
            await PersistLoopStopEventAsync();

            // ★ 被迫换页后自动接续（新页直接接着做；模型判断已完成会自然停下）
            while (ContextRollPolicy.ShouldAutoContinue(_lastRollReason, _autoContinueCount))
            {
                _autoContinueCount++;
                _lastRollReason = null;
                await bridge.ContinueAsync(ct);
                await PersistNewMessagesAsync();
                await PersistLoopStopEventAsync();
            }
        }
        catch (SessionPersistenceException ex)
        {
            HandleSessionPersistenceFailure(ex);
        }
        catch (OperationCanceledException)
        {
            if (!await TryPersistNewMessagesAsync())
                return;
            try
            {
                await store.SaveSessionEvent(
                    _sessionId,
                    agent.CurrentNovelId ?? "",
                    new SessionEvent
                    {
                        EventType = SessionEventType.Interrupt,
                        ParentEntryId = _lastParentEntryId,
                        Order = _nextOrder,
                        Content = "用户中断生成",
                    },
                    ct
                );
                _nextOrder++;
            }
            catch (Exception ex)
            {
                HandleSessionPersistenceFailure(ex);
            }
        }
        catch (ContextOverflowException)
        {
            // 上下文溢出自动恢复：按模式走各自唯一路径（最多 3 次，防止无效重试死循环）
            // - compact：分片压缩 + retry（与改动前一致）
            // - 接续：换页 + retry；换页失败直接报错，**不回落压缩**（模式互斥）
            const int maxOverflowRetries = 3;
            if (!await TryPersistNewMessagesAsync())
                return;

            try
            {
                await store.SaveSessionEvent(
                    _sessionId,
                    agent.CurrentNovelId ?? "",
                    new SessionEvent
                    {
                        EventType = SessionEventType.Error,
                        ParentEntryId = _lastParentEntryId,
                        Order = _nextOrder,
                        ErrorCode = "context_overflow",
                        Content = "上下文长度超出限制",
                    },
                    ct
                );
                _nextOrder++;
            }
            catch (Exception ex)
            {
                HandleSessionPersistenceFailure(ex);
            }
            if (_sessionPersistenceFaulted)
                return;

            if (overflowRetryCount >= maxOverflowRetries)
            {
                OnSystemNotice?.Invoke(
                    _contextManagement is { Mode: ContextMode.Continuation }
                        ? $"上下文溢出，已重试 {maxOverflowRetries} 次仍无法恢复。请开启新会话。"
                        : $"上下文溢出，已重试 {maxOverflowRetries} 次仍无法恢复。请使用 /compact 手动压缩或开启新会话。"
                );
                return;
            }

            var keepRecentOverflow = agent.ModelProfile?.ScaledKeepRecent ?? 20;
            var relieved = await RelieveOverflowAsync(keepRecentOverflow);
            if (relieved)
            {
                OnSystemNotice?.Invoke(
                    _contextManagement is { Mode: ContextMode.Continuation }
                        ? "上下文过长，已开启新的上下文页并重试..."
                        : "上下文溢出，已压缩并重试..."
                );
                _ = RunPromptAsync(text, ct, overflowRetryCount + 1);
                return;
            }
            OnSystemNotice?.Invoke(
                _contextManagement is { Mode: ContextMode.Continuation }
                    ? "上下文过长，页切换失败。请开启新会话。"
                    : "上下文过长，压缩失败。请使用 /compact 或开启新会话。"
            );
        }
        catch (Exception ex)
        {
            if (!await TryPersistNewMessagesAsync())
                return;
            try
            {
                var (httpStatusCode, _, isContextOverflow) =
                    Core.Infrastructure.LlmErrorHelper.Extract(ex);
                var errorCode = isContextOverflow
                    ? "context_overflow"
                    : httpStatusCode?.ToString() ?? "network";
                await store.SaveSessionEvent(
                    _sessionId,
                    agent.CurrentNovelId ?? "",
                    new SessionEvent
                    {
                        EventType = SessionEventType.Error,
                        ParentEntryId = _lastParentEntryId,
                        Order = _nextOrder,
                        ErrorCode = errorCode,
                        Content = ex.InnerException?.Message ?? ex.Message,
                    },
                    ct
                );
                _nextOrder++;
            }
            catch (Exception innerEx)
            {
                HandleSessionPersistenceFailure(innerEx);
            }
        }
    }

    /// <summary>更新小说上下文（每次发消息前调用）。文本块与 TUI 逐字符一致。</summary>
    private async Task InjectNovelContextAsync()
    {
        var novelId = agent.CurrentNovelId;
        if (string.IsNullOrEmpty(novelId))
        {
            agent.SetNovelContext(
                "⚡ 当前没有加载任何小说项目。如果用户想创建新小说，先用 ask_user 确认，不要直接调用 create_novel。"
            );
            return;
        }

        try
        {
            var novel = await novelStore.GetNovel(novelId);
            if (novel is null)
                return;

            // 与 TUI 共用同一构建器（Core.Prompts.NovelContextBlock）：总纲摘要 + 卷纲概览 + 章节进度
            var chapters = novel.Chapters.OrderBy(c => c.Order).ToList();
            var volumes = await novelStore.GetVolumes(novelId);
            agent.SetNovelContext(Core.Prompts.NovelContextBlock.Build(novel, chapters, volumes));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "inject novel context failed");
        }
    }

    // ══════════════════════════════════════════════════════════
    //  压缩引擎
    // ══════════════════════════════════════════════════════════

    /// <summary>手动/智能压缩入口（/compact）。</summary>
    public async Task<CompactOutcome> CompactAsync(bool smart, int keepRecent)
    {
        // ★ 模式互斥：接续模式不使用压缩（有损摘要与换页语义互相干扰），指路到换页机制
        if (_contextManagement is { Mode: ContextMode.Continuation })
            return new CompactOutcome(
                CompactOutcomeKind.Rejected,
                0,
                0,
                "接续模式不使用压缩：上下文接近上限时会自动开启新页。"
            );

        var messages = agent.State.Messages;
        var novelId = agent.CurrentNovelId;
        if (string.IsNullOrEmpty(novelId) || messages.Count == 0)
            return new CompactOutcome(CompactOutcomeKind.Rejected, 0, 0, "没有可压缩的会话");
        if (messages.Count <= keepRecent)
            return new CompactOutcome(
                CompactOutcomeKind.Rejected,
                messages.Count,
                messages.Count,
                $"消息数 ({messages.Count}) 未超过保留阈值 ({keepRecent})，无需压缩"
            );

        if (smart && agent.MessageScorer is not null)
            return await CompactSmartCoreAsync(keepRecent);

        OnCompactStart?.Invoke();
        try
        {
            var keep = Math.Min(keepRecent, messages.Count);
            var result = await PrepareAndCompactAsync(messages, keep, "manual", willRetry: false);
            if (result is CompactResult.RejectedInProgress or CompactResult.RejectedWrongMode)
            {
                OnCompactEnd?.Invoke(DescribeRejection(result));
                return new CompactOutcome(
                    CompactOutcomeKind.Rejected,
                    messages.Count,
                    keep,
                    RejectionMessage(result)
                );
            }
            if (result == CompactResult.Failed)
            {
                OnCompactEnd?.Invoke("上下文压缩失败");
                return new CompactOutcome(
                    CompactOutcomeKind.Failed,
                    messages.Count,
                    keep,
                    "压缩失败"
                );
            }
            OnCompactEnd?.Invoke($"上下文压缩完成：{messages.Count} → {keep} 条");
            return new CompactOutcome(CompactOutcomeKind.Done, messages.Count, keep, null);
        }
        catch (Exception ex)
        {
            OnCompactEnd?.Invoke("上下文压缩失败");
            return new CompactOutcome(CompactOutcomeKind.Failed, messages.Count, 0, ex.Message);
        }
    }

    private async Task<CompactOutcome> CompactSmartCoreAsync(int keepRecent)
    {
        var messages = agent.State.Messages;
        var decimator = agent.MessageScorer!;
        OnCompactStart?.Invoke();
        try
        {
            var scoredTurns = ScoreTurnsWithDecimator(messages, decimator);
            var cutIndex = FindSmartCutPoint(messages, scoredTurns, keepRecent);
            var keep = messages.Count - cutIndex;
            var result = await PrepareAndCompactAsync(messages, keep, "smart", willRetry: false);

            if (result is CompactResult.RejectedInProgress or CompactResult.RejectedWrongMode)
            {
                OnCompactEnd?.Invoke(DescribeRejection(result));
                return new CompactOutcome(
                    CompactOutcomeKind.Rejected,
                    messages.Count,
                    keep,
                    RejectionMessage(result)
                );
            }
            if (result == CompactResult.Failed)
            {
                // 智能失败退回普通
                OnCompactEnd?.Invoke($"智能压缩失败，退回普通模式");
                return await CompactAsync(smart: false, keepRecent);
            }
            OnCompactEnd?.Invoke($"智能压缩完成：{messages.Count} → {keep} 条");
            return new CompactOutcome(CompactOutcomeKind.Done, messages.Count, keep, null);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "compact session with message scorer failed");
            OnCompactEnd?.Invoke("上下文压缩失败");
            return new CompactOutcome(CompactOutcomeKind.Failed, messages.Count, 0, ex.Message);
        }
    }

    /// <summary>自动压缩：token 阈值触发，静默执行（不弹压缩进度外的额外文案）。</summary>
    private async Task AutoCompactAsync(int keepRecent)
    {
        try
        {
            var messages = agent.State.Messages;
            var novelId = agent.CurrentNovelId;
            if (string.IsNullOrEmpty(novelId) || messages.Count <= keepRecent)
                return;

            if (agent.MessageScorer is not null)
            {
                OnCompactStart?.Invoke();
                var scoredTurns = ScoreTurnsWithDecimator(messages, agent.MessageScorer);
                var cutIndex = FindSmartCutPoint(messages, scoredTurns, keepRecent);
                var keep = messages.Count - cutIndex;
                var r = await PrepareAndCompactAsync(messages, keep, "smart", willRetry: false);
                OnCompactEnd?.Invoke(
                    r is CompactResult.RejectedInProgress or CompactResult.RejectedWrongMode
                        ? DescribeRejection(r)
                        : $"自动压缩完成：{messages.Count} → {keep} 条"
                );
            }
            else
            {
                OnCompactStart?.Invoke();
                var r = await PrepareAndCompactAsync(
                    messages,
                    keepRecent,
                    "threshold",
                    willRetry: false
                );
                OnCompactEnd?.Invoke(
                    r is CompactResult.RejectedInProgress or CompactResult.RejectedWrongMode
                        ? DescribeRejection(r)
                        : "自动压缩完成"
                );
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "auto-compact session failed");
        }
    }

    /// <summary>上下文溢出时压缩：compact 后验证 token 是否真的降低，无效时返回 false 防死循环。</summary>
    private async Task<bool> CompactForOverflowAsync(int keepRecent)
    {
        try
        {
            var messages = agent.State.Messages;
            var novelId = agent.CurrentNovelId;

            // 消息数不足时：若监控器冻结（累计口径虚高触发 BudgetExhausted），解冻重设基线即恢复
            if (string.IsNullOrEmpty(novelId) || messages.Count <= keepRecent)
            {
                var monitor = agent.StabilityMonitor;
                if (monitor is not null && monitor.IsFrozen)
                {
                    monitor.Unfreeze(calibrator.Project(EstimateContextTokens(messages)));
                    return true;
                }
                return false;
            }

            var tokensBefore = EstimateContextTokens(messages);
            var result = await PrepareAndCompactAsync(
                messages,
                keepRecent,
                "overflow",
                willRetry: true
            );

            // 两种拒绝语义分开：已有压缩进行中 → true 交上层重试；模式互斥 → false 停止重试
            // （接续模式下什么都不做，重试永远无用；此路径仅在未来新增触发源漏判模式时才可达）。
            if (result is CompactResult.RejectedInProgress or CompactResult.RejectedWrongMode)
            {
                log.LogDebug(
                    "compact after context overflow: rejected ({Reason})",
                    result == CompactResult.RejectedInProgress ? "in progress" : "wrong mode"
                );
                return result == CompactResult.RejectedInProgress;
            }

            var tokensAfter = EstimateContextTokens(agent.State.Messages);
            if (tokensAfter >= tokensBefore)
            {
                log.LogWarning(
                    "compact after context overflow: token count did not decrease (before={Before}, after={After})",
                    tokensBefore,
                    tokensAfter
                );
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "compact after context overflow failed");
            return false;
        }
    }

    /// <summary>压缩被拒绝时的结束事件文案（两种拒绝语义不同，不能混）。</summary>
    private static string DescribeRejection(CompactResult result) =>
        result == CompactResult.RejectedInProgress
            ? "压缩请求被拒绝（已有压缩进行中）"
            : "压缩请求被拒绝（当前为接续模式）";

    /// <summary>压缩被拒绝时展示给用户的原因。</summary>
    private static string RejectionMessage(CompactResult result) =>
        result == CompactResult.RejectedInProgress
            ? "压缩进行中，请稍候再试"
            : "接续模式不使用压缩：上下文接近上限时会自动开启新页。";

    /// <summary>准备并执行压缩：B4 防重入单点拦截所有触发源。</summary>
    private async Task<CompactResult> PrepareAndCompactAsync(
        IReadOnlyList<LlmChatMessage> messages,
        int keepRecent,
        string reason,
        bool willRetry
    )
    {
        if (_isCompacting)
        {
            log.LogDebug(
                "prepare compaction: rejected {Reason} request because compaction is in progress",
                reason
            );
            return CompactResult.RejectedInProgress;
        }
        // ★ 模式互斥（单点兜底）：接续模式不使用压缩。调用点都已按模式分支，
        // 走到这里说明新增了漏判模式的触发源——拒绝而不是悄悄产生 compact 条目。
        if (_contextManagement is { Mode: ContextMode.Continuation })
        {
            log.LogDebug(
                "prepare compaction: rejected {Reason} request because context management mode is continuation",
                reason
            );
            return CompactResult.RejectedWrongMode;
        }
        _isCompacting = true;
        try
        {
            return await PrepareAndCompactCoreAsync(messages, keepRecent, reason, willRetry);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "prepare compaction failed");
            return CompactResult.Failed;
        }
        finally
        {
            _isCompacting = false;
        }
    }

    /// <summary>PrepareAndCompactAsync 的实际执行体（防重入标志内）。</summary>
    private async Task<CompactResult> PrepareAndCompactCoreAsync(
        IReadOnlyList<LlmChatMessage> messages,
        int keepRecent,
        string reason,
        bool willRetry
    )
    {
        var novelId = agent.CurrentNovelId ?? "";

        // 计算切割点——保证轮次完整性
        var cutIndex = FindCutPoint(messages, keepRecent);

        // 防御：cutIndex 过小意味着压缩几乎无效，强制降低 keepRecent 重算
        if (cutIndex < 3 && messages.Count > keepRecent)
        {
            var reducedKeep = Math.Max(3, keepRecent / 2);
            cutIndex = FindCutPoint(messages, reducedKeep);
            log.LogDebug(
                "prepare compaction: reduced keepRecent from {Keep} to {Reduced}; cutIndex={Cut}",
                keepRecent,
                reducedKeep,
                cutIndex
            );
        }

        var previousSummary = await store.GetLatestCompactSummaryAsync(_sessionId);

        var summary = await GenerateCompactSummaryAsync(messages, cutIndex, previousSummary);
        var recentBlock = BuildRecentExecutionBlock(messages, cutIndex);
        if (recentBlock.Length > 0)
            summary = summary + "\n\n" + recentBlock;

        // ★ 必须按当前页切片：换页后 messages（页内列表）比 dbMsgs（全量）短，
        // Skip/Take(cutIndex) 会错位到换页前的旧消息 → firstKeptEntryId 落在旧页、
        // compact 截断点跑到 window 之前 → 重载会话时页内消息全部回归。
        var pageStart = agent.ContextWindowState.StartOrder;
        var dbMsgs = (await store.GetActiveMessages(_sessionId))
            .Where(m => pageStart == 0 || m.Order >= pageStart)
            .ToList();

        if (dbMsgs.Count != messages.Count)
            log.LogDebug(
                "prepare compaction: database message count={Db} memory message count={Mem}",
                dbMsgs.Count,
                messages.Count
            );

        // firstKeptEntryId 必须是真实消息：跳过 compact 条目
        var keptEntry = dbMsgs
            .Skip(cutIndex)
            .FirstOrDefault(m => m.EventType != SessionEventType.Compact);
        var firstKeptEntryId = keptEntry?.EntryId ?? "";
        var messagesBefore = messages.Count;

        // 收集被压缩消息的 entryId 供 decompress 恢复原文（跳过 compact 条目）
        var compressedIds = dbMsgs
            .Take(cutIndex)
            .Where(m => m.EventType != SessionEventType.Compact)
            .Select(m => m.EntryId ?? $"msg_{m.Order}")
            .Where(id => !string.IsNullOrEmpty(id))
            .ToList();

        var (compactEntryId, blockId) = await store.CompactSession(
            _sessionId,
            novelId,
            summary,
            firstKeptEntryId,
            messagesBefore,
            parentEntryId: _lastParentEntryId,
            order: _nextOrder,
            reason: reason,
            willRetry: willRetry,
            estimatedTokensAfter: EstimateCompactTokensAfter(summary, cutIndex, messages),
            compressedMessageIds: compressedIds
        );

        // 直接更新 Agent 内存消息，避免重新读取 JSONL
        var snapshot = BuildNovelSnapshot();
        var summaryMsg = new LlmChatMessage(
            ChatMessageRoles.User,
            $"[上下文摘要 {blockId}]\n{summary}{(snapshot.Length > 0 ? "\n\n" + snapshot : "")}"
        );
        var keptMessages = agent.State.Messages.Skip(cutIndex).ToList();
        var newMessages = new List<LlmChatMessage>(keptMessages.Count + 1) { summaryMsg };
        newMessages.AddRange(keptMessages);
        agent.ReplaceMessages(newMessages);

        // 最小版压缩状态：记录本次压缩的 block（支持后续 decompress）
        agent.CompressionBlocks.Add((blockId, compressedIds, summary));

        // compact 后重置稳定性监控器：真正归零 + 用估算的当前上下文重设 token 基线
        // （只调 Unfreeze 无效：未冻结时直接 return，而 pre-flight 溢出通常未冻结）
        ResetStabilityMonitor(calibrator.Project(EstimateContextTokens(newMessages)));

        // B6：重置 prompt token 基线（compact 后 prompt 缩小，旧基线会算出负增量压低 TotalTokens）
        agent.ResetPromptBaseline();

        // compact 后重置增量追踪状态
        _nextOrder++; // compact 条目占一个 order 位
        _persistedMessageCount = agent.State.Messages.Count;
        AssertOrderInvariant();
        _lastParentEntryId = compactEntryId;
        return CompactResult.Success;
    }

    /// <summary>计算压缩切割点——保证轮次完整性（对齐到 user 消息边界）。</summary>
    private static int FindCutPoint(IReadOnlyList<LlmChatMessage> messages, int keepRecent)
    {
        var candidate = Math.Max(0, messages.Count - keepRecent);
        for (var i = candidate; i >= 0; i--)
        {
            if (messages[i].Role == ChatMessageRoles.User)
                return i;
        }
        return candidate;
    }

    private readonly record struct TurnScore(int StartIndex, int EndIndex, double Score);

    /// <summary>按完整轮次分组，用 IMessageScorer 评分每轮信息密度。</summary>
    private static List<TurnScore> ScoreTurnsWithDecimator(
        IReadOnlyList<LlmChatMessage> messages,
        IMessageScorer decimator
    )
    {
        var turns = new List<(int startIndex, int endIndex, string text)>();
        for (var i = 0; i < messages.Count; )
        {
            if (messages[i].Role == ChatMessageRoles.User)
            {
                var start = i;
                var sb = new StringBuilder();
                for (; i < messages.Count; i++)
                {
                    if (i > start && messages[i].Role == ChatMessageRoles.User)
                        break;
                    if (!string.IsNullOrEmpty(messages[i].Content))
                        sb.AppendLine(messages[i].Content);
                }
                turns.Add((start, i - 1, sb.ToString()));
            }
            else
            {
                i++;
            }
        }

        if (turns.Count == 0)
            return [];

        var scored = new List<TurnScore>(turns.Count);
        for (var t = 0; t < turns.Count; t++)
        {
            var (start, end, text) = turns[t];
            var context = t > 0 ? turns[t - 1].text : "";
            var score = decimator.Score(text.AsSpan(), context.AsSpan());
            scored.Add(new TurnScore(start, end, score));
        }

        // 最后一轮总是保留（连续性）
        if (scored.Count > 1)
            scored[^1] = scored[^1] with { Score = 2.0 };

        return scored;
    }

    /// <summary>基于 RGDecimator 评分的智能切割点：保留 top-N 轮 + 最近一轮。</summary>
    private static int FindSmartCutPoint(
        IReadOnlyList<LlmChatMessage> messages,
        List<TurnScore> scoredTurns,
        int keepRecent
    )
    {
        if (scoredTurns.Count == 0 || messages.Count <= keepRecent)
            return 0;

        var topN = scoredTurns
            .OrderByDescending(t => t.Score)
            .Take(Math.Max(1, keepRecent / 2))
            .Select(t => t.StartIndex)
            .ToHashSet();

        if (scoredTurns.Count > 0)
            topN.Add(scoredTurns[^1].StartIndex);

        var cutIndex = topN.Min();
        for (var i = cutIndex; i >= 0; i--)
        {
            if (messages[i].Role == ChatMessageRoles.User)
                return i;
        }
        return cutIndex;
    }

    /// <summary>估算上下文 token（锚点优先 + CJK 感知，委托 ConversationLoop）。</summary>
    private static long EstimateContextTokens(IReadOnlyList<LlmChatMessage> messages) =>
        ConversationLoop.EstimateMessagesTokens(messages);

    /// <summary>估算 compact 后的上下文 token = 摘要 token + 保留后缀 token。</summary>
    private static int EstimateCompactTokensAfter(
        string summary,
        int cutIndex,
        IReadOnlyList<LlmChatMessage> messages
    )
    {
        var tokens = ConversationLoop.EstimateTextTokens(summary);
        for (var i = cutIndex; i < messages.Count; i++)
            tokens += ConversationLoop.EstimateSingleMessageTokens(messages[i]);
        return tokens > int.MaxValue ? int.MaxValue : (int)tokens;
    }

    /// <summary>生成压缩摘要（AI 优先，规则 fallback）。达阈值才用 AI 摘要。</summary>
    private async Task<string> GenerateCompactSummaryAsync(
        IReadOnlyList<LlmChatMessage> messages,
        int cutIndex,
        string? previousSummary
    )
    {
        const int aiSummaryMinCompressed = 20;
        if (cutIndex >= aiSummaryMinCompressed)
        {
            try
            {
                var aiSummary = await TryGenerateAiSummaryAsync(
                    messages,
                    cutIndex,
                    previousSummary
                );
                if (aiSummary is not null)
                    return aiSummary;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "generate AI compact summary failed");
            }
        }

        return Truncate(
            BuildCompactSummary(messages, messages.Count - cutIndex, previousSummary),
            800
        );
    }

    private async Task<string?> TryGenerateAiSummaryAsync(
        IReadOnlyList<LlmChatMessage> messages,
        int cutIndex,
        string? previousSummary
    )
    {
        var api = agent.TornadoApi;
        var modelName = agent.State.ModelName;
        if (api is null || string.IsNullOrEmpty(modelName))
            return null;

        var sb = new StringBuilder();
        for (var i = 0; i < cutIndex; i++)
        {
            var msg = messages[i];
            var role = msg.Role?.ToString() ?? "unknown";
            var content = Truncate(msg.Content ?? "", 200);
            if (!string.IsNullOrEmpty(content))
                sb.AppendLine($"[{role}]: {content}");
        }

        if (sb.Length == 0)
            return null;

        var prompt = new StringBuilder();
        prompt.AppendLine("请为以下对话生成简洁的结构化摘要，保留关键决策、文件操作和未完成任务。");
        prompt.AppendLine("摘要格式：");
        prompt.AppendLine("## 目标");
        prompt.AppendLine("## 进展");
        prompt.AppendLine("## 关键决策");
        prompt.AppendLine("## 文件操作");
        prompt.AppendLine("## 下一步");
        prompt.AppendLine();
        prompt.AppendLine(
            "要求：列出未完成任务与相关文件路径；下一步不超过 3 条具体动作；总长度不超过 15 句。"
        );

        if (previousSummary is not null)
        {
            prompt.AppendLine("## 之前的摘要");
            prompt.AppendLine(CompactPreviousSummary(previousSummary));
            prompt.AppendLine();
            prompt.AppendLine("## 新增对话");
        }

        prompt.AppendLine(sb.ToString());

        var request = new ChatRequest { MaxTokens = 1024 };
        var conversation = api.Chat.CreateConversation(request);
        conversation.AddSystemMessage(
            "你是一个对话摘要专家。为对话生成简洁、结构化的摘要，保留关键信息。使用中文。"
        );
        conversation.AddUserMessage(prompt.ToString());

        var summaryBuilder = new StringBuilder();
        await conversation.StreamResponse(token =>
        {
            summaryBuilder.Append(token);
        });
        var result = summaryBuilder.ToString().Trim();
        return string.IsNullOrEmpty(result) ? null : Truncate(result, 800);
    }

    /// <summary>构建规则 compact 摘要（AI 摘要的 fallback）。</summary>
    private static string BuildCompactSummary(
        IReadOnlyList<LlmChatMessage> messages,
        int keepRecent,
        string? previousSummary = null
    )
    {
        var compressedCount = messages.Count - keepRecent;
        var sb = new StringBuilder();
        sb.AppendLine("## 压缩概览");
        sb.AppendLine($"压缩前 {messages.Count} 条消息，保留最近 {keepRecent} 条");
        sb.AppendLine();

        if (previousSummary is not null)
        {
            sb.AppendLine("## 之前的摘要");
            sb.AppendLine(CompactPreviousSummary(previousSummary));
            sb.AppendLine();
        }

        sb.AppendLine("## 关键上下文");
        var userMessages = messages
            .Where(m => m.Role == ChatMessageRoles.User && !string.IsNullOrEmpty(m.Content))
            .Take(compressedCount)
            .ToList();
        if (userMessages.Count > 0)
        {
            sb.AppendLine("用户请求：");
            foreach (var msg in userMessages.Take(5))
                sb.AppendLine($"- {Truncate((msg.Content ?? "").TrimStart(), 80)}");
            if (userMessages.Count > 5)
                sb.AppendLine($"- ...（共 {userMessages.Count} 条用户消息）");
        }

        var assistantMessages = messages
            .Where(m => m.Role == ChatMessageRoles.Assistant && !string.IsNullOrEmpty(m.Content))
            .Take(compressedCount)
            .ToList();
        if (assistantMessages.Count > 0)
        {
            sb.AppendLine("助手回复摘要：");
            foreach (var msg in assistantMessages.Take(3))
                sb.AppendLine($"- {Truncate((msg.Content ?? "").TrimStart(), 80)}");
            if (assistantMessages.Count > 3)
                sb.AppendLine($"- ...（共 {assistantMessages.Count} 条助手消息）");
        }

        var fileOps = messages
            .Where(m =>
                m.Role == ChatMessageRoles.Tool
                && !string.IsNullOrEmpty(m.Name)
                && m.Name is "read" or "write" or "edit" or "list" or "search"
            )
            .Take(compressedCount)
            .Select(m => $"{m.Name}: {Truncate(m.Content ?? "", 60)}")
            .Take(10)
            .ToList();
        if (fileOps.Count > 0)
        {
            sb.AppendLine("文件操作：");
            foreach (var op in fileOps)
                sb.AppendLine($"- {op}");
        }

        return sb.ToString();
    }

    /// <summary>提取被压缩区尾部最近一个完整轮次原文，拼入摘要保留精确执行状态。</summary>
    private static string BuildRecentExecutionBlock(
        IReadOnlyList<LlmChatMessage> messages,
        int cutIndex
    )
    {
        if (cutIndex <= 0)
            return "";

        var start = cutIndex - 1;
        while (start >= 0 && messages[start].Role != ChatMessageRoles.User)
            start--;
        if (start < 0)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine("## 最近执行状态（压缩前最后一轮原文）");
        var appended = 0;
        for (var i = start; i < cutIndex && appended < 5; i++)
        {
            var msg = messages[i];
            var content = (msg.Content ?? "").ReplaceLineEndings(" ");
            if (content.Length == 0 && msg.ReasoningContent is not null)
                content = msg.ReasoningContent.ReplaceLineEndings(" ");
            if (content.Length == 0)
                continue;
            var role = msg.Role switch
            {
                ChatMessageRoles.User => "user",
                ChatMessageRoles.Assistant => "assistant",
                ChatMessageRoles.Tool => "tool",
                _ => "unknown",
            };
            sb.AppendLine($"[{role}] {Truncate(content, 200)}");
            appended++;
        }
        return appended > 0 ? sb.ToString() : "";
    }

    /// <summary>压缩继承摘要：只保留最近一份并截断（进展+文件操作+下一步三段）。</summary>
    private static string CompactPreviousSummary(string previousSummary)
    {
        const int maxPrevChars = 800;
        if (previousSummary.Length <= maxPrevChars)
            return previousSummary;

        var progress = ExtractSection(previousSummary, "## 进展");
        var fileOps = ExtractSection(previousSummary, "## 文件操作");
        var next = ExtractSection(previousSummary, "## 下一步");
        var sb = new StringBuilder();
        if (progress is not null)
            sb.AppendLine("## 进展").AppendLine(progress);
        if (fileOps is not null)
            sb.AppendLine("## 文件操作").AppendLine(fileOps);
        if (next is not null)
            sb.AppendLine("## 下一步").AppendLine(next);
        var result =
            sb.Length > 0 ? sb.ToString().TrimEnd() : previousSummary[..maxPrevChars] + "…";
        return result.Length <= maxPrevChars ? result : result[..maxPrevChars] + "…";
    }

    private static string? ExtractSection(string markdown, string heading)
    {
        var idx = markdown.IndexOf(heading, StringComparison.Ordinal);
        if (idx < 0)
            return null;
        var contentStart = idx + heading.Length;
        var nextIdx = markdown.IndexOf("\n## ", contentStart, StringComparison.Ordinal);
        var section =
            nextIdx > contentStart ? markdown[contentStart..nextIdx] : markdown[contentStart..];
        return section.Trim();
    }

    /// <summary>轻量作品状态快照，压缩后随摘要注入。</summary>
    private string BuildNovelSnapshot()
    {
        var novelId = agent.CurrentNovelId;
        if (string.IsNullOrEmpty(novelId))
            return "";
        var sb = new StringBuilder();
        sb.AppendLine("## 作品状态快照");
        sb.AppendLine($"- 当前小说: {novelId}");
        if (agent.CompressionBlocks.Count > 0)
            sb.AppendLine(
                $"- 历史压缩块: {agent.CompressionBlocks.Count} 个（可用 decompress 工具恢复原文）"
            );
        return sb.ToString();
    }

    private static string Truncate(string text, int maxLen) =>
        text.Length <= maxLen ? text : text[..maxLen] + "…";

    // ══════════════════════════════════════════════════════════
    //  decompress
    // ══════════════════════════════════════════════════════════

    /// <summary>恢复被压缩的消息原文。无 blockId 返回块列表 Markdown；有则写文件，返回结果文案 Markdown。</summary>
    public async Task<string> DecompressAsync(string blockId)
    {
        if (string.IsNullOrEmpty(_sessionId))
            return "没有活动会话。";

        try
        {
            if (string.IsNullOrWhiteSpace(blockId))
            {
                var blocks = await store.ListCompactBlocksAsync(_sessionId);
                if (blocks.Count == 0)
                    return "当前没有可恢复的压缩块。";

                var list = string.Join(
                    "\n",
                    blocks.Select(b =>
                        $"- `{b.BlockId}`（{b.MessageCount} 条）：{b.Summary[..Math.Min(60, b.Summary.Length)]}"
                    )
                );
                return $"可恢复的压缩块:\n\n{list}\n\n用 `/decompress <blockId>` 恢复指定块。";
            }

            var messages = await store.ReadCompactedMessagesAsync(_sessionId, blockId);
            if (messages is null || messages.Count == 0)
                return $"压缩块 {blockId} 无法恢复（不存在或创建于迁移前）。";

            var text = string.Join("\n\n", messages.Select(m => $"[{m.Role}]\n{m.Content}"));
            var cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache",
                "mythology",
                "decompress"
            );
            Directory.CreateDirectory(cacheDir);
            var ts = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var filePath = Path.Combine(cacheDir, $"{blockId}-{ts}.txt");
            await File.WriteAllTextAsync(filePath, text);

            return $"压缩块 {blockId}（{messages.Count} 条消息）已写入：\n`{filePath}`";
        }
        catch (Exception ex)
        {
            return $"恢复失败: {ex.Message}";
        }
    }

    // ══════════════════════════════════════════════════════════
    //  分支导航 + 标签
    // ══════════════════════════════════════════════════════════

    /// <summary>切换分支：中止 Agent → 可选生成分支摘要 → 保存 leaf → 双恢复 → 重导元数据。</summary>
    public async Task SwitchBranchAsync(string branchPointEntryId, bool generateSummary)
    {
        bridge.Stop(); // 中止运行中的 Agent，防止旧事件污染新上下文

        if (generateSummary)
            await GenerateAndSaveBranchSummaryAsync(branchPointEntryId);

        await store.SaveSessionLeaf(
            _sessionId,
            agent.CurrentNovelId ?? "",
            new SessionLeaf { TargetId = branchPointEntryId }
        );

        await RestoreContextFromDbAsync(_sessionId);

        // ★ 分支回溯到最后一页之前：该分支的上下文已被页边界封住（模型拿到空上下文）。
        // 复位为首页语义——页片段仍会注入并提示可用 history_* 检索该分支的历史。
        if (agent.State.Messages.Count == 0 && agent.ContextWindowState.StartOrder > 0)
        {
            agent.SetContextWindowState(ContextWindowState.First);
            log.LogInformation(
                "branch navigation: branch point precedes last context window → reset to first page"
            );
        }

        var (nextOrder, lastEntryId) = await store.GetSessionMetadataAsync(_sessionId);
        _nextOrder = nextOrder;
        _persistedMessageCount = agent.State.Messages.Count;
        AssertOrderInvariant();
        _lastParentEntryId = lastEntryId;

        OnSystemNotice?.Invoke(generateSummary ? "已导航到新分支点（含摘要）" : "已导航到新分支点");
    }

    private async Task GenerateAndSaveBranchSummaryAsync(string branchPointEntryId)
    {
        try
        {
            var activeMessages = await store.GetActiveMessages(_sessionId);
            var branchMessages = activeMessages
                .Where(m => m.EntryId is not null && m.ParentEntryId is not null)
                .ToList();

            var fromIndex = branchMessages.FindIndex(m => m.ParentEntryId == branchPointEntryId);
            if (fromIndex < 0)
                return;

            var abandonedMessages = branchMessages.Skip(fromIndex).ToList();
            if (abandonedMessages.Count == 0)
                return;

            var contentBuilder = new StringBuilder();
            foreach (var msg in abandonedMessages)
            {
                var role = msg.Role switch
                {
                    MessageRole.User => "用户",
                    MessageRole.Assistant => "助手",
                    MessageRole.Tool => $"工具({msg.ToolName})",
                    _ => "系统",
                };
                var text = msg.Content;
                if (text.Length > 200)
                    text = text[..200] + "...";
                contentBuilder.AppendLine($"[{role}] {text}");
            }

            var summary = await GenerateSummaryWithLlmAsync(contentBuilder.ToString());
            var firstAbandonedEntryId = abandonedMessages.FirstOrDefault()?.EntryId;

            await store.SaveBranchSummary(
                _sessionId,
                agent.CurrentNovelId ?? "",
                new BranchSummary
                {
                    EntryId = null,
                    ParentEntryId = branchPointEntryId,
                    FromId = firstAbandonedEntryId,
                    Summary = summary,
                    Order = _nextOrder,
                }
            );
            _nextOrder++;
        }
        catch (Exception ex)
        {
            OnSystemNotice?.Invoke($"生成分支摘要失败: {ex.Message}");
        }
    }

    private async Task<string> GenerateSummaryWithLlmAsync(
        string abandonedContent,
        CancellationToken ct = default
    )
    {
        var api = agent.TornadoApi;
        if (api is null)
            return "（无法生成摘要：未配置 LLM）";

        var conversation = api.Chat.CreateConversation();
        conversation.AddSystemMessage(
            "你是一个对话摘要助手。请用1-3句话简洁概括以下对话内容的核心要点，"
                + "重点关注：1) 用户的主要意图 2) 助手的关键输出 3) 为什么可能被放弃。"
                + "只输出摘要文本，不要加任何前缀或格式。"
        );
        conversation.AddUserMessage($"请概括以下被放弃的对话分支：\n\n{abandonedContent}");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(CloudTimings.AuxiliaryLlmTimeout);
        var sb = new StringBuilder();
        await foreach (var chunk in conversation.StreamResponseEnumerable(token: timeoutCts.Token))
            sb.Append(chunk);
        var response = sb.ToString();
        return string.IsNullOrWhiteSpace(response) ? "（摘要生成失败）" : response.Trim();
    }

    /// <summary>为分支节点添加可读标签。</summary>
    public async Task LabelAsync(string branchPointEntryId, string targetEntryId, string labelText)
    {
        try
        {
            await store.SaveSessionLabel(
                _sessionId,
                agent.CurrentNovelId ?? "",
                new SessionLabel
                {
                    EntryId = null,
                    ParentEntryId = branchPointEntryId,
                    TargetId = targetEntryId,
                    Label = labelText,
                    Order = _nextOrder,
                }
            );
            _nextOrder++;
            OnSystemNotice?.Invoke($"已添加标签: {labelText}");
        }
        catch (Exception ex)
        {
            OnSystemNotice?.Invoke($"添加标签失败: {ex.Message}");
        }
    }

    // ══════════════════════════════════════════════════════════
    //  变体持久化 + 输入历史 + Todo 快照
    // ══════════════════════════════════════════════════════════

    /// <summary>持久化变体组到 JSONL（VariantSetCreated），确保会话恢复后可重新打开。</summary>
    public async Task PersistVariantSetAsync(ExploreDetails explore)
    {
        _lastExploreDetails = explore;
        try
        {
            var json = JsonSerializer.Serialize(explore, JsonHelper.JsonOptions);
            await store.SaveSessionEvent(
                _sessionId,
                agent.CurrentNovelId ?? "",
                new SessionEvent
                {
                    EventType = SessionEventType.VariantSetCreated,
                    ParentEntryId = _lastParentEntryId,
                    Order = _nextOrder,
                    Content = json,
                }
            );
            _nextOrder++;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "persist variant set failed");
        }
    }

    /// <summary>会话恢复时加载最近变体组（内存优先，否则从 JSONL 反序列化）。</summary>
    private async Task LoadLastVariantSetAsync()
    {
        _lastExploreDetails = null;
        try
        {
            var json = await store.GetLastVariantSetContentAsync(_sessionId);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var explore = JsonSerializer.Deserialize<ExploreDetails>(
                    json,
                    JsonHelper.JsonOptions
                );
                if (explore?.Variants is { Count: > 0 })
                    _lastExploreDetails = explore;
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "load variant set failed");
        }
    }

    /// <summary>记录一条输入历史（JSONL + 内存）。</summary>
    public async Task SaveInputHistoryAsync(string text)
    {
        var t = text.Trim();
        if (t.Length == 0)
            return;
        if (_inputHistory.Count == 0 || _inputHistory[^1] != t)
            _inputHistory.Add(t);
        try
        {
            await store.SaveInputHistory(_sessionId, t);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "save input history failed");
        }
    }

    private void SaveCurrentSessionTodos()
    {
        if (string.IsNullOrEmpty(_sessionId))
            return;
        try
        {
            todoStore.SaveTo(TodoPathFor(_sessionId));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "save session todos failed");
        }
    }

    private void LoadSessionTodos(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;
        try
        {
            todoStore.TryLoadFrom(TodoPathFor(sessionId));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "load session todos failed");
        }
    }

    private static string TodoPathFor(string sessionId)
    {
        var mythDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mythology"
        );
        return Path.Combine(mythDir, "todos", $"{sessionId}.json");
    }

    // ═══════════════════════════════════════════════════════════
    //  上下文接续（Codex context management 语义）— 镜像 TuiApp.NewContext.cs
    //
    //  时序不变量：Agent._messages 是 ConversationLoop._persistedMessages 的镜像，
    //  唯一正确的重置点是 agent.ReplaceMessages([])；_persistedMessageCount 必须与清空绑定；
    //  _nextOrder 只递增（window 行占用一个 order）。
    // ═══════════════════════════════════════════════════════════

    /// <summary>上下文管理选项（null = compact 模式：不注入页片段与提醒、不走换页分支）。</summary>
    /// <summary>
    /// 上下文管理选项（null = compact）。<b>直读 Agent</b>而不是缓存副本：设置页支持运行期热切换，
    /// 缓存副本会让换页/压缩守卫读到过期模式。
    /// </summary>
    private ContextManagementOptions? _contextManagement => agent.ContextManagement;

    /// <summary>
    /// 压缩 / 换页后重置稳定性监控器：Unfreeze 在未冻结时直接 return，只调它会让
    /// TotalTokens 停留在高位 → 重试首轮再次被 pre-flight 拦截。Reset 归零后
    /// SeedTokenCount 只在 TotalTokens == 0 时生效，用于从新基线起步。
    /// </summary>
    private void ResetStabilityMonitor(long estimatedTokens)
    {
        if (agent.StabilityMonitor is not { } monitor)
            return;

        monitor.Reset();
        if (estimatedTokens > 0)
            monitor.SeedTokenCount(estimatedTokens);
    }

    /// <summary>
    /// 执行一次换页（**run 边界**调用：循环登记、run 正常结束后执行）：写 window 条目（滚动分片 + 记账），
    /// 并把模型侧消息清空（UI 不动）。
    /// 返回 false = 模式不是接续 / 无会话 / 本页为空 / 落盘失败（调用方按模式处理，**绝不回落压缩**）。
    /// </summary>
    /// <summary>最近一次换页原因（threshold | overflow | model）；用于决定新页是否自动接续。</summary>
    private string? _lastRollReason;

    /// <summary>当前用户消息下已自动接续的次数（用户每发一条新消息清零，防无界消耗）。</summary>
    private int _autoContinueCount;

    private async Task<bool> RollContextWindowAsync(string reason)
    {
        // ★ 模式互斥：只有接续模式产生 page —— compact 模式下任何调用都是空操作
        if (_contextManagement is not { Mode: ContextMode.Continuation })
            return false;

        if (string.IsNullOrEmpty(_sessionId) || string.IsNullOrEmpty(agent.CurrentNovelId))
            return false;

        // 空页不换：本页没有可封存的内容，再换只会白写条目并原地打转。
        if (agent.State.Messages.Count == 0)
            return false;

        try
        {
            var novelId = agent.CurrentNovelId;
            var previousWindowId = agent.ContextWindowState.WindowId;

            // 交接说明（new_context(note:)，模型自写）与任务锚点（系统自动，封页最后一条用户消息）：
            // note 同时写进 window 行（重启/恢复后仍可见）与新页状态；锚点只进页状态。
            var handoffNote = agent.ConsumeNewContextNote();
            var taskAnchor = await TryReadTaskAnchorAsync(previousWindowId);

            var (windowId, entryId, order) = await store.StartNewContextWindowAsync(
                _sessionId,
                novelId,
                reason,
                _nextOrder,
                note: handoffNote
            );

            _nextOrder = order + 1;
            _lastParentEntryId = entryId;

            agent.ReplaceMessages([]);
            _persistedMessageCount = 0;

            agent.SetContextWindowState(
                new ContextWindowState(
                    windowId,
                    previousWindowId,
                    "w0",
                    _nextOrder,
                    taskAnchor,
                    reason,
                    handoffNote
                )
            );
            _lastRollReason = reason;

            ResetStabilityMonitor(calibrator.Project(EstimateContextTokens([])));
            AssertOrderInvariant();

            // 不弹通知、不写会话面板：机制对用户隐藏（仅日志可查）
            log.LogInformation(
                "context window roll: reason={Reason} window={Window} order={Order}",
                reason,
                windowId,
                order
            );

            TryIndexSealedWindow(previousWindowId, novelId);
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "context window roll failed");
            return false;
        }
    }

    /// <summary>任务锚点最大长度（超出截断；它在每轮片段里出现，必须便宜）。</summary>
    private const int TaskAnchorMaxChars = 300;

    /// <summary>
    /// 读取刚封页的最后一条用户消息作为任务锚点 —— 换页不携带历史，但模型必须知道自己在做什么
    /// （跨页续做时用户往往只说「继续」）。失败/空 → null，绝不影响换页本身。
    /// </summary>
    private async Task<string?> TryReadTaskAnchorAsync(string sealedWindowId)
    {
        if (string.IsNullOrEmpty(_sessionId) || string.IsNullOrEmpty(sealedWindowId))
            return null;

        try
        {
            var items = await store.ListHistoryItemsAsync(
                _sessionId,
                sealedWindowId,
                role: "user",
                toolName: null,
                limit: 1,
                recentFirst: true,
                maxCharsPerItem: TaskAnchorMaxChars * 4,
                CancellationToken.None
            );
            var content = items.Count > 0 ? items[0].Content?.Trim() : null;
            if (string.IsNullOrEmpty(content))
                return null;
            return content.Length <= TaskAnchorMaxChars
                ? content
                : content[..TaskAnchorMaxChars] + "…";
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "read task anchor failed");
            return null;
        }
    }

    /// <summary>
    /// 溢出恢复分支（**模式互斥**，唯一入口）：接续 → 换页；compact → 分片压缩。
    /// 两条路径互不回落——接续模式下换页失败即报错，不做有损压缩。
    /// </summary>
    private Task<bool> RelieveOverflowAsync(int keepRecent) =>
        _contextManagement is { Mode: ContextMode.Continuation }
            ? RollContextWindowAsync("overflow")
            : CompactForOverflowAsync(keepRecent);

    /// <summary>
    /// 封页后异步建立向量索引（仅 vector_retrieval: true 且注入 kernel 时）。
    /// fire-and-forget：失败只记日志。
    /// </summary>
    private void TryIndexSealedWindow(string windowId, string novelId)
    {
        if (_contextManagement is not { VectorRetrieval: true })
            return;
        var kernel = retrievalKernel;
        if (kernel is null)
            return;

        var sessionId = _sessionId;
        _ = Task.Run(async () =>
        {
            try
            {
                var items = await store.ListHistoryItemsAsync(
                    sessionId,
                    windowId,
                    role: null,
                    toolName: null,
                    limit: 200,
                    recentFirst: false,
                    maxCharsPerItem: 0
                );
                foreach (var item in items)
                    await Core.Vector.ConversationHistorySearch.IndexItemAsync(
                        kernel,
                        novelId,
                        sessionId,
                        item
                    );
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "index sealed window failed");
            }
        });
    }
}
