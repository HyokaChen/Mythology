using System.Diagnostics;
using System.Text;
using ClientPlatform;
using Core.Agent;
using Core.Data;
using Core.Infrastructure;
using Core.Notes;
using Core.Prompts;
using LlmTornado.ChatFunctions;
using Tui.Controls;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace Tui;

/// <summary>会话写入未完成，继续运行会造成无法恢复的历史缺口。</summary>
internal sealed class SessionPersistenceException(string message, Exception innerException)
    : Exception(message, innerException);

public sealed partial class TuiApp
{
    // ── 会话 ID 与输入历史 ──
    /// <summary>当前会话 ID，JSONL 文件名的一部分。</summary>
    private string _sessionId = "";

    /// <summary>任一消息写入失败后禁止继续生成，避免创建无法 resume 的历史缺口。</summary>
    private bool _sessionPersistenceFaulted;

    private void HandleSessionPersistenceFailure(Exception ex)
    {
        _sessionPersistenceFaulted = true;
        _isPromptPending = false;
        Services.TuiLogger.OperationFailed(_logger, ex, "persist session messages");
        ToastService.Error("会话保存失败，已停止生成以保护历史记录。请检查磁盘和会话文件后重启。");
    }

    /// <summary>最近一次持久化条目的 EntryId，作为下一条消息的 ParentEntryId（树状结构）。</summary>
    private string? _lastParentEntryId;

    /// <summary>已持久化的消息数，用于增量写入时跳过已保存的消息（与 Agent 内存/ConversationLoop 就绪消息对齐）。</summary>
    private int _persistedMessageCount;

    /// <summary>随手记捕获扫描游标：已扫描到 Agent 消息列表的哪一位（压缩/换页/切会话后回退自愈）。</summary>
    private int _noteScanIndex;

    /// <summary>
    /// JSONL 条目 order 计数器，保证所有条目（message/event/compact/label/branch_summary）
    /// 的 order 值唯一且严格递增，避免 GroupBy(order) 去重时误丢数据。
    /// 与 _persistedMessageCount（Agent 内存消息数）分离，因为非 message 条目也占 order 位。
    /// 不变量：_persistedMessageCount 小于 _nextOrder（非 message 条目占 order 位但不计为消息）。
    /// </summary>
    private int _nextOrder;

    /// <summary>DEBUG 条件断言：验证 _persistedMessageCount ≤ _nextOrder 不变量。</summary>
    private void AssertOrderInvariant()
    {
        Debug.Assert(
            _persistedMessageCount <= _nextOrder,
            $"Order invariant violated: _persistedMessageCount={_persistedMessageCount} > _nextOrder={_nextOrder}. "
                + "Non-message entries consume order bits but should not exceed message count."
        );
    }

    /// <summary>用户输入历史列表，用于上下箭头翻阅。</summary>
    private readonly List<string> _inputHistory = [];

    /// <summary>应用级取消令牌源，关闭窗口或中止 Agent 时触发。</summary>
    private CancellationTokenSource? _appCts;

    /// <summary>
    /// 发送用户消息给 Agent：主动压缩预检 → 更新 token 显示 → 调用 Agent.Prompt → 持久化。
    /// 处理取消（持久化 interrupt 事件）、上下文溢出（compact + retry 最多 3 次）、通用异常。
    /// </summary>
    /// <param name="text">用户输入文本（已内联引用替换后）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="overflowRetryCount">上下文溢出重试计数，防止 compact 死循环。</param>
    private async Task RunPromptAsync(string text, CancellationToken ct, int overflowRetryCount = 0)
    {
        if (_sessionPersistenceFaulted)
        {
            ToastService.Error("会话保存故障尚未恢复，已拒绝继续生成以防止历史记录缺失。");
            return;
        }

        try
        {
            await InjectNovelContextAsync();

            // 主动压缩：发送前检查，接近动态阈值时先压缩再发送
            // 在 Prompt 前评估 token 估算，避免当前提示已超预算才被动压缩。
            // 用校准后的完整 prompt 估算（固定开销 + tokenizer 修正）判断是否超限——
            // 状态栏显示的是会话上下文口径（不含固定开销），紧凑预检必须看完整 prompt；
            // 阈值通过 ModelContextProfile.CompactThresholdPercent 动态计算
            // （1M → 99.25%, 128K → 96.5%, 8K → 85%），给估算误差留余量。
            var estimatedTokens = _tokenCalibrator.Project(
                EstimateContextTokens(_agent.State.Messages)
            );
            var compactPercent = _agent.ModelProfile?.CompactThresholdPercent ?? 0.85;
            // ★ 阈值必须与显示分母一致：用模型声明/生效的上下文窗口（ModelContextProfile），
            // 而非用户手动选择的 ContextWindowSize——两者在"模型声明 ≠ 用户选择"时不一致。
            var contextWindow =
                _agent.ModelProfile?.ContextWindowTokens
                ?? _settingsService.Provider.ContextWindowSize;
            var compactThreshold = (long)(contextWindow * compactPercent);
            var keepRecent = _agent.ModelProfile?.ScaledKeepRecent ?? 20;
            // ★ 模式互斥：接续模式在发送前只换页（不压缩）；compact 模式保持原压缩路径。
            // 换页失败即放弃（不压缩、不拦发送）：真正的溢出由循环 pre-flight 抛异常后的溢出分支兜底。
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

            // ★ 发消息：先把新消息的估算加进显示（LLM 返回 usage 后由 OnTokenUpdate 用实测刷新）
            UpdateTokenDisplay(pendingText: text);

            await _agent.Prompt(text, ct);
            await PersistNewMessagesAsync();
            await CaptureNotesAsync(ct);
            // ★ 循环以可恢复原因停止（空回合/工具未继续/稳定性触发）时，落盘停止原因，
            // 否则自然收尾无 event 条目。顺序：消息先持久化，event 再以最后消息为父链。
            await PersistLoopStopEventAsync();

            // ★ 被迫换页后自动接续（新页直接接着做；模型判断已完成会自然停下）
            await AutoContinueAfterForcedRollAsync(ct);

            _isPromptPending = false; // 正常完成：清除 pending 标记
        }
        catch (SessionPersistenceException ex)
        {
            HandleSessionPersistenceFailure(ex);
        }
        catch (OperationCanceledException)
        {
            if (!await TryPersistNewMessagesAsync())
                return;
            _isPromptPending = false; // 取消：清除 pending 标记

            // 持久化 interrupt 事件
            try
            {
                await _sessionStore.SaveSessionEvent(
                    _sessionId,
                    _agent.CurrentNovelId ?? "",
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
            // - 接续：换页 + retry（无摘要、无信息损失）；换页失败直接报错，**不回落压缩**（模式互斥）
            const int maxOverflowRetries = 3;

            if (!await TryPersistNewMessagesAsync())
                return;

            // 持久化 error 事件
            try
            {
                await _sessionStore.SaveSessionEvent(
                    _sessionId,
                    _agent.CurrentNovelId ?? "",
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
                _isPromptPending = false; // 溢出重试耗尽：清理
                ToastService.Error(
                    _contextManagement is { Mode: ContextMode.Continuation }
                        ? $"上下文溢出，已重试 {maxOverflowRetries} 次仍无法恢复。请开启新会话。"
                        : $"上下文溢出，已重试 {maxOverflowRetries} 次仍无法恢复。请使用 /compact 手动压缩或开启新会话。"
                );
                return;
            }

            var keepRecentOverflow = _agent.ModelProfile?.ScaledKeepRecent ?? 20;
            var relieved = await RelieveOverflowAsync(keepRecentOverflow);
            if (relieved)
            {
                ToastService.Info(
                    _contextManagement is { Mode: ContextMode.Continuation }
                        ? "上下文过长，已开启新的上下文页并重试..."
                        : "上下文溢出，已压缩并重试..."
                );
                _isPromptPending = false; // 重试前清理（递归会重设/自己管理）
                _ = RunPromptAsync(
                    text,
                    _appCts?.Token ?? CancellationToken.None,
                    overflowRetryCount + 1
                );
                return;
            }
            ToastService.Error(
                _contextManagement is { Mode: ContextMode.Continuation }
                    ? "上下文过长，页切换失败。请开启新会话。"
                    : "上下文过长，压缩失败。请使用 /compact 或开启新会话。"
            );
            _isPromptPending = false; // 溢出失败：清理
        }
        catch (Exception ex)
        {
            // 保存会话消息（含用户消息，避免丢失）

            if (!await TryPersistNewMessagesAsync())
                return;
            try
            {
                var (httpStatusCode, _, isContextOverflow) = ExtractLlmError(ex);
                var errorCode = isContextOverflow
                    ? "context_overflow"
                    : httpStatusCode?.ToString() ?? "network";
                await _sessionStore.SaveSessionEvent(
                    _sessionId,
                    _agent.CurrentNovelId ?? "",
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
            _isPromptPending = false; // 任意异常：清理 pending
        }
    }

    /// <summary>
    /// 会话随手记捕获：把本轮新用户消息里带「记一下 / 备忘 / 我想到 …」的句子落成候选笔记。
    /// 旁路能力——任何失败都不影响对话（扫码器内部吞异常）。
    /// </summary>
    private async Task CaptureNotesAsync(CancellationToken ct)
    {
        var novelId = _agent.CurrentNovelId;
        if (string.IsNullOrEmpty(novelId))
            return;

        try
        {
            var messages = _agent.State.Messages;
            if (messages.Count < _noteScanIndex)
                _noteScanIndex = 0; // 压缩/换页/切会话后自愈
            var capturedOrder = NoteCaptureScanner.MaxWrittenOrderOf(
                await _store.GetStructure(novelId, ct)
            );
            await new NoteCaptureScanner(_store, _retrievalKernel).ScanAsync(
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
            Services.TuiLogger.OperationFailed(_logger, ex, "capture notes from session");
        }
    }

    private async Task<bool> TryPersistNewMessagesAsync()
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

    /// <summary>循环以可恢复原因停止（空回合/工具未继续/稳定性触发）时，将停止原因写入会话 JSONL。
    /// 与 Interrupt/ContextOverflow 事件同模式；自然收尾（LastLoopStop=null）不写。</summary>
    private async Task PersistLoopStopEventAsync()
    {
        var stop = _agent.LastLoopStop;
        if (stop is null)
            return;
        try
        {
            await _sessionStore.SaveSessionEvent(
                _sessionId,
                _agent.CurrentNovelId ?? "",
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

    /// <summary>
    /// 增量持久化 Agent 内存中新增的消息到 JSONL。
    /// 更新 _persistedMessageCount（消息跳过偏移）、_nextOrder（order 计数器）、_lastParentEntryId。
    /// </summary>
    private async Task PersistNewMessagesAsync()
    {
        await PersistBatchAsync(_agent.State.Messages);
    }

    /// <summary>
    /// ConversationLoop 每轮消息就绪回调：用户消息入队后、每个 LLM/tool turn 结束后
    /// 即时写入 FileSessionStore，不等 Agent loop 整体停止。
    /// </summary>
    private Task PersistReadyMessagesAsync(
        IReadOnlyList<LlmChatMessage> readyMessages,
        CancellationToken _
    ) => PersistBatchAsync(readyMessages);

    /// <summary>
    /// 将指定消息集合从 _persistedMessageCount 起增量写入 JSONL 的公共实现。
    /// 调用方可以是 Agent loop 结束后的 PersistNewMessagesAsync，
    /// 也可以是 ConversationLoop 每轮结束的 PersistReadyMessagesAsync。
    /// </summary>
    private async Task PersistBatchAsync(IReadOnlyList<LlmChatMessage> messages)
    {
        try
        {
            var novelId = _agent.CurrentNovelId ?? "";
            var newCount = messages.Count;
            if (newCount <= _persistedMessageCount)
                return;

            var newMessages = new LlmChatMessage[newCount - _persistedMessageCount];
            for (var i = 0; i < newMessages.Length; i++)
                newMessages[i] = messages[_persistedMessageCount + i];
            var lastEntryId = await _sessionStore.SaveSessionMessages(
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

    /// <summary>
    /// 从数据库恢复会话的全部消息（UI 显示用，包含已 compact 的历史）。
    /// </summary>
    /// <returns>消息数组，无消息时返回 null。</returns>
    private async Task<LlmChatMessage[]?> RestoreMessagesFromDbAsync(string sessionId)
    {
        try
        {
            var dbMessages = await _sessionStore.GetActiveMessages(sessionId);
            return dbMessages.Count == 0
                ? null
                : dbMessages.Select(MapDbMessageToChatMessage).ToArray();
        }
        catch (Exception ex)
        {
            Services.TuiLogger.OperationFailed(_logger, ex, "restore session messages");
            return null;
        }
    }

    /// <summary>
    /// 从 compact 后的上下文恢复 LLM 消息（仅保留 compact 点之后的消息）。
    /// </summary>
    private async Task RestoreContextFromDbAsync(string sessionId)
    {
        try
        {
            var boundary = await _sessionStore.GetWindowBoundaryAsync(sessionId);

            var dbMessages = await _sessionStore.GetContextMessages(sessionId);
            if (dbMessages.Count == 0)
            {
                _agent.ReplaceMessages([]);
                // ★ 两个分支都要同步页状态（否则空页 / 全被压缩的会话恢复后页状态丢失，
                // 4.6 的按页取 id 与页片段注入都会失准）
                _agent.SetContextWindowState(boundary);
                return;
            }

            var messages = dbMessages.Select(MapDbMessageToChatMessage).ToArray();
            _agent.ReplaceMessages(messages);
            _agent.SetContextWindowState(boundary);
        }
        catch (Exception ex)
        {
            Services.TuiLogger.OperationFailed(_logger, ex, "restore session context");
            _agent.ReplaceMessages([]);
        }
    }

    /// <summary>
    /// 将数据库消息记录映射为 LlmChatMessage。
    /// 从 toolCallsJson 字段恢复 ToolCalls。
    /// </summary>
    private static LlmChatMessage MapDbMessageToChatMessage(ChatMessage m)
    {
        var role = MessageRoleMapping.ToChatMessageRoles(m.Role);
        var msg = new LlmChatMessage(role, m.Content)
        {
            Name = m.ToolName,
            ToolCallId = m.ToolCallId,
            ToolInvocationSucceeded = !m.IsError,
            Tokens = m.Tokens, // 透传上下文 token 锚点（方案 B：恢复时作精确锚点）
            // B3 ref 稳定：用 NativeObject 透传 EntryId（LlmTornado 不序列化该字段，
            // 且 user 消息在 RestoreFromMessages 中原对象传递，NativeObject 跨压缩保留）。
            NativeObject = m.EntryId,
        };

        // Restore thinking/reasoning content (only ReasoningContent, no Parts manipulation)
        if (m.Role == MessageRole.Assistant && !string.IsNullOrEmpty(m.ReasoningContent))
        {
            msg.ReasoningContent = m.ReasoningContent;
        }

        // Restore ToolCalls from the dedicated toolCallsJson field
        if (m.Role == MessageRole.Assistant && !string.IsNullOrEmpty(m.ToolCallsJson))
            RestoreToolCallsFromJson(m.ToolCallsJson, msg);

        return msg;
    }

    /// <summary>
    /// 从 LLM 异常中提取结构化错误信息（委托到共享的 <see cref="Core.Infrastructure.LlmErrorHelper"/>）。
    /// </summary>
    private static (int? httpStatusCode, string rawMessage, bool isContextOverflow) ExtractLlmError(
        Exception ex
    ) => LlmErrorHelper.Extract(ex);

    /// <summary>
    /// 从 JSON 字符串恢复 ToolCalls 到消息对象。
    /// 键名使用 snake_case（id/name/arguments）。
    /// </summary>
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

    /// <summary>
    /// 打开小说（新建或恢复会话）：中止旧 Agent → 保存旧会话任务 → 恢复/创建会话 → 刷新 UI。
    /// </summary>
    /// <param name="novelId"></param>
    /// <param name="sessionId">会话 ID，null 则新建会话。</param>
    private async Task OpenNovelAsync(string novelId, string? sessionId = null)
    {
        // ★ 会话切换前置检查：若 Agent 正在运行，先中止。
        // 否则旧会话的 LLM 调用会继续写回新会话的上下文（数据污染），
        // 且其 TokenUsageEvent 会让 token 显示错误归属到新会话。
        AbortRunningAgent();

        // 会话切换前，将当前会话的任务快照落盘（会话间隔离）
        SaveCurrentSessionTodos();

        if (sessionId is not null)
        {
            // 恢复已有会话
            var session = await _sessionStore.GetSession(sessionId);
            if (session is not null)
            {
                _sessionId = session.Id;
                _agent.SessionId = _sessionId;
                _agent.CurrentNovelId = novelId;

                // UI 恢复全部消息
                var uiMessages = await RestoreMessagesFromDbAsync(_sessionId);
                _sessionPersistenceFaulted = false;
                if (uiMessages is { Length: > 0 })
                {
                    _conversationPanel.Clear();
                    _conversationPanel.RestoreMessages(uiMessages);
                }

                // LLM 仅加载 compact 后的上下文消息
                await RestoreContextFromDbAsync(_sessionId);

                // 恢复增量追踪状态：从 JSONL 轻量元数据推导
                var (nextOrder, lastEntryId) = await _sessionStore.GetSessionMetadataAsync(
                    _sessionId
                );
                _nextOrder = nextOrder;
                _persistedMessageCount = _agent.State.Messages.Count;
                AssertOrderInvariant();
                _lastParentEntryId = lastEntryId;

                // 恢复输入历史
                try
                {
                    var dbHistory = await _sessionStore.GetInputHistory(_sessionId, limit: 100);
                    dbHistory.Reverse();
                    _inputHistory.Clear();
                    _inputHistory.AddRange(dbHistory);
                    _promptEditor.SetHistory(_inputHistory);
                }
                catch (Exception ex)
                {
                    Services.TuiLogger.OperationFailed(_logger, ex, "load novel input history");
                }
                await RefreshOutline();
                // ★ 会话切换：token 显示必须按新会话重算（旧会话的 API 实测值作废）
                InvalidateContextMeasure();
                UpdateTokenDisplay();
                LoadSessionTodos(_sessionId);
                ToastService.Success($"已恢复会话: {session.Title}");
                return;
            }
            ToastService.Warning("会话不存在，将创建新会话");
        }

        // 新建会话
        _sessionId = Guid.NewGuid().ToString("N")[..12];
        _agent.SessionId = _sessionId;
        _agent.CurrentNovelId = novelId;
        _agent.ReplaceMessages([]);
        _lastParentEntryId = null;
        _persistedMessageCount = 0;
        _sessionPersistenceFaulted = false;
        _nextOrder = 0;
        AssertOrderInvariant();
        _conversationPanel.Clear();
        // 新会话无历史任务，清空面板（避免残留上个会话的任务）
        _todoStore.Clear();
        await RefreshOutline();
        // ★ 新建会话：上下文为空，重置 token 显示
        InvalidateContextMeasure();
        UpdateTokenDisplay();
    }

    /// <summary>
    /// 恢复小说的最新会话：中止旧 Agent → 加载最新会话 → 恢复 UI/LLM 上下文/增量状态。
    /// </summary>
    private async Task ResumeSessionAsync(string novelId)
    {
        try
        {
            // ★ 与 OpenNovelAsync 一致：切换上下文前中止运行中的 Agent
            AbortRunningAgent();

            var sessions = await _sessionStore.GetSessions(novelId);
            var latest = sessions.FirstOrDefault();
            if (latest is null)
            {
                ToastService.Warning($"未找到小说 {novelId} 的活跃会话");
                return;
            }

            _agent.CurrentNovelId = novelId;
            _sessionId = latest.Id;
            _agent.SessionId = _sessionId;
            _sessionPersistenceFaulted = false;

            // UI 恢复全部消息
            var uiMessages = await RestoreMessagesFromDbAsync(latest.Id);
            if (uiMessages is { Length: > 0 })
            {
                _conversationPanel.Clear();
                _conversationPanel.RestoreMessages(uiMessages);
                await RefreshOutline();
            }
            else
            {
                ToastService.Warning("会话为空");
                return;
            }

            // LLM 仅加载 compact 后的上下文消息
            await RestoreContextFromDbAsync(_sessionId);

            // 恢复增量追踪状态
            var (nextOrder, lastEntryId) = await _sessionStore.GetSessionMetadataAsync(_sessionId);
            _nextOrder = nextOrder;
            _persistedMessageCount = _agent.State.Messages.Count;
            AssertOrderInvariant();
            _lastParentEntryId = lastEntryId;

            // ★ 恢复会话后刷新 token 显示（新会话上下文，旧测量作废）
            InvalidateContextMeasure();
            UpdateTokenDisplay();
        }
        catch (Exception ex)
        {
            ToastService.Error($"恢复会话失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 更新小说上下文（每次发消息前调用）。
    /// 使用 SetNovelContext 只更新上下文部分，不重建 ConversationLoop。
    /// </summary>
    private async Task InjectNovelContextAsync()
    {
        var novelId = _agent.CurrentNovelId;
        if (string.IsNullOrEmpty(novelId))
        {
            _agent.SetNovelContext(
                "⚡ 当前没有加载任何小说项目。如果用户想创建新小说，先用 ask_user 确认，不要直接调用 create_novel。"
            );
            return;
        }

        try
        {
            var novel = await _store.GetNovel(novelId);
            if (novel is null)
                return;

            // 注入块由 Core.Prompts.NovelContextBlock 统一构建（三层大纲都在里面：总纲摘要 + 卷纲概览 +
            // 章节标题进度；细纲不进注入块，按需 get_outline）。长度有界，长篇不随规模膨胀。
            var chapters = novel.Chapters.OrderBy(c => c.Order).ToList();
            var volumes = await _store.GetVolumes(novelId);
            _agent.SetNovelContext(NovelContextBlock.Build(novel, chapters, volumes));
        }
        catch (Exception ex)
        {
            Services.TuiLogger.OperationFailed(_logger, ex, "inject novel context");
        }
    }

    /// <summary>
    /// 刷新右侧面板（SetUp 面板实体列表、状态栏小说名/增强模式、补全数据源）。
    /// </summary>
    private async Task RefreshOutline()
    {
        var novelId = _agent.CurrentNovelId;
        if (string.IsNullOrEmpty(novelId))
        {
            _statusBar.SetNovelName(null);
            _titleSpinner.SetBaseTitle("Mythology");
            return;
        }
        _setUpPanel.CurrentNovelId = novelId;
        try
        {
            await _setUpPanel.RefreshAsync(novelId);
            var novel = await _store.GetNovel(novelId);
            _statusBar.SetNovelName(novel?.Title);
            if (!string.IsNullOrEmpty(novel?.Title))
                _titleSpinner.SetBaseTitle(novel!.Title);
            _statusBar.SetEnhanceMode(novel?.AutoEnhance ?? "semi");
            UpdateEntityCompletions();
        }
        catch (Exception ex)
        {
            ToastService.Error($"刷新面板失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 将当前会话的任务快照保存到 ~/.mythology/todos/{sessionId}.json。
    /// 会话切换/退出时调用，保证任务跨会话隔离、恢复会话时还原。
    /// </summary>
    private void SaveCurrentSessionTodos()
    {
        if (_todoStore is null || string.IsNullOrEmpty(_sessionId))
            return;
        try
        {
            _todoStore.SaveTo(TodoPathFor(_sessionId));
        }
        catch (Exception ex)
        {
            Services.TuiLogger.OperationFailed(_logger, ex, "save session todos");
        }
    }

    /// <summary>
    /// 从 ~/.mythology/todos/{sessionId}.json 恢复任务快照（会话恢复时调用）。
    /// 文件不存在或损坏时静默保留当前内容。
    /// </summary>
    private void LoadSessionTodos(string sessionId)
    {
        if (_todoStore is null || string.IsNullOrEmpty(sessionId))
            return;
        try
        {
            _todoStore.TryLoadFrom(TodoPathFor(sessionId));
        }
        catch (Exception ex)
        {
            Services.TuiLogger.OperationFailed(_logger, ex, "load session todos");
        }
    }

    /// <summary>根据会话 ID 生成任务快照文件路径 ~/.mythology/todos/{sessionId}.json。</summary>
    private static string TodoPathFor(string sessionId)
    {
        var mythDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mythology"
        );
        return Path.Combine(mythDir, "todos", $"{sessionId}.json");
    }

    /// <summary>
    /// 处理树形导航结果：标签模式走标签流程，否则切换分支（中止 Agent → 保存 leaf → 恢复消息/上下文/增量状态）。
    /// </summary>
    private async Task HandleTreeNavigationAsync(TreeSelectorPanel.TreeNavigationResult result)
    {
        // 标签模式：为节点添加标签
        if (result.LabelTargetEntryId is not null)
        {
            HandleLabelAsync(result.BranchPointEntryId, result.LabelTargetEntryId);
            return;
        }

        // ★ 分支导航会切换上下文：先中止运行中的 Agent，防止旧事件污染新上下文显示
        AbortRunningAgent();

        if (result.GenerateSummary)
        {
            await GenerateAndSaveBranchSummaryAsync(result.BranchPointEntryId);
        }

        // 保存 leaf 指向分支点，恢复该路径消息
        await _sessionStore.SaveSessionLeaf(
            _sessionId,
            _agent.CurrentNovelId ?? "",
            new SessionLeaf { TargetId = result.BranchPointEntryId }
        );

        // 恢复 UI 消息
        var uiMessages = await RestoreMessagesFromDbAsync(_sessionId);
        if (uiMessages is { Length: > 0 })
        {
            _conversationPanel.Clear();
            _conversationPanel.RestoreMessages(uiMessages);
        }

        // 恢复 LLM 上下文
        await RestoreContextFromDbAsync(_sessionId);

        // ★ 分支回溯到最后一页之前：该分支的上下文已被页边界封住（模型拿到空上下文）。
        // 复位为首页语义——页片段仍会注入并提示可用 history_* 检索该分支的历史。
        if (_agent.State.Messages.Count == 0 && uiMessages is { Length: > 0 })
        {
            _agent.SetContextWindowState(ContextWindowState.First);
            Services.TuiLogger.Diagnostic(
                _logger,
                "branch navigation",
                "branch point precedes last context window → reset to first page"
            );
        }

        // 恢复增量追踪状态（与 OpenNovelAsync/ResumeSessionAsync 一致）
        var (nextOrder, lastEntryId) = await _sessionStore.GetSessionMetadataAsync(_sessionId);
        _nextOrder = nextOrder;
        _persistedMessageCount = _agent.State.Messages.Count;
        AssertOrderInvariant();
        _lastParentEntryId = lastEntryId;

        // ★ 分支导航恢复了另一条路径的上下文：token 显示重算
        InvalidateContextMeasure();
        UpdateTokenDisplay();

        ToastService.Success(
            result.GenerateSummary ? "已导航到新分支点（含摘要）" : "已导航到新分支点"
        );
    }

    /// <summary>
    /// 生成分支摘要：收集被放弃分支的消息 → LLM 摘要 → 持久化 BranchSummary。
    /// 在分支导航时调用，为被放弃的分支生成简短摘要供后续参考。
    /// </summary>
    private async Task GenerateAndSaveBranchSummaryAsync(string branchPointEntryId)
    {
        try
        {
            // 收集当前活跃路径上从分支点到末尾的消息（即将被放弃的分支）
            var activeMessages = await _sessionStore.GetActiveMessages(_sessionId);
            var branchMessages = activeMessages
                .Where(m => m.EntryId is not null && m.ParentEntryId is not null)
                .ToList();

            // 找到从分支点开始的消息
            var fromIndex = branchMessages.FindIndex(m => m.ParentEntryId == branchPointEntryId);
            if (fromIndex < 0)
            {
                // 没有被放弃的分支内容，无需生成摘要
                return;
            }

            var abandonedMessages = branchMessages.Skip(fromIndex).ToList();
            if (abandonedMessages.Count == 0)
                return;

            // 构建摘要请求
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

            var abandonedContent = contentBuilder.ToString();

            // 使用 LLM 生成摘要
            var summary = await GenerateSummaryWithLlmAsync(
                abandonedContent,
                CancellationToken.None
            );

            // 保存 BranchSummary
            var firstAbandonedEntryId = abandonedMessages.FirstOrDefault()?.EntryId;

            await _sessionStore.SaveBranchSummary(
                _sessionId,
                _agent.CurrentNovelId ?? "",
                new BranchSummary
                {
                    EntryId = null, // 由 FileSessionStore 自动生成
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
            ToastService.Warning($"生成分支摘要失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 调用 LLM 为被放弃的对话分支生成 1-3 句摘要。
    /// </summary>
    /// <returns>摘要文本。</returns>
    private async Task<string> GenerateSummaryWithLlmAsync(
        string abandonedContent,
        CancellationToken ct = default
    )
    {
        var api = _agent.TornadoApi;
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

    /// <summary>
    /// 弹出标签输入对话框，为分支节点添加可读标签。
    /// </summary>
    /// <param name="branchPointEntryId"></param>
    /// <param name="targetEntryId">标签目标 EntryId。</param>
    private void HandleLabelAsync(string branchPointEntryId, string targetEntryId)
    {
        var inputField = new MaskedInput { Template = "____________________" };
        var inputPanel = new Padder { Content = inputField, Padding = new Thickness(1, 0, 1, 0) };

        var contentFill = Style
            .None.WithBackground(Color.Rgb(0x2D, 0x24, 0x24)) // 深灰棕 — 内容区
            .WithForeground(Color.Rgb(0xC7, 0xB2, 0x99)); // 月光

        var confirmBtn = new Button(new Markup("确认")).Style(PanelThemes.DesertButtonAccent);

        var dialog = new Dialog()
            .Style(PanelThemes.DesertDialog)
            .IsResizable(false)
            .IsDraggable(false)
            .Title("🔖 添加标签")
            .Width(50)
            .MinHeight(8)
            .Padding(new Thickness(2, 1, 2, 1))
            .Content(
                new BackgroundPanel
                {
                    FillStyle = contentFill,
                    Content = new VStack(
                        inputPanel,
                        new Padder { Content = confirmBtn, Padding = new Thickness(0, 1, 0, 0) }
                    ),
                    HorizontalAlignment = Align.Stretch,
                    VerticalAlignment = Align.Stretch,
                }
            );

        var closeBtn = PanelThemes.CreateDialogCloseButton(dialog.Close);
        dialog.TopRightText(closeBtn);

        confirmBtn.Click(() =>
        {
            var labelText = inputField.Value?.Trim();
            if (string.IsNullOrEmpty(labelText))
            {
                ToastService.Warning("标签不能为空");
                return;
            }

            dialog.Close();
            _ = SaveLabelAsync(branchPointEntryId, targetEntryId, labelText);
        });

        dialog.KeyDown(
            (x, e) =>
            {
                switch (e.Key)
                {
                    case TerminalKey.Escape:
                        dialog.Close();
                        e.Handled = true;
                        break;
                    case TerminalKey.Enter:
                    {
                        var labelText = inputField.Value?.Trim();
                        if (!string.IsNullOrEmpty(labelText))
                        {
                            dialog.Close();
                            _ = SaveLabelAsync(branchPointEntryId, targetEntryId, labelText);
                        }
                        e.Handled = true;
                        break;
                    }
                }
            }
        );

        dialog.Show();
        dialog.App?.Focus(inputField);
    }

    /// <summary>
    /// 持久化标签到 JSONL，递增 _nextOrder。
    /// </summary>
    /// <param name="branchPointEntryId">分支点 EntryId。</param>
    /// <param name="targetEntryId"></param>
    /// <param name="labelText">标签文本。</param>
    private async Task SaveLabelAsync(
        string branchPointEntryId,
        string targetEntryId,
        string labelText
    )
    {
        try
        {
            await _sessionStore.SaveSessionLabel(
                _sessionId,
                _agent.CurrentNovelId ?? "",
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

            ToastService.Success($"已添加标签: {labelText}");
        }
        catch (Exception ex)
        {
            ToastService.Error($"添加标签失败: {ex.Message}");
        }
    }
}
