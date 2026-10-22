using System.ComponentModel;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Core.Vector;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 批量创建道具（一次 tool call 创建多个道具）。
/// </summary>
public sealed class BatchCreateItemsTool(INovelStore store, RetrievalKernel? kernel = null)
    : AgentTool<BatchCreateItemsParams, BatchCreateItemsDetails>
{
    public override string Name => "batch_create_items"; // 工具唯一标识
    public override string Label => "批量创建道具"; // 工具显示名称
    public override string Description =>
        "一次创建多个道具/物品。适合批量建立物品设定" + BatchToolHints.PreferBatch;
    public override ToolCategory Category => ToolCategory.EntityCrud; // 分类：实体CRUD
    public override string EntityType => "item"; // 实体类型：道具
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突

    public override async Task<AgentToolResult<BatchCreateItemsDetails>> ExecuteAsync(
        string toolCallId,
        BatchCreateItemsParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<BatchCreateItemsDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        // 参数校验：至少提供一个道具
        if (args.Items is null || args.Items.Count == 0)
        {
            return new AgentToolResult<BatchCreateItemsDetails>
            {
                Content = [new TextContent("请提供至少一个道具")],
                IsError = true,
            };
        }

        // 初始化结果收集器
        var createdIds = new List<string>(args.Items.Count);
        var createdNames = new List<string>(args.Items.Count);
        var failed = new List<string>();

        // 遍历每个道具草稿，逐个创建
        foreach (var item in args.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Name))
            {
                failed.Add("(空名称)");
                continue;
            }

            // 构建 Item 实体，自动补全默认值
            var entity = new Item
            {
                NovelId = args.NovelId,
                Name = item.Name.Trim(),
                Type = Enum.TryParse<ItemType>(
                    item.Type ?? "artifact",
                    ignoreCase: true,
                    out var it
                )
                    ? it
                    : ItemType.Artifact,
                Description = item.Description ?? "",
                OwnerId = item.OwnerId,
            };

            try
            {
                // 持久化道具并建立向量索引
                await store.SaveItem(entity, ct);
                ThrowIfCancelled(ct);
                if (kernel is not null)
                {
                    var (doc, chunks) = kernel.Builder.BuildItem(entity);
                    await kernel.IndexDocumentAsync(doc, chunks, ct);
                }
                ThrowIfCancelled(ct);

                createdIds.Add(entity.Id);
                createdNames.Add(entity.Name);
            }
            catch (Exception ex)
            {
                failed.Add($"{item.Name.Trim()}: {ex.Message}");
            }
        }

        // 构建操作结果摘要
        var summary =
            $"已创建 {createdIds.Count} 个道具"
            + (createdNames.Count > 0 ? $": {string.Join("、", createdNames)}" : "")
            + (failed.Count > 0 ? $"\n失败 {failed.Count} 个: {string.Join("；", failed)}" : "");

        return new AgentToolResult<BatchCreateItemsDetails>
        {
            Content = [new TextContent(summary)],
            Details = new BatchCreateItemsDetails
            {
                CreatedCount = createdIds.Count,
                CreatedIds = createdIds.ToArray(),
                FailedCount = failed.Count,
            },
        };
    }
}

public sealed record BatchCreateItemsParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("要创建的道具列表（每个道具至少提供 name）")]
    public List<ItemDraft>? Items { get; init; }
}

/// <summary>
/// 道具草稿 — 用于批量创建时的输入数据结构。
/// </summary>
public sealed record ItemDraft
{
    [Description("道具名称（必填）")]
    public string? Name { get; init; } // 道具名称（必填）

    [Description(
        "道具类型: weapon(武器)/artifact(法器)/token(信物)/consumable(消耗品)/other(其他)"
    )]
    public string? Type { get; init; } // 道具类型

    [Description("道具详细描述。写具体外观和功能，不用 AI 词")]
    public string? Description { get; init; } // 道具描述

    [Description("持有者角色ID")]
    public string? OwnerId { get; init; } // 持有者ID
}

/// <summary>
/// 批量创建道具结果详情。
/// </summary>
public sealed record BatchCreateItemsDetails
{
    public int CreatedCount { get; init; } // 成功创建数量
    public string[] CreatedIds { get; init; } = []; // 成功创建的ID列表
    public int FailedCount { get; init; } // 失败数量
}
