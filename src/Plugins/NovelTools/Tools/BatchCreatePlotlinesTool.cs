using System.ComponentModel;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Core.Vector;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 批量创建情节线（一次 tool call 创建多个情节线）。
/// </summary>
public sealed class BatchCreatePlotlinesTool(INovelStore store, RetrievalKernel? kernel = null)
    : AgentTool<BatchCreatePlotlinesParams, BatchCreatePlotlinesDetails>
{
    public override string Name => "batch_create_plotlines";
    public override string[] Aliases => ["批量创建情节线"];
    public override string[] ExampleQueries => ["批量建立三条支线"]; // 工具唯一标识
    public override string Label => "批量创建情节线"; // 工具显示名称
    public override string Description =>
        "一次创建多个情节线。适合在设定阶段批量建立主线、支线、伏笔等" + BatchToolHints.PreferBatch;
    public override ToolCategory Category => ToolCategory.EntityCrud; // 分类：实体CRUD
    public override string EntityType => "plotline"; // 实体类型：情节线
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突

    public override async Task<AgentToolResult<BatchCreatePlotlinesDetails>> ExecuteAsync(
        string toolCallId,
        BatchCreatePlotlinesParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<BatchCreatePlotlinesDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        // 参数校验：至少提供一个情节线
        if (args.Plotlines is null || args.Plotlines.Count == 0)
        {
            return new AgentToolResult<BatchCreatePlotlinesDetails>
            {
                Content = [new TextContent("请提供至少一个情节线")],
                IsError = true,
            };
        }

        // 初始化结果收集器
        var createdIds = new List<string>(args.Plotlines.Count);
        var createdNames = new List<string>(args.Plotlines.Count);
        var failed = new List<string>();

        // 遍历每个情节线草稿，逐个创建
        foreach (var p in args.Plotlines)
        {
            if (string.IsNullOrWhiteSpace(p.Name))
            {
                failed.Add("(空名称)");
                continue;
            }

            // 构建 Plotline 实体，自动补全默认值
            var plotline = new Plotline
            {
                NovelId = args.NovelId,
                Name = p.Name.Trim(),
                Description = p.Description ?? "",
                Type = Enum.TryParse<PlotlineType>(p.Type, ignoreCase: true, out var t)
                    ? t
                    : PlotlineType.Main, // 默认主线
                PlannedRevealChapterId = p.PlannedRevealChapterId,
                Tension = Enum.TryParse<PlotlineTension>(
                    p.Tension ?? "medium",
                    ignoreCase: true,
                    out var tn
                )
                    ? tn
                    : PlotlineTension.Medium,
                Turnpoints = p.Turnpoints ?? "",
                ResolvedAtChapterId = p.ResolvedAtChapterId,
            };

            try
            {
                // 持久化情节线并建立向量索引
                await store.SavePlotline(plotline, ct);
                ThrowIfCancelled(ct);
                if (kernel is not null)
                {
                    var (doc, chunks) = kernel.Builder.BuildPlotline(plotline);
                    await kernel.IndexDocumentAsync(doc, chunks, ct);
                }
                ThrowIfCancelled(ct);

                createdIds.Add(plotline.Id);
                createdNames.Add(plotline.Name);
            }
            catch (Exception ex)
            {
                failed.Add($"{p.Name.Trim()}: {ex.Message}");
            }
        }

        // 构建操作结果摘要
        var summary =
            $"已创建 {createdIds.Count} 条情节线"
            + (createdNames.Count > 0 ? $": {string.Join("、", createdNames)}" : "")
            + (failed.Count > 0 ? $"\n失败 {failed.Count} 个: {string.Join("；", failed)}" : "");

        return new AgentToolResult<BatchCreatePlotlinesDetails>
        {
            Content = [new TextContent(summary)],
            Details = new BatchCreatePlotlinesDetails
            {
                CreatedCount = createdIds.Count,
                CreatedIds = createdIds.ToArray(),
                FailedCount = failed.Count,
            },
        };
    }
}

public sealed record BatchCreatePlotlinesParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("要创建的情节线列表（每个情节线至少提供 name）")]
    public List<PlotlineDraft>? Plotlines { get; init; }
}

/// <summary>
/// 情节线草稿 — 用于批量创建时的输入数据结构。
/// </summary>
public sealed record PlotlineDraft
{
    [Description("情节线名称（必填）")]
    public string? Name { get; init; } // 情节线名称（必填）

    [Description("情节线详细描述")]
    public string? Description { get; init; } // 情节线描述

    [Description("类型：main / side / hidden / foreshadowing")]
    public string? Type { get; init; } // 情节线类型

    [Description("目标揭示章节ID（期望在哪一章揭示/解决）")]
    public string? PlannedRevealChapterId { get; init; } // 计划揭示章节ID

    [Description("紧张度：low / medium / high / critical")]
    public string? Tension { get; init; } // 紧张度

    [Description("关键转折（如\"第5章：真相揭露；第12章：盟友背叛\"）")]
    public string? Turnpoints { get; init; } // 关键转折

    [Description("解决章节ID（设置后标记为已解决）")]
    public string? ResolvedAtChapterId { get; init; } // 解决章节ID
}

/// <summary>
/// 批量创建情节线结果详情。
/// </summary>
public sealed record BatchCreatePlotlinesDetails
{
    public int CreatedCount { get; init; } // 成功创建数量
    public string[] CreatedIds { get; init; } = []; // 成功创建的ID列表
    public int FailedCount { get; init; } // 失败数量
}
