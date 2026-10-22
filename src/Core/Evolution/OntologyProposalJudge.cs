using Core.Data;
using Core.Inference;

namespace Core.Evolution;

/// <summary>
/// 本体提案判定器（jevgraph 管线）：确定性候选 → Jev 闭集判定 → 确定性证据/阈值闸门。
/// 只产提案，不写库；Jev 关闭或失败时零请求/失败即 Review。
/// </summary>
public sealed class OntologyProposalJudge(ISystemOneClient? systemOne)
{
    private static readonly Dictionary<string, string?> AspectCriteria = new(StringComparer.Ordinal)
    {
        ["status"] = "生存/存在状态变化（死亡、失踪、转变等）",
        ["personality"] = "性格/心态变化",
        ["motivation"] = "目标/动机变化",
        ["relationship"] = "与他人的关系变化",
        ["ability"] = "能力获得/失去",
        ["possession"] = "持有物/归属变化",
        ["location"] = "所在地变化",
        ["knowledge"] = "认知/信息差变化",
        ["behavior"] = "行为模式变化",
        ["other"] = "其他未归类变化",
        ["none"] = "本章没有发生该类变化（只是出场/被提及）",
        ["uncertain"] = "证据不足/需要前后文",
    };

    /// <summary>状态值候选的闭集选项（枚举标签 + none + uncertain）。</summary>
    private static Dictionary<string, string?> StatusCriteria(EntityType type) =>
        type switch
        {
            EntityType.Character => new(StringComparer.Ordinal)
            {
                ["active"] = "存活/活跃",
                ["deceased"] = "死亡",
                ["missing"] = "失踪",
                ["transformed"] = "转变成其他存在形态",
                ["none"] = "本章没有发生状态变化（只是出场/被提及）",
                ["uncertain"] = "证据不足/需要前后文",
            },
            EntityType.Faction => new(StringComparer.Ordinal)
            {
                ["active"] = "活跃",
                ["dormant"] = "蛰伏",
                ["dissolved"] = "覆灭/解散",
                ["none"] = "本章没有发生状态变化",
                ["uncertain"] = "证据不足/需要前后文",
            },
            EntityType.Item => new(StringComparer.Ordinal)
            {
                ["active"] = "完好",
                ["lost"] = "遗失",
                ["destroyed"] = "损毁",
                ["none"] = "本章没有发生状态变化",
                ["uncertain"] = "证据不足/需要前后文",
            },
            EntityType.Location => new(StringComparer.Ordinal)
            {
                ["active"] = "存在",
                ["destroyed"] = "毁灭",
                ["sealed"] = "封印",
                ["abandoned"] = "废弃",
                ["none"] = "本章没有发生状态变化",
                ["uncertain"] = "证据不足/需要前后文",
            },
            EntityType.Plotline => new(StringComparer.Ordinal)
            {
                ["active"] = "进行中",
                ["resolved"] = "已解决",
                ["abandoned"] = "放弃",
                ["none"] = "本章没有发生状态变化",
                ["uncertain"] = "证据不足/需要前后文",
            },
            _ => new(StringComparer.Ordinal),
        };

    private static bool SupportsStatus(EntityType type) =>
        type
            is EntityType.Character
                or EntityType.Faction
                or EntityType.Item
                or EntityType.Location
                or EntityType.Plotline;

    /// <summary>Jev 是否可用（总开关 + ontology 子开关）。</summary>
    public bool Enabled => systemOne is { Enabled: true } && systemOne.Options.Ontology.Enabled;

    public async Task<(
        List<OntologyProposal> Proposals,
        List<OntologyProposalReceipt> Receipts
    )> JudgeChapterAsync(
        Novel novel,
        Chapter chapter,
        IReadOnlyList<AppearanceRecord> appearances,
        IReadOnlyList<WorldRule> rules,
        CancellationToken ct = default
    )
    {
        if (!Enabled || systemOne is null)
        {
            return ([], []);
        }

        var options = systemOne.Options.Ontology;
        var orderMap = novel
            .Chapters.GroupBy(c => c.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Order, StringComparer.Ordinal);
        var proposals = new List<OntologyProposal>();
        var receipts = new List<OntologyProposalReceipt>();

        var required = BuildRuleLookup(rules)
            .Values.Where(r => RuleEvaluator.AppliesToChapter(r, orderMap, chapter.Order))
            .ToList();
        var ruleIds = required.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);

        var stateCandidates = BuildStateCandidates(novel, chapter, appearances)
            .Take(options.MaxCandidates)
            .ToList();
        if (stateCandidates.Count > 0)
        {
            await JudgeStateCandidatesAsync(
                chapter,
                stateCandidates,
                options,
                proposals,
                receipts,
                ct
            );
        }

        var remaining = Math.Max(0, options.MaxCandidates - stateCandidates.Count);
        if (remaining > 0 && required.Count > 0)
        {
            var ruleCandidates = BuildRuleCandidates(
                    novel,
                    chapter,
                    appearances,
                    required,
                    ruleIds,
                    remaining
                )
                .ToList();
            if (ruleCandidates.Count > 0)
            {
                await JudgeRuleCandidatesAsync(
                    novel,
                    chapter,
                    ruleCandidates,
                    options,
                    proposals,
                    receipts,
                    ct
                );
            }
        }

        return (proposals, receipts);
    }

    private async Task JudgeStateCandidatesAsync(
        Chapter chapter,
        List<StateCandidate> candidates,
        SystemOneOntologyConfig options,
        List<OntologyProposal> proposals,
        List<OntologyProposalReceipt> receipts,
        CancellationToken ct
    )
    {
        var batches = SystemOnePacking.Pack(
            candidates,
            Math.Max(systemOne!.Options.MaxStateChars, 256),
            RenderStateCandidate
        );
        for (var index = 0; index < batches.Count; index++)
        {
            var batch = batches[index];
            var questions = new Dictionary<string, SystemOneQuestion>(
                batch.Count,
                StringComparer.Ordinal
            );
            foreach (var candidate in batch)
            {
                questions[candidate.Id] = new ChoiceQuestion
                {
                    Instructions =
                        candidate.Field == "status"
                            ? $"根据证据判断{candidate.Name}在第{chapter.Order}章结束时的状态值；"
                                + "只能选 criteria 中的一项，未发生变化或只是出场选 none，证据不足选 uncertain。"
                            : $"根据证据判断{candidate.Name}在第{chapter.Order}章是否发生了哪一类本体状态变化；"
                                + "只能选 criteria 中的一项，只是出场/被提及选 none，证据不足选 uncertain。",
                    Criteria = candidate.Criteria,
                };
            }

            var state = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["candidates"] = batch.ToDictionary(
                    c => c.Id,
                    c => new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["entity"] = c.Name,
                        ["type"] = c.Type.ToKebabCaseString(),
                        ["field"] = c.Field,
                        ["chapter"] = $"第{chapter.Order}章",
                        ["evidence"] = Truncate(c.Evidence, options.MaxEvidenceChars),
                    },
                    StringComparer.Ordinal
                ),
            };

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var response = await systemOne.AskAsync(state, questions, ct);
            stopwatch.Stop();
            receipts.Add(
                new OntologyProposalReceipt(
                    Batch: index + 1,
                    Questions: batch.Count,
                    Ok: response is not null,
                    Model: response?.Model ?? "",
                    InputTokens: response?.InputTokens ?? 0,
                    OutputTokens: response?.OutputTokens ?? 0,
                    ElapsedMs: stopwatch.ElapsedMilliseconds
                )
            );

            foreach (var candidate in batch)
            {
                SystemOneAnswer? answer = null;
                if (response is not null)
                {
                    response.Answers.TryGetValue(candidate.Id, out answer);
                }
                var isStatus = candidate.Field == "status";
                var statusAnswer =
                    answer?.Choice is { } choice
                    && !choice.Equals("none", StringComparison.OrdinalIgnoreCase)
                    && !choice.Equals("uncertain", StringComparison.OrdinalIgnoreCase)
                        ? choice
                        : null;
                var baseProposal = new OntologyProposal(
                    Id: candidate.Id,
                    Element: OntologyElement.State,
                    Operation: OntologyOperation.Update,
                    TargetEntityType: candidate.Type,
                    TargetId: candidate.EntityId,
                    TargetName: candidate.Name,
                    Field: isStatus ? "status" : answer?.Choice,
                    From: null,
                    To: isStatus ? statusAnswer : null,
                    Summary: isStatus
                        ? $"疑似 {candidate.Name} 状态变为 {statusAnswer ?? "?"}"
                        : $"疑似 {candidate.Name} 发生{LabelOf(answer?.Choice)}变化",
                    RuleId: null,
                    ChapterId: chapter.Id,
                    Evidence: candidate.Evidence,
                    EvidenceStart: 0,
                    EvidenceEnd: 0,
                    Status: OntologyProposalStatus.Review,
                    ReasonCode: OntologyProposalReason.MissingAnswer
                );
                var proposal = OntologyProposalGate.JudgeChoice(
                    baseProposal,
                    answer,
                    answer?.Choice,
                    candidate.Criteria.Keys.ToHashSet(StringComparer.Ordinal),
                    options
                );
                proposals.Add(OntologyProposalGate.CheckEvidence(proposal, chapter.Content));
            }
        }
    }

    private async Task JudgeRuleCandidatesAsync(
        Novel novel,
        Chapter chapter,
        List<RuleCandidate> candidates,
        SystemOneOntologyConfig options,
        List<OntologyProposal> proposals,
        List<OntologyProposalReceipt> receipts,
        CancellationToken ct
    )
    {
        var batches = SystemOnePacking.Pack(
            candidates,
            Math.Max(systemOne!.Options.MaxStateChars, 256),
            RenderRuleCandidate
        );
        for (var index = 0; index < batches.Count; index++)
        {
            var batch = batches[index];
            var questions = new Dictionary<string, SystemOneQuestion>(
                batch.Count,
                StringComparer.Ordinal
            );
            foreach (var candidate in batch)
            {
                questions[candidate.Id] = new NoulQuestion
                {
                    Instructions =
                        $"证据是否展示了规则「{candidate.Rule.Description}」的触发条件"
                        + $"「{candidate.Rule.Action}」或违反行为「{candidate.Rule.ForbiddenAction}」？"
                        + "只依据证据判断，不推测。",
                    Criteria = new NoulCriteria
                    {
                        Yes = "证据明确展示该条件/行为",
                        No = "证据没有展示",
                    },
                };
            }

            var state = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["candidates"] = batch.ToDictionary(
                    c => c.Id,
                    c => new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["rule"] = c.Rule.Description,
                        ["subject"] = c.SubjectName,
                        ["chapter"] = $"第{chapter.Order}章",
                        ["evidence"] = Truncate(c.Evidence, options.MaxEvidenceChars),
                    },
                    StringComparer.Ordinal
                ),
            };

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var response = await systemOne.AskAsync(state, questions, ct);
            stopwatch.Stop();
            receipts.Add(
                new OntologyProposalReceipt(
                    Batch: index + 1,
                    Questions: batch.Count,
                    Ok: response is not null,
                    Model: response?.Model ?? "",
                    InputTokens: response?.InputTokens ?? 0,
                    OutputTokens: response?.OutputTokens ?? 0,
                    ElapsedMs: stopwatch.ElapsedMilliseconds
                )
            );

            foreach (var candidate in batch)
            {
                SystemOneAnswer? answer = null;
                if (response is not null)
                {
                    response.Answers.TryGetValue(candidate.Id, out answer);
                }
                var proposal = new OntologyProposal(
                    Id: candidate.Id,
                    Element: OntologyElement.Rule,
                    Operation: OntologyOperation.Update,
                    TargetEntityType: candidate.SubjectType,
                    TargetId: candidate.SubjectId,
                    TargetName: candidate.SubjectName,
                    Field: "rule-trigger",
                    From: null,
                    To: null,
                    Summary: $"规则「{candidate.Rule.Description}」疑似在本章触发或被违反",
                    RuleId: candidate.Rule.Id,
                    ChapterId: chapter.Id,
                    Evidence: candidate.Evidence,
                    EvidenceStart: 0,
                    EvidenceEnd: 0,
                    Status: OntologyProposalStatus.Review,
                    ReasonCode: OntologyProposalReason.MissingAnswer
                );
                var checkedEvidence = OntologyProposalGate.CheckEvidence(proposal, chapter.Content);
                proposals.Add(
                    checkedEvidence.ReasonCode == OntologyProposalReason.EvidenceSpanMismatch
                        ? checkedEvidence
                        : OntologyProposalGate.JudgeNoul(checkedEvidence, answer, options)
                );
            }
        }
    }

    private static IEnumerable<StateCandidate> BuildStateCandidates(
        Novel novel,
        Chapter chapter,
        IReadOnlyList<AppearanceRecord> appearances
    )
    {
        var present = appearances
            .Where(a => a.Mode == AppearanceMode.Present)
            .DistinctBy(a => (a.EntityType, a.EntityId))
            .ToList();
        if (present.Count > 0)
        {
            foreach (var record in present)
            {
                yield return MakeCandidate(
                    chapter,
                    novel,
                    record.EntityType,
                    record.EntityId,
                    string.IsNullOrWhiteSpace(record.EvidenceSpan)
                        ? ChapterPrefix(chapter.Content)
                        : record.EvidenceSpan
                );
            }
            yield break;
        }

        foreach (var (type, id, name) in Entities(novel))
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length < 2)
            {
                continue;
            }
            var evidence = FindEvidence(chapter.Content, name);
            if (evidence is null)
            {
                continue;
            }
            yield return MakeCandidate(chapter, novel, type, id, evidence);
        }
    }

    /// <summary>状态候选：状态枚举型实体问「最终状态值」，其余回退到切面闭集问题。</summary>
    private static StateCandidate MakeCandidate(
        Chapter chapter,
        Novel novel,
        EntityType type,
        string entityId,
        string evidence
    )
    {
        var name = NameOf(novel, type, entityId);
        var isStatus = SupportsStatus(type);
        return new StateCandidate(
            $"state:{chapter.Id}:{type.ToKebabCaseString()}:{entityId}:{(isStatus ? "status" : "aspect")}",
            name,
            type,
            entityId,
            evidence,
            isStatus ? "status" : "aspect",
            isStatus ? StatusCriteria(type) : AspectCriteria
        );
    }

    private static IEnumerable<RuleCandidate> BuildRuleCandidates(
        Novel novel,
        Chapter chapter,
        IReadOnlyList<AppearanceRecord> appearances,
        IReadOnlyList<WorldRule> rules,
        IReadOnlySet<string> ruleIds,
        int max
    )
    {
        var present = appearances.Where(a => a.Mode == AppearanceMode.Present).ToList();
        var emitted = 0;
        foreach (var rule in rules)
        {
            if (emitted >= max)
            {
                yield break;
            }
            if (rule.SubjectType is not { } subjectType)
            {
                continue;
            }

            var subjectId = rule.SubjectId;
            if (string.IsNullOrWhiteSpace(subjectId))
            {
                subjectId = present.FirstOrDefault(a => a.EntityType == subjectType)?.EntityId;
                if (string.IsNullOrWhiteSpace(subjectId))
                {
                    continue;
                }
            }
            if (!ruleIds.Contains(rule.Id))
            {
                continue;
            }

            var appearance = present.FirstOrDefault(a =>
                a.EntityType == subjectType
                && string.Equals(a.EntityId, subjectId, StringComparison.Ordinal)
            );
            yield return new RuleCandidate(
                $"rule:{chapter.Id}:{rule.Id}:{subjectId}",
                rule,
                subjectType,
                subjectId,
                NameOf(novel, subjectType, subjectId),
                string.IsNullOrWhiteSpace(appearance?.EvidenceSpan)
                    ? ChapterPrefix(chapter.Content)
                    : appearance.EvidenceSpan
            );
            emitted++;
        }
    }

    private static Dictionary<string, WorldRule> BuildRuleLookup(IReadOnlyList<WorldRule> rules) =>
        rules
            .Where(r => r.EntryKind == WorldRuleEntryKind.Rule)
            .GroupBy(r => r.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    private static IEnumerable<(EntityType Type, string Id, string Name)> Entities(Novel novel)
    {
        foreach (var c in novel.Characters)
            yield return (EntityType.Character, c.Id, c.Name);
        foreach (var f in novel.Factions)
            yield return (EntityType.Faction, f.Id, f.Name);
        foreach (var l in novel.Locations)
            yield return (EntityType.Location, l.Id, l.Name);
        foreach (var i in novel.Items)
            yield return (EntityType.Item, i.Id, i.Name);
        foreach (var p in novel.Plotlines)
            yield return (EntityType.Plotline, p.Id, p.Name);
    }

    private static string NameOf(Novel novel, EntityType type, string id) =>
        type switch
        {
            EntityType.Character => novel.Characters.FirstOrDefault(c => c.Id == id)?.Name ?? id,
            EntityType.Faction => novel.Factions.FirstOrDefault(f => f.Id == id)?.Name ?? id,
            EntityType.Location => novel.Locations.FirstOrDefault(l => l.Id == id)?.Name ?? id,
            EntityType.Item => novel.Items.FirstOrDefault(i => i.Id == id)?.Name ?? id,
            EntityType.Plotline => novel.Plotlines.FirstOrDefault(p => p.Id == id)?.Name ?? id,
            EntityType.Scene => novel.Scenes.FirstOrDefault(s => s.Id == id)?.Title ?? id,
            _ => id,
        };

    private static string? FindEvidence(string content, string name)
    {
        if (string.IsNullOrEmpty(content) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        var index = content.IndexOf(name, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }
        var start = Math.Max(0, index - 20);
        var end = Math.Min(content.Length, index + name.Length + 20);
        return content[start..end];
    }

    private static string ChapterPrefix(string content) =>
        string.IsNullOrEmpty(content) ? "" : content[..Math.Min(content.Length, 300)];

    private static string LabelOf(string? aspect) =>
        aspect switch
        {
            "status" => "状态",
            "personality" => "性格",
            "motivation" => "动机",
            "relationship" => "关系",
            "ability" => "能力",
            "possession" => "持有",
            "location" => "位置",
            "knowledge" => "认知",
            "behavior" => "行为",
            _ => "本体",
        };

    private static string RenderStateCandidate(StateCandidate candidate) =>
        string.Concat(
            "{\"entity\":",
            System.Text.Json.JsonSerializer.Serialize(candidate.Name),
            ",\"type\":",
            System.Text.Json.JsonSerializer.Serialize(candidate.Type.ToKebabCaseString()),
            ",\"field\":",
            System.Text.Json.JsonSerializer.Serialize(candidate.Field),
            ",\"evidence\":",
            System.Text.Json.JsonSerializer.Serialize(candidate.Evidence),
            "}"
        );

    private static string RenderRuleCandidate(RuleCandidate candidate) =>
        string.Concat(
            "{\"rule\":",
            System.Text.Json.JsonSerializer.Serialize(candidate.Rule.Description),
            ",\"subject\":",
            System.Text.Json.JsonSerializer.Serialize(candidate.SubjectName),
            ",\"evidence\":",
            System.Text.Json.JsonSerializer.Serialize(candidate.Evidence),
            "}"
        );

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    private sealed record StateCandidate(
        string Id,
        string Name,
        EntityType Type,
        string EntityId,
        string Evidence,
        string Field,
        Dictionary<string, string?> Criteria
    );

    private sealed record RuleCandidate(
        string Id,
        WorldRule Rule,
        EntityType SubjectType,
        string SubjectId,
        string SubjectName,
        string Evidence
    );
}
