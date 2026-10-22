using System.ComponentModel;
using Core.Data;
using Core.Shared;
using Core.Tools;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 关系管理工具：角色关系的增删改查（CRUD）操作
/// </summary>
public sealed class ManageRelationshipTool(INovelStore store)
    : AgentTool<ManageRelationshipParams, ManageRelationshipDetails>
{
    /// <summary>工具唯一标识符</summary>
    public override string Name => "manage_relationship";
    public override string[] Aliases => ["关系", "人物关系", "关系网"];
    public override string[] ExampleQueries => ["设置林晚和陆离的师徒关系"];

    /// <summary>工具显示名称</summary>
    public override string Label => "关系管理";
    public override string Description =>
        "角色关系 CRUD：create（建立关系）、update（更新关系类型/描述）、delete（删除关系）、list（列出所有关系）"
        + BatchToolHints.PreferBatchForMultiple;
    public override ToolCategory Category => ToolCategory.EntityCrud;
    public override string? EntityType => "relationship";
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突

    /// <summary>
    /// 执行关系管理操作
    /// </summary>
    /// <param name="toolCallId">工具调用ID</param>
    /// <param name="args">管理参数</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="onUpdate">进度更新回调</param>
    /// <returns>操作结果</returns>
    public override async Task<AgentToolResult<ManageRelationshipDetails>> ExecuteAsync(
        string toolCallId,
        ManageRelationshipParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<ManageRelationshipDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        using var _ = Core.Evolution.OntologyWriteContext.Attach(args.Evidence, args.ProposalId);
        return args.Action.ToLowerInvariant() switch
        {
            "create" => await Create(args),
            "update" => await Update(args),
            "delete" => await Delete(args),
            "list" => await List(args),
            _ => Error($"未知操作: {args.Action}。支持: create/update/delete/list"),
        };
    }

    /// <summary>
    /// 创建新关系
    /// </summary>
    private async Task<AgentToolResult<ManageRelationshipDetails>> Create(
        ManageRelationshipParams args
    )
    {
        if (string.IsNullOrEmpty(args.CharacterAId) || string.IsNullOrEmpty(args.CharacterBId))
            return Error("创建关系需要 character_a_id 和 character-b-id");

        // 容忍传角色名称而非 ID（与 manage_character 一致）
        var characterAId =
            await ResolveCharacterId(args.NovelId, args.CharacterAId) ?? args.CharacterAId;
        var characterBId =
            await ResolveCharacterId(args.NovelId, args.CharacterBId) ?? args.CharacterBId;

        var rel = new Relationship
        {
            NovelId = args.NovelId,
            CharacterAId = characterAId,
            CharacterBId = characterBId,
            Type =
                RelationshipTypeExtensions.ParseRelationshipType(args.Type)
                ?? RelationshipType.Friend,
            Strength = Enum.TryParse<RelationshipStrength>(
                args.Strength ?? "medium",
                ignoreCase: true,
                out var rs
            )
                ? rs
                : RelationshipStrength.Medium,
            Description = args.Description ?? "",
        };
        await store.SaveRelationship(rel);
        return Ok($"关系已创建: {args.CharacterAId} ↔ {args.CharacterBId} ({rel.Type})", rel.Id);
    }

    /// <summary>
    /// 更新关系信息
    /// </summary>
    private async Task<AgentToolResult<ManageRelationshipDetails>> Update(
        ManageRelationshipParams args
    )
    {
        if (string.IsNullOrEmpty(args.RelationshipId))
            return Error("更新关系需要 relationship-id");

        var rel = await store.GetRelationship(args.RelationshipId);
        if (rel is null)
            return Error($"关系 {args.RelationshipId} 不存在");

        var changes = new List<string>();
        var newType = RelationshipTypeExtensions.ParseRelationshipType(args.Type);

        var newStrength = Enum.TryParse<RelationshipStrength>(
            args.Strength,
            ignoreCase: true,
            out var parsedStrength
        )
            ? parsedStrength
            : (RelationshipStrength?)null;

        if (newType is not null && newType != rel.Type)
            changes.Add($"类型: {rel.Type.ToKebabCaseString()} → {ManageHelper.Diff(args.Type)}");
        if (newStrength is not null && newStrength != rel.Strength)
            changes.Add(
                $"强度: {rel.Strength.ToString().ToLower()} → {ManageHelper.Diff(args.Strength)}"
            );
        if (args.Description is not null && args.Description != rel.Description)
            changes.Add(
                $"描述: {ManageHelper.Diff(rel.Description)} → {ManageHelper.Diff(args.Description)}"
            );

        if (newType is not null)
            rel.Type = newType.Value;
        if (newStrength is not null)
            rel.Strength = newStrength.Value;
        if (args.Description is not null)
            rel.Description = args.Description;

        await store.SaveRelationship(rel);

        var summary =
            changes.Count > 0
                ? $"关系「{rel.CharacterAId} → {rel.CharacterBId}」已更新:\n{string.Join("\n", changes)}"
                : $"关系「{rel.CharacterAId} → {rel.CharacterBId}」无变更";
        return Ok(summary, rel.Id);
    }

    /// <summary>
    /// 删除关系
    /// </summary>
    private async Task<AgentToolResult<ManageRelationshipDetails>> Delete(
        ManageRelationshipParams args
    )
    {
        if (string.IsNullOrEmpty(args.RelationshipId))
            return Error("删除关系需要 relationship-id");

        var rel = await store.GetRelationship(args.RelationshipId);
        if (rel is null)
            return Error($"关系 {args.RelationshipId} 不存在");

        var charAId = rel.CharacterAId;
        var charBId = rel.CharacterBId;
        var relType = rel.Type;
        await store.DeleteRelationship(args.RelationshipId);
        return Ok($"关系「{charAId} → {charBId} ({relType})」已删除", args.RelationshipId);
    }

    /// <summary>
    /// 列出小说内所有关系（update/delete 时可通过这里获取 relationship_id）。
    /// </summary>
    private async Task<AgentToolResult<ManageRelationshipDetails>> List(
        ManageRelationshipParams args
    )
    {
        if (string.IsNullOrEmpty(args.NovelId))
            return Error("列出关系需要 novel-id");

        var rels = await store.GetRelationshipsByNovel(args.NovelId);
        var characters = await store.GetCharacters(args.NovelId);
        var nameOf = characters.ToDictionary(c => c.Id, c => c.Name);
        var lines = rels.Select(r =>
        {
            var a = nameOf.GetValueOrDefault(r.CharacterAId, r.CharacterAId);
            var b = nameOf.GetValueOrDefault(r.CharacterBId, r.CharacterBId);
            return $"- {a} ↔ {b} ({r.Type}, 强度: {r.Strength}) (ID: {r.Id})";
        });
        var text = rels.Count == 0 ? "暂无关系" : string.Join("\n", lines);
        return Ok(text, count: rels.Count);
    }

    /// <summary>
    /// 将角色 ID 或名称解析为真实角色 ID（名称需配合 novel_id 缩小范围）。
    /// </summary>
    private async Task<string?> ResolveCharacterId(string novelId, string? characterId)
    {
        if (string.IsNullOrEmpty(characterId))
            return null;
        var characters = await store.GetCharacters(novelId);
        var character = ManageHelper.ResolveByIdOrName(
            characters,
            characterId,
            c => c.Id,
            c => c.Name
        );
        return character?.Id;
    }

    /// <summary>
    /// 构建成功响应
    /// </summary>
    private static AgentToolResult<ManageRelationshipDetails> Ok(
        string text,
        string? id = null,
        int count = 0
    ) =>
        new()
        {
            Content = [new TextContent(text)],
            Details = new ManageRelationshipDetails { Id = id ?? "", Count = count },
        };

    /// <summary>
    /// 构建错误响应
    /// </summary>
    private static AgentToolResult<ManageRelationshipDetails> Error(string msg) =>
        new() { Content = [new TextContent(msg)], IsError = true };
}

public sealed record ManageRelationshipParams
{
    [Description("操作: create / update / delete")]
    public string Action { get; init; } = "create";

    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("关系ID（update/delete 时必填；list 可查看全部关系 ID）")]
    public string? RelationshipId { get; init; }

    [Description("关系中的第一个角色ID或名称（create 时必填）")]
    public string? CharacterAId { get; init; }

    [Description("关系中的第二个角色ID或名称（create 时必填）")]
    public string? CharacterBId { get; init; }

    [Description(
        "关系类型：friend/rival/family/lover/enemy/mentor/master-servant/fellow-disciple/parent-of/child-of/sibling/spouse/sworn/disciple/ally"
    )]
    public string? Type { get; init; }

    [Description("关系强度：weak/medium/strong/extreme（可选，默认 medium）")]
    public string? Strength { get; init; }

    [Description("关系详细描述。写具体互动和矛盾")]
    public string? Description { get; init; }

    [Description("正文证据引文（可选，应用 scan_relationships 提案时传）")]
    public string? Evidence { get; init; }

    [Description("提案 ID（可选，来自 scan_relationships / scan_ontology）")]
    public string? ProposalId { get; init; }
}

public sealed record ManageRelationshipDetails
{
    public string Id { get; init; } = "";
    public int Count { get; init; }
}
