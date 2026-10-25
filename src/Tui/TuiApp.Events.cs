using System.Diagnostics;
using System.Text.Json;
using Core.Agent;
using Core.Data;
using Core.Shared;
using Core.Tools;
using LlmTornado.Code;
using Tui.Controls;
using Tui.Services;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace Tui;

using Data;

public sealed partial class TuiApp
{
    /// <summary>
    /// Agent 状态变更回调。运行态时重置提示挂起标记和校准采样标记，
    /// 并通知任务面板和状态栏更新运行态样式。
    /// </summary>
    /// <param name="state">新的 TUI 状态</param>
    private void OnAgentStateChanged(TuiState state)
    {
        if (state == TuiState.Run)
        {
            _isPromptPending = false;
            _measuredThisRun = false; // 新一轮开始：等待新的实测用于校准采样
            _titleSpinner.SetBusy("思考中");
        }
        else
        {
            _titleSpinner.SetBusy(state == TuiState.Compact ? "压缩中" : null);
        }
        // 任务面板运行态感知：运行中 in_progress 高亮，中断/空闲时降回普通样式
        _todoPanel.SetAgentRunning(state == TuiState.Run);
        _statusBar.SetAgentState(state);
    }

    /// <summary>
    /// Agent 单轮结束回调。轮末 State.Messages 已与 ConversationLoop 同步，
    /// 用本轮实测 PromptTokens 采样一个校准点（估算→API 实测口径映射），
    /// 然后刷新 token 显示和大纲。
    /// </summary>
    private void OnAgentTurnEnded()
    {
        // ★ 轮末 State.Messages 已与 ConversationLoop 同步：
        // 用本轮实测（PromptTokens）采样一个校准点，建立估算 → API 实测口径的映射
        if (_measuredThisRun && _measuredContextTokens > 0)
        {
            _tokenCalibrator.AddSample(
                EstimateContextTokens(_agent.State.Messages),
                _measuredContextTokens
            );
            _measuredThisRun = false;
        }
        // ★ 轮末刷新显示：State.Messages 已包含完整一轮，重新计算会话上下文
        UpdateTokenDisplay();
        _ = RefreshOutline();
    }

    /// <summary>
    /// 存储实体变更回调。三重职责：
    /// 1. 将变更入队检索索引（异步）；
    /// 2. 防抖刷新大纲（200ms 延迟，可取消）；
    /// 3. 在对话面板显示变更摘要，若为用户手动编辑则向 Agent 发送 Steer 通知；
    /// 4. 写入审计日志。
    /// </summary>
    /// <param name="evt">实体变更事件</param>
    private Task OnStoreEntityChanged(EntityChangeEvent evt)
    {
        // ── 异步索引队列：实体变更后提交索引任务 ──
        if (_retrievalKernel is not null)
            _ = EnqueueIndexTaskAsync(evt);

        // 防抖刷新大纲：200ms 内多次变更只执行最后一次
        _refreshCts?.Cancel();
        _refreshCts = new CancellationTokenSource();
        var ct = _refreshCts.Token;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(200, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                PostToUi(() => _ = RefreshOutline());
            },
            ct
        );

        PostToUi(() =>
        {
            // 在对话面板显示变更摘要
            var label = EntityDisplayHelper.DisplayName(evt.EntityType);
            var changeSummary = EntityChangeSummary.Describe(evt);
            if (changeSummary is not null)
                _conversationPanel.AddSystemMessage($"📝 {changeSummary}");

            // 用户手动编辑时，向 Agent 发送 Steer 通知让 LLM 感知用户操作
            if (
                _manualEdit
                && evt.Action is AuditAction.Create or AuditAction.Update or AuditAction.Delete
            )
            {
                var steerMsg = evt.Action switch
                {
                    AuditAction.Create => $"[系统通知] 用户手动创建了{label}「{evt.EntityName}」",
                    AuditAction.Update => evt.FieldChanges is not null
                        ? $"[系统通知] 用户手动修改了{label}「{evt.EntityName}」: {FormatFieldChanges(evt.FieldChanges)}"
                        : $"[系统通知] 用户手动修改了{label}「{evt.EntityName}」",
                    AuditAction.Delete => $"[系统通知] 用户手动删除了{label}「{evt.EntityName}」",
                    _ => "",
                };
                if (!string.IsNullOrEmpty(steerMsg))
                    // ★ P0d: 系统通知改走 InjectQueue（静默注入 LLM 上下文，不触发 UI 转正气泡）
                    _agent.InjectQueue.Enqueue(new LlmChatMessage(ChatMessageRoles.User, steerMsg));
                _manualEdit = false;
            }
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// 工具调用结束回调。重置手动编辑标记；
    /// 若为实体 CRUD/读取工具且成功，解析结果中的 id 并高亮侧边栏对应实体。
    /// </summary>
    /// <param name="toolCallId">工具调用 ID</param>
    /// <param name="toolName">工具名称</param>
    /// <param name="isError">是否为错误结果</param>
    /// <param name="result">工具返回的 JSON 结果</param>
    /// <param name="details">附加详情（如变体信息）</param>
    private void OnAdapterToolEnd(
        string toolCallId,
        string toolName,
        bool isError,
        string result,
        object? details
    )
    {
        _manualEdit = false;
        var tool = _registry.TryGet(toolName);
        if (tool?.Category is ToolCategory.EntityCrud or ToolCategory.EntityRead && !isError)
        {
            var entityTypeStr = tool.EntityType;
            if (entityTypeStr is not null)
            {
                var entityType = EntityDisplayHelper.ToEntityType(entityTypeStr);
                string? entityId = null;
                try
                {
                    // 从工具返回的 JSON 中提取实体 id
                    using var doc = JsonDocument.Parse(result);
                    if (doc.RootElement.TryGetProperty("id", out var idEl))
                        entityId = idEl.GetString();
                }
                catch (JsonException)
                {
                    // 工具结果可能不含 id 字段，非异常情况
                }
                if (entityId is not null && entityType is not null)
                    _setUpPanel.SetHighlightEntity(entityType.Value, entityId);
            }
            _ = RefreshOutline();
        }
    }

    /// <summary>当前 session 是否有 API 实测的上下文 token 数。</summary>
    private bool _hasContextMeasure;

    /// <summary>最近一次 API 实测的上下文 token 数（PromptTokens，完整 prompt 大小）。</summary>
    private int _measuredContextTokens;

    /// <summary>API 测量所属的 session Id。防止旧会话的迟到事件污染新会话显示。</summary>
    private string? _measureSessionId;

    // ── 估算口径校准：用 API 实测把字符估算投影到"完整 prompt"口径（见 ContextTokenCalibrator） ──
    /// <summary>本轮运行是否产生过实测（用于轮末采样校准点）。</summary>
    private bool _measuredThisRun;

    /// <summary>估算 → API 实测口径校准器（滚动最小二乘拟合，跨会话复用）。</summary>
    private readonly ContextTokenCalibrator _tokenCalibrator = new();

    /// <summary>
    /// 收到 LLM 实测的 PromptTokens：这是当前会话此刻的真实上下文大小。
    /// 不累积、不跨会话——仅记录属于当前 session 的最新实测值。
    /// </summary>
    /// <param name="promptTokens">API 返回的 PromptTokens</param>
    private void OnTokenUpdate(int promptTokens)
    {
        if (promptTokens <= 0)
            return;

        // 会话切换后，旧 Agent 的迟到事件仍可能到达：忽略不属于当前 session 的测量
        if (_measureSessionId is not null && _measureSessionId != _sessionId)
            return;

        _hasContextMeasure = true;
        _measureSessionId = _sessionId;
        _measuredContextTokens = promptTokens;
        _measuredThisRun = true; // 本轮产生了实测，供轮末采样校准点
        UpdateTokenDisplay();
    }

    /// <summary>
    /// 上下文结构变化（会话切换 / compact / 清空 / 分支导航）后，
    /// 旧的 API 测量不再代表当前上下文，作废并回退为本地估算。
    /// </summary>
    private void InvalidateContextMeasure()
    {
        _hasContextMeasure = false;
        _measureSessionId = _sessionId;
        _measuredContextTokens = 0;
    }

    /// <summary>
    /// 刷新状态栏 token 显示：(当前会话上下文 / 选中模型的上下文窗口)。
    /// 口径 = 会话上下文（0 起算，不含系统/工具/小说上下文的固定开销），
    /// 与 pi/Claude Code 的上下文指示器一致：
    /// - 新会话 / 清空：0；
    /// - 加载历史：按恢复的消息重算（即上回的会话上下文总数）；
    /// - 发消息：把新消息的估算加进去（LLM 返回 usage 后由 OnTokenUpdate 用实测刷新）；
    /// - compact：重算摘要 + 保留后缀。
    /// </summary>
    /// <param name="pendingText">即将进入上下文的文本（发消息时传入，使计数先增后校）。</param>
    private void UpdateTokenDisplay(string? pendingText = null)
    {
        var window =
            _agent.ModelProfile?.ContextWindowTokens ?? _settingsService.Provider.ContextWindowSize;

        long used;
        if (pendingText is null && _hasContextMeasure && _tokenCalibrator.HasCalibration)
        {
            // LLM 调用进行中/刚结束：用实测值扣除固定开销，换算到会话上下文口径
            // 注意：仅在已校准时使用实测——否则原始实测是完整 prompt 口径（含固定开销），
            // 直接展示会与 0 起算的估算口径不一致（校准生效瞬间产生跳变）。
            used = _tokenCalibrator.ToConversationTokens(_measuredContextTokens);
        }
        else
        {
            // 结构变化（切换/compact/清空/恢复）或发消息：按消息内容估算会话上下文
            var est = EstimateContextTokens(_agent.State.Messages);
            if (!string.IsNullOrEmpty(pendingText))
                est += pendingText.Length * 2 / 3; // 新消息即将进入上下文，与 EstimateContextTokens 同公式
            used = _tokenCalibrator.ProjectConversation(est);
        }

        // 黄灯 = "即将触发自动 compact"。compact 阈值基于完整 prompt（含固定开销），
        // 而显示是会话上下文：把黄灯点换算到会话口径（窗口×percent − 固定开销）。
        var warningRatio = _agent.ModelProfile?.CompactThresholdPercent ?? 0.9;
        if (_tokenCalibrator.HasCalibration)
        {
            var overhead = _tokenCalibrator.OverheadTokens;
            if (overhead > 0 && overhead < window)
                warningRatio = Math.Max(0.05, (window * warningRatio - overhead) / window);
        }

        _statusBar.SetTokenStats(used, window, warningRatio);
    }

    /// <summary>模型/上下文窗口/thinking 级别变更后，旧校准不再适用（tokenizer 换算可能不同）。</summary>
    private void ResetCalibration() => _tokenCalibrator.Reset();

    /// <summary>
    /// Explore tool 变体选择事件处理。
    /// 弹出多 Tab 审阅弹窗，用户可选择：
    /// - 采用并写入章节
    /// - 基于此版本继续修改
    /// - 稍后决定
    /// </summary>
    /// <param name="toolCallId">工具调用 ID</param>
    /// <param name="details">变体详情对象</param>
    private void OnExploreVariant(string toolCallId, object? details)
    {
        if (details is null)
            return;
        _ = HandleVariantReviewAsync(details);
    }

    /// <summary>
    /// 导航到指定实体：在侧边栏中定位并展开该实体节点。
    /// </summary>
    /// <param name="entityType">实体类型字符串</param>
    /// <param name="entityId">实体 ID</param>
    private void OnNavigateToEntity(string entityType, string entityId)
    {
        var et = EntityDisplayHelper.ToEntityType(entityType);
        if (et is not null)
            _setUpPanel.NavigateToEntity(et.Value, entityId);
    }

    /// <summary>
    /// Agent 事件转发回调，将事件传递给适配器用于流式输出处理。
    /// </summary>
    /// <param name="evt">Agent 事件</param>
    /// <param name="ct">取消令牌</param>
    private Task OnAgentEvent(AgentEvent evt, CancellationToken ct)
    {
        _adapter.OnAgentEvent(evt);
        // 微信桥接自带 turn 门闩：只有微信发起的那一轮才会被转成手机消息
        _weChatBridge?.OnAgentEvent(evt);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 实体点击回调：将实体引用（如"角色「张三」"）插入输入框末尾。
    /// </summary>
    /// <param name="entityType">实体类型</param>
    /// <param name="entityName">实体显示名</param>
    private void OnEntityClicked(EntityType entityType, string entityName)
    {
        var label = EntityDisplayHelper.DisplayName(entityType);
        var current = _inputText.Value ?? "";
        var insertion = $"{label}「{entityName}」";
        _inputText.Value = string.IsNullOrEmpty(current) ? insertion : $"{current} {insertion}";
    }

    /// <summary>
    /// 🌐 运行设定检查回调（CHANGE_10 · fiction.setup.check）：
    /// 经工具注册表执行 check_setup 工具（NovelTools 插件注册，IFictionHarnessClient 注入），
    /// 报告以系统消息追加到对话面板。
    /// </summary>
    /// <param name="novelId">小说 ID</param>
    private void OnCloudSetupCheck(string novelId)
    {
        TuiSafeFire.SafeFire(
            async () =>
            {
                var tool = _registry.TryGet("check_setup");
                if (tool is null)
                {
                    _conversationPanel.AddSystemMessage(
                        "云端设定检查不可用：未找到 check_setup 工具。"
                    );
                    return;
                }
                if (string.IsNullOrEmpty(novelId))
                {
                    _conversationPanel.AddSystemMessage("请先打开一部小说再执行设定检查。");
                    return;
                }

                using var args = JsonDocument.Parse(
                    $"{{\"novel_id\":\"{novelId}\"}}",
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }
                );
                var result = await tool.ExecuteAsync(
                    $"ui-{Guid.NewGuid():N}",
                    args.RootElement,
                    CancellationToken.None
                );
                var text = string.Join(
                    "\n\n",
                    result.Content.OfType<TextContent>().Select(t => t.Text)
                );
                _conversationPanel.AddSystemMessage(
                    string.IsNullOrWhiteSpace(text) ? "云端设定检查完成（无输出）。" : text
                );
                // CHANGE_10 3b：结构化结果同步到设定面板（大纲 tab 顶部结果区 + 实体深链）
                if (result.Details is Core.Cloud.CheckSetupDetails details)
                    _setUpPanel.ShowSetupCheckResult(details);
            },
            "cloud setup check",
            _logger,
            "云端设定检查失败"
        );
    }

    /// <summary>
    /// 设定检查 finding 反馈上报（CHANGE_10 §4.3）：fire-and-forget，失败静默（日志），
    /// 不阻塞用户操作——反馈是质量信号，不是关键路径。
    /// </summary>
    private void OnSetupFeedbackPosted(string findingId, string decision)
    {
        if (_cloudHarness is null)
            return;
        TuiSafeFire.SafeFire(
            async () =>
                await _cloudHarness.ReportSetupFeedbackAsync(
                    [(findingId, decision)],
                    CancellationToken.None
                ),
            "setup feedback",
            _logger,
            "设定检查反馈上报失败"
        );
    }

    /// <summary>
    /// 查看实体时间线（状态变化 + 本体动作日志 + 旧字段审计只读合并）。
    /// 唯一真源是本体日志；旧 <c>entity_audit.jsonl</c> 只读合并渲染，不再新增写入。
    /// </summary>
    /// <param name="entityType">实体类型</param>
    /// <param name="entityId">实体 ID</param>
    private void OnViewAuditLog(EntityType entityType, string entityId) =>
        EntityLogDialog.Show(_store, _novelId, entityType, entityId);

    /// <summary>
    /// 通过 stty isig 启用 ISIG，使 Ctrl+C 产生 SIGINT 信号。
    /// XenoAtom 的 CBreak 模式将 ISIG 设为 0，Ctrl+C 作为 \x03 字节到达应用，
    /// 但被 TryCopyActiveSelection 拦截后无法可靠触发中断。
    /// 启用 ISIG 后 Ctrl+C 绕过所有 XenoAtom 路由，直接通过 SIGINT →
    /// Console.CancelKeyPress 处理，100% 可靠。
    /// </summary>
    private static void EnableCtrlCInterrupt()
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            using var proc = Process.Start(
                new ProcessStartInfo("stty", "isig")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            );
            proc?.WaitForExit(1000);
        }
        catch (Exception ex)
            when (ex
                    is InvalidOperationException
                        or PlatformNotSupportedException
                        or System.ComponentModel.Win32Exception
            )
        {
            // stty 不可用，无需操作
        }
    }
}
