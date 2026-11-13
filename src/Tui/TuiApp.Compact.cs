using System.Text;
using Core.Agent;
using Core.Data;
using Core.Stability;
using LlmTornado.Chat;
using LlmTornado.Code;
using XenoAtom.Terminal.UI.Controls;
using LlmChatMessage = LlmTornado.Chat.ChatMessage;

namespace Tui;

public sealed partial class TuiApp
{
    /// <summary>压缩结果三态（B4 防重入）：成功 / 被拒绝（已有压缩进行中 / 模式互斥）/ 失败。</summary>
    /// <remarks>
    /// 两种「被拒绝」必须分开：
    /// <list type="bullet">
    /// <item><see cref="RejectedInProgress"/> — B4 防重入，稍后重试可能成功，调用方可重试。</item>
    /// <item><see cref="RejectedWrongMode"/> — 模式互斥兜底（接续模式不产生 compact 条目），
    /// 重试永远无用，必须让用户看到真实原因而不是「压缩进行中」。</item>
    /// </list>
    /// </remarks>
    internal enum CompactResult
    {
        Success,
        RejectedInProgress,
        RejectedWrongMode,
        TooSmall,
        Failed,
    }

    /// <summary>
    /// 压缩被拒绝时的对外文案（Toast + 压缩结束事件）。
    /// 两种拒绝语义不同，文案绝不能混——见 <see cref="CompactResult"/>。
    /// </summary>
    internal static (string Toast, string CompactEnd) DescribeRejection(CompactResult result) =>
        result switch
        {
            CompactResult.RejectedInProgress => (
                "压缩进行中，请稍候再试",
                "压缩请求被拒绝（已有压缩进行中）"
            ),
            CompactResult.RejectedWrongMode => (
                "接续模式不使用压缩：上下文接近上限时会自动开启新页。",
                "压缩请求被拒绝（当前为接续模式）"
            ),
            _ => ("", ""),
        };

    /// <summary>
    /// 手动压缩上下文：保留最近 keepRecent 条消息，其余生成摘要替换。
    /// 由 /compact 命令触发。
    /// </summary>
    private async Task CompactSessionAsync(int keepRecent)
    {
        var messages = _agent.State.Messages;
        var novelId = _agent.CurrentNovelId;
        if (string.IsNullOrEmpty(novelId) || messages.Count == 0)
        {
            ToastService.Warning("没有可压缩的会话");
            return;
        }

        if (messages.Count <= keepRecent)
        {
            ToastService.Info($"消息数 ({messages.Count}) 未超过保留阈值 ({keepRecent})，无需压缩");
            return;
        }

        try
        {
            var keep = Math.Min(keepRecent, messages.Count);
            _adapter.FireCompactStart();
            var result = await PrepareAndCompactAsync(messages, keep, "manual", willRetry: false);

            if (result is CompactResult.RejectedInProgress or CompactResult.RejectedWrongMode)
            {
                var (toast, compactEnd) = DescribeRejection(result);
                ToastService.Warning(toast);
                _adapter.FireCompactEnd(compactEnd);
                return;
            }

            if (result == CompactResult.TooSmall)
            {
                ToastService.Warning(CompactionEngine.TooSmallMessage);
                _adapter.FireCompactEnd(CompactionEngine.TooSmallMessage);
                return;
            }

            if (result == CompactResult.Failed)
            {
                ToastService.Warning("压缩失败：没有可安全压缩的完整轮次");
                _adapter.FireCompactEnd("上下文压缩失败");
                return;
            }

            var kept = Math.Max(0, _agent.State.Messages.Count - 1);
            ToastService.Success($"已压缩上下文：{messages.Count} → {kept} 条 (LLM 可见)");
            _adapter.FireCompactEnd($"上下文压缩完成：{messages.Count} → {kept} 条");
        }
        catch (Exception ex)
        {
            ToastService.Error($"压缩失败: {ex.Message}");
            _adapter.FireCompactEnd("上下文压缩失败");
        }
    }

    /// <summary>智能压缩：智能切点与摘要生成都在 Core，TUI 只负责触发和展示。</summary>
    private async Task CompactSessionSmartAsync(int keepRecent)
    {
        var messages = _agent.State.Messages;
        var novelId = _agent.CurrentNovelId;
        if (string.IsNullOrEmpty(novelId) || messages.Count == 0)
        {
            ToastService.Warning("没有可压缩的会话");
            return;
        }

        if (messages.Count <= keepRecent)
        {
            ToastService.Info($"消息数 ({messages.Count}) 未超过保留阈值 ({keepRecent})，无需压缩");
            return;
        }

        // 没有 Decimator 时退回普通模式（Core 也会按普通 cut 处理）。
        if (_agent.MessageScorer is null)
        {
            await CompactSessionAsync(keepRecent);
            return;
        }

        try
        {
            _adapter.FireCompactStart();
            var result = await PrepareAndCompactAsync(
                messages,
                keepRecent,
                "smart",
                willRetry: false,
                smart: true
            );

            if (result is CompactResult.RejectedInProgress or CompactResult.RejectedWrongMode)
            {
                var (toast, compactEnd) = DescribeRejection(result);
                ToastService.Warning(toast);
                _adapter.FireCompactEnd(compactEnd);
                return;
            }

            if (result == CompactResult.TooSmall)
            {
                ToastService.Warning(CompactionEngine.TooSmallMessage);
                _adapter.FireCompactEnd(CompactionEngine.TooSmallMessage);
                return;
            }

            if (result == CompactResult.Failed)
            {
                ToastService.Warning("智能压缩失败");
                _adapter.FireCompactEnd("智能压缩失败");
                return;
            }

            var kept = Math.Max(0, _agent.State.Messages.Count - 1);
            ToastService.Success(
                $"已智能压缩：{messages.Count} → {kept} 条（RGDecimator 评分，保留信息密度最高的轮次）"
            );
            _adapter.FireCompactEnd($"智能压缩完成：{messages.Count} → {kept} 条");
        }
        catch (Exception ex)
        {
            Services.TuiLogger.OperationFailed(_logger, ex, "compact session with message scorer");
            ToastService.Warning($"智能压缩失败 ({ex.Message})");
            _adapter.FireCompactEnd("智能压缩失败");
        }
    }

    /// <summary>自动压缩：由 token 阈值触发；切点/摘要/落盘统一在 Core。</summary>
    private async Task AutoCompactAsync(int keepRecentTokens)
    {
        try
        {
            var messages = _agent.State.Messages;
            var novelId = _agent.CurrentNovelId;
            if (string.IsNullOrEmpty(novelId) || messages.Count == 0)
                return;

            await PrepareAndCompactAsync(
                messages,
                keepRecent: 0,
                "threshold",
                willRetry: false,
                keepRecentTokens: keepRecentTokens
            );
        }
        catch (Exception ex)
        {
            Services.TuiLogger.OperationFailed(_logger, ex, "auto-compact session");
        }
    }

    /// <summary>
    /// 上下文溢出时压缩——reason=overflow, willRetry=true。
    /// compact 后验证 token 估算是否真的降低，无效时返回 false 防止死循环重试。
    /// </summary>
    private async Task<bool> CompactForOverflowAsync(int keepRecentTokens)
    {
        try
        {
            var messages = _agent.State.Messages;
            var novelId = _agent.CurrentNovelId;

            if (string.IsNullOrEmpty(novelId) || messages.Count == 0)
            {
                var monitor = _agent.StabilityMonitor;
                if (monitor is not null && monitor.IsFrozen)
                {
                    monitor.Unfreeze(
                        _tokenCalibrator.Project(CompactionEngine.EstimateContextTokens(messages))
                    );
                    return true;
                }
                return false;
            }

            var tokensBefore = CompactionEngine.EstimateContextTokens(messages);
            var result = await PrepareAndCompactAsync(
                messages,
                keepRecent: 0,
                "overflow",
                willRetry: true,
                keepRecentTokens: keepRecentTokens
            );

            if (result is CompactResult.RejectedInProgress or CompactResult.RejectedWrongMode)
            {
                var why =
                    result == CompactResult.RejectedInProgress
                        ? "another compaction is in progress"
                        : "context management mode is continuation";
                Services.TuiLogger.Diagnostic(_logger, "compact after context overflow", why);
                return result == CompactResult.RejectedInProgress;
            }
            if (result is CompactResult.TooSmall or CompactResult.Failed)
                return false;

            var tokensAfter = CompactionEngine.EstimateContextTokens(_agent.State.Messages);
            var requiredDrop = Math.Max(1_024L, tokensBefore / 10);

            // compact 后没有显著降低 → 视为无效压缩，不重试
            if (tokensAfter >= tokensBefore - requiredDrop)
            {
                Services.TuiLogger.Diagnostic(
                    _logger,
                    "compact after context overflow",
                    $"token count did not drop enough (before={tokensBefore}, after={tokensAfter})"
                );
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Services.TuiLogger.OperationFailed(_logger, ex, "compact after context overflow");
            return false;
        }
    }

    /// <summary>
    /// 手动恢复被压缩的消息原文（B2）。/decompress &lt;blockId&gt; 命令入口。
    /// 无 blockId 时列出所有可恢复的压缩块。结果写入 ~/.cache/mythology/decompress/。
    /// </summary>
    private async Task DecompressAsync(string blockId)
    {
        if (string.IsNullOrEmpty(_sessionId))
        {
            ToastService.Warning("没有活动会话");
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(blockId))
            {
                var blocks = await _sessionStore.ListCompactBlocksAsync(_sessionId);
                if (blocks.Count == 0)
                {
                    ToastService.Info("当前没有可恢复的压缩块");
                    return;
                }

                var list = string.Join(
                    "\n",
                    blocks.Select(b =>
                        $"{b.BlockId}（{b.MessageCount} 条）：{b.Summary[..Math.Min(60, b.Summary.Length)]}"
                    )
                );
                ToastService.Info($"可恢复的压缩块:\n{list}");
                return;
            }

            var messages = await _sessionStore.ReadCompactedMessagesAsync(_sessionId, blockId);
            if (messages is null || messages.Count == 0)
            {
                ToastService.Warning($"压缩块 {blockId} 无法恢复（不存在或创建于迁移前）");
                return;
            }

            // 写文件 + 预览（避免撑爆对话窗口）
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

            ToastService.Success($"压缩块 {blockId}（{messages.Count} 条消息）已恢复: {filePath}");
        }
        catch (Exception ex)
        {
            ToastService.Error($"恢复失败: {ex.Message}");
        }
    }

    /// <summary>宿主适配：压缩逻辑在 Core.CompactionEngine，本方法只推进 TUI 的会话计数与显示。</summary>
    private async Task<CompactResult> PrepareAndCompactAsync(
        IReadOnlyList<LlmChatMessage> messages,
        int keepRecent,
        string reason,
        bool willRetry,
        int keepRecentTokens = 0,
        bool smart = false
    )
    {
        var execution = await _compactor.CompactAsync(
            new CompactionRequest(
                _sessionId,
                _agent.CurrentNovelId ?? "",
                reason,
                _nextOrder,
                KeepRecent: keepRecent,
                KeepRecentTokens: keepRecentTokens,
                Smart: smart,
                WillRetry: willRetry,
                ParentEntryId: _lastParentEntryId
            )
        );

        if (execution.Status == CompactionStatus.Success)
        {
            _nextOrder += execution.OrderConsumed;
            _persistedMessageCount = _agent.State.Messages.Count;
            AssertOrderInvariant();
            if (execution.CompactEntryId is not null)
                _lastParentEntryId = execution.CompactEntryId;
            ResetStabilityMonitor(
                _tokenCalibrator.Project(
                    CompactionEngine.EstimateContextTokens(_agent.State.Messages)
                )
            );
            InvalidateContextMeasure();
            UpdateTokenDisplay();
        }

        return execution.Status switch
        {
            CompactionStatus.Success => CompactResult.Success,
            CompactionStatus.RejectedInProgress => CompactResult.RejectedInProgress,
            CompactionStatus.RejectedWrongMode => CompactResult.RejectedWrongMode,
            CompactionStatus.TooSmall => CompactResult.TooSmall,
            _ => CompactResult.Failed,
        };
    }
}
