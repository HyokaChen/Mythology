using System.Text;
using Core.Data;
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
    string? ParentEntryId = null
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
    IReadOnlyList<string> CompressedMessageIds
);

/// <summary>
/// TUI / Desktop 共用的上下文压缩实现：切点、有效压缩门槛、5W1H 摘要、compact 落盘、
/// Agent 内存替换与稳定性基线重置都在 Core。宿主只负责触发时机、计数器和 UI 呈现。
/// </summary>
public sealed class CompactionEngine(
    Agent agent,
    FileSessionStore store,
    INovelStore novelStore,
    ILogger log
)
{
    public const string TooSmallMessage = "压缩范围太小（未达到有效压缩阈值），未写入会话";

    private static readonly TimeSpan AiSummaryTimeout = TimeSpan.FromSeconds(45);

    private static readonly string[] CheckpointHeadings =
    [
        "## Where / 写到哪里",
        "## Who / 谁出场",
        "## What / 发生了什么",
        "## Why / 为什么",
        "## When / 时间",
        "## How / 怎么写",
        "## Next / 下一步",
    ];

    private volatile bool _isCompacting;

    /// <summary>是否有压缩进行中（宿主 UI spinner/防重入单一事实源）。</summary>
    public bool IsCompacting => _isCompacting;

    public async Task<CompactionExecution> CompactAsync(
        CompactionRequest request,
        CancellationToken ct = default
    )
    {
        var messages = agent.State.Messages;
        if (_isCompacting)
        {
            log.LogDebug(
                "prepare compaction: rejected {Reason} request because compaction is in progress",
                request.Reason
            );
            return Failed(CompactionStatus.RejectedInProgress, messages.Count, "压缩进行中");
        }
        if (agent.ContextManagement is { Mode: ContextMode.Continuation })
        {
            log.LogDebug(
                "prepare compaction: rejected {Reason} request because context management mode is continuation",
                request.Reason
            );
            return Failed(CompactionStatus.RejectedWrongMode, messages.Count, "当前为接续模式");
        }

        _isCompacting = true;
        try
        {
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
            _isCompacting = false;
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
            || messages[cutIndex].Role != ChatMessageRoles.User
        )
        {
            log.LogWarning(
                "prepare compaction: no complete user turn can be compressed (keepRecent={Keep}, keepRecentTokens={Tokens}, cutIndex={Cut})",
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
        string novelSnapshot;
        try
        {
            novelSnapshot = await BuildNovelStateSnapshotAsync(
                request.NovelId,
                messages.Take(cutIndex).ToList(),
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
            novelSnapshot = "";
        }

        var summary = await GenerateCompactSummaryAsync(
            messages,
            cutIndex,
            previousSummary,
            novelSnapshot,
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
            compressedIds
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
            CompressedMessageIds: []
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
    /// 从尾部按 token 预算反扫切割点：至少保留预算与 minKeepMessages 条，再回退到 user 边界。
    /// 绝不切在 tool 消息上，保证工具结果跟随其 assistant tool call。
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

        while (cutIndex > 0 && messages[cutIndex].Role != ChatMessageRoles.User)
            cutIndex--;

        if (cutIndex < messages.Count && messages[cutIndex].Role != ChatMessageRoles.User)
        {
            for (var i = cutIndex + 1; i < messages.Count; i++)
            {
                if (messages[i].Role == ChatMessageRoles.User)
                {
                    cutIndex = i;
                    break;
                }
            }
        }

        return cutIndex <= 0 ? 0 : cutIndex;
    }

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
        CancellationToken ct
    )
    {
        const int aiSummaryMinCompressedTokens = 2_000;
        var compressedTokens = EstimateContextTokens(messages.Take(cutIndex).ToList());
        var baseCheckpoint = string.IsNullOrWhiteSpace(novelSnapshot)
            ? BuildConversationCheckpoint(messages, cutIndex)
            : novelSnapshot;
        if (compressedTokens >= aiSummaryMinCompressedTokens)
        {
            try
            {
                var aiSummary = await TryGenerateAiSummaryAsync(
                    messages,
                    cutIndex,
                    previousSummary,
                    novelSnapshot,
                    ct
                );
                if (aiSummary is not null)
                {
                    var normalized = NormalizeCheckpoint(aiSummary, baseCheckpoint);
                    return Truncate(MergeCheckpoint(normalized, previousSummary), 8_000);
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

        return Truncate(
            BuildCompactSummary(messages, cutIndex, previousSummary, novelSnapshot),
            8_000
        );
    }

    private async Task<string?> TryGenerateAiSummaryAsync(
        IReadOnlyList<ChatMessage> messages,
        int cutIndex,
        string? previousSummary,
        string novelSnapshot,
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

        var prompt = new StringBuilder();
        prompt.AppendLine(
            "请维护一份小说写作的 5W1H 检查点。它是压缩历史后唯一保留的上下文，必须让下一轮写作能直接继续。"
        );
        prompt.AppendLine("输出必须且只能使用以下 7 个标题（顺序固定，不得嵌套其它标题）：");
        prompt.AppendLine(string.Join('\n', CheckpointHeadings));
        prompt.AppendLine();
        prompt.AppendLine("要求：");
        prompt.AppendLine(
            "- Where 写到哪里：当前卷/章/场景、下一章；Who 谁出场；What 发生了什么；"
        );
        prompt.AppendLine("- Why 为什么写这些；When 故事内时间；How 怎么写（风格、节奏、约束）；");
        prompt.AppendLine("- Next 下一步必须是可执行的继续推进计划，不能只写“继续”。");
        prompt.AppendLine(
            "- 优先保留小说状态：写到哪里、谁出场、发生了什么、为什么、何时、怎么写。"
        );
        prompt.AppendLine("- 不得原文复述已有检查点，不得生成“压缩概览/之前的摘要/文件操作”标题，");
        prompt.AppendLine("- 不得用 read/write/edit/list/search 等工具调用结果代替剧情状态。");
        prompt.AppendLine("- 总长度不超过 8,000 字符。");
        prompt.AppendLine();

        prompt.AppendLine("## 当前小说状态（优先）");
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
            "你是小说写作的上下文检查点编辑器。只输出 5W1H 检查点，保留可执行的下一步。使用中文。"
        );
        conversation.AddUserMessage(prompt.ToString());

        using var summaryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        summaryCts.CancelAfter(AiSummaryTimeout);

        var summaryBuilder = new StringBuilder();
        await conversation.StreamResponse(
            token =>
            {
                summaryBuilder.Append(token);
            },
            summaryCts.Token
        );
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
            var role = msg.Role switch
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
        return Truncate(MergeCheckpoint(baseCheckpoint, previousSummary), 8_000);
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
        sb.AppendLine(CheckpointHeadings[0]);
        sb.AppendLine($"- {whereNote}");
        sb.AppendLine();
        sb.AppendLine(CheckpointHeadings[1]);
        sb.AppendLine("- 对话参与者：用户 / 助手");
        sb.AppendLine();
        sb.AppendLine(CheckpointHeadings[2]);
        if (events.Count == 0)
            sb.AppendLine("- 压缩区没有可提取的对话正文");
        else
            foreach (var line in events)
                sb.AppendLine($"- {line}");
        sb.AppendLine();
        sb.AppendLine(CheckpointHeadings[3]);
        sb.AppendLine($"- 用户目标：{(userText.Length > 0 ? userText : "未明确")}");
        sb.AppendLine();
        sb.AppendLine(CheckpointHeadings[4]);
        sb.AppendLine("- 故事内时间：未指定（无小说时间线）");
        sb.AppendLine();
        sb.AppendLine(CheckpointHeadings[5]);
        sb.AppendLine(
            $"- 无小说风格设定；按最近用户要求继续：{(userText.Length > 0 ? userText : "当前对话")}"
        );
        sb.AppendLine();
        sb.AppendLine(CheckpointHeadings[6]);
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
            var mergedAny = false;
            foreach (
                var heading in new[]
                {
                    CheckpointHeadings[3],
                    CheckpointHeadings[5],
                    CheckpointHeadings[6],
                }
            )
            {
                var previous = ExtractSection(previousSummary, heading);
                if (string.IsNullOrWhiteSpace(previous))
                    continue;
                mergedAny = true;
                sections[heading] = AppendMissingLines(sections[heading], previous, maxLines: 5);
            }

            if (!mergedAny)
            {
                var plain = StripHeadingLines(previousSummary);
                if (plain.Length > 0)
                {
                    var how = sections[CheckpointHeadings[5]];
                    var inherited = $"- 继承背景：{Truncate(plain, 1_000)}";
                    sections[CheckpointHeadings[5]] = string.IsNullOrEmpty(how)
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
                    userEvents.Add((i, $"用户：{Truncate(content, 120)}"));
            }
            else if (messages[i].Role == ChatMessageRoles.Assistant)
            {
                assistantEvents.Add((i, $"助手：{Truncate(content, 120)}"));
            }
        }

        return userEvents
            .TakeLast(3)
            .Concat(assistantEvents.TakeLast(3))
            .OrderBy(e => e.Index)
            .Select(e => e.Text)
            .ToList();
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
        if (string.IsNullOrWhiteSpace(novelId))
            return "";

        var structure = await novelStore.GetStructure(novelId, ct);
        if (structure is null || structure.Chapters.Count == 0)
            return BuildConversationCheckpoint(
                compressedMessages,
                compressedMessages.Count,
                "尚未建立章节（按最近对话判断）"
            );

        var orderedChapters = structure.Chapters.OrderBy(c => c.Order).ToList();
        var current = orderedChapters
            .Where(c => c.CharCount > 0)
            .OrderBy(c => c.Order)
            .LastOrDefault()
            ?? orderedChapters[0];
        var next = orderedChapters
            .Where(c => c.Order > current.Order && c.CharCount == 0)
            .OrderBy(c => c.Order)
            .FirstOrDefault()
            ?? orderedChapters.FirstOrDefault(c => c.Order > current.Order);
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
        if (!string.IsNullOrWhiteSpace(next?.Summary))
            nextParts.Add($"推进下一章《{next.Title}》：{Truncate(next.Summary, 200)}");
        if (openPlotlines.Count > 0)
            nextParts.Add(
                $"推进未回收情节线：{string.Join("、", openPlotlines.Take(3).Select(p => p.Name))}"
            );

        var sb = new StringBuilder();
        sb.AppendLine(CheckpointHeadings[0]);
        sb.AppendLine($"- 卷：{(volume is null ? "未分卷" : $"{volume.Name}（{volume.Order}）")}");
        sb.AppendLine(
            $"- 当前章：第{current.Order}章《{current.Title}》（{current.Status}，{current.CharCount} 字）"
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
            $"- 下一章：{(next is null ? "无下一章（继续当前章或新建）" : $"第{next.Order}章《{next.Title}》")}"
        );
        sb.AppendLine();

        sb.AppendLine(CheckpointHeadings[1]);
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

        sb.AppendLine(CheckpointHeadings[2]);
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

        sb.AppendLine(CheckpointHeadings[3]);
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

        sb.AppendLine(CheckpointHeadings[4]);
        sb.AppendLine(
            $"- 故事内时间：{(scene is null || string.IsNullOrWhiteSpace(scene.TimeDescription) ? "未指定" : scene.TimeDescription)}"
        );
        if (timelineLines.Count > 0)
            foreach (var line in timelineLines)
                sb.AppendLine($"- 时间线事件：{line}");
        else
            sb.AppendLine("- 时间线事件：无");
        sb.AppendLine();

        sb.AppendLine(CheckpointHeadings[5]);
        sb.AppendLine($"- 类型/风格：{structure.Novel.Genre} / {structure.Novel.WritingStyle}");
        sb.AppendLine(
            $"- 本章备注/节奏提示：{(string.IsNullOrWhiteSpace(current.Notes) ? "无" : Truncate(current.Notes, 300))}"
        );
        if (recentUserRequirements.Count > 0)
            sb.AppendLine($"- 最近用户写作要求：{string.Join("；", recentUserRequirements)}");
        else
            sb.AppendLine("- 最近用户写作要求：无");
        sb.AppendLine();

        sb.AppendLine(CheckpointHeadings[6]);
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

    private static string Truncate(string text, int maxLen) =>
        text.Length <= maxLen ? text : text[..maxLen] + "…";
}
