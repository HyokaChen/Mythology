using Core.Data;
using Core.Evolution;
using Core.Inference;
using Xunit;

namespace Core.Tests;

/// <summary>本体提案闸门 + Jev 判定器契约。</summary>
public sealed class OntologyProposalGateTests
{
    private static readonly SystemOneOntologyConfig Options = new();

    private static OntologyProposal Proposal(
        OntologyElement element = OntologyElement.State,
        string evidence = "林长歌推门而入"
    ) =>
        new(
            Id: "op1",
            Element: element,
            Operation: OntologyOperation.Update,
            TargetEntityType: EntityType.Character,
            TargetId: "lin",
            TargetName: "林长歌",
            Field: "personality",
            From: null,
            To: null,
            Summary: "疑似变化",
            RuleId: element == OntologyElement.Rule ? "r1" : null,
            ChapterId: "ch1",
            Evidence: evidence,
            EvidenceStart: 0,
            EvidenceEnd: 0,
            Status: OntologyProposalStatus.Review,
            ReasonCode: OntologyProposalReason.MissingAnswer
        );

    private static SystemOneAnswer Choice(
        string choice,
        double probability,
        double? confidence = 0.9
    ) =>
        new()
        {
            Choice = choice,
            Probabilities = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [choice] = probability,
            },
            Confidence = confidence,
        };

    [Fact]
    public void JudgeChoice_CoversReasonCodes()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "personality",
            "none",
            "uncertain",
        };

        Assert.Equal(
            OntologyProposalReason.MissingAnswer,
            OntologyProposalGate.JudgeChoice(Proposal(), null, null, allowed, Options).ReasonCode
        );
        Assert.Equal(
            OntologyProposalReason.ProviderSelectedNone,
            OntologyProposalGate
                .JudgeChoice(Proposal(), Choice("none", 0.9), "none", allowed, Options)
                .ReasonCode
        );
        Assert.Equal(
            OntologyProposalReason.ProviderRequestedMoreContext,
            OntologyProposalGate
                .JudgeChoice(Proposal(), Choice("uncertain", 0.9), "uncertain", allowed, Options)
                .ReasonCode
        );
        Assert.Equal(
            OntologyProposalReason.FailedSchemaCheck,
            OntologyProposalGate
                .JudgeChoice(Proposal(), Choice("bogus", 0.9), "bogus", allowed, Options)
                .ReasonCode
        );
        Assert.Equal(
            OntologyProposalReason.BelowProbability,
            OntologyProposalGate
                .JudgeChoice(
                    Proposal(),
                    Choice("personality", 0.2),
                    "personality",
                    allowed,
                    Options
                )
                .ReasonCode
        );
        Assert.Equal(
            OntologyProposalReason.BelowConfidence,
            OntologyProposalGate
                .JudgeChoice(
                    Proposal(),
                    Choice("personality", 0.9, confidence: 0.2),
                    "personality",
                    allowed,
                    Options
                )
                .ReasonCode
        );
        var passed = OntologyProposalGate.JudgeChoice(
            Proposal(),
            Choice("personality", 0.9),
            "personality",
            allowed,
            Options
        );
        Assert.Equal(OntologyProposalStatus.Proposed, passed.Status);
        Assert.Equal(OntologyProposalReason.Passed, passed.ReasonCode);
    }

    [Fact]
    public void JudgeNoul_CoversReasonCodes()
    {
        Assert.Equal(
            OntologyProposalReason.MissingAnswer,
            OntologyProposalGate.JudgeNoul(Proposal(OntologyElement.Rule), null, Options).ReasonCode
        );
        Assert.Equal(
            OntologyProposalReason.BelowProbability,
            OntologyProposalGate
                .JudgeNoul(
                    Proposal(OntologyElement.Rule),
                    new SystemOneAnswer { Noul = 0.1, Confidence = 0.9 },
                    Options
                )
                .ReasonCode
        );
        var passed = OntologyProposalGate.JudgeNoul(
            Proposal(OntologyElement.Rule),
            new SystemOneAnswer { Noul = 0.9, Confidence = 0.9 },
            Options
        );
        Assert.Equal(OntologyProposalStatus.Proposed, passed.Status);
    }

    [Fact]
    public void CheckEvidence_RejectsMissingSpan()
    {
        var mismatch = OntologyProposalGate.CheckEvidence(Proposal(), "完全不同的一段正文");
        Assert.Equal(OntologyProposalReason.EvidenceSpanMismatch, mismatch.ReasonCode);
        Assert.Equal(0, mismatch.EvidenceStart);

        var matched = OntologyProposalGate.CheckEvidence(Proposal(), "前文。林长歌推门而入。后文");
        Assert.Equal("林长歌推门而入", matched.Evidence);
        Assert.Equal(3, matched.EvidenceStart);
        Assert.Equal(3 + "林长歌推门而入".Length, matched.EvidenceEnd);
    }

    [Fact]
    public void Judge_Disabled_ReturnsNoRequests()
    {
        var judge = new OntologyProposalJudge(null);
        Assert.False(judge.Enabled);
    }

    [Fact]
    public async Task JudgeChapter_StateAndRuleProposals()
    {
        var chapter = new Chapter
        {
            Id = "ch1",
            NovelId = "n1",
            Title = "第一章",
            Order = 1,
            Content = "林长歌推门而入，神色骤变。",
        };
        var novel = new Novel
        {
            Id = "n1",
            Title = "书",
            Chapters = [chapter],
            Characters =
            [
                new Character
                {
                    Id = "lin",
                    NovelId = "n1",
                    Name = "林长歌",
                },
            ],
        };
        var appearances = new List<AppearanceRecord>
        {
            new()
            {
                Id = "ap1",
                NovelId = "n1",
                EntityType = EntityType.Character,
                EntityId = "lin",
                ChapterId = "ch1",
                Mode = AppearanceMode.Present,
                EvidenceSpan = "林长歌推门而入",
            },
        };
        var rules = new List<WorldRule>
        {
            new()
            {
                Id = "r1",
                EntryKind = WorldRuleEntryKind.Rule,
                RuleKind = RuleKind.Forbids,
                SubjectType = EntityType.Character,
                SubjectId = "lin",
                ForbiddenAction = "御剑",
                Description = "外门弟子不得御剑",
            },
        };
        var judge = new OntologyProposalJudge(new StubJudge());

        var (proposals, receipts) = await judge.JudgeChapterAsync(
            novel,
            chapter,
            appearances,
            rules
        );

        Assert.NotEmpty(proposals);
        Assert.NotEmpty(receipts);
        Assert.Contains(
            proposals,
            p =>
                p.Element == OntologyElement.State
                && p.Field == "status"
                && p.To == "deceased"
                && p.Status == OntologyProposalStatus.Proposed
        );
        Assert.Contains(
            proposals,
            p =>
                p.Element == OntologyElement.Rule
                && p.RuleId == "r1"
                && p.Status == OntologyProposalStatus.Proposed
        );
    }

    private sealed class StubJudge : ISystemOneClient
    {
        public bool Enabled => true;

        public SystemOneConfig Options { get; } =
            new()
            {
                Enabled = true,
                Ontology = new SystemOneOntologyConfig
                {
                    Enabled = true,
                    MinProbability = 0.5,
                    MinConfidence = 0.5,
                },
            };

        public Task<SystemOneResponse?> AskAsync(
            object state,
            IReadOnlyDictionary<string, SystemOneQuestion> questions,
            CancellationToken ct = default
        )
        {
            var isChoice = questions.Values.FirstOrDefault() is ChoiceQuestion;
            var answers = questions.Keys.ToDictionary(
                key => key,
                key =>
                    isChoice
                        ? new SystemOneAnswer
                        {
                            Choice = "deceased",
                            Probabilities = new Dictionary<string, double>(StringComparer.Ordinal)
                            {
                                ["deceased"] = 0.9,
                            },
                            Confidence = 0.9,
                        }
                        : new SystemOneAnswer { Noul = 0.9, Confidence = 0.9 },
                StringComparer.Ordinal
            );
            return Task.FromResult<SystemOneResponse?>(
                new SystemOneResponse { Model = "stub", Answers = answers }
            );
        }

        public void UpdateConfig(SystemOneConfig options) { }
    }
}
