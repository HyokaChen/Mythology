using System.ComponentModel;
using System.Text;
using System.Text.Json.Serialization;
using Core.Agent;
using Core.Data;
using Core.Evolution;
using Core.Inference;
using Core.Shared;
using Core.Tools;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 本体变化提案（jevgraph 管线）：确定性候选 → Jev 闭集判定 → 证据/阈值闸门。
/// 只提案不写库；state 用 record_evolution 应用，rule 用 plan_scene_deltas 或忽略。
/// </summary>
public sealed class ScanOntologyTool(
    INovelStore store,
    ISystemOneClient? systemOne = null,
    Agent? agent = null
) : AgentTool<ScanOntologyParams, ScanOntologyDetails>
{
    private const int DefaultChapterCount = 3;

    public override string Name => "scan_ontology";
    public override string Label => "本体变化提案";
    public override string[] Aliases => ["本体扫描", "状态提案", "规则触发扫描", "ontology scan"];
    public override string[] ExampleQueries =>
        ["扫描最近三章有哪些状态变化和规则触发", "看看这章有没有违反宗门规则"];
    public override string Description =>
        "从最近章节提取本体变化提案（状态切面/规则触发），本地 Jev 闭集判定 + 证据闸门；"
        + "只提案不写库，确认后用 record_evolution / plan_scene_deltas 写入。";
    public override ToolCategory Category => ToolCategory.EntityRead;
    public override bool DeferLoading => true;

    public override async Task<AgentToolResult<ScanOntologyDetails>> ExecuteAsync(
        string toolCallId,
        ScanOntologyParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<ScanOntologyDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var judge = new OntologyProposalJudge(systemOne);
        if (!judge.Enabled)
        {
            return Error(
                "本地判定模型未启用：请配置 system-one.enabled 与 system-one.ontology.enabled"
            );
        }

        var novelId = args.NovelId?.Trim();
        if (string.IsNullOrWhiteSpace(novelId))
            novelId = agent?.CurrentNovelId;
        if (string.IsNullOrWhiteSpace(novelId))
            return Error("novel-id 必填（或先打开小说）");

        var elements = ParseElements(args.Elements);
        if (elements.Error is not null)
            return Error(elements.Error);

        try
        {
            var novel = await store.GetNovel(novelId, ct);
            if (novel is null)
                return Error($"未找到小说（novel_id={novelId}）");

            var allChapters = await store.GetChapters(novelId, ct);
            var selected = (
                args.ChapterIds is { Count: > 0 }
                    ? allChapters.Where(c => args.ChapterIds.Contains(c.Id))
                    : allChapters
            )
                .Where(c => !string.IsNullOrWhiteSpace(c.Content))
                .OrderBy(c => c.Order)
                .ToList();
            if (selected.Count == 0)
                return Error("没有可扫描的正文章节");
            if (args.ChapterIds is not { Count: > 0 } && selected.Count > DefaultChapterCount)
                selected = selected[^DefaultChapterCount..];

            var worldRules = await store.GetWorldRules(novelId, ct);
            var rules = worldRules.Where(r => r.EntryKind == WorldRuleEntryKind.Rule).ToList();
            var proposals = new List<OntologyProposal>();
            var receipts = new List<OntologyProposalReceipt>();
            foreach (var chapter in selected)
            {
                ThrowIfCancelled(ct);
                var appearances = await store.GetAppearancesByChapter(novelId, chapter.Id, ct);
                var (chapterProposals, chapterReceipts) = await judge.JudgeChapterAsync(
                    novel,
                    chapter,
                    appearances,
                    rules,
                    ct
                );
                proposals.AddRange(
                    chapterProposals.Where(p =>
                        elements.State && p.Element == OntologyElement.State
                        || elements.Rule && p.Element == OntologyElement.Rule
                    )
                );
                receipts.AddRange(chapterReceipts);
            }

            var max = args.MaxProposals <= 0 ? 12 : Math.Clamp(args.MaxProposals, 1, 50);
            var proposed = proposals
                .Where(p => p.Status == OntologyProposalStatus.Proposed)
                .OrderByDescending(p => p.Probability ?? 0)
                .Take(max)
                .ToList();
            var review = proposals
                .Where(p => p.Status == OntologyProposalStatus.Review)
                .Take(max)
                .ToList();
            var rejectedCount = proposals.Count(p => p.Status == OntologyProposalStatus.Rejected);

            var text = new StringBuilder();
            if (proposed.Count > 0)
            {
                text.AppendLine($"【建议】({proposed.Count})");
                var index = 0;
                foreach (var proposal in proposed)
                {
                    index++;
                    text.AppendLine(
                        $"{index}. {proposal.Element.ToString().ToLowerInvariant()} | "
                            + $"{proposal.TargetName} {ProposalField(proposal)} | "
                            + $"第{OrderOf(selected, proposal.ChapterId)}章 | "
                            + $"p={Format(proposal.Probability)} | 证据「{Truncate(proposal.Evidence, 80)}」"
                    );
                    text.AppendLine(
                        proposal.Element == OntologyElement.State
                            ? proposal.Field == "status" && proposal.To is { Length: > 0 } status
                                ? $"   应用：record_evolution(target_type=\"{proposal.TargetEntityType.ToKebabCaseString()}\", target_id=\"{proposal.TargetId}\", chapter_id=\"{proposal.ChapterId}\", aspect=\"status\", effects=[{{target_type:\"{proposal.TargetEntityType.ToKebabCaseString()}\", target_id:\"{proposal.TargetId}\", field:\"status\", operation:\"set\", value:\"{status}\"}}], cause:\"…\", evidence:\"…\", proposal_id:\"{proposal.Id}\")"
                                : $"   应用：record_evolution(target_id=\"{proposal.TargetId}\", chapter_id=\"{proposal.ChapterId}\", aspect=\"{(proposal.Field ?? "other")}\", evidence=\"…\", proposal_id=\"{proposal.Id}\")"
                            : $"   应用：plan_scene_deltas(scene_id=\"…\", deltas=[{{kind:\"rule-trigger\", target_id:\"{proposal.TargetId}\", rule_id:\"{proposal.RuleId}\", summary:\"…\"}}]) 或忽略"
                    );
                }
            }
            if (review.Count > 0)
            {
                text.AppendLine($"【待确认】({review.Count})");
                foreach (var proposal in review)
                {
                    text.AppendLine(
                        $"- {proposal.Element.ToString().ToLowerInvariant()} | {proposal.TargetName} "
                            + $"| 第{OrderOf(selected, proposal.ChapterId)}章 | reason={proposal.ReasonCode}"
                    );
                }
            }
            if (rejectedCount > 0)
            {
                text.AppendLine($"【已拒绝】({rejectedCount})");
            }
            text.AppendLine(
                "（本工具不写库。state 用 record_evolution；rule 用 plan_scene_deltas 写入计划或忽略；"
                    + "关系用 scan_relationships → manage_relationship。）"
            );
            if (receipts.Count > 0)
            {
                text.AppendLine(
                    $"（判定批次 {receipts.Count}：成功 {receipts.Count(r => r.Ok)} / "
                        + $"tokens {receipts.Sum(r => r.InputTokens)}→{receipts.Sum(r => r.OutputTokens)} / "
                        + $"耗时 {receipts.Sum(r => r.ElapsedMs)}ms / model {receipts[^1].Model}）"
                );
            }

            return new AgentToolResult<ScanOntologyDetails>
            {
                Content = [new TextContent(text.ToString().TrimEnd())],
                Details = new ScanOntologyDetails
                {
                    ProposedCount = proposed.Count,
                    ReviewCount = review.Count,
                    RejectedCount = rejectedCount,
                    Proposals = proposals,
                    Receipts = receipts,
                },
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Error($"本体扫描失败：{ex.Message}");
        }
    }

    private static (bool State, bool Rule, string? Error) ParseElements(
        IReadOnlyList<string>? elements
    )
    {
        if (elements is null || elements.Count == 0)
        {
            return (true, true, null);
        }
        var state = false;
        var rule = false;
        foreach (var raw in elements)
        {
            var value = raw?.Trim().ToLowerInvariant() ?? "";
            switch (value)
            {
                case "state":
                    state = true;
                    break;
                case "rule":
                    rule = true;
                    break;
                case "relation":
                    break;
                case "":
                    break;
                default:
                    return (false, false, $"未知要素: {raw}（支持 state / rule）");
            }
        }
        if (!state && !rule)
        {
            return (false, false, "关系提案由 scan_relationships 提供（jevgraph 管线已实现）");
        }
        return (state, rule, null);
    }

    private static string ProposalField(OntologyProposal proposal) =>
        proposal.Element == OntologyElement.Rule ? "规则触发"
        : proposal.Field is { Length: > 0 } field ? field
        : "本体变化";

    private static int OrderOf(IReadOnlyList<Chapter> chapters, string chapterId) =>
        chapters.FirstOrDefault(c => c.Id == chapterId)?.Order ?? 0;

    private static string Format(double? value) =>
        value?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? "?";

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    private static AgentToolResult<ScanOntologyDetails> Error(string message) =>
        new() { Content = [new TextContent(message)], IsError = true };
}

public sealed record ScanOntologyParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("要扫描的章节ID列表（逗号分隔）；留空则最近 3 个有正文章节")]
    [JsonConverter(typeof(ChapterIdsConverter))]
    public List<string>? ChapterIds { get; init; }

    [Description("要扫描的要素：state / rule（默认两者）；关系提案请用 scan_relationships")]
    public List<string>? Elements { get; init; }

    [Description("最多返回的提案数（默认 12）")]
    public int MaxProposals { get; init; }
}

public sealed record ScanOntologyDetails
{
    public int ProposedCount { get; init; }
    public int ReviewCount { get; init; }
    public int RejectedCount { get; init; }
    public IReadOnlyList<OntologyProposal> Proposals { get; init; } = [];
    public IReadOnlyList<OntologyProposalReceipt> Receipts { get; init; } = [];
}
