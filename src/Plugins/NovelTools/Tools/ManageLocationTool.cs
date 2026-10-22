using System.ComponentModel;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Core.Vector;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 地点管理工具：地点的增删改查（CRUD）操作
/// </summary>
public sealed class ManageLocationTool(INovelStore store, RetrievalKernel? kernel = null)
    : AgentTool<ManageLocationParams, ManageLocationDetails>
{
    /// <summary>工具唯一标识符</summary>
    public override string Name => "manage_location";
    public override string[] Aliases => ["地点", "地图", "位置"];
    public override string[] ExampleQueries => ["添加一座城市临安", "修改某个地点描述"];

    /// <summary>工具显示名称</summary>
    public override string Label => "地点管理";
    public override string Description =>
        "地点 CRUD：create（创建地点）、update（更新地点设定）、delete（删除地点）、get（获取地点详情）、list（列出所有地点）"
        + BatchToolHints.PreferBatchForMultiple;
    public override ToolCategory Category => ToolCategory.EntityCrud;
    public override string EntityType => "location";
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突

    /// <summary>
    /// 执行地点管理操作
    /// </summary>
    /// <param name="toolCallId">工具调用ID</param>
    /// <param name="args">管理参数</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="onUpdate">进度更新回调</param>
    /// <returns>操作结果</returns>
    public override async Task<AgentToolResult<ManageLocationDetails>> ExecuteAsync(
        string toolCallId,
        ManageLocationParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<ManageLocationDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        return args.Action.ToLowerInvariant() switch
        {
            "create" => await Create(args, ct),
            "update" => await Update(args, ct),
            "delete" => await Delete(args),
            "get" => await Get(args),
            "list" => await List(args),
            _ => Error($"未知操作: {args.Action}。支持: create/update/delete/get/list"),
        };
    }

    /// <summary>
    /// 创建新地点
    /// </summary>
    private async Task<AgentToolResult<ManageLocationDetails>> Create(
        ManageLocationParams args,
        CancellationToken ct
    )
    {
        if (string.IsNullOrEmpty(args.Name))
            return Error("创建地点需要 name");

        var location = new Location
        {
            NovelId = args.NovelId,
            Name = args.Name,
            Type = Enum.TryParse<LocationType>(args.Type ?? "city", ignoreCase: true, out var lt)
                ? lt
                : LocationType.City,
            Atmosphere = args.Atmosphere ?? "",
            Description = args.Description ?? "",
            ParentId = args.ParentId,
        };
        await store.SaveLocation(location, ct);
        ThrowIfCancelled(ct);
        if (kernel is not null)
        {
            var (doc, chunks) = kernel.Builder.BuildLocation(location);
            await kernel.IndexDocumentAsync(doc, chunks, ct);
        }
        ThrowIfCancelled(ct);
        return Ok(
            $"地点「{location.Name}」创建成功 (ID: {location.Id})",
            location.Id,
            location.Name
        );
    }

    /// <summary>
    /// 更新地点信息
    /// </summary>
    private async Task<AgentToolResult<ManageLocationDetails>> Update(
        ManageLocationParams args,
        CancellationToken ct
    )
    {
        if (string.IsNullOrEmpty(args.LocationId))
            return Error("更新地点需要 location-id");

        var location = await ResolveLocation(args);
        if (location is null)
            return Error($"地点 {args.LocationId} 不存在（可用 list 查看 ID 或直接传名称）");

        var changes = ManageHelper.DiffFields(
            ("名称", location.Name, args.Name),
            ("类型", location.Type.ToString(), args.Type),
            ("氛围", location.Atmosphere, args.Atmosphere),
            ("描述", location.Description, args.Description)
        );

        if (args.Name is not null)
            location.Name = args.Name;
        if (
            args.Type is not null
            && Enum.TryParse<LocationType>(args.Type, ignoreCase: true, out var lt)
        )
            location.Type = lt;
        if (args.Atmosphere is not null)
            location.Atmosphere = args.Atmosphere;
        if (args.Description is not null)
            location.Description = args.Description;

        await store.SaveLocation(location, ct);
        ThrowIfCancelled(ct);
        if (kernel is not null)
        {
            var (doc, chunks) = kernel.Builder.BuildLocation(location);
            await kernel.IndexDocumentAsync(doc, chunks, ct);
        }
        ThrowIfCancelled(ct);

        var summary =
            changes.Count > 0
                ? $"地点「{location.Name}」已更新:\n{string.Join("\n", changes)}"
                : $"地点「{location.Name}」无变更";
        return Ok(summary, location.Id, location.Name);
    }

    /// <summary>
    /// 删除地点
    /// </summary>
    private async Task<AgentToolResult<ManageLocationDetails>> Delete(ManageLocationParams args)
    {
        if (string.IsNullOrEmpty(args.LocationId))
            return Error("删除地点需要 location-id");

        var location = await ResolveLocation(args);
        if (location is null)
            return Error($"地点 {args.LocationId} 不存在（可用 list 查看 ID 或直接传名称）");

        var name = location.Name;
        await store.DeleteLocation(location.Id);
        return Ok($"地点「{name}」已删除", location.Id);
    }

    /// <summary>
    /// 获取地点详情
    /// </summary>
    private async Task<AgentToolResult<ManageLocationDetails>> Get(ManageLocationParams args)
    {
        if (string.IsNullOrEmpty(args.LocationId))
            return Error("获取地点需要 location-id");

        var location = await ResolveLocation(args);
        if (location is null)
            return Error($"地点 {args.LocationId} 不存在（可用 list 查看 ID 或直接传名称）");

        var typeLabel = location.Type switch
        {
            LocationType.City => "城市",
            LocationType.Rural => "乡村",
            LocationType.Indoor => "室内",
            LocationType.Wilderness => "荒野",
            LocationType.Fictional => "虚构",
            _ => "其他",
        };
        var parentInfo = location.ParentId is not null ? $" (父地点: {location.ParentId})" : "";
        var atmosphereInfo = string.IsNullOrEmpty(location.Atmosphere)
            ? ""
            : $"\n氛围: {location.Atmosphere}";
        var text =
            $"地点: {location.Name}{parentInfo}\n类型: {typeLabel}{atmosphereInfo}\n{location.Description}";
        return Ok(text, location.Id, location.Name);
    }

    /// <summary>
    /// 列出所有地点
    /// </summary>
    private async Task<AgentToolResult<ManageLocationDetails>> List(ManageLocationParams args)
    {
        var locations = await store.GetLocations(args.NovelId);
        var lines = locations.Select(l => $"- {l.Name} (ID: {l.Id}, 父地点: {l.ParentId ?? "无"})");
        var text = locations.Count == 0 ? "暂无地点" : string.Join("\n", lines);
        return Ok(text, count: locations.Count);
    }

    /// <summary>
    /// 将地点 ID 或名称解析为真实地点（名称需配合 novel_id 缩小范围）。
    /// </summary>
    private async Task<Location?> ResolveLocation(ManageLocationParams args)
    {
        if (string.IsNullOrEmpty(args.LocationId))
            return null;
        var locations = await store.GetLocations(args.NovelId);
        return ManageHelper.ResolveByIdOrName(locations, args.LocationId, l => l.Id, l => l.Name);
    }

    /// <summary>
    /// 构建成功响应
    /// </summary>
    private static AgentToolResult<ManageLocationDetails> Ok(
        string text,
        string? id = null,
        string? name = null,
        int count = 0
    ) =>
        new()
        {
            Content = [new TextContent(text)],
            Details = new ManageLocationDetails
            {
                Id = id ?? "",
                Name = name ?? "",
                Count = count,
            },
        };

    /// <summary>
    /// 构建错误响应
    /// </summary>
    private static AgentToolResult<ManageLocationDetails> Error(string msg) =>
        new() { Content = [new TextContent(msg)], IsError = true };
}

public sealed record ManageLocationParams
{
    [Description("操作: create / update / delete / get / list")]
    public string Action { get; init; } = "list";

    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("地点ID（update/delete/get 时必填；也支持直接传地点名称）")]
    public string? LocationId { get; init; }

    [Description("地点名称（create 时必填，update 时可选）")]
    public string? Name { get; init; }

    [Description(
        "地点类型: city(城市)/rural(乡村)/indoor(室内)/wilderness(荒野)/fictional(虚构)/other(其他)"
    )]
    public string? Type { get; init; }

    [Description("氛围/情绪基调")]
    public string? Atmosphere { get; init; }

    [Description("地点详细描述。写具体特征，不用 AI 宣传语")]
    public string? Description { get; init; }

    [Description("父地点ID（支持层级嵌套，create 时可选）")]
    public string? ParentId { get; init; }
}

public sealed record ManageLocationDetails
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public int Count { get; init; }
}
