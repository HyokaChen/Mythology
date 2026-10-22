using System.ComponentModel;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Core.Vector;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 情节线管理工具：情节线的增删改查（CRUD）操作
/// </summary>
public sealed class ManagePlotlineTool(INovelStore store, RetrievalKernel? kernel = null)
    : AgentTool<ManagePlotlineParams, ManagePlotlineDetails>
{
    public override string Name => "manage_plotline";
    public override string[] Aliases =>
        ["情节线", "故事线", "主线", "支线", "伏笔管理", "管理伏笔", "暗线", "剧情线"];
    public override string[] ExampleQueries => ["新建一条支线情节", "调整主线走向", "管理伏笔回收"];

    /// <summary>工具显示名称</summary>
    public override string Label => "情节线管理";
    public override string Description =>
        "情节线 CRUD：create（创建情节线）、update（更新情节线设定）、delete（删除情节线）、get（获取未解决情节线）、list（列出所有情节线）"
        + BatchToolHints.PreferBatchForMultiple;
    public override ToolCategory Category => ToolCategory.EntityCrud;
    public override string EntityType => "plotline";
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突

    /// <summary>
    /// 执行情节线管理操作
    /// </summary>
    /// <param name="toolCallId">工具调用ID</param>
    /// <param name="args">管理参数</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="onUpdate">进度更新回调</param>
    /// <returns>操作结果</returns>
    public override async Task<AgentToolResult<ManagePlotlineDetails>> ExecuteAsync(
        string toolCallId,
        ManagePlotlineParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<ManagePlotlineDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        return args.Action.ToLowerInvariant() switch
        {
            "create" => await Create(args, ct),
            "update" => await Update(args, ct),
            "delete" => await Delete(args),
            "get" => await GetOpen(args),
            "list" => await List(args),
            _ => Error($"未知操作: {args.Action}。支持: create/update/delete/get/list"),
        };
    }

    /// <summary>
    /// 创建新情节线
    /// </summary>
    private async Task<AgentToolResult<ManagePlotlineDetails>> Create(
        ManagePlotlineParams args,
        CancellationToken ct
    )
    {
        if (string.IsNullOrEmpty(args.Name))
            return Error("创建情节线需要 name");

        var plotline = new Plotline
        {
            NovelId = args.NovelId,
            Name = args.Name,
            Description = args.Description ?? "",
            Type = Enum.TryParse<PlotlineType>(args.Type, ignoreCase: true, out var t)
                ? t
                : PlotlineType.Main,
            PlannedRevealChapterId = args.PlannedRevealChapterId,
            Tension = Enum.TryParse<PlotlineTension>(
                args.Tension ?? "medium",
                ignoreCase: true,
                out var tn
            )
                ? tn
                : PlotlineTension.Medium,
            Turnpoints = args.Turnpoints ?? "",
            ResolvedAtChapterId = args.ResolvedAtChapterId,
        };
        await store.SavePlotline(plotline, ct);
        ThrowIfCancelled(ct);
        if (kernel is not null)
        {
            var (doc, chunks) = kernel.Builder.BuildPlotline(plotline);
            await kernel.IndexDocumentAsync(doc, chunks, ct);
        }
        ThrowIfCancelled(ct);
        return Ok(
            $"情节线「{plotline.Name}」创建成功 (类型: {plotline.Type})",
            plotline.Id,
            plotline.Name
        );
    }

    /// <summary>
    /// 更新情节线信息
    /// </summary>
    private async Task<AgentToolResult<ManagePlotlineDetails>> Update(
        ManagePlotlineParams args,
        CancellationToken ct
    )
    {
        if (string.IsNullOrEmpty(args.PlotlineId))
            return Error("更新情节线需要 plotline-id");

        var plotline = await ResolvePlotline(args);
        if (plotline is null)
            return Error($"情节线 {args.PlotlineId} 不存在（可用 list 查看 ID 或直接传名称）");

        var changes = new List<string>();
        changes.AddRange(
            ManageHelper.DiffFields(
                ("名称", plotline.Name, args.Name),
                ("描述", plotline.Description, args.Description)
            )
        );

        if (
            args.Type is not null
            && Enum.TryParse<PlotlineType>(args.Type, ignoreCase: true, out var parsedType)
            && parsedType != plotline.Type
        )
        {
            changes.Add($"类型: {plotline.Type} → {args.Type}");
        }

        var chapters = await store.GetChapters(args.NovelId, ct);
        var chMap = chapters.ToDictionary(c => c.Id, c => $"第{c.Order}章 {c.Title}");

        if (
            args.PlannedRevealChapterId != null
            && args.PlannedRevealChapterId != plotline.PlannedRevealChapterId
        )
        {
            var oldLabel =
                plotline.PlannedRevealChapterId != null
                && chMap.TryGetValue(plotline.PlannedRevealChapterId, out var oldCh)
                    ? oldCh
                    : "未设定";
            var newLabel = chMap.TryGetValue(args.PlannedRevealChapterId, out var newCh)
                ? newCh
                : args.PlannedRevealChapterId;
            changes.Add($"计划揭示章节: {oldLabel} → {newLabel}");
        }

        if (
            args.Tension is not null
            && Enum.TryParse<PlotlineTension>(args.Tension, ignoreCase: true, out var parsedTension)
            && parsedTension != plotline.Tension
        )
        {
            changes.Add($"紧张度: {plotline.Tension} → {args.Tension}");
        }

        if (args.Turnpoints is not null && args.Turnpoints != plotline.Turnpoints)
        {
            changes.Add($"关键转折: {ManageHelper.Diff(args.Turnpoints)}");
        }

        if (
            args.ResolvedAtChapterId != null
            && args.ResolvedAtChapterId != plotline.ResolvedAtChapterId
        )
        {
            var oldLabel =
                plotline.ResolvedAtChapterId != null
                && chMap.TryGetValue(plotline.ResolvedAtChapterId, out var oldRCh)
                    ? oldRCh
                    : "未解决";
            var newLabel = chMap.TryGetValue(args.ResolvedAtChapterId, out var newRCh)
                ? newRCh
                : args.ResolvedAtChapterId;
            changes.Add($"解决章节: {oldLabel} → {newLabel}");
        }

        if (args.Name is not null)
            plotline.Name = args.Name;
        if (args.Description is not null)
            plotline.Description = args.Description;
        if (
            args.Type is not null
            && Enum.TryParse<PlotlineType>(args.Type, ignoreCase: true, out var pt)
        )
            plotline.Type = pt;
        if (args.PlannedRevealChapterId != null)
            plotline.PlannedRevealChapterId = args.PlannedRevealChapterId;
        if (
            args.Tension is not null
            && Enum.TryParse<PlotlineTension>(args.Tension, ignoreCase: true, out var ptn)
        )
            plotline.Tension = ptn;
        if (args.Turnpoints is not null)
            plotline.Turnpoints = args.Turnpoints;
        if (args.ResolvedAtChapterId != null)
            plotline.ResolvedAtChapterId = args.ResolvedAtChapterId;

        await store.SavePlotline(plotline, ct);
        if (kernel is not null)
        {
            var (doc, chunks) = kernel.Builder.BuildPlotline(plotline);
            await kernel.IndexDocumentAsync(doc, chunks, ct);
        }

        var summary =
            changes.Count > 0
                ? $"情节线「{plotline.Name}」已更新:\n{string.Join("\n", changes)}"
                : $"情节线「{plotline.Name}」无变更";
        return Ok(summary, plotline.Id, plotline.Name);
    }

    /// <summary>
    /// 删除情节线
    /// </summary>
    private async Task<AgentToolResult<ManagePlotlineDetails>> Delete(ManagePlotlineParams args)
    {
        if (string.IsNullOrEmpty(args.PlotlineId))
            return Error("删除情节线需要 plotline-id");

        var plotline = await ResolvePlotline(args);
        if (plotline is null)
            return Error($"情节线 {args.PlotlineId} 不存在（可用 list 查看 ID 或直接传名称）");

        var name = plotline.Name;
        await store.DeletePlotline(plotline.Id);
        return Ok($"情节线「{name}」已删除", plotline.Id);
    }

    /// <summary>
    /// 获取未解决的情节线
    /// </summary>
    private async Task<AgentToolResult<ManagePlotlineDetails>> GetOpen(ManagePlotlineParams args)
    {
        var plotlines = await store.GetOpenPlotlines(args.NovelId);
        var chapters = await store.GetChapters(args.NovelId);
        var chMap = chapters.ToDictionary(c => c.Id, c => $"第{c.Order}章 {c.Title}");
        var lines = plotlines.Select(p =>
        {
            var reveal =
                p.PlannedRevealChapterId != null
                && chMap.TryGetValue(p.PlannedRevealChapterId, out var rn)
                    ? rn
                    : "未设定";
            return $"- {p.Name} (类型: {p.Type}, 计划揭示: {reveal})";
        });
        var text = plotlines.Count == 0 ? "所有情节线已解决" : string.Join("\n", lines);
        return Ok(text, count: plotlines.Count);
    }

    /// <summary>
    /// 列出所有情节线
    /// </summary>
    private async Task<AgentToolResult<ManagePlotlineDetails>> List(ManagePlotlineParams args)
    {
        var plotlines = await store.GetPlotlines(args.NovelId);
        var chapters = await store.GetChapters(args.NovelId);
        var chMap = chapters.ToDictionary(c => c.Id, c => $"第{c.Order}章 {c.Title}");
        var lines = plotlines.Select(p =>
        {
            var status =
                p.ResolvedAtChapterId != null
                && chMap.TryGetValue(p.ResolvedAtChapterId, out var rn)
                    ? $"已解决({rn})"
                    : "进行中";
            return $"- {p.Name} (ID: {p.Id}, 类型: {p.Type}, {status})";
        });
        var text = plotlines.Count == 0 ? "暂无情节线" : string.Join("\n", lines);
        return Ok(text, count: plotlines.Count);
    }

    /// <summary>
    /// 将情节线 ID 或名称解析为真实情节线（名称需配合 novel_id 缩小范围）。
    /// </summary>
    private async Task<Plotline?> ResolvePlotline(ManagePlotlineParams args)
    {
        if (string.IsNullOrEmpty(args.PlotlineId))
            return null;
        var plotlines = await store.GetPlotlines(args.NovelId);
        return ManageHelper.ResolveByIdOrName(plotlines, args.PlotlineId, p => p.Id, p => p.Name);
    }

    /// <summary>
    /// 构建成功响应
    /// </summary>
    private static AgentToolResult<ManagePlotlineDetails> Ok(
        string text,
        string? id = null,
        string? name = null,
        int count = 0
    ) =>
        new()
        {
            Content = [new TextContent(text)],
            Details = new ManagePlotlineDetails
            {
                Id = id ?? "",
                Name = name ?? "",
                Count = count,
            },
        };

    /// <summary>
    /// 构建错误响应
    /// </summary>
    private static AgentToolResult<ManagePlotlineDetails> Error(string msg) =>
        new() { Content = [new TextContent(msg)], IsError = true };
}

public sealed record ManagePlotlineParams
{
    [Description("操作: create / update / delete / get / list")]
    public string Action { get; init; } = "list";

    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("情节线ID（update/delete 时必填；也支持直接传情节线名称）")]
    public string? PlotlineId { get; init; }

    [Description("情节线名称（create 时必填，update 时可选）")]
    public string? Name { get; init; }

    [Description("情节线详细描述。直接叙述事件，不用 AI 句式")]
    public string? Description { get; init; }

    [Description("类型：main（主线）/ side（支线）/ hidden（暗线）/ foreshadowing（伏笔）")]
    public string? Type { get; init; }

    [Description("计划揭示的章节ID（期望在哪一章揭示/解决）")]
    public string? PlannedRevealChapterId { get; init; }

    [Description("紧张度：low（低）/ medium（中）/ high（高）/ critical（临界）")]
    public string? Tension { get; init; }

    [Description("关键转折（自由文本，如\"第5章：真相揭露；第12章：盟友背叛\"）")]
    public string? Turnpoints { get; init; }

    [Description("解决章节ID（设置后标记为已解决）")]
    public string? ResolvedAtChapterId { get; init; }
}

public sealed record ManagePlotlineDetails
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public int Count { get; init; }
}
