using System.ComponentModel;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Core.Vector;

namespace Plugins.NovelTools.Tools;

// ── find_similar_character（语义搜索，独立保留） ──

/// <summary>
/// 查找相似角色工具 — 基于向量搜索或关键词匹配，避免重复设定。
/// </summary>
public sealed class FindSimilarCharacterTool(INovelStore store, RetrievalKernel? kernel = null)
    : AgentTool<FindSimilarCharacterParams, FindSimilarCharacterDetails>
{
    public override string Name => "find_similar_character"; // 工具唯一标识
    public override string Label => "查找相似角色"; // 工具显示名称
    public override string Description => "基于文本描述在当前小说中查找相似角色，避免重复设定";
    public override ToolCategory Category => ToolCategory.EntityRead; // 分类：实体读取
    public override string EntityType => "character"; // 实体类型：角色

    public override async Task<AgentToolResult<FindSimilarCharacterDetails>> ExecuteAsync(
        string toolCallId,
        FindSimilarCharacterParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<FindSimilarCharacterDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        var query = args.Description?.ToLowerInvariant() ?? "";
        if (string.IsNullOrEmpty(query))
        {
            return new AgentToolResult<FindSimilarCharacterDetails>
            {
                Content = [new TextContent("请提供角色描述用于搜索")],
                IsError = true,
            };
        }

        if (kernel is not null)
        {
            var results = await kernel.SearchAsync(
                new RetrievalSearchRequest
                {
                    Domain = RetrievalDomain.NovelKnowledge,
                    OwnerId = "local",
                    NovelId = args.NovelId,
                    Query = query,
                    DocumentKinds = [DocumentKind.CharacterProfile],
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
                return new AgentToolResult<FindSimilarCharacterDetails>
                {
                    Content = [new TextContent(string.Join("\n", semanticLines))],
                    Details = new FindSimilarCharacterDetails { Count = results.Count },
                };
            }
        }

        // 回退到关键词匹配（向量搜索不可用或无结果时）
        var characters = await store.GetCharacters(args.NovelId, ct);
        ThrowIfCancelled(ct);
        var scored = characters
            .Select(c => new { Character = c, Score = CountMatches(c, query) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(5)
            .ToList();

        if (scored.Count == 0)
            return new AgentToolResult<FindSimilarCharacterDetails>
            {
                Content = [new TextContent("未找到相似角色")],
                Details = new FindSimilarCharacterDetails { Count = 0 },
            };

        var lines = scored.Select(x =>
            $"- {x.Character.Name} (匹配度: {x.Score}): 性格={x.Character.Personality}, 外貌={x.Character.Appearance}"
        );

        return new AgentToolResult<FindSimilarCharacterDetails>
        {
            Content = [new TextContent(string.Join("\n", lines))],
            Details = new FindSimilarCharacterDetails { Count = scored.Count },
        };
    }

    /// <summary>
    /// 计算角色与查询词的匹配度 — 基于多字段关键词匹配。
    /// </summary>
    private static int CountMatches(Character c, string query)
    {
        // 提取角色的所有文本字段
        var fields = new[]
        {
            c.Name,
            c.Gender,
            c.Role.ToString(),
            c.Personality,
            c.Appearance,
            c.Background,
            c.Motivation,
        };
        // 对每个字段分词后统计匹配数
        return fields.Sum(f =>
            (f.ToLowerInvariant() ?? "")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Count(query.Contains)
        );
    }
}

/// <summary>
/// 查找相似角色工具参数。
/// </summary>
public sealed record FindSimilarCharacterParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("用于搜索相似角色的文本描述")]
    public string? Description { get; init; } // 角色描述文本
}

/// <summary>
/// 查找相似角色结果详情。
/// </summary>
public sealed record FindSimilarCharacterDetails
{
    public int Count { get; init; } // 匹配结果数量
}

// ── get_relationship_network（图查询，独立保留） ──

/// <summary>
/// 获取关系网工具 — 查询某个角色的完整关系图。
/// </summary>
public sealed class GetRelationshipNetworkTool(INovelStore store)
    : AgentTool<GetRelationshipNetworkParams, RelationshipNetworkDetails>
{
    public override string Name => "get_relationship_network";
    public override string[] Aliases => ["关系网", "人物关系图"];
    public override string[] ExampleQueries => ["查看人物关系网络"]; // 工具唯一标识
    public override string Label => "获取关系网"; // 工具显示名称
    public override string Description => "获取某个角色的完整关系图";
    public override ToolCategory Category => ToolCategory.EntityRead; // 分类：实体读取
    public override string EntityType => "relationship"; // 实体类型：关系

    public override async Task<AgentToolResult<RelationshipNetworkDetails>> ExecuteAsync(
        string toolCallId,
        GetRelationshipNetworkParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<RelationshipNetworkDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        // 获取指定角色的所有关系
        var rels = await store.GetRelationshipsByCharacter(args.CharacterId, ct);
        ThrowIfCancelled(ct);
        var lines = rels.Select(r =>
        {
            // 找出关系中的另一方角色ID
            var otherId = r.CharacterAId == args.CharacterId ? r.CharacterBId : r.CharacterAId;
            return $"- ↔ {otherId} ({r.Type}: {r.Description})";
        });
        var text = rels.Count == 0 ? "该角色暂无关系" : string.Join("\n", lines);
        return new AgentToolResult<RelationshipNetworkDetails>
        {
            Content = [new TextContent(text)],
            Details = new RelationshipNetworkDetails { RelationshipCount = rels.Count },
        };
    }
}

/// <summary>
/// 获取关系网工具参数。
/// </summary>
public sealed record GetRelationshipNetworkParams
{
    [Description("要查询关系网的角色ID")]
    public string CharacterId { get; init; } = ""; // 角色ID
}

/// <summary>
/// 获取关系网结果详情。
/// </summary>
public sealed record RelationshipNetworkDetails
{
    public int RelationshipCount { get; init; } // 关系数量
}
