using Core.Shared;
using Core.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Core.Interactions;

/// <summary>
/// Core 的唯一交互状态机。
///
/// 职责：
/// - 创建/认领/提交/取消/过期交互；
/// - 保证同一交互最多一个展示宿主、一次有效结果；
/// - 向宿主广播不可变快照（UI 只渲染，不写状态）；
/// - 为工具/Hook 提供等待答案的 Core 原语。
///
/// 非职责：
/// - 不持有 UI 类型；
/// - 不执行窗口/终端渲染；
/// - 不把变体业务分支下发给 UI（只产出 <see cref="VariantEffect"/>）。
/// </summary>
public sealed class InteractionHub
{
    private readonly object _gate = new();
    private readonly Dictionary<string, InteractionRecord> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _activeBySource = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _generationBySource = new(StringComparer.Ordinal);
    private readonly ILogger _logger;
    private long _revisionSeed;
    private long _questionIdSeed;

    public InteractionHub(ILogger<InteractionHub>? logger = null)
    {
        _logger = logger ?? NullLogger<InteractionHub>.Instance;
    }

    /// <summary>当前 turn 的上下文。宿主在运行 Agent 前设置；空值只用于测试/无宿主场景。</summary>
    public InteractionContext CurrentContext { get; set; } = InteractionContext.Unscoped;

    /// <summary>任何交互状态变化（锁外广播）。处理器只能入队，不能阻塞/重入 Core。</summary>
    public event Action<InteractionSnapshot>? Changed;

    public InteractionSnapshot CreateQuestionSet(
        InteractionContext context,
        IReadOnlyList<QuestionDef> definitions
    )
    {
        ArgumentNullException.ThrowIfNull(definitions);
        if (definitions.Count is < 1 or > 4)
            throw new ArgumentException("问题组必须包含 1-4 个问题", nameof(definitions));

        var now = DateTimeOffset.UtcNow;
        InteractionSnapshot snapshot;
        lock (_gate)
        {
            var id = "q" + Interlocked.Increment(ref _questionIdSeed).ToString("x");
            var slots = new List<QuestionSlot>(definitions.Count);
            var tcs = new Dictionary<string, TaskCompletionSource<string[]>>(
                StringComparer.Ordinal
            );
            foreach (var def in definitions)
            {
                var questionId = "q" + Interlocked.Increment(ref _questionIdSeed).ToString("x");
                var question = new PendingQuestion
                {
                    Id = questionId,
                    Header = def.Header,
                    Question = def.Question,
                    Options = def.Options.ToArray(),
                    MultiSelect = def.MultiSelect,
                    CreatedAt = now.UtcDateTime,
                };
                slots.Add(new QuestionSlot(question));
                tcs[questionId] = new TaskCompletionSource<string[]>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
            }

            var sourceKey = "question:" + id;
            var record = new InteractionRecord
            {
                Id = id,
                Kind = InteractionKind.QuestionSet,
                SourceKey = sourceKey,
                SessionId = context.SessionId ?? "",
                NovelId = context.NovelId,
                OwnerHost = string.IsNullOrWhiteSpace(context.HostId) ? null : context.HostId,
                State = InteractionState.Available,
                Revision = Interlocked.Increment(ref _revisionSeed),
                CreatedAt = now,
                UpdatedAt = now,
                Questions = slots,
                AnswerTcs = tcs,
            };

            _records[id] = record;
            _activeBySource[SourceMapKey(record.SessionId, record.SourceKey)] = id;
            snapshot = BuildSnapshot(record);
        }

        Raise(snapshot);
        return snapshot;
    }

    /// <summary>
    /// 按 source key 幂等创建/复用变体审阅交互。
    /// 同一 VariantSetId 同时最多一个活动交互；重复 ToolExecutionEnd 不会开第二个。
    /// </summary>
    public InteractionSnapshot CreateOrGetVariantReview(
        InteractionContext context,
        ExploreDetails details
    )
    {
        ArgumentNullException.ThrowIfNull(details);
        if (details.Variants.Count == 0 || details.Variants.All(v => v.IsFailed))
            throw new ArgumentException("没有可审阅的变体", nameof(details));

        var sourceKey = BuildVariantSourceKey(details);
        var mapKey = SourceMapKey(context.SessionId ?? "", sourceKey);
        InteractionSnapshot snapshot;
        var created = false;
        lock (_gate)
        {
            if (_activeBySource.TryGetValue(mapKey, out var existingId))
            {
                var existing = _records[existingId];
                if (!existing.IsTerminal)
                {
                    snapshot = BuildSnapshot(existing);
                    return snapshot;
                }
            }

            var now = DateTimeOffset.UtcNow;
            var generation = _generationBySource.GetValueOrDefault(mapKey) + 1;
            _generationBySource[mapKey] = generation;
            var id = "v" + Guid.NewGuid().ToString("N")[..12];
            var record = new InteractionRecord
            {
                Id = id,
                Kind = InteractionKind.VariantReview,
                SourceKey = sourceKey,
                SessionId = context.SessionId ?? "",
                NovelId = context.NovelId,
                OwnerHost = string.IsNullOrWhiteSpace(context.HostId) ? null : context.HostId,
                State = InteractionState.Available,
                Revision = Interlocked.Increment(ref _revisionSeed),
                CreatedAt = now,
                UpdatedAt = now,
                Explore = CopyExplore(details),
                Generation = generation,
                EffectState = VariantEffectState.None,
            };
            _records[id] = record;
            _activeBySource[mapKey] = id;
            snapshot = BuildSnapshot(record);
            created = true;
        }

        if (created)
            Raise(snapshot);
        return snapshot;
    }

    public InteractionSnapshot? Get(string id)
    {
        lock (_gate)
            return _records.TryGetValue(id, out var record) ? BuildSnapshot(record) : null;
    }

    /// <summary>当前活动交互（Available/Claimed）；UI 启动时用它做初始同步。</summary>
    public IReadOnlyList<InteractionSnapshot> ListOpen(string? sessionId = null)
    {
        lock (_gate)
        {
            return _records
                .Values.Where(r => !r.IsTerminal)
                .Where(r => string.IsNullOrEmpty(sessionId) || r.SessionId == sessionId)
                .OrderBy(r => r.CreatedAt)
                .Select(BuildSnapshot)
                .ToList();
        }
    }

    /// <summary>当前 session 的未回答问题（供 get_pending_questions / 兼容读取）。</summary>
    public IReadOnlyList<PendingQuestion> GetPendingQuestions(string? sessionId = null)
    {
        lock (_gate)
        {
            return _records
                .Values.Where(r => r.Kind == InteractionKind.QuestionSet && !r.IsTerminal)
                .Where(r => string.IsNullOrEmpty(sessionId) || r.SessionId == sessionId)
                .SelectMany(r => r.Questions!)
                .Where(s => s.Outcome == QuestionOutcome.Unanswered)
                .Select(s => s.Question)
                .OrderBy(q => q.CreatedAt)
                .ToList();
        }
    }

    public SubmitStatus TryClaim(string id, string hostId, out InteractionSnapshot? snapshot)
    {
        snapshot = null;
        if (string.IsNullOrWhiteSpace(hostId))
            return SubmitStatus.Invalid;

        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var record))
                return SubmitStatus.NotFound;
            if (
                record.OwnerHost is { Length: > 0 } owner
                && !string.Equals(owner, hostId, StringComparison.Ordinal)
            )
                return SubmitStatus.NotOwner;
            if (record.State == InteractionState.Claimed)
            {
                snapshot = BuildSnapshot(record);
                return record.Claim?.HostId == hostId
                    ? SubmitStatus.AlreadyClaimed
                    : SubmitStatus.AlreadyClaimed;
            }
            if (record.IsTerminal)
            {
                snapshot = BuildSnapshot(record);
                return record.State == InteractionState.Expired
                    ? SubmitStatus.Expired
                    : SubmitStatus.AlreadyResolved;
            }

            record.Claim = new InteractionClaim(
                hostId,
                Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow
            );
            record.State = InteractionState.Claimed;
            Touch(record);
            snapshot = BuildSnapshot(record);
        }

        Raise(snapshot);
        return SubmitStatus.Accepted;
    }

    public SubmitResult SubmitQuestionAnswer(
        string id,
        string questionId,
        IReadOnlyList<string> answers,
        string leaseId
    )
    {
        InteractionSnapshot? snapshot = null;
        TaskCompletionSource<string[]>? completed = null;
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var record))
                return new SubmitResult(SubmitStatus.NotFound);
            if (record.Kind != InteractionKind.QuestionSet)
                return new SubmitResult(SubmitStatus.Invalid);
            var claimStatus = ValidateClaim(record, leaseId);
            if (claimStatus is not SubmitStatus.Accepted)
                return new SubmitResult(claimStatus, BuildSnapshot(record));
            if (record.State == InteractionState.Expired)
                return new SubmitResult(SubmitStatus.Expired, BuildSnapshot(record));
            if (
                record.State == InteractionState.Resolved
                || record.State == InteractionState.Cancelled
            )
                return new SubmitResult(SubmitStatus.AlreadyResolved, BuildSnapshot(record));

            var slot = record.Questions?.FirstOrDefault(q => q.Question.Id == questionId);
            if (slot is null)
                return new SubmitResult(SubmitStatus.Invalid);
            if (slot.Outcome != QuestionOutcome.Unanswered)
                return new SubmitResult(SubmitStatus.AlreadyResolved, BuildSnapshot(record));

            var cleaned = answers.Select(a => a?.Trim() ?? "").Where(a => a.Length > 0).ToArray();
            if (cleaned.Length == 0)
                return new SubmitResult(SubmitStatus.Invalid, BuildSnapshot(record));
            if (!slot.Question.MultiSelect && cleaned.Length > 1)
                return new SubmitResult(SubmitStatus.Invalid, BuildSnapshot(record));

            slot.Answers = cleaned;
            slot.Outcome = QuestionOutcome.Answered;
            slot.Question = slot.Question with
            {
                SelectedAnswers = cleaned,
                AnsweredAt = DateTime.UtcNow,
            };
            completed = record.AnswerTcs?.GetValueOrDefault(questionId);
            if (record.IsQuestionComplete())
            {
                record.State = InteractionState.Resolved;
                record.Claim = null;
            }
            Touch(record);
            snapshot = BuildSnapshot(record);
        }

        completed?.TrySetResult(answers.ToArray());
        Raise(snapshot!);
        return new SubmitResult(SubmitStatus.Accepted, snapshot);
    }

    /// <summary>取消当前问题，继续下一题；完成该题 waiter。</summary>
    public SubmitResult CancelQuestion(
        string id,
        string questionId,
        string leaseId,
        string reason = ""
    )
    {
        InteractionSnapshot? snapshot = null;
        TaskCompletionSource<string[]>? completed = null;
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var record))
                return new SubmitResult(SubmitStatus.NotFound);
            if (record.Kind != InteractionKind.QuestionSet)
                return new SubmitResult(SubmitStatus.Invalid);
            var claimStatus = ValidateClaim(record, leaseId);
            if (claimStatus is not SubmitStatus.Accepted)
                return new SubmitResult(claimStatus, BuildSnapshot(record));
            if (
                record.State
                is InteractionState.Resolved
                    or InteractionState.Cancelled
                    or InteractionState.Expired
            )
                return new SubmitResult(SubmitStatus.AlreadyResolved, BuildSnapshot(record));

            var slot = record.Questions?.FirstOrDefault(q => q.Question.Id == questionId);
            if (slot is null)
                return new SubmitResult(SubmitStatus.Invalid);
            if (slot.Outcome != QuestionOutcome.Unanswered)
                return new SubmitResult(SubmitStatus.AlreadyResolved, BuildSnapshot(record));

            slot.Outcome = QuestionOutcome.Cancelled;
            slot.Answers = [AskUserReplyFormatter.CancelledAnswer];
            slot.Question = slot.Question with { SelectedAnswers = slot.Answers };
            completed = record.AnswerTcs?.GetValueOrDefault(questionId);
            if (record.IsQuestionComplete())
            {
                record.State = InteractionState.Resolved;
                record.Claim = null;
            }
            Touch(record);
            snapshot = BuildSnapshot(record);
        }

        completed?.TrySetResult([AskUserReplyFormatter.CancelledAnswer]);
        Raise(snapshot!);
        return new SubmitResult(SubmitStatus.Accepted, snapshot);
    }

    /// <summary>取消整组（turn abort / session switch）；cancelWaiters=true 让工具走 OCE 路径。</summary>
    public SubmitResult Cancel(string id, string reason, bool cancelWaiters, string? leaseId = null)
    {
        InteractionSnapshot? snapshot = null;
        var waiters = new List<TaskCompletionSource<string[]>>();
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var record))
                return new SubmitResult(SubmitStatus.NotFound);
            if (record.IsTerminal)
                return new SubmitResult(SubmitStatus.AlreadyResolved, BuildSnapshot(record));
            if (!string.IsNullOrEmpty(leaseId))
            {
                var claimStatus = ValidateClaim(record, leaseId);
                if (claimStatus is not SubmitStatus.Accepted)
                    return new SubmitResult(claimStatus, BuildSnapshot(record));
            }

            record.State = InteractionState.Cancelled;
            record.Claim = null;
            if (record.Questions is not null)
            {
                foreach (
                    var slot in record.Questions.Where(s => s.Outcome == QuestionOutcome.Unanswered)
                )
                {
                    slot.Outcome = QuestionOutcome.Cancelled;
                    slot.Answers = [AskUserReplyFormatter.CancelledAnswer];
                    slot.Question = slot.Question with { SelectedAnswers = slot.Answers };
                    var tcs = record.AnswerTcs?.GetValueOrDefault(slot.Question.Id);
                    if (tcs is not null)
                        waiters.Add(tcs);
                }
            }
            Touch(record);
            snapshot = BuildSnapshot(record);
        }

        foreach (var tcs in waiters)
        {
            if (cancelWaiters)
                tcs.TrySetCanceled();
            else
                tcs.TrySetResult([AskUserReplyFormatter.CancelledAnswer]);
        }
        Raise(snapshot!);
        return new SubmitResult(SubmitStatus.Accepted, snapshot);
    }

    /// <summary>超时：终态 Expired，waiter 走异常路径（云端裁决据此走保守分支）。</summary>
    public SubmitResult Expire(string id, string reason)
    {
        InteractionSnapshot? snapshot = null;
        var waiters = new List<TaskCompletionSource<string[]>>();
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var record))
                return new SubmitResult(SubmitStatus.NotFound);
            if (record.IsTerminal)
                return new SubmitResult(SubmitStatus.AlreadyResolved, BuildSnapshot(record));

            record.State = InteractionState.Expired;
            record.Claim = null;
            if (record.Questions is not null)
            {
                foreach (
                    var slot in record.Questions.Where(s => s.Outcome == QuestionOutcome.Unanswered)
                )
                {
                    slot.Outcome = QuestionOutcome.Expired;
                    var tcs = record.AnswerTcs?.GetValueOrDefault(slot.Question.Id);
                    if (tcs is not null)
                        waiters.Add(tcs);
                }
            }
            Touch(record);
            snapshot = BuildSnapshot(record);
        }

        foreach (var tcs in waiters)
            tcs.TrySetCanceled();
        Raise(snapshot!);
        return new SubmitResult(SubmitStatus.Accepted, snapshot);
    }

    public SubmitStatus Release(string id, string leaseId)
    {
        InteractionSnapshot? snapshot = null;
        SubmitStatus status;
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var record))
                return SubmitStatus.NotFound;
            if (record.State != InteractionState.Claimed)
                return record.IsTerminal ? SubmitStatus.AlreadyResolved : SubmitStatus.NotClaimed;
            if (record.Claim?.LeaseId != leaseId)
                return SubmitStatus.LeaseLost;
            record.Claim = null;
            record.State = InteractionState.Available;
            Touch(record);
            snapshot = BuildSnapshot(record);
            status = SubmitStatus.Accepted;
        }

        Raise(snapshot);
        return status;
    }

    public SubmitResult SubmitVariantDecision(
        string id,
        VariantReviewDecision decision,
        string leaseId
    )
    {
        InteractionSnapshot? snapshot = null;
        VariantEffect? effect = null;
        SubmitStatus status;
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var record))
                return new SubmitResult(SubmitStatus.NotFound);
            if (record.Kind != InteractionKind.VariantReview)
                return new SubmitResult(SubmitStatus.Invalid);
            var claimStatus = ValidateClaim(record, leaseId);
            if (claimStatus is not SubmitStatus.Accepted)
                return new SubmitResult(claimStatus, BuildSnapshot(record));
            if (
                record.State
                is InteractionState.Resolved
                    or InteractionState.Cancelled
                    or InteractionState.Expired
            )
                return new SubmitResult(SubmitStatus.AlreadyResolved, BuildSnapshot(record));

            effect = BuildVariantEffect(record, decision);
            if (effect is null)
                return new SubmitResult(SubmitStatus.Invalid, BuildSnapshot(record));

            record.Decision = decision;
            record.Effect = effect;
            record.EffectState = VariantEffectState.Pending;
            record.State = InteractionState.Resolved;
            record.Claim = null;
            Touch(record);
            snapshot = BuildSnapshot(record);
            status = SubmitStatus.Accepted;
        }

        Raise(snapshot);
        return new SubmitResult(status, snapshot, effect);
    }

    public SubmitStatus SetVariantEffectState(
        string id,
        string effectId,
        VariantEffectState state,
        string? failureReason = null
    )
    {
        InteractionSnapshot? snapshot = null;
        SubmitStatus status;
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var record))
                return SubmitStatus.NotFound;
            if (record.Kind != InteractionKind.VariantReview || record.Effect is null)
                return SubmitStatus.Invalid;
            if (!string.Equals(record.Effect.EffectId, effectId, StringComparison.Ordinal))
                return SubmitStatus.Invalid;

            record.EffectState = state;
            record.Effect = record.Effect with { State = state, FailureReason = failureReason };
            Touch(record);
            snapshot = BuildSnapshot(record);
            status = SubmitStatus.Accepted;
        }

        Raise(snapshot);
        return status;
    }

    /// <summary>
    /// 重新打开最近变体组：活动存在则复用；否则用同一 source 创建新 generation。
    /// 持久化 set/decision 由宿主 journal 负责；hub 不写 JSONL。
    /// </summary>
    public InteractionSnapshot? ReopenVariantReview(
        InteractionContext context,
        ExploreDetails details
    )
    {
        if (details.Variants.Count == 0 || details.Variants.All(v => v.IsFailed))
            return null;
        return CreateOrGetVariantReview(context, details);
    }

    /// <summary>取消给定 session 的所有未终态交互；用于会话/分支切换。</summary>
    public void CancelSession(string sessionId, string reason)
    {
        List<InteractionSnapshot> snapshots = [];
        List<TaskCompletionSource<string[]>> waiters = [];
        lock (_gate)
        {
            foreach (
                var record in _records
                    .Values.Where(r => r.SessionId == sessionId && !r.IsTerminal)
                    .ToList()
            )
            {
                record.State = InteractionState.Cancelled;
                record.Claim = null;
                if (record.Questions is not null)
                {
                    foreach (
                        var slot in record.Questions.Where(s =>
                            s.Outcome == QuestionOutcome.Unanswered
                        )
                    )
                    {
                        slot.Outcome = QuestionOutcome.Cancelled;
                        slot.Answers = [AskUserReplyFormatter.CancelledAnswer];
                        if (record.AnswerTcs?.GetValueOrDefault(slot.Question.Id) is { } tcs)
                            waiters.Add(tcs);
                    }
                }
                Touch(record);
                snapshots.Add(BuildSnapshot(record));
            }
        }

        foreach (var tcs in waiters)
            tcs.TrySetCanceled();
        foreach (var snap in snapshots)
            Raise(snap);
    }

    /// <summary>取消某 host 拥有的全部交互；用于 adapter/通道断开、进程退出。</summary>
    public void CancelHost(string hostId)
    {
        if (string.IsNullOrWhiteSpace(hostId))
            return;
        List<InteractionSnapshot> snapshots = [];
        List<TaskCompletionSource<string[]>> waiters = [];
        lock (_gate)
        {
            foreach (
                var record in _records
                    .Values.Where(r => r.Claim?.HostId == hostId && !r.IsTerminal)
                    .ToList()
            )
            {
                record.State = InteractionState.Cancelled;
                record.Claim = null;
                if (record.Questions is not null)
                {
                    foreach (
                        var slot in record.Questions.Where(s =>
                            s.Outcome == QuestionOutcome.Unanswered
                        )
                    )
                    {
                        slot.Outcome = QuestionOutcome.Cancelled;
                        slot.Answers = [AskUserReplyFormatter.CancelledAnswer];
                        if (record.AnswerTcs?.GetValueOrDefault(slot.Question.Id) is { } tcs)
                            waiters.Add(tcs);
                    }
                }
                Touch(record);
                snapshots.Add(BuildSnapshot(record));
            }
        }

        foreach (var tcs in waiters)
            tcs.TrySetCanceled();
        foreach (var snap in snapshots)
            Raise(snap);
    }

    /// <summary>清理某 host 的孤儿 claim（adapter 重启），不改变问题和答案。</summary>
    public void ReleaseHost(string hostId)
    {
        if (string.IsNullOrWhiteSpace(hostId))
            return;
        List<InteractionSnapshot> snapshots = [];
        lock (_gate)
        {
            foreach (
                var record in _records
                    .Values.Where(r => r.Claim?.HostId == hostId && !r.IsTerminal)
                    .ToList()
            )
            {
                record.Claim = null;
                record.State = InteractionState.Available;
                Touch(record);
                snapshots.Add(BuildSnapshot(record));
            }
        }
        foreach (var snap in snapshots)
            Raise(snap);
    }

    public async Task<string[]> WaitForQuestionAnswerAsync(
        string interactionId,
        string questionId,
        CancellationToken ct = default
    )
    {
        TaskCompletionSource<string[]> tcs;
        QuestionOutcome outcome;
        string[]? immediate = null;
        lock (_gate)
        {
            if (!_records.TryGetValue(interactionId, out var record))
                throw new InvalidOperationException($"交互不存在: {interactionId}");
            if (record.Kind != InteractionKind.QuestionSet)
                throw new InvalidOperationException($"交互不是问题组: {interactionId}");
            var slot = record.Questions?.FirstOrDefault(q => q.Question.Id == questionId);
            if (slot is null)
                throw new InvalidOperationException($"问题不存在: {questionId}");
            outcome = slot.Outcome;
            if (outcome == QuestionOutcome.Answered)
            {
                immediate = slot.Answers ?? [];
                tcs = null!;
            }
            else if (outcome is QuestionOutcome.Cancelled or QuestionOutcome.Expired)
            {
                tcs = null!;
            }
            else
            {
                tcs = record.AnswerTcs![questionId];
            }
        }

        if (immediate is not null)
            return immediate;
        if (outcome is QuestionOutcome.Cancelled or QuestionOutcome.Expired)
            throw new TaskCanceledException($"问题已结束: {outcome}");

        // 调用方取消只应取消本次等待，不能污染共享 TCS；否则超时后的留存问题无法再被等待。
        return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    // ── 内部 ──

    private static SubmitStatus ValidateClaim(InteractionRecord record, string leaseId)
    {
        if (record.State == InteractionState.Expired)
            return SubmitStatus.Expired;
        if (record.State is InteractionState.Resolved or InteractionState.Cancelled)
            return SubmitStatus.AlreadyResolved;
        if (record.State != InteractionState.Claimed || record.Claim is null)
            return SubmitStatus.NotClaimed;
        return record.Claim.LeaseId == leaseId ? SubmitStatus.Accepted : SubmitStatus.LeaseLost;
    }

    private VariantEffect? BuildVariantEffect(
        InteractionRecord record,
        VariantReviewDecision decision
    )
    {
        var explore = record.Explore!;
        var variant = string.IsNullOrWhiteSpace(decision.VariantId)
            ? null
            : explore.Variants.FirstOrDefault(v =>
                string.Equals(v.Id, decision.VariantId, StringComparison.Ordinal)
            );
        if (decision.Action is VariantReviewAction.Apply or VariantReviewAction.Refine)
        {
            if (variant is null || variant.IsFailed || string.IsNullOrWhiteSpace(variant.Content))
                return null;
        }

        var variantId = variant?.Id ?? decision.VariantId ?? "";
        var label = variant?.Label ?? decision.VariantLabel ?? "";
        var content = variant?.Content ?? decision.VariantContent ?? "";
        var effectId = Guid.NewGuid().ToString("N")[..12];

        if (decision.Action == VariantReviewAction.Dismiss)
            return new VariantEffect(
                effectId,
                VariantEffectKind.Dismiss,
                variantId,
                label,
                content,
                explore.ExploreType,
                explore.Prompt,
                explore.ApplyTarget,
                "",
                false
            );

        if (decision.Action == VariantReviewAction.Refine)
            return new VariantEffect(
                effectId,
                VariantEffectKind.PrefillRefinement,
                variantId,
                label,
                content,
                explore.ExploreType,
                explore.Prompt,
                explore.ApplyTarget,
                "",
                false
            );

        if (explore.ApplyTarget is null)
        {
            if (
                explore.ExploreType is "character" or "plot" or "outline" or "style"
                && !string.IsNullOrWhiteSpace(content)
            )
            {
                var typeName = explore.ExploreType switch
                {
                    "character" => "角色",
                    "plot" => "情节线",
                    "outline" => "大纲",
                    _ => "创作内容",
                };
                var variantXml = VariantXml.Wrap(content, variantId, label, explore.ExploreType);
                var prompt =
                    $"用户从探索结果中选择了变体「{label}」——一个{typeName}方案。\n\n"
                    + variantXml
                    + "\n\n请根据以上内容，使用相应的工具（如 batch_create_characters）创建结构化实体。"
                    + "如果内容为大纲/风格描述，直接将其作为参考存入上下文，不需要创建实体。";
                return new VariantEffect(
                    effectId,
                    VariantEffectKind.SendExtractionPrompt,
                    variantId,
                    label,
                    content,
                    explore.ExploreType,
                    explore.Prompt,
                    null,
                    prompt,
                    false
                );
            }

            return new VariantEffect(
                effectId,
                VariantEffectKind.AppendToContext,
                variantId,
                label,
                content,
                explore.ExploreType,
                explore.Prompt,
                null,
                "",
                false
            );
        }

        return new VariantEffect(
            effectId,
            VariantEffectKind.WriteChapter,
            variantId,
            label,
            content,
            explore.ExploreType,
            explore.Prompt,
            explore.ApplyTarget,
            "",
            RequiresOverwriteConfirmation: !string.IsNullOrEmpty(explore.ApplyTarget.ChapterId)
        );
    }

    private InteractionSnapshot BuildSnapshot(InteractionRecord record)
    {
        QuestionSetSnapshot? questions = null;
        if (record.Questions is not null)
        {
            questions = new QuestionSetSnapshot(
                record
                    .Questions.Select(s => new QuestionSlotSnapshot(s.Question, s.Outcome))
                    .ToList()
            );
        }

        VariantReviewSnapshot? variant = null;
        if (record.Kind == InteractionKind.VariantReview && record.Explore is not null)
        {
            variant = new VariantReviewSnapshot(
                record.Explore.VariantSetId,
                record.Generation,
                record.Explore,
                record.Decision,
                record.Effect,
                record.EffectState
            );
        }

        return new InteractionSnapshot(
            record.Id,
            record.Kind,
            record.SourceKey,
            record.SessionId,
            record.NovelId,
            record.OwnerHost,
            record.State,
            record.Revision,
            record.Claim,
            questions,
            variant,
            record.Confirmation,
            record.CreatedAt,
            record.UpdatedAt
        );
    }

    private void Touch(InteractionRecord record)
    {
        record.Revision = Interlocked.Increment(ref _revisionSeed);
        record.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private void Raise(InteractionSnapshot? snapshot)
    {
        if (snapshot is null)
            return;
        var handlers = Changed;
        if (handlers is null)
            return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<InteractionSnapshot>>())
        {
            try
            {
                handler(snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "InteractionChanged 处理器失败: {InteractionId}",
                    snapshot.Id
                );
            }
        }
    }

    private static string SourceMapKey(string sessionId, string sourceKey) =>
        (sessionId ?? "") + "|" + sourceKey;

    private static string BuildVariantSourceKey(ExploreDetails details)
    {
        if (!string.IsNullOrWhiteSpace(details.VariantSetId))
            return "variant:" + details.VariantSetId;

        var fingerprint = string.Join(
            "|",
            details.ExploreType,
            details.Prompt,
            details.ApplyTarget?.NovelId,
            details.ApplyTarget?.ChapterId,
            details.ApplyTarget?.Title,
            details.ApplyTarget?.Order,
            string.Join(",", details.Variants.Select(v => v.Id + ":" + v.Label))
        );
        var hash = Convert
            .ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(fingerprint)
                )
            )[..12]
            .ToLowerInvariant();
        return "variant:" + hash;
    }

    private static ExploreDetails CopyExplore(ExploreDetails details) =>
        new()
        {
            VariantSetId = details.VariantSetId,
            ExploreType = details.ExploreType,
            Prompt = details.Prompt,
            Variants = details.Variants.ToList(),
            ApplyTarget = details.ApplyTarget,
        };

    private sealed class InteractionRecord
    {
        public required string Id { get; init; }
        public required InteractionKind Kind { get; init; }
        public required string SourceKey { get; init; }
        public required string SessionId { get; init; }
        public string? NovelId { get; init; }
        public string? OwnerHost { get; init; }
        public InteractionState State { get; set; }
        public long Revision { get; set; }
        public InteractionClaim? Claim { get; set; }
        public required DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; set; }
        public List<QuestionSlot>? Questions { get; init; }
        public Dictionary<string, TaskCompletionSource<string[]>>? AnswerTcs { get; init; }
        public ExploreDetails? Explore { get; init; }
        public VariantReviewDecision? Decision { get; set; }
        public VariantEffect? Effect { get; set; }
        public VariantEffectState EffectState { get; set; }
        public int Generation { get; init; }
        public ConfirmationSnapshot? Confirmation { get; init; }

        public bool IsTerminal =>
            State
                is InteractionState.Resolved
                    or InteractionState.Cancelled
                    or InteractionState.Expired;

        public bool IsQuestionComplete() =>
            Questions is { Count: > 0 }
            && Questions.All(q =>
                q.Outcome is QuestionOutcome.Answered or QuestionOutcome.Cancelled
            );
    }

    private sealed class QuestionSlot
    {
        public QuestionSlot(PendingQuestion question)
        {
            Question = question;
        }

        public PendingQuestion Question { get; set; }
        public QuestionOutcome Outcome { get; set; } = QuestionOutcome.Unanswered;
        public string[]? Answers { get; set; }
    }
}
