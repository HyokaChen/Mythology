using Core.Agent;
using Core.Data;
using Core.Tools;

namespace Tui;

/// <summary>
/// 上下文接续（Codex context management 语义）在 TUI 侧的接线：
/// 换页执行体、溢出缓解、稳定性监控器归零、封页后的可选向量索引。
///
/// 时序不变量（改这里前先读）：
/// - 模型侧消息列表是 ConversationLoop._persistedMessages，Agent._messages 只是镜像；
///   唯一正确的重置点是 _agent.ReplaceMessages([])（清两份并重建 Conversation）。
/// - 重置必须发生在本轮 StreamOneTurnAsync 之外——换页请求由循环在迭代最顶部消费。
/// - _persistedMessageCount 必须与列表清空绑定（否则旧消息会被整段重复写入 JSONL）。
/// - _nextOrder 只递增：window 行已占用一个 order。
/// </summary>
public sealed partial class TuiApp
{
    /// <summary>
    /// 上下文管理选项（null = compact 模式：不注入页片段与提醒、不走换页分支）。
    /// <b>直读 Agent</b>而不是缓存副本：设置页可在运行期热切换模式，缓存副本会立刻过期
    /// （换页守卫/压缩守卫全部按它判断）。
    /// </summary>
    private ContextManagementOptions? _contextManagement => _agent.ContextManagement;

    /// <summary>最近一次换页原因（threshold | overflow | model）；用于决定新页是否自动接续。</summary>
    private string? _lastRollReason;

    /// <summary>当前用户消息下已自动接续的次数（用户每发一条新消息清零，防无界消耗）。</summary>
    private int _autoContinueCount;

    /// <summary>
    /// 压缩 / 换页后重置稳定性监控器。
    ///
    /// 注意：Unfreeze 在未冻结时直接 return（LyapunovMonitor/GrowthRatioGuard），
    /// 而 pre-flight 溢出通常发生在尚未冻结时（冻结在 100%）。只调 Unfreeze 会让
    /// TotalTokens 停留在高位 → 重试首轮立刻再次被 pre-flight 拦截（重试 3 次后失败并白写空页）。
    /// Reset() 归零后 SeedTokenCount 只在 TotalTokens == 0 时生效，恰好用于从新基线起步。
    /// </summary>
    private void ResetStabilityMonitor(long estimatedTokens)
    {
        if (_agent.StabilityMonitor is not { } monitor)
            return;

        monitor.Reset();
        if (estimatedTokens > 0)
            monitor.SeedTokenCount(estimatedTokens);
    }

    /// <summary>
    /// 执行一次换页（**run 边界**调用：循环登记、run 正常结束后执行）：写 window 条目（滚动分片 + 记账），
    /// 并把模型侧消息清空（UI 面板不动）。
    /// 返回 false = 模式不是接续 / 无会话 / 本页为空 / 落盘失败（调用方按模式处理，**绝不回落压缩**）。
    /// </summary>
    private async Task<bool> RollContextWindowAsync(string reason)
    {
        // ★ 模式互斥：只有接续模式产生 page —— compact 模式下任何调用都是空操作
        //（防御性拦截：新触发点若忘记判模式，绝不能悄悄写出 window 条目）。
        if (_contextManagement is not { Mode: ContextMode.Continuation })
            return false;

        if (string.IsNullOrEmpty(_sessionId) || string.IsNullOrEmpty(_agent.CurrentNovelId))
            return false;

        // 空页不换：本页没有可封存的内容，再换只会白写条目并原地打转
        //（溢出重试路径连续触发时由此收敛，最终走报错而不是无限换页）。
        if (_agent.State.Messages.Count == 0)
            return false;

        try
        {
            var novelId = _agent.CurrentNovelId;
            var previousWindowId = _agent.ContextWindowState.WindowId;

            // 交接说明（new_context(note:)，模型自写）与任务锚点（系统自动，封页最后一条用户消息）：
            // note 同时写进 window 行（重启/恢复后仍可见）与新页状态；锚点只进页状态。
            var handoffNote = _agent.ConsumeNewContextNote();
            var taskAnchor = await TryReadTaskAnchorAsync(previousWindowId);

            // 1) 落盘由每轮 NotifyMessagesReadyAsync 保证（Agent._messages 在 run 中只是上一轮镜像，
            //    此处调用 PersistBatchAsync 会是 no-op）；换页点的安全性来自
            //    「window 行的 order > 已落盘消息的 order」。
            // 2) 写 window 行并滚动分片（不生成摘要、不复制消息、不删除旧分片）
            var (windowId, entryId, order) = await _sessionStore.StartNewContextWindowAsync(
                _sessionId,
                novelId,
                reason,
                _nextOrder,
                note: handoffNote
            );

            _nextOrder = order + 1; // window 行占用一个 order
            _lastParentEntryId = entryId;

            // 3) 清空模型侧（UI 面板持有自己的历史副本，不受影响）
            _agent.ReplaceMessages([]);
            _persistedMessageCount = 0;

            // 4) 页状态 + prompt 基线（SetContextWindowState 内部会重置提醒标志与 prompt 基线）
            _agent.SetContextWindowState(
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

            // 5) 监控器真正归零（空页 ≈ 固定开销；为 0 时 Seed 自动跳过 →
            //    下一轮增量走 seeded=false 分支 = full prompt，即新页真实占用）
            ResetStabilityMonitor(_tokenCalibrator.Project(EstimateContextTokens([])));

            InvalidateContextMeasure();
            UpdateTokenDisplay();
            AssertOrderInvariant();

            // 不弹 Toast、不写会话面板：机制对用户隐藏（仅日志可查）
            Services.TuiLogger.Diagnostic(
                _logger,
                "context window roll",
                $"reason={reason} window={windowId} order={order}"
            );

            TryIndexSealedWindow(previousWindowId);
            return true;
        }
        catch (Exception ex)
        {
            Services.TuiLogger.OperationFailed(_logger, ex, "context window roll");
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
            var items = await _sessionStore.ListHistoryItemsAsync(
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
            Services.TuiLogger.OperationFailed(_logger, ex, "read task anchor");
            return null;
        }
    }

    /// <summary>
    /// 溢出恢复分支（**模式互斥**，唯一入口）：接续 → 换页（无摘要、无信息损失）；
    /// compact → 分片压缩。两条路径互不回落——接续模式下换页失败即报错，不做有损压缩。
    /// </summary>
    private Task<bool> RelieveOverflowAsync(int keepRecent) =>
        _contextManagement is { Mode: ContextMode.Continuation }
            ? RollContextWindowAsync("overflow")
            : CompactForOverflowAsync(keepRecent);

    /// <summary>
    /// 被迫换页（接近上限 / 溢出恢复）后的自动接续：新页不再等用户发话，直接把上一任务接着做。
    /// 模型自己判断已完成时会自然停下（不再换页 → 不再接续）；次数上限见 <see cref="ContextRollPolicy"/>。
    /// </summary>
    private async Task AutoContinueAfterForcedRollAsync(CancellationToken ct)
    {
        while (ContextRollPolicy.ShouldAutoContinue(_lastRollReason, _autoContinueCount))
        {
            _autoContinueCount++;
            _lastRollReason = null; // 下一轮由新的换页覆盖
            await _agent.Continue(ct);
            await PersistNewMessagesAsync();
            await PersistLoopStopEventAsync();
        }
    }

    /// <summary>
    /// 封页后异步建立向量索引（仅 vector_retrieval: true 且注入 kernel 时）。
    /// fire-and-forget：失败只记日志，绝不影响换页结果。
    /// </summary>
    private void TryIndexSealedWindow(string windowId)
    {
        if (_contextManagement is not { VectorRetrieval: true } || _retrievalKernel is null)
            return;

        var sessionId = _sessionId;
        var novelId = _agent.CurrentNovelId;
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(novelId))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                var items = await _sessionStore.ListHistoryItemsAsync(
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
                        _retrievalKernel,
                        novelId,
                        sessionId,
                        item
                    );
            }
            catch (Exception ex)
            {
                Services.TuiLogger.OperationFailed(_logger, ex, "index sealed window");
            }
        });
    }
}
