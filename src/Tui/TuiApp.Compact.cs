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
    /// B4 防重入标志：压缩进行中时拒绝新压缩请求。
    /// 所有触发源（manual/smart/auto/overflow）都汇入 PrepareAndCompactAsync，
    /// 在该处单点拦截即可，避免并发触发导致双重压缩 / 状态错乱。
    /// </summary>
    private volatile bool _isCompacting;

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

            ToastService.Success($"已压缩上下文：{messages.Count} → {keep} 条 (LLM 可见)");
            _adapter.FireCompactEnd($"上下文压缩完成：{messages.Count} → {keep} 条");
        }
        catch (Exception ex)
        {
            ToastService.Error($"压缩失败: {ex.Message}");
            _adapter.FireCompactEnd("上下文压缩失败");
        }
    }

    /// <summary>
    /// 智能压缩 — 使用 RGDecimator 按信息密度评分每条消息，
    /// 保留得分最高的 N 条 + 最近一轮完整对话，而非简单保留最后 N 条。
    /// 适合长对话中保留关键角色设定、情节转折等重要信息。
    /// </summary>
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

        // 没有 Decimator 时退回普通模式
        var decimator = _agent.MessageScorer;
        if (decimator is null)
        {
            await CompactSessionAsync(keepRecent);
            return;
        }

        try
        {
            var scoredTurns = ScoreTurnsWithDecimator(messages, decimator);
            var cutIndex = FindSmartCutPoint(messages, scoredTurns, keepRecent);
            var keep = messages.Count - cutIndex;

            _adapter.FireCompactStart();
            var result = await PrepareAndCompactAsync(messages, keep, "smart", willRetry: false);

            if (result is CompactResult.RejectedInProgress or CompactResult.RejectedWrongMode)
            {
                var (toast, compactEnd) = DescribeRejection(result);
                ToastService.Warning(toast);
                _adapter.FireCompactEnd(compactEnd);
                return;
            }

            ToastService.Success(
                $"已智能压缩：{messages.Count} → {keep} 条（RGDecimator 评分，保留信息密度最高的轮次）"
            );
            _adapter.FireCompactEnd($"智能压缩完成：{messages.Count} → {keep} 条");
        }
        catch (Exception ex)
        {
            Services.TuiLogger.OperationFailed(_logger, ex, "compact session with message scorer");
            ToastService.Warning($"智能压缩失败 ({ex.Message})，退回普通模式");
            await CompactSessionAsync(keepRecent);
        }
    }

    /// <summary>
    /// 自动压缩：由 token 阈值触发，有 Decimator 时用智能模式，否则普通模式。
    /// 静默执行，不弹 Toast。
    /// </summary>
    private async Task AutoCompactAsync(int keepRecent)
    {
        try
        {
            var messages = _agent.State.Messages;
            var novelId = _agent.CurrentNovelId;
            if (string.IsNullOrEmpty(novelId) || messages.Count <= keepRecent)
                return;

            // 有 Decimator 时用智能模式，否则退回到普通模式
            if (_agent.MessageScorer is not null)
                await CompactSessionSmartAsync(keepRecent);
            else
                await PrepareAndCompactAsync(messages, keepRecent, "threshold", willRetry: false);
            // 注意：AutoCompact 是安全网，被拒绝（已有压缩进行中 / 接续模式）时静默返回，不重复触发。
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
    private async Task<bool> CompactForOverflowAsync(int keepRecent)
    {
        try
        {
            var messages = _agent.State.Messages;
            var novelId = _agent.CurrentNovelId;

            // 消息数不足 keepRecent 时无法压缩消息；但若监控器处于冻结态
            // （累计口径 bug 或旧数据导致 TotalTokens 虚高触发 BudgetExhausted），
            // 解冻并重设 token 基线即可恢复，无需压缩。若 API 真拒绝（非冻结态），
            // 返回 false 由重试上限兜底。
            if (string.IsNullOrEmpty(novelId) || messages.Count <= keepRecent)
            {
                var monitor = _agent.StabilityMonitor;
                if (monitor is not null && monitor.IsFrozen)
                {
                    monitor.Unfreeze(_tokenCalibrator.Project(EstimateContextTokens(messages)));
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

            // 两种拒绝语义分开：
            // - 已有压缩进行中（B4 防重入）→ 返回 true 让上层重试，不误报"压缩失败"。
            // - 模式互斥（接续模式）→ 什么都没做，返回 false 让上层停止重试并报错
            //   （重试永远无用；此路径仅在未来新增触发源漏判模式时才可达）。
            if (result is CompactResult.RejectedInProgress or CompactResult.RejectedWrongMode)
            {
                var why =
                    result == CompactResult.RejectedInProgress
                        ? "another compaction is in progress"
                        : "context management mode is continuation";
                Services.TuiLogger.Diagnostic(_logger, "compact after context overflow", why);
                return result == CompactResult.RejectedInProgress;
            }

            var tokensAfter = EstimateContextTokens(_agent.State.Messages);

            // compact 后 token 数未降低 → 视为无效压缩，不重试
            if (tokensAfter >= tokensBefore)
            {
                Services.TuiLogger.Diagnostic(
                    _logger,
                    "compact after context overflow",
                    $"token count did not decrease (before={tokensBefore}, after={tokensAfter})"
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

    /// <summary>
    /// 准备并执行压缩：计算切割点、生成摘要、写入 compact 条目、重置增量状态。
    /// 当 cutIndex 过小（保留消息过多）时，自动降低 keepRecent 以确保压缩有效。
    /// B4：入口处拦截并发——已有压缩进行中时返回 <see cref="CompactResult.RejectedInProgress"/>。
    /// </summary>
    private async Task<CompactResult> PrepareAndCompactAsync(
        IReadOnlyList<LlmChatMessage> messages,
        int keepRecent,
        string reason,
        bool willRetry
    )
    {
        // B4 防重入：所有触发源（manual/smart/auto/overflow）都汇入此处，单点拦截。
        if (_isCompacting)
        {
            Services.TuiLogger.Diagnostic(
                _logger,
                "prepare compaction",
                $"rejected {reason} request because compaction is in progress"
            );
            return CompactResult.RejectedInProgress;
        }
        // ★ 模式互斥（单点兜底）：接续模式不使用压缩。
        // 调用点（预检 / 溢出 / 手动）都已按模式分支，走到这里说明新增了漏判模式的触发源——
        // 记警告并拒绝，而不是悄悄产生 compact 条目（压缩与换页混用会互相干扰）。
        if (_contextManagement is { Mode: ContextMode.Continuation })
        {
            Services.TuiLogger.Diagnostic(
                _logger,
                "prepare compaction",
                $"rejected {reason} request because context management mode is continuation"
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
            Services.TuiLogger.OperationFailed(_logger, ex, "prepare compaction");
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
        var novelId = _agent.CurrentNovelId ?? "";

        // 计算切割点——保证轮次完整性
        var cutIndex = FindCutPoint(messages, keepRecent);

        // 防御：cutIndex 过小意味着压缩几乎无效（保留消息过多）
        // 当被压缩的消息不足 3 条时，强制降低 keepRecent 重新计算
        if (cutIndex < 3 && messages.Count > keepRecent)
        {
            var reducedKeep = Math.Max(3, keepRecent / 2);
            cutIndex = FindCutPoint(messages, reducedKeep);
            Services.TuiLogger.Diagnostic(
                _logger,
                "prepare compaction",
                $"reduced keepRecent from {keepRecent} to {reducedKeep}; cutIndex={cutIndex}"
            );
        }

        // 获取 previousSummary（增量摘要）—— 使用轻量方法避免全消息构建
        var previousSummary = await _sessionStore.GetLatestCompactSummaryAsync(_sessionId);

        // 生成摘要（AI 优先，规则 fallback），并内嵌被压缩区尾部的精确执行状态
        var summary = await GenerateCompactSummaryAsync(messages, cutIndex, previousSummary);
        var recentBlock = BuildRecentExecutionBlock(messages, cutIndex);
        if (recentBlock.Length > 0)
            summary = summary + "\n\n" + recentBlock;

        // 找到保留起始点的 entryId（对齐到完整轮次边界）。
        // ★ 必须按当前页切片：换页后 messages（页内列表）比 dbMsgs（全量）短，
        // Skip/Take(cutIndex) 会错位到换页前的旧消息 → firstKeptEntryId 落在旧页、
        // compact 截断点跑到 window 之前 → 重载会话时页内消息全部回归。
        var pageStart = _agent.ContextWindowState.StartOrder;
        var dbMsgs = (await _sessionStore.GetActiveMessages(_sessionId))
            .Where(m => pageStart == 0 || m.Order >= pageStart)
            .ToList();

        // ★ 防御：内存消息与 DB 渲染消息数量应一致（compact 条目两种渲染模式各占一位）。
        // 不一致意味着渲染差异或数据异常，索引对齐（Skip/Take）会错位，记录以便排查。
        if (dbMsgs.Count != messages.Count)
        {
            Services.TuiLogger.Diagnostic(
                _logger,
                "prepare compaction",
                $"database message count={dbMsgs.Count}; memory message count={messages.Count}"
            );
        }

        // ★ firstKeptEntryId 必须是真实消息：跳过 compact 条目（Active 模式渲染为 Unknown+EventType）
        var keptEntry = dbMsgs
            .Skip(cutIndex)
            .FirstOrDefault(m => m.EventType != SessionEventType.Compact);
        var firstKeptEntryId = keptEntry?.EntryId ?? "";
        var messagesBefore = messages.Count;

        // ★ 收集被压缩消息的 entryId 供 decompress 恢复原文。
        // 跳过 compact 条目（EventType==Compact，非消息，decompress 无法恢复），
        // 只收集真实消息 ID，避免嵌套压缩时把旧 compact 条目的 id 混入。
        var compressedIds = dbMsgs
            .Take(cutIndex)
            .Where(m => m.EventType != SessionEventType.Compact)
            .Select(m => m.EntryId ?? $"msg_{m.Order}")
            .Where(id => !string.IsNullOrEmpty(id))
            .ToList();

        var (compactEntryId, blockId) = await _sessionStore.CompactSession(
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

        // 直接更新 Agent 内存消息，避免 RestoreContextFromDbAsync 重新读取 JSONL
        var snapshot = BuildNovelSnapshot();
        var summaryMsg = new LlmChatMessage(
            ChatMessageRoles.User,
            $"[上下文摘要 {blockId}]\n{summary}{(snapshot.Length > 0 ? "\n\n" + snapshot : "")}"
        );
        var keptMessages = _agent.State.Messages.Skip(cutIndex).ToList();
        var newMessages = new List<LlmChatMessage>(keptMessages.Count + 1) { summaryMsg };
        newMessages.AddRange(keptMessages);
        _agent.ReplaceMessages(newMessages);

        // ★ 最小版压缩状态：记录本次压缩的 block（支持后续 decompress）
        // # ponytail: 无字典索引，无引擎抽象，直接追加到列表（O(1) 追加）
        // blockId 来自 FileSessionStore（b{分片seq}），与摘要消息 [上下文摘要 bN] 一致
        _agent.CompressionBlocks.Add((blockId, compressedIds, summary));

        // ★ compact 后重置稳定性监控器：真正归零 + 用估算的当前上下文重设 token 基线。
        // 只调 Unfreeze 是无效的（未冻结时直接 return，而 pre-flight 溢出通常未冻结）——
        // 见 ResetStabilityMonitor 的说明。
        ResetStabilityMonitor(_tokenCalibrator.Project(EstimateContextTokens(newMessages)));

        // ★ B6：重置 prompt token 基线——compact 后 prompt 大幅缩小，旧 _lastPromptTokens
        // 会让下一轮 ComputeTurnIncrement 算出大幅负增量，压低 TotalTokens 导致 pre-flight 漏检。
        _agent.ResetPromptBaseline();

        // ★ compact 后上下文已重算（摘要 + 保留后缀）：旧 API 实测值作废，重新计数显示
        InvalidateContextMeasure();
        UpdateTokenDisplay();

        // compact 后重置增量追踪状态
        _nextOrder++; // compact 条目占一个 order 位
        _persistedMessageCount = _agent.State.Messages.Count;
        AssertOrderInvariant();
        _lastParentEntryId = compactEntryId;
        return CompactResult.Success;
    }

    /// <summary>
    /// 计算压缩切割点——保证轮次完整性。
    /// 切割点必须指向一个完整轮次的起点（user 消息），
    /// 避免将 assistant + tool calls 拆到 compact 两侧导致 LLM 上下文断裂。
    /// </summary>
    private static int FindCutPoint(IReadOnlyList<LlmChatMessage> messages, int keepRecent)
    {
        var candidate = Math.Max(0, messages.Count - keepRecent);

        // 向前扫描，找到最近的一个完整轮次起点（user 消息）
        for (var i = candidate; i >= 0; i--)
        {
            if (messages[i].Role == ChatMessageRoles.User)
                return i;
        }

        // 找不到 user 消息（极端情况），回退到 candidate
        return candidate;
    }

    /// <summary>
    /// 将消息按完整轮次分组（user→assistant→tool calls），
    /// 用 IMessageScorer 评分每个轮次的信息密度。
    /// 返回每轮信息价值得分的列表（按消息索引→得分）。
    /// </summary>
    private static List<TurnScore> ScoreTurnsWithDecimator(
        IReadOnlyList<LlmChatMessage> messages,
        IMessageScorer decimator
    )
    {
        // 将消息合并为完整轮次（user 消息作为轮次起点）
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

        // 评分每个轮次（用前一 turn 作为上下文，计算新颖度）
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
            scored[^1] = scored[^1] with { Score = 2.0 }; // 强制最高分，确保最近轮次一定保留

        return scored;
    }

    /// <summary>
    /// 基于 RGDecimator 评分的智能切割点计算。
    /// 按得分降序排列所有轮次，取 top N 个，再加上最近一轮（保证连续性），
    /// 返回最早的保留轮次的起始索引。
    /// </summary>
    private static int FindSmartCutPoint(
        IReadOnlyList<LlmChatMessage> messages,
        List<TurnScore> scoredTurns,
        int keepRecent
    )
    {
        if (scoredTurns.Count == 0 || messages.Count <= keepRecent)
            return 0;

        // 按得分排序，取 top N 个轮次
        var topN = scoredTurns
            .OrderByDescending(t => t.Score)
            .Take(Math.Max(1, keepRecent / 2)) // 保留约一半轮次而不是简单最后N条
            .Select(t => t.StartIndex)
            .ToHashSet();

        // 总是保留最后一轮
        if (scoredTurns.Count > 0)
            topN.Add(scoredTurns[^1].StartIndex);

        // 找到最早的保留轮次的起始索引
        var cutIndex = topN.Min();

        // 对齐到 user 消息边界
        for (var i = cutIndex; i >= 0; i--)
        {
            if (messages[i].Role == ChatMessageRoles.User)
                return i;
        }

        return cutIndex;
    }

    /// <summary>轮次评分结果。</summary>
    private readonly record struct TurnScore(int StartIndex, int EndIndex, double Score);

    /// <summary>
    /// 估算上下文的 token 数（B6 统一口径）。
    /// 委托到 ConversationLoop.EstimateMessagesTokens——锚点优先（最后带 Tokens 的
    /// assistant 消息作为精确锚点）+ CJK 感知（CJK≈1 token/字，ASCII≈4 字符/token）。
    /// 不再使用旧的 chars×2/3 粗估算，消除与监控器/锚点估算的双轨漂移。
    /// </summary>
    private static long EstimateContextTokens(IReadOnlyList<LlmChatMessage> messages) =>
        ConversationLoop.EstimateMessagesTokens(messages);

    /// <summary>
    /// 估算 compact 后的上下文 token 数 = 摘要 token + 保留后缀 token。
    /// P2 修复：统一用 CJK 感知估算（中文 ≈ 1 token/字），与 EstimateMessagesTokens 口径一致。
    /// 原先 summary.Length/2 与 suffixTokens/2 对中文低估一半，导致 compact 后 Unfreeze 基线偏低、
    /// 后续 pre-flight 漏检。
    /// </summary>
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

    /// <summary>
    /// 生成压缩摘要。优先使用 AI 摘要（调用 LLM），失败时 fallback 到规则摘要。
    /// </summary>
    private async Task<string> GenerateCompactSummaryAsync(
        IReadOnlyList<LlmChatMessage> messages,
        int cutIndex,
        string? previousSummary
    )
    {
        // P4：被压缩消息较少时规则摘要足够，跳过 AI 摘要（省一次 LLM 调用 + 阻塞延迟）。
        // 仅消息量达阈值时才用 AI 摘要提升长对话摘要质量。
        const int aiSummaryMinCompressed = 20;
        if (cutIndex >= aiSummaryMinCompressed)
        {
            // 尝试 AI 摘要
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
                Services.TuiLogger.OperationFailed(_logger, ex, "generate AI compact summary");
            }
        }

        // 规则摘要 fallback
        return Truncate(
            BuildCompactSummary(messages, messages.Count - cutIndex, previousSummary),
            800
        );
    }

    /// <summary>
    /// 尝试调用 LLM 生成 AI 摘要。返回 null 表示失败。
    /// </summary>
    private async Task<string?> TryGenerateAiSummaryAsync(
        IReadOnlyList<LlmChatMessage> messages,
        int cutIndex,
        string? previousSummary
    )
    {
        var api = _agent.TornadoApi;
        var modelName = _agent.State.ModelName;
        if (api is null || string.IsNullOrEmpty(modelName))
            return null;

        // 序列化被压缩的消息为文本
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

        // 构建 summarizer prompt
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

        // 调用 LLM 生成摘要（流式收集）
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

    /// <summary>
    /// 构建规则 compact 摘要（AI 摘要的 fallback）。
    /// 从即将被压缩的消息中提取关键信息：用户请求、助手回复、文件操作、之前的摘要。
    /// </summary>
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

        // 之前的摘要（增量摘要，仅保留最近一份并截断，防链式膨胀）
        if (previousSummary is not null)
        {
            sb.AppendLine("## 之前的摘要");
            sb.AppendLine(CompactPreviousSummary(previousSummary));
            sb.AppendLine();
        }

        // 从被压缩的消息中提取关键信息
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

        // 文件操作提取
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

    /// <summary>
    /// 提取被压缩区尾部最近一个完整轮次（user→assistant→tool）的原文，
    /// 单行截断后拼入摘要，保留压缩前的精确执行状态
    /// （对应 Tura recent_pre_compact_tool_context_messages：压缩后模型立即知道上一步输出）。
    /// </summary>
    private static string BuildRecentExecutionBlock(
        IReadOnlyList<LlmChatMessage> messages,
        int cutIndex
    )
    {
        if (cutIndex <= 0)
            return "";

        // 被压缩区尾部：从 cutIndex-1 向前找最近一个 user 消息作为轮次起点
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

    /// <summary>
    /// 压缩继承摘要：只保留最近一份并截断。
    /// P3：优先提取「进展 + 文件操作 + 下一步」三段（创作场景下目标几乎不变，
    /// 真正需要跨压缩继承的状态是进展与已落盘的文件操作），
    /// 防止多次压缩后摘要链膨胀与「摘要的摘要」信息衰减（对应 Tura 继承摘要治理）。
    /// </summary>
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

    /// <summary>提取 Markdown 段：从标题行到下一个同级标题之前。</summary>
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

    /// <summary>
    /// 轻量作品状态快照：当前小说 + 压缩块信息，压缩后随摘要注入，
    /// 让模型立即知道作品上下文位置（对应 Tura workspace_snapshot）。
    /// </summary>
    private string BuildNovelSnapshot()
    {
        var novelId = _agent.CurrentNovelId;
        if (string.IsNullOrEmpty(novelId))
            return "";
        var sb = new StringBuilder();
        sb.AppendLine("## 作品状态快照");
        sb.AppendLine($"- 当前小说: {novelId}");
        if (_agent.CompressionBlocks.Count > 0)
            sb.AppendLine(
                $"- 历史压缩块: {_agent.CompressionBlocks.Count} 个（可用 decompress 工具恢复原文）"
            );
        return sb.ToString();
    }
}
