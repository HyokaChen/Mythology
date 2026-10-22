using System.ComponentModel;
using Core.Data;
using Core.Shared;
using Core.Tools;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 批量创建时间线事件（一次 tool call 创建多个事件）。
/// </summary>
public sealed class BatchCreateTimelineEventsTool(INovelStore store)
    : AgentTool<BatchCreateTimelineEventsParams, BatchCreateTimelineEventsDetails>
{
    public override string Name => "batch_create_timeline_events"; // 工具唯一标识
    public override string Label => "批量创建时间线事件"; // 工具显示名称
    public override string Description =>
        "一次创建多个时间线事件。适合大纲阶段批量建立事件年表" + BatchToolHints.PreferBatch;
    public override ToolCategory Category => ToolCategory.EntityCrud; // 分类：实体CRUD
    public override string EntityType => "timeline-event"; // 实体类型：时间线事件
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突

    public override async Task<AgentToolResult<BatchCreateTimelineEventsDetails>> ExecuteAsync(
        string toolCallId,
        BatchCreateTimelineEventsParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<BatchCreateTimelineEventsDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        // 参数校验：至少提供一个事件
        if (args.Events is null || args.Events.Count == 0)
        {
            return new AgentToolResult<BatchCreateTimelineEventsDetails>
            {
                Content = [new TextContent("请提供至少一个时间线事件")],
                IsError = true,
            };
        }

        // 初始化结果收集器
        var createdIds = new List<string>(args.Events.Count);
        var createdTitles = new List<string>(args.Events.Count);
        var failed = new List<string>();

        // 遍历每个事件草稿，逐个创建
        foreach (var evt in args.Events)
        {
            if (string.IsNullOrWhiteSpace(evt.Title))
            {
                failed.Add("(空标题)");
                continue;
            }

            // 构建 TimelineEvent 实体，自动补全默认值
            var entity = new TimelineEvent
            {
                NovelId = args.NovelId,
                Title = evt.Title.Trim(),
                Description = evt.Description ?? "",
                ChapterId = evt.ChapterId,
            };

            try
            {
                // 持久化事件
                await store.SaveTimelineEvent(entity, ct);
                ThrowIfCancelled(ct);
                createdIds.Add(entity.Id);
                createdTitles.Add(entity.Title);
            }
            catch (Exception ex)
            {
                failed.Add($"{evt.Title.Trim()}: {ex.Message}");
            }
        }

        // 构建操作结果摘要
        var summary =
            $"已创建 {createdIds.Count} 个时间线事件"
            + (createdTitles.Count > 0 ? $": {string.Join("、", createdTitles)}" : "")
            + (failed.Count > 0 ? $"\n失败 {failed.Count} 个: {string.Join("；", failed)}" : "");

        return new AgentToolResult<BatchCreateTimelineEventsDetails>
        {
            Content = [new TextContent(summary)],
            Details = new BatchCreateTimelineEventsDetails
            {
                CreatedCount = createdIds.Count,
                CreatedIds = createdIds.ToArray(),
                FailedCount = failed.Count,
            },
        };
    }
}

public sealed record BatchCreateTimelineEventsParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("要创建的时间线事件列表（每个事件至少提供 title）")]
    public List<TimelineEventDraft>? Events { get; init; }
}

/// <summary>
/// 时间线事件草稿 — 用于批量创建时的输入数据结构。
/// </summary>
public sealed record TimelineEventDraft
{
    [Description("事件标题（必填）")]
    public string? Title { get; init; } // 事件标题（必填）

    [Description("事件详细描述")]
    public string? Description { get; init; } // 事件描述

    [Description("事件发生的章节ID")]
    public string? ChapterId { get; init; } // 所属章节ID
}

/// <summary>
/// 批量创建时间线事件结果详情。
/// </summary>
public sealed record BatchCreateTimelineEventsDetails
{
    public int CreatedCount { get; init; } // 成功创建数量
    public string[] CreatedIds { get; init; } = []; // 成功创建的ID列表
    public int FailedCount { get; init; } // 失败数量
}
