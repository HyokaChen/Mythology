using Core.Data;
using Core.Inference;

namespace Core.Evolution;

/// <summary>
/// 本体提案的确定性闸门（复用关系提案的 jevgraph 分支顺序）：
/// none 先拒；其余不确定一律 Review；概率/置信度双阈值。
/// </summary>
public static class OntologyProposalGate
{
    /// <summary>choice 题判定（状态切面/关系类型等闭集）。</summary>
    public static OntologyProposal JudgeChoice(
        OntologyProposal proposal,
        SystemOneAnswer? answer,
        string? selectedToken,
        IReadOnlySet<string> allowedTokens,
        SystemOneOntologyConfig options
    )
    {
        if (answer is null)
        {
            return proposal with
            {
                Status = OntologyProposalStatus.Review,
                ReasonCode = OntologyProposalReason.MissingAnswer,
            };
        }

        if (string.Equals(selectedToken, "none", StringComparison.OrdinalIgnoreCase))
        {
            return proposal with
            {
                Status = OntologyProposalStatus.Rejected,
                ReasonCode = OntologyProposalReason.ProviderSelectedNone,
                Probability = ResolveProbability(answer, selectedToken),
                Confidence = answer.Confidence,
            };
        }

        if (string.Equals(selectedToken, "uncertain", StringComparison.OrdinalIgnoreCase))
        {
            return proposal with
            {
                Status = OntologyProposalStatus.Review,
                ReasonCode = OntologyProposalReason.ProviderRequestedMoreContext,
                Probability = ResolveProbability(answer, selectedToken),
                Confidence = answer.Confidence,
            };
        }

        if (selectedToken is null || !allowedTokens.Contains(selectedToken))
        {
            return proposal with
            {
                Status = OntologyProposalStatus.Review,
                ReasonCode = OntologyProposalReason.FailedSchemaCheck,
                Probability = ResolveProbability(answer, selectedToken),
                Confidence = answer.Confidence,
            };
        }

        var probability = ResolveProbability(answer, selectedToken);
        if (probability is not { } p || p < options.MinProbability)
        {
            return proposal with
            {
                Status = OntologyProposalStatus.Review,
                ReasonCode = OntologyProposalReason.BelowProbability,
                Probability = probability,
                Confidence = answer.Confidence,
            };
        }

        if (answer.Confidence is { } confidence && confidence < options.MinConfidence)
        {
            return proposal with
            {
                Status = OntologyProposalStatus.Review,
                ReasonCode = OntologyProposalReason.BelowConfidence,
                Probability = probability,
                Confidence = confidence,
            };
        }

        return proposal with
        {
            Status = OntologyProposalStatus.Proposed,
            ReasonCode = OntologyProposalReason.Passed,
            Probability = probability,
            Confidence = answer.Confidence,
        };
    }

    /// <summary>noul 题判定（规则是否在正文中被展示）。</summary>
    public static OntologyProposal JudgeNoul(
        OntologyProposal proposal,
        SystemOneAnswer? answer,
        SystemOneOntologyConfig options
    )
    {
        if (answer?.Noul is not { } probability)
        {
            return proposal with
            {
                Status = OntologyProposalStatus.Review,
                ReasonCode = OntologyProposalReason.MissingAnswer,
            };
        }

        if (probability < options.MinProbability)
        {
            return proposal with
            {
                Status = OntologyProposalStatus.Review,
                ReasonCode = OntologyProposalReason.BelowProbability,
                Probability = probability,
                Confidence = answer.Confidence,
            };
        }

        if (answer.Confidence is { } confidence && confidence < options.MinConfidence)
        {
            return proposal with
            {
                Status = OntologyProposalStatus.Review,
                ReasonCode = OntologyProposalReason.BelowConfidence,
                Probability = probability,
                Confidence = confidence,
            };
        }

        return proposal with
        {
            Status = OntologyProposalStatus.Proposed,
            ReasonCode = OntologyProposalReason.Passed,
            Probability = probability,
            Confidence = answer.Confidence,
        };
    }

    /// <summary>证据必须真实出现在正文中；否则降级 Review 并把 span 归零。</summary>
    public static OntologyProposal CheckEvidence(OntologyProposal proposal, string chapterContent)
    {
        if (proposal.Status == OntologyProposalStatus.Rejected)
        {
            return proposal;
        }

        var start =
            string.IsNullOrWhiteSpace(proposal.Evidence) || string.IsNullOrEmpty(chapterContent)
                ? -1
                : chapterContent.IndexOf(proposal.Evidence, StringComparison.Ordinal);
        if (start < 0)
        {
            return proposal with
            {
                Status = OntologyProposalStatus.Review,
                ReasonCode = OntologyProposalReason.EvidenceSpanMismatch,
                EvidenceStart = 0,
                EvidenceEnd = 0,
            };
        }

        return proposal with
        {
            EvidenceStart = start,
            EvidenceEnd = start + proposal.Evidence.Length,
        };
    }

    private static double? ResolveProbability(SystemOneAnswer answer, string? selectedToken) =>
        answer.Probabilities is { } map
        && selectedToken is not null
        && map.TryGetValue(selectedToken, out var probability)
            ? probability
            : answer.Confidence;
}
