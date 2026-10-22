using System.ComponentModel;
using Core.Data;
using Core.Shared;
using Core.Tools;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 分卷管理工具（大纲二层：卷纲）——一卷覆盖连续若干章（网文标准结构）。
/// 章节归属通过 Chapter.VolumeId 表达；本工具只管卷纲本身与卷级情节线/角色关系。
/// </summary>
public sealed class ManageVolumeTool(INovelStore store)
    : AgentTool<ManageVolumeParams, ManageVolumeDetails>
{
    /// <summary>工具唯一标识符</summary>
    public override string Name => "manage_volume";
    public override string[] Aliases => ["分卷", "卷纲", "分卷管理", "新建分卷"];
    public override string[] ExampleQueries => ["为这本小说规划三卷结构", "把第10章归入第一卷"];

    /// <summary>工具显示名称</summary>
    public override string Label => "分卷管理";
    public override string Description =>
        "分卷 CRUD（大纲二层：卷纲）：create（创建分卷）/ update（更新卷纲）/ delete（删除分卷并清空章节归属）/ get（获取卷纲详情）/ list（列出全部分卷）";
    public override ToolCategory Category => ToolCategory.EntityCrud;
    public override string? EntityType => "volume";
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突

    /// <summary>
    /// 执行分卷管理操作
    /// </summary>
    /// <param name="toolCallId">工具调用ID</param>
    /// <param name="args">管理参数</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="onUpdate">进度更新回调</param>
    /// <returns>操作结果</returns>
    public override async Task<AgentToolResult<ManageVolumeDetails>> ExecuteAsync(
        string toolCallId,
        ManageVolumeParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<ManageVolumeDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        return args.Action.ToLowerInvariant() switch
        {
            "create" => await Create(args),
            "update" => await Update(args),
            "delete" => await Delete(args),
            "get" => await Get(args),
            "list" => await List(args),
            _ => Error($"未知操作: {args.Action}。支持: create/update/delete/get/list"),
        };
    }

    /// <summary>
    /// 将卷序号或 ID 解析为真实的分卷。
    /// AI 可能传入序号（如 "2"）而非 ID，与 ManageSceneTool 的序号解析保持一致。
    /// </summary>
    private async Task<Volume?> ResolveVolume(ManageVolumeParams args)
    {
        if (string.IsNullOrEmpty(args.VolumeId) || string.IsNullOrEmpty(args.NovelId))
            return null;

        if (!int.TryParse(args.VolumeId, out var order))
            return await store.GetVolume(args.NovelId, args.VolumeId);

        var volumes = await store.GetVolumes(args.NovelId);
        return volumes.Find(v => v.Order == order);
    }

    /// <summary>创建分卷。</summary>
    private async Task<AgentToolResult<ManageVolumeDetails>> Create(ManageVolumeParams args)
    {
        if (string.IsNullOrEmpty(args.Name))
            return Error("创建分卷需要 name");
        if (string.IsNullOrEmpty(args.NovelId))
            return Error("创建分卷需要 novel-id");

        var volumes = await store.GetVolumes(args.NovelId);
        var volume = new Volume
        {
            NovelId = args.NovelId,
            Name = args.Name,
            Order = args.Order ?? (volumes.Count == 0 ? 1 : volumes.Max(v => v.Order) + 1),
            Summary = args.Summary ?? "",
            Description = args.Description ?? "",
            Goal = args.Goal ?? "",
            PlotlineIds = args.PlotlineIds ?? [],
            CharacterIds = args.CharacterIds ?? [],
            Notes = args.Notes ?? "",
        };
        await store.SaveVolume(volume);
        return Ok(
            $"分卷「第{volume.Order}卷 {volume.Name}」创建成功 (ID: {volume.Id})",
            volume.Id,
            volume.Name
        );
    }

    /// <summary>更新卷纲（未传字段保持原值）。</summary>
    private async Task<AgentToolResult<ManageVolumeDetails>> Update(ManageVolumeParams args)
    {
        if (string.IsNullOrEmpty(args.VolumeId))
            return Error("更新分卷需要 volume-id");

        var volume = await ResolveVolume(args);
        if (volume is null)
            return Error($"分卷 {args.VolumeId} 不存在（可用 list 查看序号或 ID）");

        if (args.Name is not null)
            volume.Name = args.Name;
        if (args.Order is not null)
            volume.Order = args.Order.Value;
        if (args.Summary is not null)
            volume.Summary = args.Summary;
        if (args.Description is not null)
            volume.Description = args.Description;
        if (args.Goal is not null)
            volume.Goal = args.Goal;
        if (args.PlotlineIds is not null)
            volume.PlotlineIds = args.PlotlineIds;
        if (args.CharacterIds is not null)
            volume.CharacterIds = args.CharacterIds;
        if (args.Notes is not null)
            volume.Notes = args.Notes;

        await store.SaveVolume(volume);
        return Ok($"分卷「第{volume.Order}卷 {volume.Name}」已更新", volume.Id, volume.Name);
    }

    /// <summary>删除分卷（级联清空章节的卷归属）。</summary>
    private async Task<AgentToolResult<ManageVolumeDetails>> Delete(ManageVolumeParams args)
    {
        if (string.IsNullOrEmpty(args.VolumeId))
            return Error("删除分卷需要 volume-id");

        var volume = await ResolveVolume(args);
        if (volume is null)
            return Error($"分卷 {args.VolumeId} 不存在（可用 list 查看序号或 ID）");

        var name = volume.Name;
        await store.DeleteVolume(volume.NovelId, volume.Id);
        return Ok($"分卷「{name}」已删除（原属章节已归入未分卷）", volume.Id);
    }

    /// <summary>获取卷纲详情。</summary>
    private async Task<AgentToolResult<ManageVolumeDetails>> Get(ManageVolumeParams args)
    {
        if (string.IsNullOrEmpty(args.VolumeId))
            return Error("获取分卷需要 volume-id");

        var volume = await ResolveVolume(args);
        if (volume is null)
            return Error($"分卷 {args.VolumeId} 不存在（可用 list 查看序号或 ID）");

        var chapters = (await store.GetChapters(volume.NovelId))
            .Where(c => c.VolumeId == volume.Id)
            .OrderBy(c => c.Order)
            .ToList();
        var plotlines = await store.GetPlotlines(volume.NovelId);
        var plotlineNames = plotlines.ToDictionary(p => p.Id, p => p.Name);
        var characters = await store.GetCharacters(volume.NovelId);
        var charNames = characters.ToDictionary(c => c.Id, c => c.Name);

        var text = $"""
分卷: 第{volume.Order}卷 {volume.Name}
梗概: {volume.Summary}
描述: {volume.Description}
目标: {volume.Goal}
推进情节线: {string.Join(
                "、",
                volume.PlotlineIds.Select(id => plotlineNames.GetValueOrDefault(id, id))
            )}
关键角色: {string.Join("、", volume.CharacterIds.Select(id => charNames.GetValueOrDefault(id, id)))}
备注: {volume.Notes}
包含章节（{chapters.Count}）: {string.Join(", ", chapters.Select(c => $"第{c.Order}章 {c.Title}"))}
""";
        return Ok(text, volume.Id, volume.Name);
    }

    /// <summary>列出全部分卷（含章节数）。</summary>
    private async Task<AgentToolResult<ManageVolumeDetails>> List(ManageVolumeParams args)
    {
        if (string.IsNullOrEmpty(args.NovelId))
            return Error("列出分卷需要 novel-id");

        var volumes = await store.GetVolumes(args.NovelId);
        var chapters = await store.GetChapters(args.NovelId);
        var lines = volumes.Select(v =>
        {
            var count = chapters.Count(c => c.VolumeId == v.Id);
            return $"📦 第{v.Order}卷 {v.Name} (ID: {v.Id}) — {count}章 · {v.Summary}";
        });
        var text = volumes.Count == 0 ? "暂无分卷" : string.Join("\n", lines);
        return Ok(text, count: volumes.Count);
    }

    /// <summary>构建成功响应。</summary>
    private static AgentToolResult<ManageVolumeDetails> Ok(
        string text,
        string? id = null,
        string? name = null,
        int count = 0
    ) =>
        new()
        {
            Content = [new TextContent(text)],
            Details = new ManageVolumeDetails
            {
                Id = id ?? "",
                Name = name ?? "",
                Count = count,
            },
        };

    /// <summary>构建错误响应。</summary>
    private static AgentToolResult<ManageVolumeDetails> Error(string msg) =>
        new() { Content = [new TextContent(msg)], IsError = true };
}

public sealed record ManageVolumeParams
{
    [Description("操作: create / update / delete / get / list")]
    public string Action { get; init; } = "list";

    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("分卷ID（update/delete/get 时必填；也支持卷序号如 1，自动解析为真实 ID）")]
    public string? VolumeId { get; init; }

    [Description("卷名，如「风起云涌」")]
    public string? Name { get; init; }

    [Description("卷序号（从 1 开始；create 留空则自动取最大序号 +1）")]
    public int? Order { get; init; }

    [Description("本卷梗概（这一卷发生什么）")]
    public string? Summary { get; init; }

    [Description("卷描述（本卷简介/看点）")]
    public string? Description { get; init; }

    [Description("本卷目标 / 核心冲突（读完这一卷读者获得什么）")]
    public string? Goal { get; init; }

    [Description("本卷推进的情节线 ID 列表")]
    public List<string>? PlotlineIds { get; init; }

    [Description("本卷关键角色 ID 列表")]
    public List<string>? CharacterIds { get; init; }

    [Description("备注（节奏/伏笔/卷末钩子提示）")]
    public string? Notes { get; init; }
}

public sealed record ManageVolumeDetails
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public int Count { get; init; }
}
