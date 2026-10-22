using System.ComponentModel;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Core.Vector;

namespace Plugins.NovelTools.Tools;

// ── 独立保留的领域行为工具（未被 manage_* 吸收） ──

// ── 推进情节线（领域行为） ──

/// <summary>
/// 推进情节线工具：记录情节线在某一章节的进展或解决
/// </summary>
public sealed class AdvancePlotlineTool(INovelStore store, RetrievalKernel? kernel = null)
    : AgentTool<AdvancePlotlineParams, PlotlineDetails>
{
    public override string Name => "advance_plotline";
    public override string[] Aliases =>
        ["推进情节线", "情节进展", "追踪情节", "剧情进展", "推进主线", "剧情推进", "暗线推进"];
    public override string[] ExampleQueries => ["推进主线到下一个节点", "追踪剧情暗线进展"];

    /// <summary>工具显示名称</summary>
    public override string Label => "推进情节线";
    public override string Description => "记录情节线在某一章节的进展或解决";
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突
    public override ToolCategory Category => ToolCategory.EntityCrud;
    public override string EntityType => "plotline";

    /// <summary>
    /// 执行推进情节线操作
    /// </summary>
    /// <param name="toolCallId">工具调用ID</param>
    /// <param name="args">推进参数</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="onUpdate">进度更新回调</param>
    /// <returns>推进结果</returns>
    public override async Task<AgentToolResult<PlotlineDetails>> ExecuteAsync(
        string toolCallId,
        AdvancePlotlineParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<PlotlineDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var plotline = await ResolvePlotline(args, ct);
        ThrowIfCancelled(ct);
        if (plotline is null)
            return new AgentToolResult<PlotlineDetails>
            {
                Content =
                [
                    new TextContent(
                        $"情节线 {args.PlotlineId} 不存在（可用 manage_plotline list 查看 ID 或直接传名称）"
                    ),
                ],
                IsError = true,
            };
        var changes = new List<string>();
        var chapters = await store.GetChapters(plotline.NovelId, ct);
        var chMap = chapters.ToDictionary(c => c.Id, c => $"第{c.Order}章 {c.Title}");
        if (
            args.ResolvedAtChapterId != null
            && args.ResolvedAtChapterId != plotline.ResolvedAtChapterId
        )
        {
            var oldLabel =
                plotline.ResolvedAtChapterId != null
                && chMap.TryGetValue(plotline.ResolvedAtChapterId, out var oldCh)
                    ? oldCh
                    : "未解决";
            var newLabel = chMap.TryGetValue(args.ResolvedAtChapterId, out var newCh)
                ? newCh
                : args.ResolvedAtChapterId;
            changes.Add($"解决章节: {oldLabel} → {newLabel}");
            plotline.ResolvedAtChapterId = args.ResolvedAtChapterId;
        }
        if (args.ProgressNote is not null)
        {
            changes.Add($"进展: + {ManageHelper.Diff(args.ProgressNote)}");
            plotline.Description += $"\n[进展] {args.ProgressNote}";
        }
        if (!string.IsNullOrEmpty(args.Turnpoint))
        {
            plotline.Turnpoints = string.IsNullOrEmpty(plotline.Turnpoints)
                ? args.Turnpoint
                : $"{plotline.Turnpoints}；{args.Turnpoint}";
            changes.Add($"关键转折: + {args.Turnpoint}");
        }
        await store.SavePlotline(plotline, ct);
        ThrowIfCancelled(ct);
        if (kernel is not null)
        {
            var (doc, chunks) = kernel.Builder.BuildPlotline(plotline);
            await kernel.IndexDocumentAsync(doc, chunks, ct);
        }
        ThrowIfCancelled(ct);
        var summary =
            changes.Count > 0
                ? $"情节线「{plotline.Name}」已更新:\n{string.Join("\n", changes)}"
                : $"情节线「{plotline.Name}」无变更";
        return new AgentToolResult<PlotlineDetails>
        {
            Content = [new TextContent(summary)],
            Details = new PlotlineDetails { Id = plotline.Id, Name = plotline.Name },
        };
    }

    /// <summary>
    /// 将情节线 ID 或名称解析为真实情节线：优先精确 ID（全局唯一），
    /// 名称匹配时按 novel_id 缩小范围，缺省扫描全部小说。
    /// </summary>
    private async Task<Plotline?> ResolvePlotline(AdvancePlotlineParams args, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(args.PlotlineId))
            return null;
        var plotlines = new List<Plotline>();
        if (!string.IsNullOrEmpty(args.NovelId))
        {
            plotlines = await store.GetPlotlines(args.NovelId, ct);
        }
        else
        {
            var novels = await store.GetAllNovels(ct);
            foreach (var novel in novels)
                plotlines.AddRange(await store.GetPlotlines(novel.Id, ct));
        }
        return ManageHelper.ResolveByIdOrName(plotlines, args.PlotlineId, p => p.Id, p => p.Name);
    }
}

public sealed record AdvancePlotlineParams
{
    [Description("小说项目ID（传情节线名称时建议提供，用于缩小范围；缺省扫描全部小说）")]
    public string NovelId { get; init; } = "";

    [Description("情节线ID或名称")]
    public string PlotlineId { get; init; } = "";

    [Description("可指定解决的章节ID，设置后标记为已解决")]
    public string? ResolvedAtChapterId { get; init; }

    [Description("进展说明，描述当前进展内容")]
    public string? ProgressNote { get; init; }

    [Description("关键转折，追加到情节线的转折记录中")]
    public string? Turnpoint { get; init; }
}

public sealed record PlotlineDetails
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
}

// ── 检索伏笔（领域行为） ──

/// <summary>
/// 检索伏笔工具：根据当前上下文（章节编号）检索应回收的伏笔/暗线
/// </summary>
public sealed class RecallForeshadowingTool(INovelStore store)
    : AgentTool<RecallForeshadowingParams, RecallForeshadowingDetails>
{
    /// <summary>工具唯一标识符</summary>
    public override string Name => "recall_foreshadowing";
    public override string[] Aliases => ["伏笔", "埋伏笔", "伏笔召回"];
    public override string[] ExampleQueries => ["回忆前面埋下的伏笔"];

    /// <summary>工具显示名称</summary>
    public override string Label => "检索伏笔";
    public override string Description => "根据当前上下文（章节编号）检索应回收的伏笔/暗线";
    public override ToolCategory Category => ToolCategory.SearchTool;

    /// <summary>
    /// 执行检索伏笔操作
    /// </summary>
    /// <param name="toolCallId">工具调用ID</param>
    /// <param name="args">检索参数</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="onUpdate">进度更新回调</param>
    /// <returns>检索结果</returns>
    public override async Task<AgentToolResult<RecallForeshadowingDetails>> ExecuteAsync(
        string toolCallId,
        RecallForeshadowingParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<RecallForeshadowingDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var plotlines = await store.GetOpenPlotlines(args.NovelId, ct);
        var chapters = await store.GetChapters(args.NovelId, ct);
        ThrowIfCancelled(ct);
        var chapterOrderMap = chapters.ToDictionary(c => c.Id, c => c.Order);
        var currentChapterOrder = ManageHelper.ResolveChapterOrder(
            chapters,
            args.CurrentChapter,
            chapters.Count > 0 ? chapters.Max(c => c.Order) : 0
        );

        var relevant = plotlines
            .Select(p => new
            {
                Plotline = p,
                Urgency = p.PlannedRevealChapterId != null
                && chapterOrderMap.TryGetValue(p.PlannedRevealChapterId, out var order)
                    ? currentChapterOrder - order
                    : 0,
            })
            .OrderByDescending(x => x.Urgency)
            .ToList();

        if (relevant.Count == 0)
            return new AgentToolResult<RecallForeshadowingDetails>
            {
                Content = [new TextContent("暂无未回收的伏笔/暗线")],
                Details = new RecallForeshadowingDetails { Count = 0, OverdueCount = 0 },
            };

        var lines = new List<string>();
        var overdue = 0;
        foreach (var item in relevant)
        {
            var marker =
                item.Urgency > 0 ? " ⚠️ 已超期"
                : item.Urgency >= -2 ? " 🔔 即将到期"
                : "";
            var tensionMarker = item.Plotline.Tension switch
            {
                PlotlineTension.High => " [紧张度:高]",
                PlotlineTension.Critical => " [紧张度:临界]",
                _ => "",
            };
            var turnpointInfo = !string.IsNullOrEmpty(item.Plotline.Turnpoints)
                ? $" [转折:{item.Plotline.Turnpoints}]"
                : "";
            var chNameMap = chapters.ToDictionary(c => c.Id, c => $"第{c.Order}章 {c.Title}");
            var revealLabel =
                item.Plotline.PlannedRevealChapterId != null
                && chNameMap.TryGetValue(item.Plotline.PlannedRevealChapterId, out var rn)
                    ? rn
                    : "未设定";
            lines.Add(
                $"- {item.Plotline.Name} (类型: {item.Plotline.Type}, 计划揭示: {revealLabel}){marker}{tensionMarker}{turnpointInfo}"
            );
            if (item.Urgency > 0)
                overdue++;
        }

        return new AgentToolResult<RecallForeshadowingDetails>
        {
            Content =
            [
                new TextContent(
                    $"共 {relevant.Count} 条未回收伏笔（{overdue} 条已超期）:\n"
                        + string.Join("\n", lines)
                ),
            ],
            Details = new RecallForeshadowingDetails
            {
                Count = relevant.Count,
                OverdueCount = overdue,
            },
        };
    }
}

public sealed record RecallForeshadowingParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("当前正在写作的章节，支持章节ID或序号，用于计算哪些伏笔即将到期或已超期")]
    public string? CurrentChapter { get; init; }
}

public sealed record RecallForeshadowingDetails
{
    public int Count { get; init; }
    public int OverdueCount { get; init; }
}

// ── 更新组织关系（跨实体） ──

/// <summary>
/// 更新组织关系工具：设置两个组织之间的关系（敌对/同盟等）
/// </summary>
public sealed class UpdateFactionRelationshipTool(INovelStore store)
    : AgentTool<UpdateFactionRelationshipParams, FactionRelationshipDetails>
{
    /// <summary>工具唯一标识符</summary>
    public override string Name => "update_faction_relationship";

    /// <summary>工具显示名称</summary>
    public override string Label => "更新组织关系";
    public override string Description => "设置两个组织之间的关系（敌对/同盟等）";
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突
    public override ToolCategory Category => ToolCategory.EntityCrud;
    public override string EntityType => "faction";

    /// <summary>
    /// 执行更新组织关系操作
    /// </summary>
    /// <param name="toolCallId">工具调用ID</param>
    /// <param name="args">关系参数</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="onUpdate">进度更新回调</param>
    /// <returns>关系更新结果</returns>
    public override async Task<AgentToolResult<FactionRelationshipDetails>> ExecuteAsync(
        string toolCallId,
        UpdateFactionRelationshipParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<FactionRelationshipDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var rel = new FactionRelationship
        {
            NovelId = args.NovelId,
            FactionAId = args.FactionAId,
            FactionBId = args.FactionBId,
            Type = Enum.TryParse<FactionRelationType>(args.Type, ignoreCase: true, out var t)
                ? t
                : FactionRelationType.Neutral,
            Description = args.Description ?? "",
        };
        await store.SaveFactionRelationship(rel, ct);
        ThrowIfCancelled(ct);
        var summary = args.Type is not null
            ? $"组织关系已建立: {args.FactionAId} ↔ {args.FactionBId} ({rel.Type})"
            : $"组织关系已建立: {args.FactionAId} ↔ {args.FactionBId}";
        if (args.Description is not null)
            summary += $"\n描述: {ManageHelper.Diff(args.Description)}";
        return new AgentToolResult<FactionRelationshipDetails>
        {
            Content = [new TextContent(summary)],
            Details = new FactionRelationshipDetails { Id = rel.Id },
        };
    }
}

public sealed record UpdateFactionRelationshipParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("第一个组织ID")]
    public string FactionAId { get; init; } = "";

    [Description("第二个组织ID")]
    public string FactionBId { get; init; } = "";

    [Description(
        "关系类型：ally（同盟）/ enemy（敌对）/ neutral（中立）/ trade（贸易）/ vassal（附属）"
    )]
    public string? Type { get; init; }

    [Description("关系详细描述。写具体利益和冲突")]
    public string? Description { get; init; }
}

public sealed record FactionRelationshipDetails
{
    public string Id { get; init; } = "";
}

// ── 语义搜索地点（独立保留） ──

/// <summary>
/// 语义搜索地点工具：基于文本描述搜索匹配的地点
/// </summary>
public sealed class FindLocationByDescriptionTool(INovelStore store, RetrievalKernel? kernel = null)
    : AgentTool<FindLocationByDescriptionParams, FindLocationByDescriptionDetails>
{
    /// <summary>工具唯一标识符</summary>
    public override string Name => "find_location_by_description";

    /// <summary>工具显示名称</summary>
    public override string Label => "语义搜索地点";
    public override string Description => "基于文本描述搜索匹配的地点";
    public override ToolCategory Category => ToolCategory.EntityRead;
    public override string EntityType => "location";

    /// <summary>
    /// 执行语义搜索地点操作
    /// </summary>
    /// <param name="toolCallId">工具调用ID</param>
    /// <param name="args">搜索参数</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="onUpdate">进度更新回调</param>
    /// <returns>搜索结果</returns>
    public override async Task<AgentToolResult<FindLocationByDescriptionDetails>> ExecuteAsync(
        string toolCallId,
        FindLocationByDescriptionParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<FindLocationByDescriptionDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var query = args.Description?.ToLowerInvariant() ?? "";
        if (string.IsNullOrEmpty(query))
            return new AgentToolResult<FindLocationByDescriptionDetails>
            {
                Content = [new TextContent("请提供地点描述用于搜索")],
                IsError = true,
            };

        if (kernel is not null)
        {
            var results = await kernel.SearchAsync(
                new RetrievalSearchRequest
                {
                    Domain = RetrievalDomain.NovelKnowledge,
                    OwnerId = "local",
                    NovelId = args.NovelId,
                    Query = query,
                    DocumentKinds = [DocumentKind.LocationProfile],
                    FinalTopK = 5,
                },
                ct
            );
            ThrowIfCancelled(ct);
            if (results.Count > 0)
            {
                var semanticLines = results.Select(r =>
                    "- (score: " + r.Score.ToString("F2") + ") " + r.Text
                );
                return new AgentToolResult<FindLocationByDescriptionDetails>
                {
                    Content = [new TextContent(string.Join("\n", semanticLines))],
                    Details = new FindLocationByDescriptionDetails { Count = results.Count },
                };
            }
        }

        var locations = await store.GetLocations(args.NovelId, ct);
        ThrowIfCancelled(ct);
        var scored = locations
            .Select(l => new
            {
                Location = l,
                Score = l.Name.ToLowerInvariant().Split(' ').Count(query.Contains)
                    + l.Description.ToLowerInvariant().Split(' ').Count(query.Contains),
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(5)
            .ToList();

        if (scored.Count == 0)
            return new AgentToolResult<FindLocationByDescriptionDetails>
            {
                Content = [new TextContent("未找到匹配的地点")],
                Details = new FindLocationByDescriptionDetails { Count = 0 },
            };

        var lines = scored.Select(x =>
            $"- {x.Location.Name} (匹配度: {x.Score}): {x.Location.Description}"
        );

        return new AgentToolResult<FindLocationByDescriptionDetails>
        {
            Content = [new TextContent(string.Join("\n", lines))],
            Details = new FindLocationByDescriptionDetails { Count = scored.Count },
        };
    }
}

public sealed record FindLocationByDescriptionParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("用于搜索地点的文本描述")]
    public string? Description { get; init; }
}

public sealed record FindLocationByDescriptionDetails
{
    public int Count { get; init; }
}

// ── 转移道具（领域行为） ──

/// <summary>
/// 转移道具工具：记录道具所有权转移
/// </summary>
public sealed class TransferItemTool(INovelStore store, RetrievalKernel? kernel = null)
    : AgentTool<TransferItemParams, ItemDetails>
{
    /// <summary>工具唯一标识符</summary>
    public override string Name => "transfer_item";

    /// <summary>工具显示名称</summary>
    public override string Label => "转移道具";
    public override string Description => "记录道具所有权转移";
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突
    public override ToolCategory Category => ToolCategory.EntityCrud;
    public override string EntityType => "item";

    /// <summary>
    /// 执行转移道具操作
    /// </summary>
    /// <param name="toolCallId">工具调用ID</param>
    /// <param name="args">转移参数</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="onUpdate">进度更新回调</param>
    /// <returns>转移结果</returns>
    public override async Task<AgentToolResult<ItemDetails>> ExecuteAsync(
        string toolCallId,
        TransferItemParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<ItemDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var item = await ResolveItem(args, ct);
        ThrowIfCancelled(ct);
        if (item is null)
            return new AgentToolResult<ItemDetails>
            {
                Content =
                [
                    new TextContent(
                        $"道具 {args.ItemId} 不存在（可用 manage_item list 查看 ID 或直接传名称）"
                    ),
                ],
                IsError = true,
            };
        var oldOwnerId = string.IsNullOrEmpty(item.OwnerId) ? null : item.OwnerId;
        var oldOwner = oldOwnerId is null ? null : await ResolveOwner(args, oldOwnerId, ct);
        var newOwner = string.IsNullOrEmpty(args.NewOwnerId)
            ? null
            : await ResolveOwner(args, args.NewOwnerId, ct);
        var oldOwnerLabel = oldOwner?.Name ?? oldOwnerId ?? "无人";
        var newOwnerLabel =
            newOwner?.Name ?? (string.IsNullOrEmpty(args.NewOwnerId) ? "无人" : args.NewOwnerId);
        item.OwnerId =
            newOwner?.Id ?? (string.IsNullOrEmpty(args.NewOwnerId) ? null : args.NewOwnerId);
        await store.SaveItem(item, ct);
        ThrowIfCancelled(ct);
        if (kernel is not null)
        {
            var (doc, chunks) = kernel.Builder.BuildItem(item);
            await kernel.IndexDocumentAsync(doc, chunks, ct);
        }
        ThrowIfCancelled(ct);
        var summary =
            oldOwnerLabel != newOwnerLabel
                ? $"道具「{item.Name}」持有者: {oldOwnerLabel} → {newOwnerLabel}"
                : $"道具「{item.Name}」无变更";
        return new AgentToolResult<ItemDetails>
        {
            Content = [new TextContent(summary)],
            Details = new ItemDetails { Id = item.Id, Name = item.Name },
        };
    }

    /// <summary>
    /// 将道具 ID 或名称解析为真实道具：优先精确 ID（全局唯一），
    /// 名称匹配时按 novel_id 缩小范围，缺省扫描全部小说。
    /// </summary>
    private async Task<Item?> ResolveItem(TransferItemParams args, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(args.ItemId))
            return null;
        var items = new List<Item>();
        if (!string.IsNullOrEmpty(args.NovelId))
        {
            items = await store.GetItems(args.NovelId, ct);
        }
        else
        {
            var novels = await store.GetAllNovels(ct);
            foreach (var novel in novels)
                items.AddRange(await store.GetItems(novel.Id, ct));
        }
        return ManageHelper.ResolveByIdOrName(items, args.ItemId, i => i.Id, i => i.Name);
    }

    /// <summary>
    /// 将角色 ID 或名称解析为角色（新持有者；名称按 novel_id 缩小范围，缺省扫描全部小说）。
    /// </summary>
    private async Task<Character?> ResolveOwner(
        TransferItemParams args,
        string? idOrName,
        CancellationToken ct
    )
    {
        if (string.IsNullOrEmpty(idOrName))
            return null;
        var characters = new List<Character>();
        if (!string.IsNullOrEmpty(args.NovelId))
        {
            characters = await store.GetCharacters(args.NovelId, ct);
        }
        else
        {
            var novels = await store.GetAllNovels(ct);
            foreach (var novel in novels)
                characters.AddRange(await store.GetCharacters(novel.Id, ct));
        }
        return ManageHelper.ResolveByIdOrName(characters, idOrName, c => c.Id, c => c.Name);
    }
}

public sealed record TransferItemParams
{
    [Description("小说项目ID（传名称时建议提供，用于缩小范围；缺省扫描全部小说）")]
    public string NovelId { get; init; } = "";

    [Description("道具ID或名称")]
    public string ItemId { get; init; } = "";

    [Description("新持有者角色ID或名称")]
    public string NewOwnerId { get; init; } = "";
}

public sealed record ItemDetails
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
}

// ── 检查时间线一致性 ──

/// <summary>
/// 检查时间线一致性工具：检测指定章节范围内的时间矛盾（年龄、事件顺序）
/// </summary>
public sealed class CheckTimelineConsistencyTool(INovelStore store)
    : AgentTool<CheckTimelineConsistencyParams, TimelineConsistencyDetails>
{
    /// <summary>工具唯一标识符</summary>
    public override string Name => "check_timeline_consistency";

    /// <summary>工具显示名称</summary>
    public override string Label => "检查时间线一致性";
    public override string Description => "检测指定章节范围内的时间矛盾（年龄、事件顺序）";
    public override ToolCategory Category => ToolCategory.EntityRead;

    /// <summary>
    /// 执行检查时间线一致性操作
    /// </summary>
    /// <param name="toolCallId">工具调用ID</param>
    /// <param name="args">检查参数</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="onUpdate">进度更新回调</param>
    /// <returns>检查结果</returns>
    public override async Task<AgentToolResult<TimelineConsistencyDetails>> ExecuteAsync(
        string toolCallId,
        CheckTimelineConsistencyParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<TimelineConsistencyDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var events = await store.GetTimelineEvents(args.NovelId, ct);
        var chapters = await store.GetChapters(args.NovelId, ct);
        ThrowIfCancelled(ct);
        var fromOrder = ManageHelper.ResolveChapterOrder(chapters, args.FromChapter, 1);
        var toOrder = ManageHelper.ResolveChapterOrder(chapters, args.ToChapter, int.MaxValue);
        var idToOrder = chapters.ToDictionary(c => c.Id, c => c.Order);
        var chMap = chapters.ToDictionary(c => c.Id, c => $"第{c.Order}章 {c.Title}");
        var filtered = events.Where(e => e.ChapterId != null).ToList();

        var duplicates = filtered
            .GroupBy(e => new { e.ChapterId, e.Title })
            .Where(g => g.Count() > 1)
            .Select(g =>
            {
                var chLabel = chMap.TryGetValue(g.Key.ChapterId!, out var cn)
                    ? cn
                    : g.Key.ChapterId;
                return $"{chLabel}: 重复事件「{g.Key.Title}」";
            })
            .ToList();

        var text =
            duplicates.Count == 0
                ? $"时间线一致，共{filtered.Count}个事件"
                : $"发现{duplicates.Count}个问题:\n" + string.Join("\n", duplicates);

        return new AgentToolResult<TimelineConsistencyDetails>
        {
            Content = [new TextContent(text)],
            Details = new TimelineConsistencyDetails
            {
                EventCount = filtered.Count,
                IssueCount = duplicates.Count,
            },
        };
    }
}

public sealed record CheckTimelineConsistencyParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("检查起始章节，支持章节ID或序号，留空则从第1章")]
    public string? FromChapter { get; init; }

    [Description("检查结束章节，支持章节ID或序号，留空则到最后")]
    public string? ToChapter { get; init; }
}

public sealed record TimelineConsistencyDetails
{
    public int EventCount { get; init; }
    public int IssueCount { get; init; }
}
