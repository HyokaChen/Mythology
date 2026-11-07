using Core.Shared;
using Core.Tools;

namespace Core.Interactions;

/// <summary>交互种类。封闭集合：不引入插件式工作流。</summary>
public enum InteractionKind
{
    QuestionSet,
    VariantReview,
    Confirmation,
}

/// <summary>交互生命周期。</summary>
public enum InteractionState
{
    /// <summary>已创建，等待某个宿主认领展示。</summary>
    Available,

    /// <summary>已被唯一宿主认领；只有持 lease 的宿主能提交结果。</summary>
    Claimed,

    /// <summary>已获得唯一结果。</summary>
    Resolved,

    /// <summary>被取消（用户取消 / turn abort / session switch）。</summary>
    Cancelled,

    /// <summary>超时或宿主失联。</summary>
    Expired,
}

/// <summary>单条问题的终态。取消不是答案字符串，必须显式区分。</summary>
public enum QuestionOutcome
{
    Unanswered,
    Answered,
    Cancelled,
    Expired,
}

/// <summary>提交结果的统一返回。UI 不应通过异常猜状态。</summary>
public enum SubmitStatus
{
    Accepted,
    AlreadyResolved,
    AlreadyClaimed,
    LeaseLost,
    NotClaimed,
    NotOwner,
    Expired,
    Invalid,
    JournalFailed,
    NotFound,
}

/// <summary>
/// 一轮（turn）的交互上下文。宿主在运行 Agent 前设置；
/// Core 用它决定 owner、session、novel、分支与 turn 归属。
/// </summary>
public sealed record InteractionContext(
    string HostId,
    string SessionId,
    string? NovelId = null,
    string? BranchId = null,
    string? TurnId = null
)
{
    public static InteractionContext Unscoped { get; } = new("", "", null, null, null);

    public bool HasSession => !string.IsNullOrEmpty(SessionId);

    public bool HasHost => !string.IsNullOrEmpty(HostId);
}

/// <summary>当前展示权。lease 是提交/取消的唯一凭证。</summary>
public sealed record InteractionClaim(string HostId, string LeaseId, DateTimeOffset ClaimedAt);

/// <summary>问题槽快照。Question 保留兼容形态；Outcome 才是权威状态。</summary>
public sealed record QuestionSlotSnapshot(PendingQuestion Question, QuestionOutcome Outcome);

/// <summary>问题组快照。</summary>
public sealed record QuestionSetSnapshot(IReadOnlyList<QuestionSlotSnapshot> Items)
{
    public int AnsweredCount => Items.Count(i => i.Outcome == QuestionOutcome.Answered);

    public int TerminalCount =>
        Items.Count(i => i.Outcome is QuestionOutcome.Answered or QuestionOutcome.Cancelled);

    public bool IsComplete => Items.Count > 0 && TerminalCount == Items.Count;
}

/// <summary>变体审阅快照。Decision/Effect 由 Core 产生；UI 只读。</summary>
public sealed record VariantReviewSnapshot(
    string VariantSetId,
    int Generation,
    ExploreDetails Details,
    VariantReviewDecision? Decision,
    VariantEffect? Effect,
    VariantEffectState EffectState
);

/// <summary>确认交互快照（Core 发出的二次确认，如覆盖章节）。</summary>
public sealed record ConfirmationSnapshot(
    string Title,
    string Message,
    string ConfirmLabel,
    string CancelLabel,
    bool? Confirmed
);

/// <summary>
/// 不可变交互快照。Revision 单调递增；UI 只接受更高 Revision。
/// Payload 引用大对象（ExploreDetails/选项数组由创建时复制），不做无意义深拷贝。
/// </summary>
public sealed record InteractionSnapshot(
    string Id,
    InteractionKind Kind,
    string SourceKey,
    string SessionId,
    string? NovelId,
    string? OwnerHost,
    InteractionState State,
    long Revision,
    InteractionClaim? Claim,
    QuestionSetSnapshot? Questions,
    VariantReviewSnapshot? VariantReview,
    ConfirmationSnapshot? Confirmation,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
)
{
    public bool IsTerminal =>
        State
            is InteractionState.Resolved
                or InteractionState.Cancelled
                or InteractionState.Expired;
}

/// <summary>Core 决定的变体效果。Host executor 只执行这个 plan，不再做业务分支。</summary>
public enum VariantEffectKind
{
    Dismiss,
    PrefillRefinement,
    AppendToContext,
    SendExtractionPrompt,
    WriteChapter,
}

/// <summary>效果执行状态（用于重入/崩溃恢复）。</summary>
public enum VariantEffectState
{
    None,
    Pending,
    Running,
    Applied,
    Failed,
    Cancelled,
}

/// <summary>
/// 变体效果 plan。EffectId 幂等键；Host 只能用该 id 执行一次。
/// </summary>
public sealed record VariantEffect(
    string EffectId,
    VariantEffectKind Kind,
    string VariantId,
    string VariantLabel,
    string VariantContent,
    string ExploreType,
    string ExplorePrompt,
    VariantApplyTarget? ApplyTarget,
    string ExtractionPrompt,
    bool RequiresOverwriteConfirmation,
    VariantEffectState State = VariantEffectState.Pending,
    string? FailureReason = null
);

/// <summary>提交/状态转换结果；UI 只根据 Status 决策，不靠异常猜。</summary>
public sealed record SubmitResult(
    SubmitStatus Status,
    InteractionSnapshot? Snapshot = null,
    VariantEffect? Effect = null,
    string? Message = null
);

/// <summary>问题组创建结果。</summary>
public sealed record QuestionSetCreation(
    InteractionSnapshot Snapshot,
    IReadOnlyList<PendingQuestion> Questions
)
{
    public string InteractionId => Snapshot.Id;
}
