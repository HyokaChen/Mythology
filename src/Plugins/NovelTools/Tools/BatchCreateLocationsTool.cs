using System.ComponentModel;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Core.Vector;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 批量创建地点（一次 tool call 创建多个地点）。
/// </summary>
public sealed class BatchCreateLocationsTool(INovelStore store, RetrievalKernel? kernel = null)
    : AgentTool<BatchCreateLocationsParams, BatchCreateLocationsDetails>
{
    public override string Name => "batch_create_locations"; // 工具唯一标识
    public override string Label => "批量创建地点"; // 工具显示名称
    public override string Description =>
        "一次创建多个地点。支持层级嵌套（parent_id）。适合世界观设定时批量建立地点"
        + BatchToolHints.PreferBatch;
    public override ToolCategory Category => ToolCategory.EntityCrud; // 分类：实体CRUD
    public override string EntityType => "location"; // 实体类型：地点
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突

    public override async Task<AgentToolResult<BatchCreateLocationsDetails>> ExecuteAsync(
        string toolCallId,
        BatchCreateLocationsParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<BatchCreateLocationsDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        // 参数校验：至少提供一个地点
        if (args.Locations is null || args.Locations.Count == 0)
        {
            return new AgentToolResult<BatchCreateLocationsDetails>
            {
                Content = [new TextContent("请提供至少一个地点")],
                IsError = true,
            };
        }

        // 初始化结果收集器
        var createdIds = new List<string>(args.Locations.Count);
        var createdNames = new List<string>(args.Locations.Count);
        var failed = new List<string>();

        // 遍历每个地点草稿，逐个创建
        foreach (var l in args.Locations)
        {
            if (string.IsNullOrWhiteSpace(l.Name))
            {
                failed.Add("(空名称)");
                continue;
            }

            // 构建 Location 实体，自动补全默认值
            var location = new Location
            {
                NovelId = args.NovelId,
                Name = l.Name.Trim(),
                Type = Enum.TryParse<LocationType>(l.Type ?? "city", ignoreCase: true, out var lt)
                    ? lt
                    : LocationType.City,
                Atmosphere = l.Atmosphere ?? "",
                Description = l.Description ?? "",
                ParentId = l.ParentId,
            };

            try
            {
                // 持久化地点并建立向量索引
                await store.SaveLocation(location, ct);
                ThrowIfCancelled(ct);
                if (kernel is not null)
                {
                    var (doc, chunks) = kernel.Builder.BuildLocation(location);
                    await kernel.IndexDocumentAsync(doc, chunks, ct);
                }
                ThrowIfCancelled(ct);

                createdIds.Add(location.Id);
                createdNames.Add(location.Name);
            }
            catch (Exception ex)
            {
                failed.Add($"{l.Name.Trim()}: {ex.Message}");
            }
        }

        // 构建操作结果摘要
        var summary =
            $"已创建 {createdIds.Count} 个地点"
            + (createdNames.Count > 0 ? $": {string.Join("、", createdNames)}" : "")
            + (failed.Count > 0 ? $"\n失败 {failed.Count} 个: {string.Join("；", failed)}" : "");

        return new AgentToolResult<BatchCreateLocationsDetails>
        {
            Content = [new TextContent(summary)],
            Details = new BatchCreateLocationsDetails
            {
                CreatedCount = createdIds.Count,
                CreatedIds = createdIds.ToArray(),
                FailedCount = failed.Count,
            },
        };
    }
}

public sealed record BatchCreateLocationsParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("要创建的地点列表（每个地点至少提供 name）")]
    public List<LocationDraft>? Locations { get; init; }
}

/// <summary>
/// 地点草稿 — 用于批量创建时的输入数据结构。
/// </summary>
public sealed record LocationDraft
{
    [Description("地点名称（必填）")]
    public string? Name { get; init; } // 地点名称（必填）

    [Description(
        "地点类型: city(城市)/rural(乡村)/indoor(室内)/wilderness(荒野)/fictional(虚构)/other(其他)"
    )]
    public string? Type { get; init; } // 地点类型

    [Description("氛围/情绪基调")]
    public string? Atmosphere { get; init; } // 氛围

    [Description("地点详细描述。写具体特征，不用 AI 宣传语")]
    public string? Description { get; init; } // 地点描述

    [Description("父地点ID（支持层级嵌套，可选）")]
    public string? ParentId { get; init; } // 父地点ID（层级嵌套）
}

/// <summary>
/// 批量创建地点结果详情。
/// </summary>
public sealed record BatchCreateLocationsDetails
{
    public int CreatedCount { get; init; } // 成功创建数量
    public string[] CreatedIds { get; init; } = []; // 成功创建的ID列表
    public int FailedCount { get; init; } // 失败数量
}
