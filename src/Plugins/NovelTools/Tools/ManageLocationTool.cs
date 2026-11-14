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
        if (string.IsNullOrWhiteSpace(args.Name))
            return Error("创建地点需要 name");

        var existingLocations = await store.GetLocations(args.NovelId, ct);
        var normalizedName = args.Name.Trim();
        if (
            existingLocations.Any(l =>
                string.Equals(l.Name.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase)
            )
        )
            return Error($"地点名「{normalizedName}」已存在，请换一个名称");

        var parentId = await ResolveParentId(args.NovelId, args.ParentId, ct);
        if (parentId is null && !string.IsNullOrWhiteSpace(args.ParentId))
            return Error($"父地点 {args.ParentId} 不存在");
        var type = LocationType.City;
        if (!string.IsNullOrWhiteSpace(args.Type))
        {
            if (!NovelEnum.TryParse<LocationType>(args.Type, out type))
                return Error($"未知地点类型: {args.Type}");
        }

        var location = new Location
        {
            NovelId = args.NovelId,
            Name = normalizedName,
            Type = type,
            Atmosphere = args.Atmosphere ?? "",
            Description = args.Description ?? "",
            ParentId = parentId,
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

        var locations = await store.GetLocations(args.NovelId, ct);
        var newName = location.Name;
        if (args.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(args.Name))
                return Error("地点 name 不能为空");
            newName = args.Name.Trim();
            if (
                locations.Any(l =>
                    l.Id != location.Id
                    && string.Equals(l.Name.Trim(), newName, StringComparison.OrdinalIgnoreCase)
                )
            )
                return Error($"地点名「{newName}」已存在，请换一个名称");
        }

        var newType = location.Type;
        if (args.Type is not null)
        {
            if (!NovelEnum.TryParse<LocationType>(args.Type, out newType))
                return Error($"未知地点类型: {args.Type}");
        }

        var newParentId = location.ParentId;
        if (args.ParentId is not null)
        {
            if (string.IsNullOrWhiteSpace(args.ParentId))
            {
                newParentId = null;
            }
            else
            {
                var parentId = await ResolveParentId(args.NovelId, args.ParentId, ct);
                if (parentId is null)
                    return Error($"父地点 {args.ParentId} 不存在");
                if (string.Equals(parentId, location.Id, StringComparison.Ordinal))
                    return Error("父地点不能指向自己");
                if (WouldCreateCycle(locations, location.Id, parentId))
                    return Error("父地点链会形成循环，未保存");
                newParentId = parentId;
            }
        }

        var changes = ManageHelper.DiffFields(
            ("名称", location.Name, newName),
            ("类型", location.Type.ToString(), newType.ToString()),
            ("氛围", location.Atmosphere, args.Atmosphere),
            ("描述", location.Description, args.Description)
        );

        location.Name = newName;
        location.Type = newType;
        if (args.Atmosphere is not null)
            location.Atmosphere = args.Atmosphere;
        if (args.Description is not null)
            location.Description = args.Description;
        location.ParentId = newParentId;

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

    private async Task<string?> ResolveParentId(
        string novelId,
        string? idOrName,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(idOrName))
            return null;
        var parent = await NovelReferenceResolver.LocationAsync(store, novelId, idOrName, ct);
        return parent?.Id;
    }

    private static bool WouldCreateCycle(
        IReadOnlyList<Location> locations,
        string locationId,
        string parentId
    )
    {
        var byId = locations.ToDictionary(l => l.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = parentId;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (string.Equals(current, locationId, StringComparison.Ordinal))
                return true;
            if (!seen.Add(current))
                return true;
            current = byId.TryGetValue(current, out var parent) ? parent.ParentId : null;
        }
        return false;
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
