using System.ComponentModel;
using System.Text;
using Core.Agent;
using Core.Data;
using Core.Shared;
using Core.Tools;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 写作统计工具：获取小说写作统计数据（总览、章节进度、角色活跃度、情节线覆盖、历史验证战术）
/// </summary>
public sealed class GetWritingStatsTool(INovelStore store, TacticLedger? ledger = null)
    : AgentTool<GetWritingStatsParams, WritingStatsDetails>
{
    /// <summary>工具唯一标识符</summary>
    public override string Name => "get_writing_stats";

    /// <summary>工具显示名称</summary>
    public override string Label => "写作统计";
    public override string Description =>
        "获取小说写作统计数据：总览、章节进度、角色活跃度、情节线覆盖、历史验证战术";
    public override ToolCategory Category => ToolCategory.Generic;

    /// <summary>
    /// 执行写作统计
    /// </summary>
    /// <param name="toolCallId">工具调用ID</param>
    /// <param name="args">统计参数</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="onUpdate">进度更新回调</param>
    /// <returns>统计结果</returns>
    public override async Task<AgentToolResult<WritingStatsDetails>> ExecuteAsync(
        string toolCallId,
        GetWritingStatsParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<WritingStatsDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var scope = args.Scope?.ToLowerInvariant() ?? "overview";

        // 历史验证战术：不触达任何 store.* —— 必须在实体扇出之前早退（否则白付 7 次 IO）。
        if (scope == "tactics")
            return BuildTacticsResult(args);

        var novel = await store.GetNovelSummary(args.NovelId, ct);
        ThrowIfCancelled(ct);
        if (novel is null)
            return new AgentToolResult<WritingStatsDetails>
            {
                Content = [new TextContent($"小说 {args.NovelId} 不存在")],
                IsError = true,
            };

        var stats = new WritingStatsDetails { NovelId = novel.Id, Title = novel.Title };

        // 总览始终包含 — 并行加载所有实体数据
        var allChaptersTask = store.GetChapters(args.NovelId, ct);
        var allCharactersTask = store.GetCharacters(args.NovelId, ct);
        var allPlotlinesTask = store.GetPlotlines(args.NovelId, ct);
        var allFactionsTask = store.GetFactions(args.NovelId, ct);
        var allLocationsTask = store.GetLocations(args.NovelId, ct);
        var allItemsTask = store.GetItems(args.NovelId, ct);
        var allRelationshipsTask = store.GetRelationshipsByNovel(args.NovelId, ct);
        await Task.WhenAll(
            allChaptersTask,
            allCharactersTask,
            allPlotlinesTask,
            allFactionsTask,
            allLocationsTask,
            allItemsTask,
            allRelationshipsTask
        );
        ThrowIfCancelled(ct);

        var allChapters = allChaptersTask.Result;
        var allCharacters = allCharactersTask.Result;
        var allPlotlines = allPlotlinesTask.Result;
        var allFactions = allFactionsTask.Result;
        var allLocations = allLocationsTask.Result;
        var allItems = allItemsTask.Result;
        var allRelationships = allRelationshipsTask.Result;

        stats.TotalChapters = allChapters.Count;
        stats.DraftChapters = allChapters.Count(c => c.Status == ChapterStatus.Draft);
        stats.PublishedChapters = allChapters.Count(c => c.Status == ChapterStatus.Published);
        stats.TotalWords = allChapters.Sum(c => c.Content.Length);
        stats.TotalCharacters = allCharacters.Count;
        stats.TotalPlotlines = allPlotlines.Count;
        stats.OpenPlotlines = allPlotlines.Count(p => p.ResolvedAtChapterId is null);
        stats.TotalFactions = allFactions.Count;
        stats.TotalLocations = allLocations.Count;
        stats.TotalItems = allItems.Count;
        stats.TotalRelationships = allRelationships.Count;

        // 章节进度
        if (scope is "chapters" or "overview")
        {
            stats.ChapterProgress = allChapters
                .OrderBy(c => c.Order)
                .Select(c => new ChapterProgressEntry
                {
                    ChapterId = c.Id,
                    Title = c.Title,
                    Order = c.Order,
                    Status = c.Status.ToString(),
                    WordCount = c.Content.Length,
                })
                .ToList();
        }

        // 角色活跃度
        if (scope is "characters" or "overview")
        {
            var activity = new List<CharacterActivityEntry>();
            foreach (var character in allCharacters)
            {
                var appearanceCount = 0;
                var lastSeenChapter = "";
                foreach (var chapter in allChapters.OrderBy(c => c.Order))
                {
                    if (
                        chapter.Content.Contains(character.Name, StringComparison.OrdinalIgnoreCase)
                    )
                    {
                        appearanceCount++;
                        lastSeenChapter = $"第{chapter.Order}章";
                    }
                }

                activity.Add(
                    new CharacterActivityEntry
                    {
                        CharacterId = character.Id,
                        Name = character.Name,
                        AppearanceCount = appearanceCount,
                        LastSeenChapter = lastSeenChapter,
                    }
                );
            }
            stats.CharacterActivity = activity.OrderByDescending(a => a.AppearanceCount).ToList();
        }

        // 情节线覆盖
        if (scope is "plotlines" or "overview")
        {
            stats.PlotlineCoverage = allPlotlines
                .Select(p =>
                {
                    var plannedCh =
                        p.PlannedRevealChapterId != null
                            ? allChapters.FirstOrDefault(c => c.Id == p.PlannedRevealChapterId)
                            : null;
                    var resolvedCh =
                        p.ResolvedAtChapterId != null
                            ? allChapters.FirstOrDefault(c => c.Id == p.ResolvedAtChapterId)
                            : null;
                    var plannedOrder = plannedCh?.Order ?? 0;
                    var status =
                        p.ResolvedAtChapterId is not null ? "resolved"
                        : plannedOrder > 0 && allChapters.Count >= plannedOrder ? "overdue"
                        : "open";
                    return new PlotlineCoverageEntry
                    {
                        PlotlineId = p.Id,
                        Name = p.Name,
                        Type = p.Type.ToString(),
                        Status = status,
                        TargetChapterId = p.PlannedRevealChapterId,
                        TargetChapterLabel =
                            plannedCh != null ? $"第{plannedCh.Order}章 {plannedCh.Title}" : null,
                        ResolvedChapterId = p.ResolvedAtChapterId,
                        ResolvedChapterLabel =
                            resolvedCh != null
                                ? $"第{resolvedCh.Order}章 {resolvedCh.Title}"
                                : null,
                    };
                })
                .ToList();
        }

        var report = BuildReportText(stats);
        return new AgentToolResult<WritingStatsDetails>
        {
            Content = [new TextContent(report)],
            Details = stats,
        };
    }

    /// <summary>
    /// 历史验证战术分支（双速自进化账本快照；不读文件、不触达存储）：
    /// 按 Laplace 胜率降序取前 50 条。
    /// </summary>
    private AgentToolResult<WritingStatsDetails> BuildTacticsResult(GetWritingStatsParams args)
    {
        if (ledger is null)
            return new AgentToolResult<WritingStatsDetails>
            {
                Content = [new TextContent("历史验证战术未启用（进化账本未装配）")],
            };

        var tactics = ledger
            .Snapshot.OrderByDescending(t => t.WinRate)
            .ThenBy(t => t.Id, StringComparer.Ordinal)
            .Take(50)
            .Select(t => new TacticStatLine(t.Id, t.Trigger, t.Genre, t.WinRate, t.Uses, t.Status))
            .ToList();

        if (tactics.Count == 0)
            return new AgentToolResult<WritingStatsDetails>
            {
                Content = [new TextContent("尚无历史验证战术")],
                Details = new WritingStatsDetails { NovelId = args.NovelId, Tactics = tactics },
            };

        var sb = new StringBuilder();
        sb.AppendLine($"## 🎯 历史验证战术（{tactics.Count} 条）");
        sb.AppendLine();
        sb.AppendLine("| 战术 | 触发规则 | 题材 | 胜率 | 使用 | 状态 |");
        sb.AppendLine("|------|---------|------|------|------|------|");
        foreach (var tactic in tactics)
            sb.AppendLine(
                $"| {tactic.Id} | {tactic.Trigger} | {tactic.Genre} | {tactic.WinRate * 100:F0}% | {tactic.Uses} 次 | {tactic.Status} |"
            );

        return new AgentToolResult<WritingStatsDetails>
        {
            Content = [new TextContent(sb.ToString())],
            Details = new WritingStatsDetails { NovelId = args.NovelId, Tactics = tactics },
        };
    }

    /// <summary>
    /// 构建统计报告文本
    /// </summary>
    /// <param name="s">写作统计详情</param>
    /// <returns>格式化报告文本</returns>
    private static string BuildReportText(WritingStatsDetails s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## 📊 写作统计: {s.Title}");
        sb.AppendLine();

        var progress =
            s.TotalChapters > 0 ? (double)s.PublishedChapters / s.TotalChapters * 100 : 0;
        sb.AppendLine("### 📈 总览");
        sb.AppendLine();
        sb.AppendLine("| 指标 | 值 |");
        sb.AppendLine("|------|-----|");
        sb.AppendLine($"| 总字数 | {s.TotalWords} |");
        sb.AppendLine(
            $"| 章节 | {s.TotalChapters} (草稿: {s.DraftChapters}, 发布: {s.PublishedChapters}) |"
        );
        sb.AppendLine($"| 进度 | {progress:F1}% |");
        sb.AppendLine($"| 角色 | {s.TotalCharacters} |");
        sb.AppendLine($"| 情节线 | {s.TotalPlotlines} (开放: {s.OpenPlotlines}) |");
        sb.AppendLine($"| 组织 | {s.TotalFactions} |");
        sb.AppendLine($"| 地点 | {s.TotalLocations} |");
        sb.AppendLine($"| 道具 | {s.TotalItems} |");
        sb.AppendLine($"| 关系 | {s.TotalRelationships} |");

        if (s.ChapterProgress.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### 📑 章节进度");
            sb.AppendLine();
            sb.AppendLine("| 章 | 标题 | 状态 | 字数 |");
            sb.AppendLine("|---|------|------|------|");
            foreach (var cp in s.ChapterProgress)
            {
                var statusEmoji = cp.Status switch
                {
                    "Final" => "✅",
                    "Review" => "📝",
                    _ => "📄",
                };
                sb.AppendLine(
                    $"| {cp.Order} | {cp.Title} | {statusEmoji} {cp.Status} | {cp.WordCount} |"
                );
            }
        }

        if (s.CharacterActivity.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### 🧑 角色活跃度");
            sb.AppendLine();
            sb.AppendLine("| 角色 | 出场章节 | 最后出现 |");
            sb.AppendLine("|------|---------|---------|");
            foreach (var ca in s.CharacterActivity.Take(10))
                sb.AppendLine($"| {ca.Name} | {ca.AppearanceCount} 章 | {ca.LastSeenChapter} |");
        }

        if (s.PlotlineCoverage.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### 🔗 情节线覆盖");
            sb.AppendLine();
            sb.AppendLine("| 名称 | 类型 | 状态 | 目标章节 |");
            sb.AppendLine("|------|------|------|---------|");
            foreach (var pc in s.PlotlineCoverage)
            {
                var statusEmoji = pc.Status switch
                {
                    "resolved" => "✅",
                    "overdue" => "🔴",
                    _ => "🟡",
                };
                var target = pc.TargetChapterLabel ?? pc.TargetChapterId ?? "-";
                sb.AppendLine($"| {pc.Name} | {pc.Type} | {statusEmoji} {pc.Status} | {target} |");
            }
        }

        return sb.ToString();
    }
}

public sealed record GetWritingStatsParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description(
        "统计维度：overview（总览）/ chapters（章节进度）/ characters（角色活跃度）/ plotlines（情节线覆盖）/ tactics（历史验证战术）"
    )]
    public string? Scope { get; init; }
}

public sealed record WritingStatsDetails
{
    public string NovelId { get; set; } = "";
    public string Title { get; set; } = "";
    public int TotalChapters { get; set; }
    public int DraftChapters { get; set; }
    public int PublishedChapters { get; set; }
    public int TotalWords { get; set; }
    public int TotalCharacters { get; set; }
    public int TotalPlotlines { get; set; }
    public int OpenPlotlines { get; set; }
    public int TotalFactions { get; set; }
    public int TotalLocations { get; set; }
    public int TotalItems { get; set; }
    public int TotalRelationships { get; set; }
    public List<ChapterProgressEntry> ChapterProgress { get; set; } = [];
    public List<CharacterActivityEntry> CharacterActivity { get; set; } = [];
    public List<PlotlineCoverageEntry> PlotlineCoverage { get; set; } = [];

    /// <summary>历史验证战术（tactics 档；其余档为 null）。</summary>
    public IReadOnlyList<TacticStatLine>? Tactics { get; set; }
}

/// <summary>历史验证战术行（值类型，避免每次查询为列表元素分配）。</summary>
public readonly record struct TacticStatLine(
    string Id,
    string Trigger,
    string Genre,
    double WinRate,
    int Uses,
    TacticStatus Status
);

public sealed record ChapterProgressEntry
{
    public string ChapterId { get; set; } = "";
    public string Title { get; set; } = "";
    public int Order { get; set; }
    public string Status { get; set; } = "";
    public int WordCount { get; set; }
}

public sealed record CharacterActivityEntry
{
    public string CharacterId { get; set; } = "";
    public string Name { get; set; } = "";
    public int AppearanceCount { get; set; }
    public string LastSeenChapter { get; set; } = "";
}

public sealed record PlotlineCoverageEntry
{
    public string PlotlineId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Status { get; set; } = "";
    public string? TargetChapterId { get; set; }
    public string? TargetChapterLabel { get; set; }
    public string? ResolvedChapterId { get; set; }
    public string? ResolvedChapterLabel { get; set; }
}
