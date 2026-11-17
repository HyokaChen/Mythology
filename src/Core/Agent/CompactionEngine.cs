using System.Text;
using Core.Data;
using Core.Novels;
using Core.Shared;
using Core.Stability;
using LlmTornado.Chat;
using LlmTornado.Code;
using Microsoft.Extensions.Logging;
using ChatMessage = LlmTornado.Chat.ChatMessage;

namespace Core.Agent;

/// <summary>压缩结果。TooSmall 表示压缩范围/收益不足，未写 JSONL。</summary>
public enum CompactionStatus
{
    Success,
    RejectedInProgress,
    RejectedWrongMode,
    TooSmall,
    Failed,
}

/// <summary>一次压缩请求。Order/ParentEntryId 由宿主会话状态提供，其余是压缩策略。</summary>
public sealed record CompactionRequest(
    string SessionId,
    string NovelId,
    string Reason,
    int Order,
    int KeepRecent = 0,
    int KeepRecentTokens = 0,
    bool Smart = false,
    bool WillRetry = false,
    string? ParentEntryId = null,
    string? PendingAuthorMessage = null
);

/// <summary>压缩执行结果；OrderConsumed 供宿主推进 _nextOrder。</summary>
public sealed record CompactionExecution(
    CompactionStatus Status,
    int MessagesBefore,
    int MessagesAfter,
    int OrderConsumed,
    string? CompactEntryId,
    string? BlockId,
    string? Error,
    int EstimatedTokensAfter,
    IReadOnlyList<string> CompressedMessageIds,
    string? Summary = null
);

/// <summary>
/// TUI / Desktop 共用的上下文压缩实现：切点、有效压缩门槛、5W1H 摘要、compact 落盘、
/// Agent 内存替换与稳定性基线重置都在 Core。宿主只负责触发时机、计数器和 UI 呈现。
/// </summary>
public sealed class CompactionEngine(
    Agent agent,
    FileSessionStore store,
    INovelStore novelStore,
    ILogger log,
    TodoStore? todoStore = null
)
{
    public const string TooSmallMessage = "压缩范围太小（未达到有效压缩阈值），未写入会话";

    // 124K 上下文的检查点摘要实测会超过 45 秒；3 分钟给足流式生成时间，
    // 超时仍然回退到确定性检查点，不会让压缩失败。
    private static readonly TimeSpan AiSummaryTimeout = TimeSpan.FromMinutes(3);

    private const string ScopeHeading = CompactionTaskScope.Heading;
    private const string ProgressHeading = CompactionTaskProgress.Heading;
    private const string TodosHeading = "## Todos / 任务清单";
    private const string WhereHeading = "## Where / 写到哪里";
    private const string WhoHeading = "## Who / 谁出场";
    private const string WhatHeading = "## What / 发生了什么";
    private const string WhyHeading = "## Why / 为什么";
    private const string WhenHeading = "## When / 时间";
    private const string HowHeading = "## How / 怎么写";
    private const string NextHeading = "## Next / 下一步";

    private static readonly string[] CheckpointHeadings =
    [
        ScopeHeading,
        ProgressHeading,
        TodosHeading,
        WhereHeading,
        WhoHeading,
        WhatHeading,
        WhyHeading,
        WhenHeading,
        HowHeading,
        NextHeading,
    ];

    private sealed record NovelStateBuildResult(
        string Snapshot,
        CompactionTaskScope? Scope,
        CompactionTaskProgress? Progress,
        IReadOnlyList<string> AuthorMessages
    );

    private int _compactGate;

    /// <summary>是否有压缩进行中（宿主 UI spinner/防重入单一事实源）。</summary>
    public bool IsCompacting => Volatile.Read(ref _compactGate) == 1;

    public async Task<CompactionExecution> CompactAsync(
        CompactionRequest request,
        CancellationToken ct = default
    )
    {
        var messages = agent.State.Messages;
        // 原子闸门：自动压缩与手动 /compact 可能从不同线程同时到达。
        // volatile bool 的“先读后写”存在竞态，两个请求都可能通过检查。
        if (Interlocked.CompareExchange(ref _compactGate, 1, 0) != 0)
        {
            log.LogDebug(
                "prepare compaction: rejected {Reason} request because compaction is in progress",
                request.Reason
            );
            return Failed(CompactionStatus.RejectedInProgress, messages.Count, "压缩进行中");
        }

        try
        {
            if (agent.ContextManagement is { Mode: ContextMode.Continuation })
            {
                log.LogDebug(
                    "prepare compaction: rejected {Reason} request because context management mode is continuation",
                    request.Reason
                );
                return Failed(CompactionStatus.RejectedWrongMode, messages.Count, "当前为接续模式");
            }

            // 拿到闸门后再检查取消：调用方在等待期间按下停止时，不应白跑一整轮压缩。
            ct.ThrowIfCancellationRequested();
            return await CompactCoreAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "prepare compaction failed");
            return Failed(CompactionStatus.Failed, messages.Count, ex.Message);
        }
        finally
        {
            Volatile.Write(ref _compactGate, 0);
        }
    }

    private async Task<CompactionExecution> CompactCoreAsync(
        CompactionRequest request,
        CancellationToken ct
    )
    {
        var messages = agent.State.Messages;
        if (string.IsNullOrEmpty(request.SessionId) || string.IsNullOrEmpty(request.NovelId))
            return Failed(CompactionStatus.Failed, messages.Count, "没有可压缩的会话");
        if (messages.Count == 0)
            return Failed(CompactionStatus.TooSmall, 0, TooSmallMessage);

        var cutIndex =
            request.Smart && agent.MessageScorer is { } scorer
                ? FindSmartCutPoint(
                    messages,
                    ScoreTurnsWithDecimator(messages, scorer),
                    request.KeepRecent
                )
            : request.KeepRecentTokens > 0
                ? FindTokenCutPoint(messages, request.KeepRecentTokens, minKeepMessages: 4)
            : FindCutPoint(messages, request.KeepRecent);

        if (
            cutIndex <= 0
            || cutIndex >= messages.Count
            || !IsSafeCompactBoundary(messages[cutIndex])
        )
        {
            log.LogWarning(
                "prepare compaction: no safe compact boundary can be compressed (keepRecent={Keep}, keepRecentTokens={Tokens}, cutIndex={Cut})",
                request.KeepRecent,
                request.KeepRecentTokens,
                cutIndex
            );
            return Failed(CompactionStatus.TooSmall, messages.Count, TooSmallMessage);
        }

        var tokensBefore = EstimateContextTokens(messages);
        var compressedTokens = EstimateContextTokens(messages.Take(cutIndex).ToList());
        var minCompressed = Math.Max(2_000L, tokensBefore / 10);
        if (compressedTokens < minCompressed)
        {
            log.LogDebug(
                "prepare compaction: compressed range too small (compressed={Compressed}, min={Min}, before={Before}, cut={Cut})",
                compressedTokens,
                minCompressed,
                tokensBefore,
                cutIndex
            );
            return Failed(CompactionStatus.TooSmall, messages.Count, TooSmallMessage);
        }

        var previousSummary = await store.GetLatestCompactSummaryAsync(request.SessionId, ct);
        NovelStateBuildResult novelState;
        try
        {
            novelState = await BuildNovelStateAsync(
                request.NovelId,
                messages.Take(cutIndex).ToList(),
                previousSummary,
                request.PendingAuthorMessage,
                ct
            );
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 设定/结构读取失败不能阻断上下文压缩；退化为对话 5W1H。
            log.LogWarning(ex, "build novel state snapshot failed; falling back to conversation");
            novelState = new NovelStateBuildResult("", null, null, []);
        }

        var summary = await GenerateCompactSummaryAsync(
            messages,
            cutIndex,
            previousSummary,
            novelState.Snapshot,
            novelState.Scope,
            novelState.Progress,
            novelState.AuthorMessages,
            ct
        );
        var estimatedTokensAfter = EstimateCompactTokensAfter(summary, cutIndex, messages);
        var requiredDrop = Math.Max(1_024L, tokensBefore / 10);
        if (estimatedTokensAfter >= tokensBefore - requiredDrop)
        {
            log.LogDebug(
                "prepare compaction: estimated savings too small (before={Before}, after={After}, requiredDrop={RequiredDrop})",
                tokensBefore,
                estimatedTokensAfter,
                requiredDrop
            );
            return Failed(CompactionStatus.TooSmall, messages.Count, TooSmallMessage);
        }

        var pageStart = agent.ContextWindowState.StartOrder;
        var dbMsgs = (await store.GetActiveMessages(request.SessionId, ct))
            .Where(m => pageStart == 0 || m.Order >= pageStart)
            .ToList();
        if (dbMsgs.Count != messages.Count)
        {
            log.LogDebug(
                "prepare compaction: database message count={Db} memory message count={Mem}",
                dbMsgs.Count,
                messages.Count
            );
        }

        var keptEntry = dbMsgs
            .Skip(cutIndex)
            .FirstOrDefault(m => m.EventType != SessionEventType.Compact);
        var firstKeptEntryId = keptEntry?.EntryId ?? "";
        if (string.IsNullOrEmpty(firstKeptEntryId))
        {
            log.LogWarning("prepare compaction: no first-kept message can be resolved");
            return Failed(CompactionStatus.TooSmall, messages.Count, TooSmallMessage);
        }

        var compressedIds = dbMsgs
            .Take(cutIndex)
            .Where(m => m.EventType != SessionEventType.Compact)
            .Select(m => m.EntryId ?? $"msg_{m.Order}")
            .Where(id => !string.IsNullOrEmpty(id))
            .ToList();
        var messagesBefore = messages.Count;

        var (compactEntryId, blockId) = await store.CompactSession(
            request.SessionId,
            request.NovelId,
            summary,
            firstKeptEntryId,
            messagesBefore,
            parentEntryId: request.ParentEntryId,
            order: request.Order,
            reason: request.Reason,
            willRetry: request.WillRetry,
            estimatedTokensAfter: estimatedTokensAfter,
            compressedMessageIds: compressedIds,
            ct: ct
        );

        var summaryMsg = new ChatMessage(
            ChatMessageRoles.User,
            $"[上下文摘要 {blockId}]\n{summary}"
        );
        var keptMessages = agent.State.Messages.Skip(cutIndex).ToList();
        // 保留消息上的 Tokens 是压缩前完整上下文的锚点。压缩后它已不再代表新上下文；
        // 不清掉会让 token 估算仍返回压缩前大小，溢出恢复误判为“压缩未降低 token”。
        foreach (var msg in keptMessages)
            msg.Tokens = null;
        var newMessages = new List<ChatMessage>(keptMessages.Count + 1) { summaryMsg };
        newMessages.AddRange(keptMessages);
        agent.ReplaceMessages(newMessages);
        agent.CompressionBlocks.Add((blockId, compressedIds, summary));
        agent.ResetPromptBaseline();

        return new CompactionExecution(
            CompactionStatus.Success,
            messagesBefore,
            newMessages.Count,
            OrderConsumed: 1,
            compactEntryId,
            blockId,
            Error: null,
            estimatedTokensAfter,
            compressedIds,
            Summary: summary
        );
    }

    private CompactionExecution Failed(CompactionStatus status, int before, string? error) =>
        new(
            status,
            before,
            agent.State.Messages.Count,
            OrderConsumed: 0,
            CompactEntryId: null,
            BlockId: null,
            Error: error,
            EstimatedTokensAfter: 0,
            CompressedMessageIds: [],
            Summary: null
        );

    private static int FindCutPoint(IReadOnlyList<ChatMessage> messages, int keepRecent)
    {
        if (messages.Count == 0)
            return 0;
        var candidate = Math.Clamp(messages.Count - keepRecent, 0, messages.Count - 1);
        for (var i = candidate; i >= 0; i--)
        {
            if (messages[i].Role == ChatMessageRoles.User)
                return i;
        }
        return candidate;
    }

    /// <summary>
    /// 从尾部按 token 预算反扫切割点：至少保留预算与 minKeepMessages 条，再向尾部找最近的
    /// user/assistant 边界。绝不切在 tool 消息上，保证工具结果跟随其 assistant tool call。
    /// 注意不能向前回退到更早的 user：单轮长工具循环没有就近 user 边界，回退会直接跳到
    /// 整轮的第一个 user，把整轮工具结果都留在尾部，压缩收益不足（TooSmall）。
    /// </summary>
    private static int FindTokenCutPoint(
        IReadOnlyList<ChatMessage> messages,
        int keepRecentTokens,
        int minKeepMessages
    )
    {
        if (messages.Count == 0)
            return 0;

        long tailTokens = 0;
        var keptMessages = 0;
        var cutIndex = messages.Count;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            tailTokens += ConversationLoop.EstimateSingleMessageTokens(messages[i]);
            keptMessages++;
            cutIndex = i;
            if (tailTokens >= keepRecentTokens && keptMessages >= minKeepMessages)
                break;
        }

        if (cutIndex >= messages.Count)
            return 0;
        if (IsSafeCompactBoundary(messages[cutIndex]))
            return cutIndex;

        // 切点落在 tool 消息上：优先向尾部找最近的 user/assistant，保留的尾部最小；
        // 若尾部全是 tool（例如最后一条就是 tool result），回退到最近的 assistant，
        // 保证 tool result 不脱离它的 tool call。绝不回退到 user，否则会吞掉整轮工具循环。
        for (var i = cutIndex + 1; i < messages.Count; i++)
            if (IsSafeCompactBoundary(messages[i]))
                return i;

        for (var i = cutIndex - 1; i > 0; i--)
            if (messages[i].Role == ChatMessageRoles.Assistant)
                return i;

        return 0;
    }

    /// <summary>可安全作为压缩切点的消息角色：user 或 assistant；tool 必须跟随其 assistant call。</summary>
    private static bool IsSafeCompactBoundary(ChatMessage message) =>
        message.Role == ChatMessageRoles.User || message.Role == ChatMessageRoles.Assistant;

    private readonly record struct TurnScore(int StartIndex, int EndIndex, double Score);

    private static List<TurnScore> ScoreTurnsWithDecimator(
        IReadOnlyList<ChatMessage> messages,
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

        if (scored.Count > 1)
            scored[^1] = scored[^1] with { Score = 2.0 };

        return scored;
    }

    /// <summary>
    /// 基于评分的智能切点：保留高分轮次 + 最近一轮，并强制连续后缀不超过一半消息，
    /// 防止早期高分轮次把整段对话原样留下（202→202 假压缩）。
    /// </summary>
    private static int FindSmartCutPoint(
        IReadOnlyList<ChatMessage> messages,
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
        topN.Add(scoredTurns[^1].StartIndex);

        var cutIndex = topN.Min();
        for (var i = cutIndex; i >= 0; i--)
        {
            if (messages[i].Role == ChatMessageRoles.User)
            {
                cutIndex = i;
                break;
            }
        }

        var maxKeep = Math.Max(keepRecent, messages.Count / 2);
        var capIndex = messages.Count - maxKeep;
        for (var i = capIndex; i < messages.Count; i++)
        {
            if (messages[i].Role == ChatMessageRoles.User)
            {
                cutIndex = Math.Max(cutIndex, i);
                break;
            }
        }

        return cutIndex;
    }

    /// <summary>共享 token 估算口径：锚点优先 + CJK 感知。</summary>
    public static long EstimateContextTokens(IReadOnlyList<ChatMessage> messages) =>
        ConversationLoop.EstimateMessagesTokens(messages);

    private static int EstimateCompactTokensAfter(
        string summary,
        int cutIndex,
        IReadOnlyList<ChatMessage> messages
    )
    {
        var tokens = ConversationLoop.EstimateTextTokens(summary);
        for (var i = cutIndex; i < messages.Count; i++)
            tokens += ConversationLoop.EstimateSingleMessageTokens(messages[i]);
        return tokens > int.MaxValue ? int.MaxValue : (int)tokens;
    }

    private async Task<string> GenerateCompactSummaryAsync(
        IReadOnlyList<ChatMessage> messages,
        int cutIndex,
        string? previousSummary,
        string novelSnapshot,
        CompactionTaskScope? scope,
        CompactionTaskProgress? progress,
        IReadOnlyList<string> authorMessages,
        CancellationToken ct
    )
    {
        const int aiSummaryMinCompressedTokens = 2_000;
        var compressedTokens = EstimateContextTokens(messages.Take(cutIndex).ToList());
        var todoItems = todoStore?.List();
        var baseCheckpoint = string.IsNullOrWhiteSpace(novelSnapshot)
            ? BuildConversationCheckpoint(messages, cutIndex)
            : novelSnapshot;
        // AI 提示词中的 Scope/Progress/Todos 另有独立小节；这里保留小说/对话快照，
        // 且暂时不把 Todos 合入快照，避免“当前小说状态”里重复同一份任务清单。
        var promptSnapshot = baseCheckpoint;
        if (scope is not null)
            baseCheckpoint = EnsureScopeSection(baseCheckpoint, scope);
        if (progress is not null)
            baseCheckpoint = EnsureProgressSection(baseCheckpoint, progress);
        if (todoItems is not null)
            baseCheckpoint = EnsureTodoSection(baseCheckpoint, todoItems);

        if (compressedTokens >= aiSummaryMinCompressedTokens)
        {
            try
            {
                var aiSummary = await TryGenerateAiSummaryAsync(
                    messages,
                    cutIndex,
                    previousSummary,
                    promptSnapshot,
                    scope,
                    progress,
                    authorMessages,
                    todoItems,
                    ct
                );
                if (aiSummary is not null)
                {
                    var normalized = NormalizeCheckpoint(aiSummary, baseCheckpoint);
                    var merged = MergeCheckpoint(normalized, previousSummary);
                    if (todoItems is not null)
                        merged = EnsureTodoSection(merged, todoItems);
                    merged = Truncate(merged, 8_000);
                    var scoped = EnforceTaskScope(
                        PrependAuthorScope(merged, messages, cutIndex, authorMessages, scope),
                        scope,
                        previousSummary,
                        progress,
                        baseCheckpoint
                    );
                    return ApplyTodoNext(scoped, todoItems, scope);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "generate AI compact summary failed");
            }
        }

        var fallback = BuildCompactSummaryWithScope(
            messages,
            cutIndex,
            previousSummary,
            baseCheckpoint,
            scope,
            progress,
            authorMessages
        );
        var withTodos = todoItems is null ? fallback : EnsureTodoSection(fallback, todoItems);
        return ApplyTodoNext(withTodos, todoItems, scope);
    }

    private async Task<string?> TryGenerateAiSummaryAsync(
        IReadOnlyList<ChatMessage> messages,
        int cutIndex,
        string? previousSummary,
        string novelSnapshot,
        CompactionTaskScope? scope,
        CompactionTaskProgress? progress,
        IReadOnlyList<string> authorMessages,
        IReadOnlyList<TodoItem>? todoItems,
        CancellationToken ct
    )
    {
        var api = agent.TornadoApi;
        var modelName = agent.State.ModelName;
        if (api is null || string.IsNullOrEmpty(modelName))
            return null;

        var dialogue = BuildCompressedDialogue(messages, cutIndex);
        if (dialogue.Length == 0)
            return null;
        var authorInstructions = CollectAuthorInstructions(messages, cutIndex);

        var prompt = new StringBuilder();
        prompt.AppendLine(
            "请维护一份小说写作的 5W1H 检查点。它是压缩历史后唯一保留的上下文，必须让下一轮写作能直接继续。"
        );
        prompt.AppendLine("输出必须且只能使用以下 10 个标题（顺序固定，不得嵌套其它标题）：");
        prompt.AppendLine(string.Join('\n', CheckpointHeadings));
        prompt.AppendLine();
        prompt.AppendLine("要求：");
        prompt.AppendLine(
            "- Scope、Progress 与 Todos 是代码维护的硬状态：必须原样保留字段和值，不得改写、合并或删除。"
        );
        prompt.AppendLine(
            "- Next 必须读取“当前任务清单”：优先继续 in_progress 项；没有 in_progress 时取第一个未被未完成任务阻塞的 pending；"
                + "不得跳过未完成任务；完成后用 todo_write 更新对应 #id。"
        );
        prompt.AppendLine(
            "- Progress 只记录工具事实：已实际编辑、已读/诊断、当前、下一章、范围外活动；不要把“读过”写成“已完成”。"
        );
        prompt.AppendLine(
            "- Where 写到哪里：当前卷/章/场景、下一章；Who 谁出场；What 发生了什么；"
        );
        prompt.AppendLine("- Why 为什么写这些；When 故事内时间；How 怎么写（风格、节奏、约束）；");
        prompt.AppendLine(
            "- Next 必须具体到 Scope 内的某章，不能只写“继续”或“下一个待优化章”；禁止追加上一份摘要的 Next。"
        );
        prompt.AppendLine(
            "- 优先保留小说状态与任务进度：写到哪里、谁出场、发生了什么、为什么、何时、怎么写。"
        );
        prompt.AppendLine("- 不得原文复述已有检查点，不得生成“压缩概览/之前的摘要/文件操作”标题，");
        prompt.AppendLine("- 不得用 read/write/edit/list/search 等工具调用结果代替剧情状态。");
        prompt.AppendLine("- 总长度不超过 8,000 字符。");
        prompt.AppendLine(
            "- 只有“作者指令（逐字）”是用户目标；[system-note]、[tool]、[assistant] 是系统/工具事件，不得写成“用户明确目标”或“用户要求”。"
        );
        prompt.AppendLine(
            "- 作者指令中的范围（如“第二卷/第21-40章”）是硬约束；不得把范围扩大到其它卷或其它章节。"
        );
        prompt.AppendLine(
            "- 当 Scope 是修订/优化时，Next 必须是范围内尚未编辑/待复核的章，不能写全书写作前沿的下一章，也不能写范围外的新章。"
        );
        prompt.AppendLine(
            "- 当前小说状态里的“当前章/下一章”是全局写作前沿，不是本任务进度；与 Scope 冲突时，Scope 优先。"
        );
        prompt.AppendLine(
            "- 作者没有发出“取消/切换任务”的新指令时，原有 Scope 继续有效；不得把“没有新指令”解释为旧任务结束。"
        );
        prompt.AppendLine(
            "- 范围外写入（例如 Scope 是卷二却写了第54章）只记入 Progress 的“范围外活动”，不是任务切换，不能成为 Next。"
        );
        prompt.AppendLine(
            "- 没有新作者消息时，Scope 原样继承；工具活动、全局前沿、最新章节都不是新任务。"
        );
        prompt.AppendLine();
        prompt.AppendLine("## 最近作者消息（从新到旧，Scope 的唯一新来源）");
        if (authorMessages.Count == 0)
        {
            prompt.AppendLine("- （本轮没有新的作者消息；只能继承已有检查点中的作者指令/Scope）");
        }
        else
        {
            for (var i = 0; i < authorMessages.Count; i++)
            {
                var label = i == 0 ? "最新" : "更早";
                prompt.AppendLine(
                    $"- [{label}] {Truncate(authorMessages[i].ReplaceLineEndings(" "), 1_200)}"
                );
            }
        }
        prompt.AppendLine();

        prompt.AppendLine("## 作者指令（压缩区，逐字）");
        if (authorInstructions.Count == 0)
            prompt.AppendLine("- （压缩区没有作者指令）");
        else
            foreach (var instruction in authorInstructions)
                prompt.AppendLine($"- {Truncate(instruction.ReplaceLineEndings(" "), 1_200)}");
        prompt.AppendLine();

        prompt.AppendLine("## 任务范围（LLM 从作者消息提取，最高优先级）");
        if (scope is null)
            prompt.AppendLine("- 未识别到明确范围；按作者指令判断，不得自行扩大。");
        else
            prompt.AppendLine(scope.FormatBody());
        prompt.AppendLine();

        prompt.AppendLine("## 任务进度（工具事实，不得改写）");
        if (progress is null)
            prompt.AppendLine(CompactionTaskProgress.Unrecorded);
        else
            prompt.AppendLine(progress.FormatBody());
        prompt.AppendLine();

        prompt.AppendLine("## 当前任务清单（工具事实，不得改写）");
        prompt.AppendLine(todoItems is null ? "- 未记录" : FormatTodoBody(todoItems));
        prompt.AppendLine();

        prompt.AppendLine("## 当前小说状态（全局写作前沿；Scope 冲突时以 Scope 为准）");
        if (string.IsNullOrWhiteSpace(novelSnapshot))
            prompt.AppendLine("（本会话未关联小说结构；按对话推断，不要编造章节）");
        else
            prompt.AppendLine(novelSnapshot.Trim());
        prompt.AppendLine();

        if (previousSummary is not null)
        {
            prompt.AppendLine("## 已有检查点（只作为背景，不要原文复述，不要嵌套标题）");
            prompt.AppendLine(Truncate(previousSummary, 4_000));
            prompt.AppendLine();
        }

        prompt.AppendLine("## 新增对话（按时间顺序）");
        prompt.AppendLine(dialogue);

        // 摘要目标是 8,000 字符，CJK 约 1 token/字；2048 会把摘要截断。
        // 按模型输出预算预留，同时不超过 8K，避免小窗口模型被 output 挤空输入。
        var maxSummaryTokens = Math.Clamp(
            agent.ModelProfile?.OutputTokenBudget ?? 8_192,
            1_024,
            8_192
        );
        var request = BuildSummaryRequest(modelName, maxSummaryTokens);
        var conversation = api.Chat.CreateConversation(request);
        conversation.AddSystemMessage(
            "你是小说写作的上下文检查点编辑器。只输出 10 个检查点标题，保留可执行的下一步。使用中文。"
                + "只有“作者指令（逐字，最高优先级）”是用户目标；[远期推演]/[云端诊断]/[本体 harness] 是系统注入，不是作者指令。"
                + "任务范围（Scope）、任务进度（Progress）与任务清单（Todos）是硬约束/工具事实，不得改写；"
                + "Next 必须落在 Scope 内、由任务清单中的未完成项决定，不能把全局写作前沿的下一章当成任务下一步，不能把范围外活动当成任务切换。"
        );
        conversation.AddUserMessage(prompt.ToString());

        using var summaryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        summaryCts.CancelAfter(AiSummaryTimeout);

        var summaryBuilder = new StringBuilder();
        try
        {
            await conversation.StreamResponse(
                token =>
                {
                    summaryBuilder.Append(token);
                },
                summaryCts.Token
            );
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 摘要超时不是压缩失败：回退到确定性检查点即可。
            // 只记一条简洁警告，不打完整堆栈——此前 45 秒超时把一次正常回退
            // 记成了“压缩失败”，掩盖了真正的问题。
            log.LogWarning(
                "AI compact summary timed out after {Seconds:0}s; falling back to deterministic checkpoint",
                AiSummaryTimeout.TotalSeconds
            );
            return null;
        }
        var result = summaryBuilder.ToString().Trim();
        return string.IsNullOrEmpty(result) ? null : Truncate(result, 32_000);
    }

    /// <summary>AI 摘要必须沿用当前会话模型，并给足输出预算，不能让服务端回退到默认模型。</summary>
    internal static ChatRequest BuildSummaryRequest(string modelName, int maxTokens) =>
        new() { Model = modelName, MaxTokens = maxTokens };

    private static string BuildCompressedDialogue(IReadOnlyList<ChatMessage> messages, int cutIndex)
    {
        const int maxPartChars = 2_000;
        const int maxTotalChars = 24_000;
        var lines = new List<string>();
        var used = 0;
        for (var i = cutIndex - 1; i >= 0; i--)
        {
            var msg = messages[i];
            var content = msg.Content ?? "";
            if (
                string.IsNullOrWhiteSpace(content)
                || SessionInternalMessages.IsCompactSummary(content)
            )
                continue;
            var role =
                msg.Role == ChatMessageRoles.User
                && SessionInternalMessages.IsInjectedUserMessage(content)
                    ? "system-note"
                    : msg.Role switch
                    {
                        ChatMessageRoles.User => "user",
                        ChatMessageRoles.Assistant => "assistant",
                        ChatMessageRoles.Tool => "tool",
                        _ => "unknown",
                    };
            var line = $"[{role}]: {Truncate(content.ReplaceLineEndings(" "), maxPartChars)}";
            if (used + line.Length > maxTotalChars)
                break;
            lines.Add(line);
            used += line.Length + 1;
        }

        lines.Reverse();
        return string.Join('\n', lines);
    }

    private static string BuildCompactSummary(
        IReadOnlyList<ChatMessage> messages,
        int cutIndex,
        string? previousSummary,
        string novelSnapshot
    )
    {
        var baseCheckpoint = string.IsNullOrWhiteSpace(novelSnapshot)
            ? BuildConversationCheckpoint(messages, cutIndex)
            : novelSnapshot;
        var summary = Truncate(MergeCheckpoint(baseCheckpoint, previousSummary), 8_000);
        return PrependAuthorScope(summary, messages, cutIndex, [], null);
    }

    private static string BuildCompactSummaryWithScope(
        IReadOnlyList<ChatMessage> messages,
        int cutIndex,
        string? previousSummary,
        string novelSnapshot,
        CompactionTaskScope? scope,
        CompactionTaskProgress? progress,
        IReadOnlyList<string> authorMessages
    )
    {
        var baseCheckpoint = string.IsNullOrWhiteSpace(novelSnapshot)
            ? BuildConversationCheckpoint(messages, cutIndex)
            : novelSnapshot;
        if (scope is not null)
            baseCheckpoint = EnsureScopeSection(baseCheckpoint, scope);
        if (progress is not null)
            baseCheckpoint = EnsureProgressSection(baseCheckpoint, progress);

        var summary = Truncate(MergeCheckpoint(baseCheckpoint, previousSummary), 8_000);
        summary = PrependAuthorScope(summary, messages, cutIndex, authorMessages, scope);
        return EnforceTaskScope(summary, scope, previousSummary, progress, baseCheckpoint);
    }

    private static string BuildConversationCheckpoint(
        IReadOnlyList<ChatMessage> messages,
        int compressedCount,
        string whereNote = "未关联小说结构（按最近对话判断）"
    )
    {
        var events = CollectDialogueEvents(messages, compressedCount);
        var latestUser = FindLatestVisibleUser(messages, compressedCount);
        var userText = latestUser is null ? "" : Truncate(latestUser, 200);

        var sb = new StringBuilder();
        sb.AppendLine(WhereHeading);
        sb.AppendLine($"- {whereNote}");
        sb.AppendLine();
        sb.AppendLine(WhoHeading);
        sb.AppendLine("- 对话参与者：用户 / 助手");
        sb.AppendLine();
        sb.AppendLine(WhatHeading);
        if (events.Count == 0)
            sb.AppendLine("- 压缩区没有可提取的对话正文");
        else
            foreach (var line in events)
                sb.AppendLine($"- {line}");
        sb.AppendLine();
        sb.AppendLine(WhyHeading);
        sb.AppendLine($"- 用户目标：{(userText.Length > 0 ? userText : "未明确")}");
        sb.AppendLine();
        sb.AppendLine(WhenHeading);
        sb.AppendLine("- 故事内时间：未指定（无小说时间线）");
        sb.AppendLine();
        sb.AppendLine(HowHeading);
        sb.AppendLine("- 无小说风格设定；按最近用户要求继续，不重复复述用户目标。");
        sb.AppendLine();
        sb.AppendLine(NextHeading);
        sb.AppendLine("- 直接下一步：完成用户最近请求（见 Why），并按当前对话继续");
        return sb.ToString();
    }

    private static string MergeCheckpoint(string baseCheckpoint, string? previousSummary)
    {
        var sections = new Dictionary<string, string>(
            CheckpointHeadings.Length,
            StringComparer.Ordinal
        );
        foreach (var heading in CheckpointHeadings)
            sections[heading] = ExtractSection(baseCheckpoint, heading)?.Trim() ?? "";

        if (!string.IsNullOrWhiteSpace(previousSummary))
        {
            // 新解析出的 Scope 是权威；只有本轮没有识别出 Scope 时才继承上一份。
            if (IsUnrecorded(sections[ScopeHeading]))
            {
                var previousScope = ExtractSection(previousSummary, ScopeHeading);
                if (!string.IsNullOrWhiteSpace(previousScope))
                {
                    sections[ScopeHeading] = previousScope.Trim();

                    // Scope 继承自上一份摘要时，同一条任务链的 Progress 必须一起继承；
                    // 否则一次提取失败就会清空进度，下一轮退回按字数猜焦点。
                    if (IsUnrecorded(sections[ProgressHeading]))
                    {
                        var previousProgress = ExtractSection(previousSummary, ProgressHeading);
                        if (!string.IsNullOrWhiteSpace(previousProgress))
                            sections[ProgressHeading] = previousProgress.Trim();
                    }
                }
            }

            // Why/How 可以累积背景；Next 只能回退，不能把上一轮任务清单追加到新任务后面。
            var mergedAny = false;
            foreach (var heading in new[] { WhyHeading, HowHeading })
            {
                var previous = ExtractSection(previousSummary, heading);
                if (string.IsNullOrWhiteSpace(previous))
                    continue;
                mergedAny = true;
                sections[heading] = AppendMissingLines(sections[heading], previous, maxLines: 5);
            }

            if (string.IsNullOrWhiteSpace(sections[NextHeading]))
            {
                var previousNext = ExtractSection(previousSummary, NextHeading);
                if (!string.IsNullOrWhiteSpace(previousNext))
                    sections[NextHeading] = previousNext.Trim();
            }

            if (!mergedAny)
            {
                var plain = StripHeadingLines(previousSummary);
                if (plain.Length > 0)
                {
                    var how = sections[HowHeading];
                    var inherited = $"- 继承背景：{Truncate(plain, 1_000)}";
                    sections[HowHeading] = string.IsNullOrEmpty(how)
                        ? inherited
                        : how + "\n" + inherited;
                }
            }
        }

        var sb = new StringBuilder();
        foreach (var heading in CheckpointHeadings)
        {
            sb.AppendLine(heading);
            var content = sections[heading];
            sb.AppendLine(string.IsNullOrWhiteSpace(content) ? "- 未记录" : content);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string NormalizeCheckpoint(string aiSummary, string baseCheckpoint)
    {
        var sb = new StringBuilder();
        foreach (var heading in CheckpointHeadings)
        {
            var content = ExtractSection(aiSummary, heading);
            if (string.IsNullOrWhiteSpace(content))
                content = ExtractSection(baseCheckpoint, heading);
            sb.AppendLine(heading);
            sb.AppendLine(string.IsNullOrWhiteSpace(content) ? "- 未记录" : content.Trim());
            sb.AppendLine();
        }
        return Truncate(sb.ToString().TrimEnd(), 8_000);
    }

    private static string AppendMissingLines(string current, string previous, int maxLines)
    {
        var existing = current;
        var sb = new StringBuilder(current);
        var added = 0;
        foreach (var rawLine in previous.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("##", StringComparison.Ordinal))
                continue;
            if (existing.Contains(line, StringComparison.Ordinal))
                continue;
            sb.AppendLine(Truncate(line, 300));
            existing += "\n" + line;
            if (++added >= maxLines)
                break;
        }
        return sb.ToString().TrimEnd();
    }

    private static string StripHeadingLines(string markdown) =>
        string.Join(
                ' ',
                markdown
                    .Split('\n')
                    .Select(line => line.Trim())
                    .Where(line =>
                        line.Length > 0 && !line.StartsWith("##", StringComparison.Ordinal)
                    )
            )
            .Trim();

    private static List<string> CollectDialogueEvents(
        IReadOnlyList<ChatMessage> messages,
        int cutIndex
    )
    {
        var userEvents = new List<(int Index, string Text)>();
        var assistantEvents = new List<(int Index, string Text)>();
        for (var i = 0; i < cutIndex; i++)
        {
            var content = (messages[i].Content ?? "").ReplaceLineEndings(" ").Trim();
            if (content.Length == 0 || SessionInternalMessages.IsCompactSummary(content))
                continue;
            if (messages[i].Role == ChatMessageRoles.User)
            {
                if (!SessionInternalMessages.IsInjectedUserMessage(content))
                    userEvents.Add((i, $"用户：{Truncate(content, 100)}"));
            }
            else if (messages[i].Role == ChatMessageRoles.Assistant)
            {
                assistantEvents.Add((i, $"助手：{Truncate(content, 100)}"));
            }
        }

        return userEvents
            .TakeLast(2)
            .Concat(assistantEvents.TakeLast(2))
            .OrderBy(e => e.Index)
            .Select(e => e.Text)
            .ToList();
    }

    private static List<string> CollectAuthorInstructions(
        IReadOnlyList<ChatMessage> messages,
        int cutIndex,
        int max = 3
    )
    {
        var result = new List<string>();
        for (var i = cutIndex - 1; i >= 0 && result.Count < max; i--)
        {
            var content = (messages[i].Content ?? "").Trim();
            if (
                messages[i].Role != ChatMessageRoles.User
                || content.Length == 0
                || SessionInternalMessages.IsCompactSummary(content)
                || SessionInternalMessages.IsInjectedUserMessage(content)
            )
                continue;
            result.Add(content);
        }
        result.Reverse();
        return result;
    }

    private static string PrependAuthorScope(
        string summary,
        IReadOnlyList<ChatMessage> messages,
        int cutIndex,
        IReadOnlyList<string> authorMessages,
        CompactionTaskScope? scope
    )
    {
        var instruction = scope?.Instruction;
        if (string.IsNullOrWhiteSpace(instruction))
            instruction = CompactionTaskScope.SelectTaskInstruction(authorMessages);
        if (string.IsNullOrWhiteSpace(instruction))
            instruction = FindLatestVisibleUser(messages, cutIndex);
        if (string.IsNullOrWhiteSpace(instruction))
            return summary;
        var text = Truncate(instruction.ReplaceLineEndings(" "), 300);
        return $"> 作者指令（逐字，最高优先级）：{text}\n\n{summary}";
    }

    private static string? FindLatestVisibleUser(IReadOnlyList<ChatMessage> messages, int cutIndex)
    {
        for (var i = cutIndex - 1; i >= 0; i--)
        {
            var content = (messages[i].Content ?? "").Trim();
            if (
                messages[i].Role == ChatMessageRoles.User
                && content.Length > 0
                && !SessionInternalMessages.IsCompactSummary(content)
                && !SessionInternalMessages.IsInjectedUserMessage(content)
            )
                return content;
        }
        return null;
    }

    private async Task<string> BuildNovelStateSnapshotAsync(
        string novelId,
        IReadOnlyList<ChatMessage> compressedMessages,
        CancellationToken ct
    )
    {
        var result = await BuildNovelStateAsync(novelId, compressedMessages, null, null, ct);
        return result.Snapshot;
    }

    private async Task<NovelStateBuildResult> BuildNovelStateAsync(
        string novelId,
        IReadOnlyList<ChatMessage> compressedMessages,
        string? previousSummary,
        string? pendingAuthorMessage,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(novelId))
            return new NovelStateBuildResult("", null, null, []);

        var structure = await novelStore.GetStructure(novelId, ct);
        if (structure is null || structure.Chapters.Count == 0)
            return new NovelStateBuildResult(
                BuildConversationCheckpoint(
                    compressedMessages,
                    compressedMessages.Count,
                    "尚未建立章节（按最近对话判断）"
                ),
                null,
                null,
                []
            );

        // 待发送消息优先（主动压缩发生在 Prompt 之前）；其余从会话分片取，
        // 保证“继续”不会覆盖更早的任务原话。
        var authorMessages = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(pendingAuthorMessage))
            authorMessages.Add(pendingAuthorMessage.Trim());
        if (!string.IsNullOrWhiteSpace(agent.SessionId))
            authorMessages.AddRange(
                await store.GetRecentAuthorMessagesAsync(agent.SessionId, 3, ct)
            );
        if (authorMessages.Count == 0)
            authorMessages.AddRange(
                Enumerable.Reverse(
                    CollectAuthorInstructions(compressedMessages, compressedMessages.Count, max: 3)
                )
            );
        authorMessages = authorMessages
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToList();

        // 主路径：LLM 从最近作者消息 + 上一份摘要提取 Scope；不落本地状态。
        var extraction = await CompactionScopeExtractor.TryExtractAsync(
            agent,
            structure,
            authorMessages,
            previousSummary,
            ct
        );
        var previousScope = previousSummary is null
            ? null
            : ExtractSection(previousSummary, ScopeHeading);
        // mode=none 表示模型无法确定，不等于作者取消任务；必须让确定性兜底
        // 用更早的作者消息/上一份 Scope 恢复，避免“继续”把任务边界清空。
        var scope = extraction.Scope
            ?? CompactionTaskScope.TryCreate(authorMessages, structure, previousScope);

        var previousProgress = previousSummary is null
            ? null
            : CompactionTaskProgress.TryParse(previousSummary);
        var progress = scope is null
            ? null
            : CompactionProgressExtractor.Extract(
                compressedMessages,
                structure,
                scope,
                previousProgress
            );

        return new NovelStateBuildResult(
            await BuildNovelStateSnapshotCoreAsync(
                novelId,
                structure,
                compressedMessages,
                scope,
                progress,
                ct
            ),
            scope,
            progress,
            authorMessages
        );
    }

    private async Task<string> BuildNovelStateSnapshotCoreAsync(
        string novelId,
        NovelStructure structure,
        IReadOnlyList<ChatMessage> compressedMessages,
        CompactionTaskScope? scope,
        CompactionTaskProgress? progress,
        CancellationToken ct
    )
    {
        var orderedChapters = structure.Chapters.OrderBy(c => c.Order).ToList();
        var globalCurrent = orderedChapters
            .Where(c => c.CharCount > 0)
            .OrderBy(c => c.Order)
            .LastOrDefault()
            ?? orderedChapters[0];
        var globalNext = orderedChapters
            .Where(c => c.Order > globalCurrent.Order && c.CharCount == 0)
            .OrderBy(c => c.Order)
            .FirstOrDefault()
            ?? orderedChapters.FirstOrDefault(c => c.Order > globalCurrent.Order);
        var current = (
            progress?.CurrentOrder is int progressCurrent
                ? scope?.Chapters.FirstOrDefault(c => c.Order == progressCurrent)
                : null
        )
            ?? scope?.FindFocusChapter(progress)
            ?? globalCurrent;
        // 有 Scope 时，范围内没有下一章就是 null；不能回退到全书写作前沿。
        var next = (
            progress?.NextOrder is int progressNext
                ? scope?.Chapters.FirstOrDefault(c => c.Order == progressNext)
                : null
        ) ?? (scope is null ? globalNext : scope.FindNextChapter(progress));
        var volume = structure.Volumes.FirstOrDefault(v => v.Id == current.VolumeId);

        var scenesTask = novelStore.GetScenesByChapter(current.Id, ct);
        var charactersTask = novelStore.GetCharacters(novelId, ct);
        var plotlinesTask = novelStore.GetPlotlines(novelId, ct);
        var locationsTask = novelStore.GetLocations(novelId, ct);
        var factionsTask = novelStore.GetFactions(novelId, ct);
        var itemsTask = novelStore.GetItems(novelId, ct);
        var relationshipsTask = novelStore.GetRelationshipsByNovel(novelId, ct);
        var appearancesTask = novelStore.GetAppearancesByChapter(novelId, current.Id, ct);
        var stateChangesTask = novelStore.GetEntityStateChangesByChapter(novelId, current.Id, ct);
        var timelineTask = novelStore.GetTimelineEvents(novelId, ct);
        await Task.WhenAll(
            scenesTask,
            charactersTask,
            plotlinesTask,
            locationsTask,
            factionsTask,
            itemsTask,
            relationshipsTask,
            appearancesTask,
            stateChangesTask,
            timelineTask
        );

        var scenes = await scenesTask;
        var characters = await charactersTask;
        var plotlines = await plotlinesTask;
        var openPlotlines = plotlines.Where(p => p.ResolvedAtChapterId == null).ToList();
        var locations = await locationsTask;
        var factions = await factionsTask;
        var items = await itemsTask;
        var relationships = await relationshipsTask;
        var appearances = await appearancesTask;
        var stateChanges = await stateChangesTask;
        var timeline = await timelineTask;

        var scene = scenes.OrderBy(s => s.Order).LastOrDefault();
        var characterById = characters.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var locationNames = locations.ToDictionary(l => l.Id, l => l.Name, StringComparer.Ordinal);
        var factionNames = factions.ToDictionary(f => f.Id, f => f.Name, StringComparer.Ordinal);
        var itemNames = items.ToDictionary(i => i.Id, i => i.Name, StringComparer.Ordinal);
        var plotlineNames = plotlines.ToDictionary(p => p.Id, p => p.Name, StringComparer.Ordinal);

        string CharacterName(string id) =>
            characterById.TryGetValue(id, out var character) ? character.Name : id;
        string LocationName(string? id) =>
            id is not null && locationNames.TryGetValue(id, out var name) ? name : (id ?? "未指定");
        string ItemName(string id) => itemNames.TryGetValue(id, out var name) ? name : id;
        string PlotlineName(string id) => plotlineNames.TryGetValue(id, out var name) ? name : id;

        var involvedCharacterIds = current
            .CharacterIds.Concat(scene?.ParticipantIds ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var involvedSet = involvedCharacterIds.ToHashSet(StringComparer.Ordinal);
        var involvedFactionIds = involvedCharacterIds
            .Select(id =>
                characterById.TryGetValue(id, out var character) ? character.FactionId : null
            )
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var plannedNames = current.CharacterIds.Select(CharacterName).ToList();
        var presentNames = (scene?.ParticipantIds ?? []).Select(CharacterName).ToList();
        var factionLine =
            involvedFactionIds.Count == 0
                ? "无"
                : string.Join(
                    "、",
                    involvedFactionIds.Select(id =>
                        factionNames.TryGetValue(id!, out var name) ? name : id!
                    )
                );
        var relationshipLines = relationships
            .Where(r =>
                involvedSet.Contains(r.CharacterAId) || involvedSet.Contains(r.CharacterBId)
            )
            .Take(3)
            .Select(r =>
                $"{CharacterName(r.CharacterAId)}—{CharacterName(r.CharacterBId)}（{r.Type}/{r.Strength}）"
                + (
                    string.IsNullOrWhiteSpace(r.Description)
                        ? ""
                        : $"：{Truncate(r.Description, 100)}"
                )
            )
            .ToList();

        var relevantPlotlines = plotlines
            .OrderByDescending(p =>
                current.PlotlineIds.Contains(p.Id) || (scene?.PlotlineIds.Contains(p.Id) ?? false)
            )
            .ThenByDescending(p => p.Tension)
            .Take(6)
            .ToList();
        var plotlineLines = relevantPlotlines
            .Select(p =>
                $"{p.Name}（{p.Type}/{p.Status}/{p.Tension}）"
                + (
                    string.IsNullOrWhiteSpace(p.Turnpoints)
                        ? ""
                        : $"；转折：{Truncate(p.Turnpoints, 120)}"
                )
            )
            .ToList();

        var motivationLines = involvedCharacterIds
            .Select(id => characterById.TryGetValue(id, out var c) ? c : null)
            .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Motivation))
            .Take(4)
            .Select(c => $"{c!.Name}：{Truncate(c.Motivation, 120)}")
            .ToList();

        var stateChangeLines = stateChanges
            .OrderByDescending(c => c.Sequence)
            .Take(5)
            .Select(c =>
            {
                var change =
                    string.IsNullOrWhiteSpace(c.FromSummary)
                    && string.IsNullOrWhiteSpace(c.ToSummary)
                        ? c.Summary
                        : $"{c.FromSummary ?? "?"} → {c.ToSummary ?? "?"}";
                var cause = string.IsNullOrWhiteSpace(c.Cause)
                    ? ""
                    : $"；原因：{Truncate(c.Cause!, 80)}";
                return $"{c.Summary}（{change}{cause}）";
            })
            .ToList();

        var timelineLines = timeline
            .Where(t => t.ChapterId == current.Id)
            .Take(5)
            .Select(t =>
                $"{t.Title}"
                + (
                    string.IsNullOrWhiteSpace(t.Description)
                        ? ""
                        : $"：{Truncate(t.Description, 120)}"
                )
            )
            .ToList();

        var appearanceLines = appearances
            .Take(5)
            .Select(a =>
                $"{a.EntityType}:{NameForAppearance(a.EntityType, a.EntityId)}"
                + (a.Mode == AppearanceMode.Present ? "（在场）" : "（提及）")
            )
            .ToList();

        var dialogueLines = CollectDialogueEvents(compressedMessages, compressedMessages.Count);
        var latestUser = FindLatestVisibleUser(compressedMessages, compressedMessages.Count);
        var recentUserRequirements = compressedMessages
            .Where(m =>
                m.Role == ChatMessageRoles.User
                && !string.IsNullOrWhiteSpace(m.Content)
                && !SessionInternalMessages.IsCompactSummary(m.Content)
                && !SessionInternalMessages.IsInjectedUserMessage(m.Content)
            )
            .TakeLast(3)
            .Select(m => Truncate((m.Content ?? "").ReplaceLineEndings(" ").Trim(), 120))
            .ToList();

        var nextParts = new List<string>(3);
        if (latestUser is not null)
            nextParts.Add($"回应用户最近请求：{Truncate(latestUser, 200)}");
        if (scope is not null)
            nextParts.Add($"按任务范围继续：{scope.BuildSafeNext(progress: progress)}");
        else if (!string.IsNullOrWhiteSpace(next?.Summary))
            nextParts.Add($"推进下一章《{next.Title}》：{Truncate(next.Summary, 200)}");
        if (openPlotlines.Count > 0)
            nextParts.Add(
                $"推进未回收情节线：{string.Join("、", openPlotlines.Take(3).Select(p => p.Name))}"
            );

        var sb = new StringBuilder();
        if (scope is not null)
        {
            sb.AppendLine(scope.FormatSection());
            sb.AppendLine();
        }
        if (progress is not null)
        {
            sb.AppendLine(progress.FormatSection());
            sb.AppendLine();
        }

        sb.AppendLine(WhereHeading);
        sb.AppendLine($"- 卷：{(volume is null ? "未分卷" : $"{volume.Name}（{volume.Order}）")}");
        sb.AppendLine(
            scope is null
                ? $"- 当前章：第{current.Order}章《{current.Title}》（{current.Status}，{current.CharCount} 字）"
                : $"- 当前章（任务焦点）：第{current.Order}章《{current.Title}》（{current.Status}，{current.CharCount} 字）"
        );
        sb.AppendLine(
            scene is null
                ? "- 当前场景：尚无场景"
                : $"- 当前场景：{scene.Title}；地点：{LocationName(scene.LocationId)}；章节内顺序：{scene.Order}"
        );
        sb.AppendLine(
            $"- 本章道具：{(scene is null || scene.ItemIds.Count == 0 ? "无" : string.Join("、", scene.ItemIds.Take(5).Select(ItemName)))}"
        );
        sb.AppendLine(
            scope is null
                ? $"- 下一章：{(next is null ? "无下一章（继续当前章或新建）" : $"第{next.Order}章《{next.Title}》")}"
                : $"- 下一章（任务焦点）：{(next is null ? "范围内已无下一章；按 Scope 继续复核或收尾" : $"第{next.Order}章《{next.Title}》")}"
        );
        if (scope is not null)
        {
            sb.AppendLine(
                $"- 全局写作前沿（不是本任务进度）：第{globalCurrent.Order}章《{globalCurrent.Title}》（{globalCurrent.Status}，{globalCurrent.CharCount} 字）"
            );
            sb.AppendLine(
                $"- 全局下一章（不是本任务 Next）：{(globalNext is null ? "无" : $"第{globalNext.Order}章《{globalNext.Title}》")}"
            );
        }
        sb.AppendLine();

        sb.AppendLine(WhoHeading);
        sb.AppendLine(
            $"- 本章计划角色：{(plannedNames.Count == 0 ? "未指定" : string.Join("、", plannedNames.Take(8)))}"
        );
        sb.AppendLine(
            $"- 当前场景在场：{(presentNames.Count == 0 ? "未指定" : string.Join("、", presentNames))}"
        );
        sb.AppendLine($"- 组织：{factionLine}");
        if (relationshipLines.Count > 0)
            foreach (var line in relationshipLines)
                sb.AppendLine($"- 关系：{line}");
        if (appearanceLines.Count > 0)
            sb.AppendLine($"- 最近出场：{string.Join("；", appearanceLines)}");
        sb.AppendLine();

        sb.AppendLine(WhatHeading);
        sb.AppendLine(
            $"- 本章细纲：{(string.IsNullOrWhiteSpace(current.Summary) ? "未填写" : Truncate(current.Summary, 500))}"
        );
        if (stateChangeLines.Count > 0)
            foreach (var line in stateChangeLines)
                sb.AppendLine($"- 状态变化：{line}");
        if (dialogueLines.Count > 0)
            foreach (var line in dialogueLines)
                sb.AppendLine($"- {line}");
        else
            sb.AppendLine("- 最近对话事件：无");
        sb.AppendLine();

        sb.AppendLine(WhyHeading);
        sb.AppendLine($"- 用户目标：{(latestUser is null ? "未明确" : Truncate(latestUser, 200))}");
        sb.AppendLine(
            scene is null
                ? "- 场景冲突/目标：尚无场景"
                : $"- 场景冲突/目标：{(string.IsNullOrWhiteSpace(scene.Conflict) ? "未指定" : Truncate(scene.Conflict, 120))} / {(string.IsNullOrWhiteSpace(scene.Goal) ? "未指定" : Truncate(scene.Goal, 120))}"
        );
        if (motivationLines.Count > 0)
            foreach (var line in motivationLines)
                sb.AppendLine($"- 角色动机：{line}");
        if (plotlineLines.Count > 0)
            foreach (var line in plotlineLines)
                sb.AppendLine($"- 情节线：{line}");
        sb.AppendLine();

        sb.AppendLine(WhenHeading);
        sb.AppendLine(
            $"- 故事内时间：{(scene is null || string.IsNullOrWhiteSpace(scene.TimeDescription) ? "未指定" : scene.TimeDescription)}"
        );
        if (timelineLines.Count > 0)
            foreach (var line in timelineLines)
                sb.AppendLine($"- 时间线事件：{line}");
        else
            sb.AppendLine("- 时间线事件：无");
        sb.AppendLine();

        sb.AppendLine(HowHeading);
        sb.AppendLine($"- 类型/风格：{structure.Novel.Genre} / {structure.Novel.WritingStyle}");
        sb.AppendLine(
            $"- 本章备注/节奏提示：{(string.IsNullOrWhiteSpace(current.Notes) ? "无" : Truncate(current.Notes, 300))}"
        );
        if (recentUserRequirements.Count > 0)
            sb.AppendLine($"- 最近用户写作要求：{string.Join("；", recentUserRequirements)}");
        else
            sb.AppendLine("- 最近用户写作要求：无");
        sb.AppendLine();

        sb.AppendLine(NextHeading);
        if (scope is not null)
        {
            sb.AppendLine(
                $"- 本任务下一步（必须在 Scope 内）：{scope.BuildSafeNext(progress: progress)}"
            );
            sb.AppendLine(
                scope.Mode == CompactionTaskMode.Revise
                    ? "- 全局下一章只是小说写作前沿，不是本任务 Next；不得写入范围外章节。"
                    : "- 只写 Scope 范围内章节，不推进范围外内容。"
            );
            sb.AppendLine(
                $"- 未回收情节线：{(openPlotlines.Count == 0 ? "无" : string.Join("；", openPlotlines.Take(5).Select(p => $"{p.Name}（{p.Tension}）")))}"
            );
        }
        else
        {
            sb.AppendLine(
                $"- 下一章细纲：{(string.IsNullOrWhiteSpace(next?.Summary) ? "未填写" : Truncate(next!.Summary, 300))}"
            );
            sb.AppendLine(
                $"- 未回收情节线：{(openPlotlines.Count == 0 ? "无" : string.Join("；", openPlotlines.Take(5).Select(p => $"{p.Name}（{p.Tension}）")))}"
            );
            sb.AppendLine(
                $"- 下一场景计划：{(string.IsNullOrWhiteSpace(next?.Notes) ? "无" : Truncate(next!.Notes, 300))}"
            );
            sb.AppendLine(
                $"- 直接下一步：{(nextParts.Count == 0 ? "按当前章继续写作" : string.Join("；", nextParts))}"
            );
        }

        return Truncate(sb.ToString().TrimEnd(), 8_000);

        string NameForAppearance(EntityType type, string id) =>
            type switch
            {
                EntityType.Character => CharacterName(id),
                EntityType.Location => LocationName(id),
                EntityType.Faction => factionNames.TryGetValue(id, out var name) ? name : id,
                EntityType.Item => ItemName(id),
                EntityType.Plotline => PlotlineName(id),
                _ => id,
            };
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

    private static string EnsureScopeSection(string markdown, CompactionTaskScope scope)
    {
        var existing = ExtractSection(markdown, ScopeHeading);
        if (!string.IsNullOrWhiteSpace(existing) && !IsUnrecorded(existing))
            return markdown;
        return ReplaceSection(markdown, ScopeHeading, scope.FormatBody());
    }

    private static string EnsureProgressSection(string markdown, CompactionTaskProgress progress)
    {
        var existing = ExtractSection(markdown, ProgressHeading);
        if (!string.IsNullOrWhiteSpace(existing) && !IsUnrecorded(existing))
            return markdown;
        return ReplaceSection(markdown, ProgressHeading, progress.FormatBody());
    }

    /// <summary>
    /// Todos 是宿主内存中的权威任务状态；摘要无论由 AI 还是确定性回退生成，
    /// 都必须用压缩开始时的快照覆盖，避免压缩掉 todo_write 调用后模型失忆。
    /// </summary>
    private static string EnsureTodoSection(string markdown, IReadOnlyList<TodoItem> todos) =>
        ReplaceSection(markdown, TodosHeading, FormatTodoBody(todos));

    private static string FormatTodoBody(IReadOnlyList<TodoItem> todos)
    {
        var visible = todos
            .Where(item => item.Status != TodoStatus.Deleted)
            .OrderBy(item => item.Id)
            .ToList();
        if (visible.Count == 0)
            return "- 当前没有任务（如 Progress/Next 仍有未完成工作，先用 todo_write 建立任务清单）";

        var sb = new StringBuilder();
        sb.AppendLine(
            "- 维护：完成/推进任务后立即用 todo_write 更新对应 #id；收尾时逐项标记 completed。"
        );
        foreach (var item in visible)
        {
            var subject = string.IsNullOrWhiteSpace(item.Subject)
                ? "(untitled)"
                : item.Subject.Trim();
            sb.Append("- #")
                .Append(item.Id)
                .Append(" [")
                .Append(TodoStatusText(item.Status))
                .Append("] ")
                .Append(Truncate(subject.ReplaceLineEndings(" "), 200));

            if (item.Status == TodoStatus.InProgress && !string.IsNullOrWhiteSpace(item.ActiveForm))
                sb.Append("（")
                    .Append(Truncate(item.ActiveForm.ReplaceLineEndings(" ").Trim(), 80))
                    .Append('）');

            if (item.BlockedBy is { Length: > 0 } blockedBy)
                sb.Append("（blocked by: ")
                    .Append(string.Join(", ", blockedBy.Select(id => $"#{id}")))
                    .Append('）');

            if (!string.IsNullOrWhiteSpace(item.Description))
                sb.Append(" — ")
                    .Append(Truncate(item.Description.ReplaceLineEndings(" ").Trim(), 120));
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string TodoStatusText(TodoStatus status) =>
        status switch
        {
            TodoStatus.InProgress => "in_progress",
            TodoStatus.Completed => "completed",
            TodoStatus.Deleted => "deleted",
            _ => "pending",
        };

    /// <summary>
    /// Todo 清单是任务进度的一部分，不只是展示：Next 必须优先落在未完成的下一个 Todo。
    /// 优先 in_progress；其次第一个未被未完成任务阻塞的 pending；最后退回被阻塞的 pending。
    /// Scope 存在时，Todo 文案里的章节/卷必须仍在范围内，避免把范围外任务当成下一步。
    /// </summary>
    internal static string? BuildTodoNext(
        IReadOnlyList<TodoItem>? todos,
        CompactionTaskScope? scope
    )
    {
        if (todos is null || todos.Count == 0)
            return null;

        var visible = todos
            .Where(item => item.Status != TodoStatus.Deleted)
            .OrderBy(item => item.Id)
            .ToList();
        if (visible.Count == 0)
            return null;

        var statusById = visible.ToDictionary(
            item => item.Id.ToString(),
            item => item.Status,
            StringComparer.Ordinal
        );
        bool IsBlocked(TodoItem item) =>
            item.BlockedBy is { Length: > 0 } blockedBy
            && blockedBy.Any(id =>
                statusById.TryGetValue(id, out var status)
                && status is not (TodoStatus.Completed or TodoStatus.Deleted)
            );
        // 只校验任务标题/进行时描述；Description 常含跨章伏笔/参考，不是本任务动作。
        bool InScope(TodoItem item) =>
            scope is null || scope.IsTextWithinScope($"{item.Subject}\n{item.ActiveForm}");

        var selected = visible.FirstOrDefault(item =>
            item.Status == TodoStatus.InProgress && InScope(item)
        )
            ?? visible.FirstOrDefault(item =>
                item.Status == TodoStatus.Pending && !IsBlocked(item) && InScope(item)
            )
            ?? visible.FirstOrDefault(item => item.Status == TodoStatus.Pending && InScope(item));
        if (selected is null)
            return null;

        var subject = string.IsNullOrWhiteSpace(selected.Subject)
            ? "(untitled)"
            : Truncate(selected.Subject.ReplaceLineEndings(" ").Trim(), 200);
        var action = selected.Status == TodoStatus.InProgress ? "继续" : "开始";
        var sb = new StringBuilder();
        sb.Append("- 任务清单下一步：")
            .Append(action)
            .Append(" #")
            .Append(selected.Id)
            .Append("「")
            .Append(subject)
            .Append('」');
        if (
            selected.Status == TodoStatus.InProgress
            && !string.IsNullOrWhiteSpace(selected.ActiveForm)
        )
            sb.Append("（")
                .Append(Truncate(selected.ActiveForm.ReplaceLineEndings(" ").Trim(), 80))
                .Append('）');
        if (selected.Status == TodoStatus.Pending && IsBlocked(selected))
            sb.Append("；注意：该项仍被未完成任务阻塞，先解除阻塞");
        sb.Append("；只在 Scope 范围内推进，完成后立即用 todo_write 将 #")
            .Append(selected.Id)
            .Append(" 标记为 completed。");
        return sb.ToString();
    }

    private static string ApplyTodoNext(
        string summary,
        IReadOnlyList<TodoItem>? todos,
        CompactionTaskScope? scope
    )
    {
        var next = BuildTodoNext(todos, scope);
        return next is null ? summary : ReplaceSection(summary, NextHeading, next);
    }

    /// <summary>
    /// 摘要生成后的最后一道确定性闸门：Scope/Progress/Where 以本轮事实为准；
    /// Next 为空、越界或与 Progress 下一章矛盾时统一用 BuildSafeNext 修复。
    /// </summary>
    internal static string EnforceTaskScope(
        string summary,
        CompactionTaskScope? scope,
        string? previousSummary,
        CompactionTaskProgress? progress = null,
        string? baseCheckpoint = null
    )
    {
        if (scope is null)
            return summary;

        var withScope = ReplaceSection(summary, ScopeHeading, scope.FormatBody());
        if (progress is not null)
            withScope = ReplaceSection(withScope, ProgressHeading, progress.FormatBody());

        // Where 由小说状态快照生成；AI 摘要里的全局前沿不能覆盖任务焦点。
        var baseWhere = baseCheckpoint is null
            ? null
            : ExtractSection(baseCheckpoint, WhereHeading);
        if (!string.IsNullOrWhiteSpace(baseWhere))
            withScope = ReplaceSection(withScope, WhereHeading, baseWhere);

        var next = ExtractSection(withScope, NextHeading);
        var previousNext = previousSummary is null
            ? null
            : ExtractSection(previousSummary, NextHeading);

        // Progress 给出下一章时，Next 必须指向同一章；范围内但与进度矛盾的 Next
        // （例如已编辑的第21章）与空/越界 Next 一样，统一用 BuildSafeNext 修复。
        var progressNextOrder =
            progress?.NextOrder is { } nextOrder
            && scope.Chapters.Any(chapter => chapter.Order == nextOrder)
                ? nextOrder
                : (int?)null;
        var nextMatchesProgress =
            progressNextOrder is not int expected
            || scope.ReferencedChapterOrders(next).Contains(expected);

        if (
            string.IsNullOrWhiteSpace(next)
            || !scope.IsNextWithinScope(next)
            || !nextMatchesProgress
        )
        {
            withScope = ReplaceSection(
                withScope,
                NextHeading,
                scope.BuildSafeNext(previousNext, progress)
            );
        }
        return withScope;
    }

    private static bool IsUnrecorded(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;
        var trimmed = text.Trim();
        return string.Equals(trimmed, CompactionTaskScope.Unrecorded, StringComparison.Ordinal)
            || string.Equals(trimmed, CompactionTaskProgress.Unrecorded, StringComparison.Ordinal);
    }

    /// <summary>
    /// 用 body 替换指定二级标题的整段内容；标题不存在时追加到末尾。
    /// 同时清掉重复的同名标题，避免多轮 compact 堆叠出多个 Scope/Next。
    /// </summary>
    private static string ReplaceSection(string markdown, string heading, string body)
    {
        var normalized = (markdown ?? "").ReplaceLineEndings("\n").TrimEnd();
        var bodyText = (body ?? "").ReplaceLineEndings("\n").Trim();
        var lines = normalized.Length == 0 ? new List<string>() : normalized.Split('\n').ToList();
        var output = new List<string>(lines.Count + 4);
        var inserted = false;
        var skipping = false;

        foreach (var line in lines)
        {
            var isHeading = line.StartsWith("## ", StringComparison.Ordinal);
            if (string.Equals(line.TrimEnd(), heading, StringComparison.Ordinal))
            {
                if (!inserted)
                {
                    if (output.Count > 0 && output[^1].Length != 0)
                        output.Add("");
                    output.Add(heading);
                    if (bodyText.Length > 0)
                        output.AddRange(bodyText.Split('\n'));
                    inserted = true;
                }
                // 同名标题只保留第一次替换结果；后续重复标题及其内容一并删除。
                skipping = true;
                continue;
            }

            if (skipping)
            {
                if (isHeading)
                    skipping = false;
                else
                    continue;
            }

            output.Add(line);
        }

        if (!inserted)
        {
            while (output.Count > 0 && output[^1].Length == 0)
                output.RemoveAt(output.Count - 1);
            if (output.Count > 0)
                output.Add("");
            output.Add(heading);
            if (bodyText.Length > 0)
                output.AddRange(bodyText.Split('\n'));
        }

        return string.Join('\n', output).TrimEnd();
    }

    private static string Truncate(string text, int maxLen) =>
        text.Length <= maxLen ? text : text[..maxLen] + "…";
}
