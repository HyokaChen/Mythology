using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Core.Data;
using Core.Inference;
using Core.Shared;
using Core.Tools;
using EntityKind = Core.Data.EntityType;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// scan_relationships：从章节正文提取关系候选（jevgraph 提炼 + 本地判定模型）。
/// 流程：全实体提及 → 同句封锁 + 本体域/值域门 → Choice 判定（类型对允许的关系 + none/uncertain）
/// → 确定性证据闸门（状态 + 原因码）→ 输出提案。
/// 本工具**不写库**：用户确认后由 agent 调用 manage_relationship / batch_create_relationships。
/// </summary>
public sealed class RelationshipScanTool(INovelStore store, ISystemOneClient? systemOne = null)
    : AgentTool<RelationshipScanParams, RelationshipScanDetails>
{
    /// <summary>缺省扫描窗口：最近 N 个有正文章节。</summary>
    private const int DefaultChapterCount = 3;

    /// <summary>候选对数量硬上限（单次扫描）。</summary>
    private const int MaxCandidatesCap = 100;

    /// <summary>证据页每条截断长度（送判定）。</summary>
    private const int EvidenceChars = 400;

    public override string Name => "scan_relationships";
    public override string Label => "关系提案扫描";
    public override string Description =>
        "从最近章节正文提取关系候选（本地判定模型提案）；只提议不写库，确认后经 manage_relationship 写入";
    public override ToolCategory Category => ToolCategory.EntityRead;
    public override string[] Aliases => ["关系提案", "关系扫描", "人物关系提取", "关系候选"];
    public override string[] ExampleQueries =>
        ["扫描最近几章的人物关系", "这两章里谁和谁有关系", "提取关系候选", "关系网有什么新变化"];

    public override async Task<AgentToolResult<RelationshipScanDetails>> ExecuteAsync(
        string toolCallId,
        RelationshipScanParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<RelationshipScanDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        if (systemOne is null || !systemOne.Enabled || !systemOne.Options.Relations.Enabled)
            return ErrorResult(
                "本地判定模型未启用：请在 config.yaml 配置 system-one.enabled 与 system-one.relations.enabled"
            );

        var novel = await store.GetNovel(args.NovelId, ct);
        ThrowIfCancelled(ct);
        if (novel is null)
            return ErrorResult($"未找到小说（novel_id={args.NovelId}）");

        // 候选目录：全部可提及实体（非角色实体也占区间：避免「青云宗」里的「青云」被当成角色）
        var catalog = await LoadCatalogAsync(args.NovelId, ct);
        ThrowIfCancelled(ct);
        if (catalog.Count(entry => entry.Name.Length >= 2) < 2)
            return OkResult("可匹配实体不足 2 个，无可扫描对", new RelationshipScanDetails());

        var allChapters = await store.GetChapters(args.NovelId, ct);
        ThrowIfCancelled(ct);
        var selected = (
            args.ChapterIds is { Count: > 0 }
                ? allChapters.Where(c => args.ChapterIds.Contains(c.Id))
                : allChapters
        )
            .Where(c => !string.IsNullOrWhiteSpace(c.Content))
            .OrderBy(c => c.Order)
            .ToList();
        if (selected.Count == 0)
            return OkResult("没有可扫描的正文章节", new RelationshipScanDetails());
        if (args.ChapterIds is not { Count: > 0 } && selected.Count > DefaultChapterCount)
            selected = selected[^DefaultChapterCount..]; // 缺省扫描最近 N 章

        var options = systemOne.Options.Relations;
        var maxCandidates =
            args.MaxCandidates > 0
                ? Math.Clamp(args.MaxCandidates, 1, MaxCandidatesCap)
                : options.MaxCandidates;

        // 既有关系对（角色×角色）排除：判定成本只花在新关系上
        var existing = (await store.GetRelationshipsByNovel(args.NovelId, ct))
            .Select(r =>
                RelationshipScanPipeline.PairKey(
                    new EntityRef(EntityKind.Character, r.CharacterAId),
                    new EntityRef(EntityKind.Character, r.CharacterBId)
                )
            )
            .ToHashSet();

        var candidates = RelationshipScanPipeline.Find(
            args.NovelId,
            selected,
            catalog,
            existing,
            options.MaxNeighbors,
            maxCandidates
        );
        if (candidates.Count == 0)
            return OkResult(
                "未发现新的关系候选（已排除既有关系对）",
                new RelationshipScanDetails()
            );

        // 每类（源类型, 目标类型）的闭集判据：本体允许的关系 + none/uncertain（jevgraph 保留结局）
        var criteriaByPair =
            new Dictionary<(EntityType, EntityType), Dictionary<string, string?>>();
        Dictionary<string, string?> CriteriaFor(RelationCandidate candidate) =>
            ResolveCriteria(criteriaByPair, candidate);

        var (verdicts, receipts) = await JudgeAsync(candidates, CriteriaFor, ct);

        // 确定性证据闸门（对照 jevgraph builder：none→拒 / uncertain 或越界→review / 低于阈值→review）
        var proposed = new List<(RelationCandidate Candidate, GateRow Row)>();
        var review = new List<(RelationCandidate Candidate, GateRow Row)>();
        var rejected = new List<(RelationCandidate Candidate, GateRow Row)>();
        foreach (var candidate in candidates)
        {
            verdicts.TryGetValue(candidate.CandidateId, out var answer);
            var row = Classify(candidate, answer, CriteriaFor(candidate), options);
            switch (row.State)
            {
                case GateState.Proposed:
                    proposed.Add((candidate, row));
                    break;
                case GateState.Review:
                    review.Add((candidate, row));
                    break;
                default:
                    rejected.Add((candidate, row));
                    break;
            }
        }

        var text = new StringBuilder();
        if (proposed.Count > 0)
        {
            text.AppendLine($"【建议】({proposed.Count})");
            var index = 0;
            foreach (
                var (candidate, row) in proposed.OrderByDescending(x => x.Row.Probability ?? 0)
            )
            {
                index++;
                text.AppendLine(
                    $"{index}. {CandidateLabel(candidate)} | {RowLabel(row)} "
                        + $"(p={Format(row.Probability)}) | 第{candidate.ChapterOrder}章 offset {candidate.EvidenceStart}-{candidate.EvidenceEnd}"
                );
                text.AppendLine($"   证据：「{candidate.EvidenceText.Trim()}」");
            }
        }
        if (review.Count > 0)
        {
            text.AppendLine($"【待确认】({review.Count})");
            var index = 0;
            foreach (var (candidate, row) in review.OrderByDescending(x => x.Row.Probability ?? 0))
            {
                index++;
                text.AppendLine(
                    $"{index}. {CandidateLabel(candidate)} | {RowLabel(row)} "
                        + $"(p={Format(row.Probability)}, {row.Reason}) | 第{candidate.ChapterOrder}章 offset {candidate.EvidenceStart}-{candidate.EvidenceEnd}"
                );
                text.AppendLine($"   证据：「{candidate.EvidenceText.Trim()}」");
            }
        }
        // 已拒绝也返回（jevgraph 三元态）：只带定位与原因码，不回传整句，控制 token
        if (rejected.Count > 0)
        {
            text.AppendLine($"【已拒绝】({rejected.Count})");
            foreach (var (candidate, row) in rejected)
            {
                text.AppendLine(
                    $"- {candidate.CandidateId} | 第{candidate.ChapterOrder}章 offset {candidate.EvidenceStart}-{candidate.EvidenceEnd}"
                        + $" | {row.Reason}{(row.Type is null ? "" : $" ({row.Type})")}"
                );
            }
        }
        text.AppendLine(
            $"（共扫描 {candidates.Count} 对：建议 {proposed.Count} / 待确认 {review.Count} / 已拒绝 {rejected.Count}；"
                + "确认后调用 manage_relationship / batch_create_relationships 写入，本工具不写库。）"
        );
        if (receipts.Count > 0)
        {
            text.AppendLine(
                $"（判定批次 {receipts.Count}：成功 {receipts.Count(r => r.Ok)} / "
                    + $"tokens {receipts.Sum(r => r.InputTokens)}→{receipts.Sum(r => r.OutputTokens)} / "
                    + $"耗时 {receipts.Sum(r => r.ElapsedMs)}ms / model {receipts[^1].Model}）"
            );
        }

        return OkResult(
            text.ToString().TrimEnd(),
            new RelationshipScanDetails
            {
                ProposedCount = proposed.Count,
                ReviewCount = review.Count,
                RejectedCount = rejected.Count,
                Rejected = rejected
                    .Select(x => new RelationshipScanRejected(
                        x.Candidate.CandidateId,
                        x.Candidate.ChapterOrder,
                        x.Candidate.EvidenceStart,
                        x.Candidate.EvidenceEnd,
                        x.Row.Reason,
                        x.Row.Type
                    ))
                    .ToList(),
                Receipts = receipts,
            }
        );
    }

    /// <summary>候选目录：角色/组织/地点/道具/情节线/场景（名称长度 ≥2 的在管线里过滤）。</summary>
    private async Task<List<CatalogEntry>> LoadCatalogAsync(string novelId, CancellationToken ct)
    {
        var catalog = new List<CatalogEntry>();
        foreach (var character in await store.GetCharacters(novelId, ct))
            catalog.Add(new CatalogEntry(character.Id, EntityKind.Character, character.Name));
        foreach (var faction in await store.GetFactions(novelId, ct))
            catalog.Add(new CatalogEntry(faction.Id, EntityKind.Faction, faction.Name));
        foreach (var location in await store.GetLocations(novelId, ct))
            catalog.Add(new CatalogEntry(location.Id, EntityKind.Location, location.Name));
        foreach (var item in await store.GetItems(novelId, ct))
            catalog.Add(new CatalogEntry(item.Id, EntityKind.Item, item.Name));
        foreach (var plotline in await store.GetPlotlines(novelId, ct))
            catalog.Add(new CatalogEntry(plotline.Id, EntityKind.Plotline, plotline.Name));
        foreach (var scene in await store.GetScenesByNovel(novelId, ct))
            catalog.Add(new CatalogEntry(scene.Id, EntityKind.Scene, scene.Title));
        return catalog;
    }

    /// <summary>类型对的闭集判据（本体关系 + none/uncertain），按类型对缓存。</summary>
    private static Dictionary<string, string?> ResolveCriteria(
        Dictionary<(EntityType, EntityType), Dictionary<string, string?>> cache,
        RelationCandidate candidate
    )
    {
        var key = (candidate.SourceEntityType, candidate.TargetEntityType);
        if (cache.TryGetValue(key, out var cached))
            return cached;

        var criteria = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var spec in RelationshipOntology.Allowed(key.Item1, key.Item2))
            criteria[spec.Token] = spec.Description;
        criteria[RelationshipOntology.NoneToken] =
            "该句并未表达两者存在稳定关系（只是同场出现、并列提及等）";
        criteria[RelationshipOntology.UncertainToken] = "句子信息不足/需要前后文才能判断";
        cache[key] = criteria;
        return criteria;
    }

    /// <summary>
    /// 证据闸门（jevgraph builder 的 EdgeStatus 语义）：返回状态 + 原因码。
    /// 分支顺序固定——none 先拒，其余不确定因素一律进 review（宁可不报也不错报）。
    /// </summary>
    private static GateRow Classify(
        RelationCandidate candidate,
        SystemOneAnswer? answer,
        IReadOnlyDictionary<string, string?> criteria,
        SystemOneRelationConfig options
    )
    {
        if (answer is null)
            return new GateRow(GateState.Review, "missing_answer", null, null, null);

        var selected = answer.Choice;
        var probability = ResolveProbability(answer);
        var row = new GateRow(GateState.Review, "", selected, probability, answer.Confidence);

        if (selected == RelationshipOntology.NoneToken)
            return row with { State = GateState.Rejected, Reason = "provider_selected_none" };
        if (selected == RelationshipOntology.UncertainToken)
            return row with { Reason = "provider_requested_more_context" };
        if (selected is null || !criteria.ContainsKey(selected))
            return row with { Reason = "relation_failed_schema_check" };
        if (probability is not { } p || p < options.MinProbability)
            return row with { Reason = "below_probability_threshold" };
        if (answer.Confidence is { } confidence && confidence < options.MinConfidence)
            return row with { Reason = "below_confidence_threshold" };
        return row with { State = GateState.Proposed, Reason = "passed_configured_gates" };
    }

    /// <summary>分批判定：单批失败按"未知"处理（该批全部进 review，绝不视为拒绝）。</summary>
    private async Task<(
        Dictionary<string, SystemOneAnswer> Verdicts,
        List<RelationshipScanReceipt> Receipts
    )> JudgeAsync(
        List<RelationCandidate> candidates,
        Func<RelationCandidate, Dictionary<string, string?>> criteriaFor,
        CancellationToken ct
    )
    {
        var verdicts = new Dictionary<string, SystemOneAnswer>(StringComparer.Ordinal);
        var receipts = new List<RelationshipScanReceipt>();
        if (systemOne is null)
            return (verdicts, receipts);

        var budget = Math.Max(systemOne.Options.MaxStateChars, 256);
        var batches = SystemOnePacking.Pack(candidates, budget, RenderCandidate);
        for (var index = 0; index < batches.Count; index++)
        {
            var batch = batches[index];
            var questions = BuildQuestions(batch, criteriaFor);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var response = await systemOne.AskAsync(BuildState(batch), questions, ct);
            stopwatch.Stop();

            receipts.Add(
                new RelationshipScanReceipt(
                    Batch: index + 1,
                    Questions: batch.Count,
                    Ok: response is not null,
                    Model: response?.Model ?? "",
                    InputTokens: response?.InputTokens ?? 0,
                    OutputTokens: response?.OutputTokens ?? 0,
                    ElapsedMs: stopwatch.ElapsedMilliseconds
                )
            );
            if (response is null)
                continue;

            foreach (var candidate in batch)
            {
                if (response.Answers.TryGetValue(candidate.CandidateId, out var answer))
                    verdicts[candidate.CandidateId] = answer;
            }
        }
        return (verdicts, receipts);
    }

    private static Dictionary<string, object?> BuildState(List<RelationCandidate> batch)
    {
        var candidates = new Dictionary<string, object?>(batch.Count, StringComparer.Ordinal);
        foreach (var candidate in batch)
        {
            candidates[candidate.CandidateId] = new Dictionary<string, object?>(
                StringComparer.Ordinal
            )
            {
                ["sentence"] = Truncate(candidate.EvidenceText, EvidenceChars),
                ["a"] = candidate.SourceName,
                ["a-type"] = candidate.SourceEntityType.ToKebabCaseString(),
                ["b"] = candidate.TargetName,
                ["b-type"] = candidate.TargetEntityType.ToKebabCaseString(),
                ["chapter"] = $"第{candidate.ChapterOrder}章",
            };
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["candidates"] = candidates,
        };
    }

    private static Dictionary<string, SystemOneQuestion> BuildQuestions(
        List<RelationCandidate> batch,
        Func<RelationCandidate, Dictionary<string, string?>> criteriaFor
    )
    {
        var questions = new Dictionary<string, SystemOneQuestion>(
            batch.Count,
            StringComparer.Ordinal
        );
        foreach (var candidate in batch)
        {
            questions[candidate.CandidateId] = new ChoiceQuestion
            {
                Instructions =
                    $"根据证据句子判断 A（{candidate.SourceName}，{candidate.SourceEntityType.ToKebabCaseString()}）"
                    + $"与 B（{candidate.TargetName}，{candidate.TargetEntityType.ToKebabCaseString()}）"
                    + "之间表达的关系类型；只能选 criteria 中的一项。",
                Criteria = criteriaFor(candidate),
            };
        }
        return questions;
    }

    private static string RenderCandidate(RelationCandidate candidate) =>
        string.Concat(
            "{\"sentence\":",
            System.Text.Json.JsonSerializer.Serialize(
                Truncate(candidate.EvidenceText, EvidenceChars)
            ),
            ",\"a\":",
            System.Text.Json.JsonSerializer.Serialize(candidate.SourceName),
            ",\"b\":",
            System.Text.Json.JsonSerializer.Serialize(candidate.TargetName),
            "}"
        );

    /// <summary>概率口径：优先取所选 token 的概率分布值；缺分布时退回整体置信度（旧行为）。</summary>
    private static double? ResolveProbability(SystemOneAnswer answer) =>
        answer.Probabilities is { } map
        && answer.Choice is { } selected
        && map.TryGetValue(selected, out var p)
            ? p
            : answer.Confidence;

    private static string CandidateLabel(RelationCandidate candidate) =>
        $"{candidate.SourceName}[{candidate.SourceEntityType.ToKebabCaseString()}] → "
        + $"{candidate.TargetName}[{candidate.TargetEntityType.ToKebabCaseString()}]";

    private static string RowLabel(GateRow row) =>
        row.Type is { } type ? $"{type} {TypeLabel(type)}" : "无法判定";

    private static string TypeLabel(string type) =>
        RelationshipOntology.ByTokenOrNull(type)?.Label ?? type;

    private static string Format(double? probability) =>
        probability?.ToString("0.00", CultureInfo.InvariantCulture) ?? "?";

    private static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars];

    private static AgentToolResult<RelationshipScanDetails> OkResult(
        string message,
        RelationshipScanDetails details
    ) => new() { Content = [new TextContent(message)], Details = details };

    private static AgentToolResult<RelationshipScanDetails> ErrorResult(string message) =>
        new() { Content = [new TextContent(message)], IsError = true };

    /// <summary>闸门状态（对齐 jevgraph builder 的 proposed / review / rejected）。</summary>
    private enum GateState
    {
        Proposed,
        Review,
        Rejected,
    }

    /// <summary>闸门结果：状态 + 原因码 + 所选 token 与两个概率口径。</summary>
    private sealed record GateRow(
        GateState State,
        string Reason,
        string? Type,
        double? Probability,
        double? Confidence
    );
}

/// <summary>scan_relationships 参数。</summary>
public sealed record RelationshipScanParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("要扫描的章节ID列表（逗号分隔）；留空则扫描最近 3 个有正文章节")]
    [JsonConverter(typeof(ChapterIdsConverter))]
    public List<string>? ChapterIds { get; init; }

    [Description("本次最多判定的候选对数量（0 = 用配置默认值）")]
    public int MaxCandidates { get; init; }
}

/// <summary>单批判定的回执（jevgraph RequestReceipt 的最小镜像：模型/tokens/耗时/成败）。</summary>
public sealed record RelationshipScanReceipt(
    int Batch,
    int Questions,
    bool Ok,
    string Model,
    long InputTokens,
    long OutputTokens,
    long ElapsedMs
);

/// <summary>被确定性闸门拒绝的一条候选（只读审计视图：定位 + 原因码，不含原句）。</summary>
public sealed record RelationshipScanRejected(
    string CandidateId,
    int ChapterOrder,
    int EvidenceStart,
    int EvidenceEnd,
    string Reason,
    string? Type
);

/// <summary>scan_relationships 结果统计。</summary>
public sealed record RelationshipScanDetails
{
    /// <summary>建议（proposed）条数。</summary>
    public int ProposedCount { get; init; }

    /// <summary>待人工确认（review）条数。</summary>
    public int ReviewCount { get; init; }

    /// <summary>已拒绝（none / 超界 / 低置信 / 缺答案）条数。</summary>
    public int RejectedCount { get; init; }

    /// <summary>已拒绝明细（定位 + 原因码；不含原句），供审计与调参。</summary>
    public IReadOnlyList<RelationshipScanRejected> Rejected { get; init; } = [];

    /// <summary>逐批判定回执（模型/tokens/耗时/成败）；空 = 未发生判定调用。</summary>
    public IReadOnlyList<RelationshipScanReceipt> Receipts { get; init; } = [];
}
