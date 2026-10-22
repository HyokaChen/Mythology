using Core.Data;

namespace Core.Evolution;

/// <summary>提案状态（对齐 jevgraph 的 proposed / review / rejected）。</summary>
public enum OntologyProposalStatus
{
    /// <summary>通过全部确定性闸门，可建议应用。</summary>
    Proposed,

    /// <summary>不确定，交人工/下一轮确认。</summary>
    Review,

    /// <summary>明确拒绝（如模型选择 none）。</summary>
    Rejected,
}

/// <summary>
/// 本体变化提案（只提案，不写库）：来自 Jev/System One 的闭集判定 + 确定性闸门。
/// </summary>
public sealed record OntologyProposal(
    string Id,
    OntologyElement Element,
    OntologyOperation Operation,
    EntityType TargetEntityType,
    string TargetId,
    string TargetName,
    string? Field,
    string? From,
    string? To,
    string Summary,
    string? RuleId,
    string ChapterId,
    string Evidence,
    int EvidenceStart,
    int EvidenceEnd,
    OntologyProposalStatus Status,
    string ReasonCode,
    double? Probability = null,
    double? Confidence = null
);

/// <summary>单批判定回执（模型/tokens/耗时/成败）。</summary>
public sealed record OntologyProposalReceipt(
    int Batch,
    int Questions,
    bool Ok,
    string Model,
    long InputTokens,
    long OutputTokens,
    long ElapsedMs
);

/// <summary>闸门原因码（与 jevgraph builder 同形）。</summary>
public static class OntologyProposalReason
{
    public const string MissingAnswer = "missing_answer";
    public const string ProviderSelectedNone = "provider_selected_none";
    public const string ProviderRequestedMoreContext = "provider_requested_more_context";
    public const string FailedSchemaCheck = "proposal_failed_schema_check";
    public const string EvidenceSpanMismatch = "evidence_span_mismatch";
    public const string BelowProbability = "below_probability_threshold";
    public const string BelowConfidence = "below_confidence_threshold";
    public const string Passed = "passed_configured_gates";
}
