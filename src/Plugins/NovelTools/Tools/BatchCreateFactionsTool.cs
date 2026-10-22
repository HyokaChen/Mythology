using System.ComponentModel;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Core.Vector;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 批量创建组织/门派（一次 tool call 创建多个组织）。
/// </summary>
public sealed class BatchCreateFactionsTool(INovelStore store, RetrievalKernel? kernel = null)
    : AgentTool<BatchCreateFactionsParams, BatchCreateFactionsDetails>
{
    public override string Name => "batch_create_factions"; // 工具唯一标识
    public override string Label => "批量创建组织"; // 工具显示名称
    public override string Description =>
        "一次创建多个组织/门派。适合世界观设定时批量建立势力" + BatchToolHints.PreferBatch;
    public override ToolCategory Category => ToolCategory.EntityCrud; // 分类：实体CRUD
    public override string EntityType => "faction"; // 实体类型：组织
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突

    public override async Task<AgentToolResult<BatchCreateFactionsDetails>> ExecuteAsync(
        string toolCallId,
        BatchCreateFactionsParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<BatchCreateFactionsDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        // 参数校验：至少提供一个组织
        if (args.Factions is null || args.Factions.Count == 0)
        {
            return new AgentToolResult<BatchCreateFactionsDetails>
            {
                Content = [new TextContent("请提供至少一个组织")],
                IsError = true,
            };
        }

        // 初始化结果收集器
        var createdIds = new List<string>(args.Factions.Count);
        var createdNames = new List<string>(args.Factions.Count);
        var failed = new List<string>();

        // 遍历每个组织草稿，逐个创建
        foreach (var f in args.Factions)
        {
            if (string.IsNullOrWhiteSpace(f.Name))
            {
                failed.Add("(空名称)");
                continue;
            }

            // 构建 Faction 实体，自动补全默认值
            var faction = new Faction
            {
                NovelId = args.NovelId,
                Name = f.Name.Trim(),
                Type = Enum.TryParse<FactionType>(f.Type ?? "sect", ignoreCase: true, out var ft)
                    ? ft
                    : FactionType.Sect,
                Description = f.Description ?? "",
                Leader = f.Leader ?? "",
                HeadquartersId = f.HeadquartersId,
                Goal = f.Goal ?? "",
                Members = f.Members ?? [],
            };

            try
            {
                // 持久化组织并建立向量索引
                await store.SaveFaction(faction, ct);
                ThrowIfCancelled(ct);
                if (kernel is not null)
                {
                    var (doc, chunks) = kernel.Builder.BuildFaction(faction);
                    await kernel.IndexDocumentAsync(doc, chunks, ct);
                }
                ThrowIfCancelled(ct);

                createdIds.Add(faction.Id);
                createdNames.Add(faction.Name);
            }
            catch (Exception ex)
            {
                failed.Add($"{f.Name.Trim()}: {ex.Message}");
            }
        }

        // 构建操作结果摘要
        var summary =
            $"已创建 {createdIds.Count} 个组织"
            + (createdNames.Count > 0 ? $": {string.Join("、", createdNames)}" : "")
            + (failed.Count > 0 ? $"\n失败 {failed.Count} 个: {string.Join("；", failed)}" : "");

        return new AgentToolResult<BatchCreateFactionsDetails>
        {
            Content = [new TextContent(summary)],
            Details = new BatchCreateFactionsDetails
            {
                CreatedCount = createdIds.Count,
                CreatedIds = createdIds.ToArray(),
                FailedCount = failed.Count,
            },
        };
    }
}

public sealed record BatchCreateFactionsParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("要创建的组织列表（每个组织至少提供 name）")]
    public List<FactionDraft>? Factions { get; init; }
}

/// <summary>
/// 组织草稿 — 用于批量创建时的输入数据结构。
/// </summary>
public sealed record FactionDraft
{
    [Description("组织名称（必填）")]
    public string? Name { get; init; } // 组织名称（必填）

    [Description(
        "组织类型: sect(门派)/company(公司)/family(家族)/gang(帮会)/government(政府)/other(其他)"
    )]
    public string? Type { get; init; } // 组织类型

    [Description("组织详细描述。写具体事实，不用 AI 宣传语")]
    public string? Description { get; init; } // 组织描述

    [Description("组织领袖姓名或角色ID")]
    public string? Leader { get; init; } // 领袖

    [Description("总部地点ID")]
    public string? HeadquartersId { get; init; } // 总部地点ID

    [Description("组织目标")]
    public string? Goal { get; init; } // 组织目标

    [Description("成员列表，JSON 数组字符串")]
    public List<string>? Members { get; init; } // 成员ID列表
}

/// <summary>
/// 批量创建组织结果详情。
/// </summary>
public sealed record BatchCreateFactionsDetails
{
    public int CreatedCount { get; init; } // 成功创建数量
    public string[] CreatedIds { get; init; } = []; // 成功创建的ID列表
    public int FailedCount { get; init; } // 失败数量
}
