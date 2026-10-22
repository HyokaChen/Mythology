using System.ComponentModel;
using Core.Data;
using Core.Shared;
using Core.Tools;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 批量创建角色关系（一次 tool call 创建多个关系）。
/// </summary>
public sealed class BatchCreateRelationshipsTool(INovelStore store)
    : AgentTool<BatchCreateRelationshipsParams, BatchCreateRelationshipsDetails>
{
    public override string Name => "batch_create_relationships"; // 工具唯一标识
    public override string Label => "批量创建关系"; // 工具显示名称
    public override string Description =>
        "一次创建多个角色关系。适合角色设定完成后批量建立关系网" + BatchToolHints.PreferBatch;
    public override ToolCategory Category => ToolCategory.EntityCrud; // 分类：实体CRUD
    public override string EntityType => "relationship"; // 实体类型：关系
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突

    public override async Task<AgentToolResult<BatchCreateRelationshipsDetails>> ExecuteAsync(
        string toolCallId,
        BatchCreateRelationshipsParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<BatchCreateRelationshipsDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        // 参数校验：至少提供一个关系
        if (args.Relationships is null || args.Relationships.Count == 0)
        {
            return new AgentToolResult<BatchCreateRelationshipsDetails>
            {
                Content = [new TextContent("请提供至少一个关系")],
                IsError = true,
            };
        }

        // 初始化结果收集器
        var createdIds = new List<string>(args.Relationships.Count);
        var failed = new List<string>();

        // 遍历每个关系草稿，逐个创建
        foreach (var r in args.Relationships)
        {
            // 校验：必须提供两个角色ID
            if (
                string.IsNullOrWhiteSpace(r.CharacterAId)
                || string.IsNullOrWhiteSpace(r.CharacterBId)
            )
            {
                failed.Add("(缺少角色ID)");
                continue;
            }

            // 构建 Relationship 实体，自动补全默认值
            var rel = new Relationship
            {
                NovelId = args.NovelId,
                CharacterAId = r.CharacterAId.Trim(),
                CharacterBId = r.CharacterBId.Trim(),
                Type =
                    RelationshipTypeExtensions.ParseRelationshipType(r.Type)
                    ?? RelationshipType.Friend, // 默认朋友
                Strength = Enum.TryParse<RelationshipStrength>(
                    r.Strength ?? "medium",
                    ignoreCase: true,
                    out var rs
                )
                    ? rs
                    : RelationshipStrength.Medium, // 默认中等强度
                Description = r.Description ?? "",
            };

            try
            {
                // 持久化关系
                await store.SaveRelationship(rel, ct);
                ThrowIfCancelled(ct);
                createdIds.Add(rel.Id);
            }
            catch (Exception ex)
            {
                failed.Add($"{r.CharacterAId.Trim()} ↔ {r.CharacterBId.Trim()}: {ex.Message}");
            }
        }

        // 构建操作结果摘要
        var summary =
            $"已创建 {createdIds.Count} 个关系"
            + (failed.Count > 0 ? $"\n失败 {failed.Count} 个: {string.Join("；", failed)}" : "");

        return new AgentToolResult<BatchCreateRelationshipsDetails>
        {
            Content = [new TextContent(summary)],
            Details = new BatchCreateRelationshipsDetails
            {
                CreatedCount = createdIds.Count,
                CreatedIds = createdIds.ToArray(),
                FailedCount = failed.Count,
            },
        };
    }
}

public sealed record BatchCreateRelationshipsParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("要创建的关系列表（每个关系至少提供 character_a_id 和 character_b_id）")]
    public List<RelationshipDraft>? Relationships { get; init; }
}

/// <summary>
/// 关系草稿 — 用于批量创建时的输入数据结构。
/// </summary>
public sealed record RelationshipDraft
{
    [Description("第一个角色ID（必填）")]
    public string? CharacterAId { get; init; } // 第一个角色ID（必填）

    [Description("第二个角色ID（必填）")]
    public string? CharacterBId { get; init; } // 第二个角色ID（必填）

    [Description("关系类型：friend/rival/family/lover/enemy/mentor/master_servant/fellow_disciple")]
    public string? Type { get; init; } // 关系类型

    [Description("关系强度：weak/medium/strong/extreme（可选，默认 medium）")]
    public string? Strength { get; init; } // 关系强度

    [Description("关系详细描述。写具体互动和矛盾")]
    public string? Description { get; init; } // 关系描述
}

/// <summary>
/// 批量创建关系结果详情。
/// </summary>
public sealed record BatchCreateRelationshipsDetails
{
    public int CreatedCount { get; init; } // 成功创建数量
    public string[] CreatedIds { get; init; } = []; // 成功创建的ID列表
    public int FailedCount { get; init; } // 失败数量
}
