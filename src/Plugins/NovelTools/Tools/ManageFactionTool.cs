using System.ComponentModel;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Core.Vector;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 组织管理工具：组织的增删改查（CRUD）操作
/// </summary>
public sealed class ManageFactionTool(INovelStore store, RetrievalKernel? kernel = null)
    : AgentTool<ManageFactionParams, ManageFactionDetails>
{
    /// <summary>工具唯一标识符</summary>
    public override string Name => "manage_faction";
    public override string[] Aliases => ["组织", "门派", "势力"];
    public override string[] ExampleQueries => ["创建宗门天机阁", "修改门派关系"];

    /// <summary>工具显示名称</summary>
    public override string Label => "组织管理";
    public override string Description =>
        "组织 CRUD：create（创建组织）、update（更新组织设定）、delete（删除组织）、get（获取组织详情）、list（列出所有组织）"
        + BatchToolHints.PreferBatchForMultiple;
    public override ToolCategory Category => ToolCategory.EntityCrud;
    public override string EntityType => "faction";
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突

    /// <summary>
    /// 执行组织管理操作
    /// </summary>
    /// <param name="toolCallId">工具调用ID</param>
    /// <param name="args">管理参数</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="onUpdate">进度更新回调</param>
    /// <returns>操作结果</returns>
    public override async Task<AgentToolResult<ManageFactionDetails>> ExecuteAsync(
        string toolCallId,
        ManageFactionParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<ManageFactionDetails>>? onUpdate = null
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
    /// 创建新组织
    /// </summary>
    private async Task<AgentToolResult<ManageFactionDetails>> Create(
        ManageFactionParams args,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(args.Name))
            return Error("创建组织需要 name");

        var existingFactions = await store.GetFactions(args.NovelId, ct);
        var normalizedName = args.Name.Trim();
        if (
            existingFactions.Any(f =>
                string.Equals(f.Name.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase)
            )
        )
            return Error($"组织名「{normalizedName}」已存在，请换一个名称");

        var headquartersId = await ResolveLocationId(args.NovelId, args.HeadquartersId, ct);
        if (headquartersId is null && !string.IsNullOrWhiteSpace(args.HeadquartersId))
            return Error($"地点 {args.HeadquartersId} 不存在");
        var leaderId = await ResolveLeaderId(args.NovelId, args.Leader, ct);
        if (leaderId is null && !string.IsNullOrWhiteSpace(args.Leader))
            return Error($"首领角色 {args.Leader} 不存在");
        var members = await ResolveMemberIds(args.NovelId, args.Members, ct);
        if (members is null)
            return Error("成员列表中有角色不存在");

        var type = FactionType.Sect;
        if (!string.IsNullOrWhiteSpace(args.Type))
        {
            if (!NovelEnum.TryParse<FactionType>(args.Type, out type))
                return Error($"未知组织类型: {args.Type}");
        }

        var faction = new Faction
        {
            NovelId = args.NovelId,
            Name = normalizedName,
            Type = type,
            Description = args.Description ?? "",
            Leader = leaderId ?? "",
            HeadquartersId = headquartersId,
            Goal = args.Goal ?? "",
            Members = members,
        };
        if (leaderId is not null && !members.Contains(leaderId, StringComparer.Ordinal))
            members.Add(leaderId);
        await EntityFieldWriter.SaveAsync(store, faction, new MembershipBefore(), ct);
        ThrowIfCancelled(ct);
        if (kernel is not null)
        {
            var (doc, chunks) = kernel.Builder.BuildFaction(faction);
            await kernel.IndexDocumentAsync(doc, chunks, ct);
        }
        ThrowIfCancelled(ct);
        return Ok($"组织「{faction.Name}」创建成功 (ID: {faction.Id})", faction.Id, faction.Name);
    }

    /// <summary>
    /// 更新组织信息
    /// </summary>
    private async Task<AgentToolResult<ManageFactionDetails>> Update(
        ManageFactionParams args,
        CancellationToken ct
    )
    {
        if (string.IsNullOrEmpty(args.FactionId))
            return Error("更新组织需要 faction-id");

        var faction = await ResolveFaction(args);
        if (faction is null)
            return Error($"组织 {args.FactionId} 不存在（可用 list 查看 ID 或直接传名称）");

        var before = EntityFieldWriter.CaptureMembership(faction);

        var newName = faction.Name;
        if (args.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(args.Name))
                return Error("组织 name 不能为空");
            newName = args.Name.Trim();
            var factions = await store.GetFactions(args.NovelId, ct);
            if (
                factions.Any(f =>
                    f.Id != faction.Id
                    && string.Equals(f.Name.Trim(), newName, StringComparison.OrdinalIgnoreCase)
                )
            )
                return Error($"组织名「{newName}」已存在，请换一个名称");
        }
        var newType = faction.Type;
        if (args.Type is not null)
        {
            if (!NovelEnum.TryParse<FactionType>(args.Type, out newType))
                return Error($"未知组织类型: {args.Type}");
        }
        var newLeader = faction.Leader;
        if (args.Leader is not null)
        {
            if (string.IsNullOrWhiteSpace(args.Leader))
            {
                newLeader = "";
            }
            else
            {
                newLeader = await ResolveLeaderId(args.NovelId, args.Leader, ct);
                if (newLeader is null)
                    return Error($"首领角色 {args.Leader} 不存在");
            }
        }
        var newHeadquartersId = faction.HeadquartersId;
        if (args.HeadquartersId is not null)
        {
            if (string.IsNullOrWhiteSpace(args.HeadquartersId))
            {
                newHeadquartersId = null;
            }
            else
            {
                newHeadquartersId = await ResolveLocationId(args.NovelId, args.HeadquartersId, ct);
                if (newHeadquartersId is null)
                    return Error($"地点 {args.HeadquartersId} 不存在");
            }
        }
        List<string>? newMembers = null;
        if (args.Members is not null)
        {
            newMembers = await ResolveMemberIds(args.NovelId, args.Members, ct);
            if (newMembers is null)
                return Error("成员列表中有角色不存在");
        }

        var changes = ManageHelper.DiffFields(
            ("名称", faction.Name, newName),
            ("类型", faction.Type.ToString(), newType.ToString()),
            ("描述", faction.Description, args.Description),
            ("首领", faction.Leader, newLeader),
            ("总部", faction.HeadquartersId, newHeadquartersId),
            ("目标", faction.Goal, args.Goal)
        );

        faction.Name = newName;
        faction.Type = newType;
        if (args.Description is not null)
            faction.Description = args.Description;
        faction.Leader = newLeader;
        faction.HeadquartersId = newHeadquartersId;
        if (args.Goal is not null)
            faction.Goal = args.Goal;
        if (newMembers is not null)
            faction.Members = newMembers;

        if (
            !string.IsNullOrWhiteSpace(faction.Leader)
            && !faction.Members.Contains(faction.Leader, StringComparer.Ordinal)
        )
            faction.Members.Add(faction.Leader);

        await EntityFieldWriter.SaveAsync(store, faction, before, ct);
        ThrowIfCancelled(ct);
        if (kernel is not null)
        {
            var (doc, chunks) = kernel.Builder.BuildFaction(faction);
            await kernel.IndexDocumentAsync(doc, chunks, ct);
        }
        ThrowIfCancelled(ct);

        var summary =
            changes.Count > 0
                ? $"组织「{faction.Name}」已更新:\n{string.Join("\n", changes)}"
                : $"组织「{faction.Name}」无变更";
        return Ok(summary, faction.Id, faction.Name);
    }

    /// <summary>
    /// 删除组织
    /// </summary>
    private async Task<AgentToolResult<ManageFactionDetails>> Delete(ManageFactionParams args)
    {
        if (string.IsNullOrEmpty(args.FactionId))
            return Error("删除组织需要 faction-id");

        var faction = await ResolveFaction(args);
        if (faction is null)
            return Error($"组织 {args.FactionId} 不存在（可用 list 查看 ID 或直接传名称）");

        var name = faction.Name;
        await store.DeleteFaction(faction.Id);
        return Ok($"组织「{name}」已删除", faction.Id);
    }

    /// <summary>
    /// 获取组织详情
    /// </summary>
    private async Task<AgentToolResult<ManageFactionDetails>> Get(ManageFactionParams args)
    {
        if (string.IsNullOrEmpty(args.FactionId))
            return Error("获取组织需要 faction-id");

        var faction = await ResolveFaction(args);
        if (faction is null)
            return Error($"组织 {args.FactionId} 不存在（可用 list 查看 ID 或直接传名称）");

        var typeLabel = faction.Type switch
        {
            FactionType.Sect => "门派",
            FactionType.Company => "公司",
            FactionType.Family => "家族",
            FactionType.Gang => "帮会",
            FactionType.Government => "政府",
            _ => "其他",
        };
        var text =
            $"组织: {faction.Name}\n类型: {typeLabel}\n描述: {faction.Description}\n领袖: {faction.Leader}\n总部: {faction.HeadquartersId ?? "无"}\n目标: {faction.Goal}\n成员: {string.Join(", ", faction.Members)}";
        return Ok(text, faction.Id, faction.Name);
    }

    /// <summary>
    /// 列出所有组织
    /// </summary>
    private async Task<AgentToolResult<ManageFactionDetails>> List(ManageFactionParams args)
    {
        var factions = await store.GetFactions(args.NovelId);
        var lines = factions.Select(f => $"- {f.Name} (ID: {f.Id}, 领袖: {f.Leader})");
        var text = factions.Count == 0 ? "暂无组织" : string.Join("\n", lines);
        return Ok(text, count: factions.Count);
    }

    /// <summary>
    /// 将组织 ID 或名称解析为真实组织（名称需配合 novel_id 缩小范围）。
    /// </summary>
    private async Task<Faction?> ResolveFaction(ManageFactionParams args)
    {
        if (string.IsNullOrEmpty(args.FactionId))
            return null;
        var factions = await store.GetFactions(args.NovelId);
        return ManageHelper.ResolveByIdOrName(factions, args.FactionId, f => f.Id, f => f.Name);
    }

    /// <summary>解析总部地点；空输入返回 null，非空但不存在也返回 null（调用方按输入区分）。</summary>
    private async Task<string?> ResolveLocationId(
        string novelId,
        string? idOrName,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(idOrName))
            return null;
        var location = await NovelReferenceResolver.LocationAsync(store, novelId, idOrName, ct);
        return location?.Id;
    }

    /// <summary>解析全部成员角色；任意一个不存在则返回 null。</summary>
    private async Task<List<string>?> ResolveMemberIds(
        string novelId,
        IReadOnlyList<string>? members,
        CancellationToken ct
    )
    {
        if (members is null || members.Count == 0)
            return [];
        var result = new List<string>(members.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            var character = await NovelReferenceResolver.CharacterAsync(store, novelId, member, ct);
            if (character is null)
                return null;
            if (seen.Add(character.Id))
                result.Add(character.Id);
        }
        return result;
    }

    private async Task<string?> ResolveLeaderId(
        string novelId,
        string? idOrName,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(idOrName))
            return null;
        var character = await NovelReferenceResolver.CharacterAsync(store, novelId, idOrName, ct);
        return character?.Id;
    }

    /// <summary>
    /// 构建成功响应
    /// </summary>
    private static AgentToolResult<ManageFactionDetails> Ok(
        string text,
        string? id = null,
        string? name = null,
        int count = 0
    ) =>
        new()
        {
            Content = [new TextContent(text)],
            Details = new ManageFactionDetails
            {
                Id = id ?? "",
                Name = name ?? "",
                Count = count,
            },
        };

    /// <summary>
    /// 构建错误响应
    /// </summary>
    private static AgentToolResult<ManageFactionDetails> Error(string msg) =>
        new() { Content = [new TextContent(msg)], IsError = true };
}

public sealed record ManageFactionParams
{
    [Description("操作: create / update / delete / get / list")]
    public string Action { get; init; } = "list";

    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("组织ID（update/delete/get 时必填；也支持直接传组织名称）")]
    public string? FactionId { get; init; }

    [Description("组织名称（create 时必填，update 时可选）")]
    public string? Name { get; init; }

    [Description(
        "组织类型: sect(门派)/company(公司)/family(家族)/gang(帮会)/government(政府)/other(其他)"
    )]
    public string? Type { get; init; }

    [Description("组织详细描述。写具体事实，不用 AI 宣传语")]
    public string? Description { get; init; }

    [Description("组织领袖姓名或角色ID")]
    public string? Leader { get; init; }

    [Description("总部地点ID")]
    public string? HeadquartersId { get; init; }

    [Description("组织目标")]
    public string? Goal { get; init; }

    [Description("成员角色ID列表")]
    public List<string>? Members { get; init; }
}

public sealed record ManageFactionDetails
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public int Count { get; init; }
}
