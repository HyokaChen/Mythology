using System.ComponentModel;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Core.Vector;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 道具管理工具：道具的增删改查（CRUD）操作
/// </summary>
public sealed class ManageItemTool(INovelStore store, RetrievalKernel? kernel = null)
    : AgentTool<ManageItemParams, ManageItemDetails>
{
    /// <summary>工具唯一标识符</summary>
    public override string Name => "manage_item";
    public override string[] Aliases => ["道具", "物品", "法宝"];
    public override string[] ExampleQueries => ["给主角添加法宝青霜剑"];

    /// <summary>工具显示名称</summary>
    public override string Label => "道具管理";
    public override string Description =>
        "道具 CRUD：create（创建道具）、update（更新道具设定）、delete（删除道具）、get（获取道具详情）、list（列出道具）"
        + BatchToolHints.PreferBatchForMultiple;
    public override ToolCategory Category => ToolCategory.EntityCrud;
    public override string EntityType => "item";
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突

    /// <summary>
    /// 执行道具管理操作
    /// </summary>
    /// <param name="toolCallId">工具调用ID</param>
    /// <param name="args">管理参数</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="onUpdate">进度更新回调</param>
    /// <returns>操作结果</returns>
    public override async Task<AgentToolResult<ManageItemDetails>> ExecuteAsync(
        string toolCallId,
        ManageItemParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<ManageItemDetails>>? onUpdate = null
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
    /// 创建新道具
    /// </summary>
    private async Task<AgentToolResult<ManageItemDetails>> Create(
        ManageItemParams args,
        CancellationToken ct
    )
    {
        if (string.IsNullOrEmpty(args.Name))
            return Error("创建道具需要 name");

        var item = new Item
        {
            NovelId = args.NovelId,
            Name = args.Name,
            Type = Enum.TryParse<ItemType>(args.Type ?? "artifact", ignoreCase: true, out var it)
                ? it
                : ItemType.Artifact,
            Description = args.Description ?? "",
            OwnerId = args.OwnerId,
        };
        await store.SaveItem(item, ct);
        ThrowIfCancelled(ct);
        if (kernel is not null)
        {
            var (doc, chunks) = kernel.Builder.BuildItem(item);
            await kernel.IndexDocumentAsync(doc, chunks, ct);
        }
        ThrowIfCancelled(ct);
        return Ok($"道具「{item.Name}」创建成功 (ID: {item.Id})", item.Id, item.Name);
    }

    /// <summary>
    /// 更新道具信息
    /// </summary>
    private async Task<AgentToolResult<ManageItemDetails>> Update(
        ManageItemParams args,
        CancellationToken ct
    )
    {
        if (string.IsNullOrEmpty(args.ItemId))
            return Error("更新道具需要 item-id");

        var item = await ResolveItem(args);
        if (item is null)
            return Error($"道具 {args.ItemId} 不存在（可用 list 查看 ID 或直接传名称）");

        var changes = ManageHelper.DiffFields(
            ("名称", item.Name, args.Name),
            ("类型", item.Type.ToString(), args.Type),
            ("描述", item.Description, args.Description),
            ("持有者", item.OwnerId, args.OwnerId)
        );

        if (args.Name is not null)
            item.Name = args.Name;
        if (
            args.Type is not null
            && Enum.TryParse<ItemType>(args.Type, ignoreCase: true, out var it)
        )
            item.Type = it;
        if (args.Description is not null)
            item.Description = args.Description;
        if (args.OwnerId is not null)
            item.OwnerId = args.OwnerId;

        await store.SaveItem(item, ct);
        ThrowIfCancelled(ct);
        if (kernel is not null)
        {
            var (doc, chunks) = kernel.Builder.BuildItem(item);
            await kernel.IndexDocumentAsync(doc, chunks, ct);
        }
        ThrowIfCancelled(ct);

        var summary =
            changes.Count > 0
                ? $"道具「{item.Name}」已更新:\n{string.Join("\n", changes)}"
                : $"道具「{item.Name}」无变更";
        return Ok(summary, item.Id, item.Name);
    }

    /// <summary>
    /// 删除道具
    /// </summary>
    private async Task<AgentToolResult<ManageItemDetails>> Delete(ManageItemParams args)
    {
        if (string.IsNullOrEmpty(args.ItemId))
            return Error("删除道具需要 item-id");

        var item = await ResolveItem(args);
        if (item is null)
            return Error($"道具 {args.ItemId} 不存在（可用 list 查看 ID 或直接传名称）");

        var name = item.Name;
        await store.DeleteItem(item.Id);
        return Ok($"道具「{name}」已删除", item.Id);
    }

    /// <summary>
    /// 获取道具详情
    /// </summary>
    private async Task<AgentToolResult<ManageItemDetails>> Get(ManageItemParams args)
    {
        if (string.IsNullOrEmpty(args.ItemId))
            return Error("获取道具需要 item-id");

        var item = await ResolveItem(args);
        if (item is null)
            return Error($"道具 {args.ItemId} 不存在（可用 list 查看 ID 或直接传名称）");

        var typeLabel = item.Type switch
        {
            ItemType.Weapon => "武器",
            ItemType.Artifact => "法器",
            ItemType.Token => "信物",
            ItemType.Consumable => "消耗品",
            _ => "其他",
        };
        var text =
            $"道具: {item.Name}\n类型: {typeLabel}\n描述: {item.Description}\n持有者: {item.OwnerId ?? "无人"}";
        return Ok(text, item.Id, item.Name);
    }

    /// <summary>
    /// 列出所有道具
    /// </summary>
    private async Task<AgentToolResult<ManageItemDetails>> List(ManageItemParams args)
    {
        var items = await store.GetItems(args.NovelId);
        var lines = items.Select(i => $"- {i.Name} (ID: {i.Id}, 持有者: {i.OwnerId ?? "无"})");
        var text = items.Count == 0 ? "暂无道具" : string.Join("\n", lines);
        return Ok(text, count: items.Count);
    }

    /// <summary>
    /// 将道具 ID 或名称解析为真实道具（名称需配合 novel_id 缩小范围）。
    /// </summary>
    private async Task<Item?> ResolveItem(ManageItemParams args)
    {
        if (string.IsNullOrEmpty(args.ItemId))
            return null;
        var items = await store.GetItems(args.NovelId);
        return ManageHelper.ResolveByIdOrName(items, args.ItemId, i => i.Id, i => i.Name);
    }

    /// <summary>
    /// 构建成功响应
    /// </summary>
    private static AgentToolResult<ManageItemDetails> Ok(
        string text,
        string? id = null,
        string? name = null,
        int count = 0
    ) =>
        new()
        {
            Content = [new TextContent(text)],
            Details = new ManageItemDetails
            {
                Id = id ?? "",
                Name = name ?? "",
                Count = count,
            },
        };

    /// <summary>
    /// 构建错误响应
    /// </summary>
    private static AgentToolResult<ManageItemDetails> Error(string msg) =>
        new() { Content = [new TextContent(msg)], IsError = true };
}

public sealed record ManageItemParams
{
    [Description("操作: create / update / delete / get / list")]
    public string Action { get; init; } = "list";

    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("道具ID（update/delete/get 时必填；也支持直接传道具名称）")]
    public string? ItemId { get; init; }

    [Description("道具名称（create 时必填，update 时可选）")]
    public string? Name { get; init; }

    [Description(
        "道具类型: weapon(武器)/artifact(法器)/token(信物)/consumable(消耗品)/other(其他)"
    )]
    public string? Type { get; init; }

    [Description("道具详细描述。写具体外观和功能，不用 AI 词")]
    public string? Description { get; init; }

    [Description("持有者角色ID")]
    public string? OwnerId { get; init; }
}

public sealed record ManageItemDetails
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public int Count { get; init; }
}
