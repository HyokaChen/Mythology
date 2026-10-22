using System.ComponentModel;
using Core.Data;
using Core.Shared;
using Core.Tools;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 批量创建场景（一次 tool call 创建多个场景）。
/// </summary>
public sealed class BatchCreateScenesTool(INovelStore store)
    : AgentTool<BatchCreateScenesParams, BatchCreateScenesDetails>
{
    public override string Name => "batch_create_scenes"; // 工具唯一标识
    public override string Label => "批量创建场景"; // 工具显示名称
    public override string Description =>
        "一次创建多个场景。每个场景只需提供核心字段（标题、冲突、参与角色等），系统会自动补全 ID 和序号。适合规划章节场景结构时批量建立"
        + BatchToolHints.PreferBatch;
    public override ToolCategory Category => ToolCategory.EntityCrud; // 分类：实体CRUD
    public override string EntityType => "scene"; // 实体类型：场景
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突

    public override async Task<AgentToolResult<BatchCreateScenesDetails>> ExecuteAsync(
        string toolCallId,
        BatchCreateScenesParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<BatchCreateScenesDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        // 参数校验：至少提供一个场景
        if (args.Scenes is null || args.Scenes.Count == 0)
        {
            return new AgentToolResult<BatchCreateScenesDetails>
            {
                Content = [new TextContent("请提供至少一个场景")],
                IsError = true,
            };
        }

        // 获取已有场景数，确定新场景的起始序号
        var existingScenes = await store.GetScenesByNovel(args.NovelId, ct);
        ThrowIfCancelled(ct);
        var nextOrder = existingScenes.Count > 0 ? existingScenes.Max(s => s.Order) + 1 : 1;

        // 初始化结果收集器
        var createdIds = new List<string>(args.Scenes.Count);
        var createdTitles = new List<string>(args.Scenes.Count);
        var failed = new List<string>();

        // 遍历每个场景草稿，逐个创建
        for (var i = 0; i < args.Scenes.Count; i++)
        {
            var s = args.Scenes[i];
            if (string.IsNullOrWhiteSpace(s.Title))
            {
                failed.Add($"(第{i + 1}个: 空标题)");
                continue;
            }

            // 解析章节 ID（AI 可能传入序号而非 GUID）
            var resolvedChapterId = await ResolveChapterId(args.NovelId, s.ChapterId);

            // 构建 SceneDefinition 实体，自动补全默认值
            var scene = new SceneDefinition
            {
                NovelId = args.NovelId,
                ChapterId = resolvedChapterId,
                Title = s.Title.Trim(),
                Order = s.Order ?? nextOrder + i,
                LocationId = s.LocationId,
                TimeDescription = s.TimeDescription ?? "",
                ParticipantIds = s.ParticipantIds ?? [],
                PlotlineIds = s.PlotlineIds ?? [],
                Conflict = s.Conflict ?? "",
                Goal = s.Goal ?? "",
                EmotionalArc = s.EmotionalArc ?? "",
                SceneType = ParseSceneType(s.SceneType),
                Notes = s.Notes ?? "",
            };

            try
            {
                // 持久化场景
                await store.SaveScene(scene, ct);
                ThrowIfCancelled(ct);

                createdIds.Add(scene.Id);
                createdTitles.Add(scene.Title);
            }
            catch (Exception ex)
            {
                failed.Add($"{s.Title.Trim()}: {ex.Message}");
            }
        }

        // 构建操作结果摘要
        var summary =
            $"已创建 {createdIds.Count} 个场景"
            + (createdTitles.Count > 0 ? $": {string.Join("、", createdTitles)}" : "")
            + (failed.Count > 0 ? $"\n失败 {failed.Count} 个: {string.Join("；", failed)}" : "");

        return new AgentToolResult<BatchCreateScenesDetails>
        {
            Content = [new TextContent(summary)],
            Details = new BatchCreateScenesDetails
            {
                CreatedCount = createdIds.Count,
                CreatedIds = createdIds.ToArray(),
                FailedCount = failed.Count,
            },
        };
    }

    /// <summary>
    /// 将章节序号或 ID 解析为真实的章节 ID。
    /// AI 可能传入序号（如 "2"）而非 ID，需要转换为实际 ID 以保证关联正确。
    /// </summary>
    private async Task<string?> ResolveChapterId(string novelId, string? chapterId)
    {
        if (string.IsNullOrEmpty(chapterId))
            return null;

        if (!int.TryParse(chapterId, out var order))
            return chapterId;

        var chapters = await store.GetChapters(novelId);
        var chapter = chapters.Find(c => c.Order == order);
        return chapter?.Id;
    }

    /// <summary>
    /// 解析场景类型字符串。
    /// </summary>
    private static SceneType ParseSceneType(string? type) =>
        type?.ToLowerInvariant() switch
        {
            "action" => SceneType.Action,
            "dialogue" => SceneType.Dialogue,
            "reflection" => SceneType.Reflection,
            "transition" => SceneType.Transition,
            _ => SceneType.Dialogue, // 默认对话场景
        };
}

public sealed record BatchCreateScenesParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("要创建的场景列表（每个场景至少提供 title）")]
    public List<SceneDraft>? Scenes { get; init; }
}

/// <summary>
/// 场景草稿 — 用于批量创建时的输入数据结构。
/// </summary>
public sealed record SceneDraft
{
    [Description("场景标题（必填），如「悬崖对峙」「酒馆密谋」")]
    public string? Title { get; init; }

    [Description("章节ID（GUID或序号，序号会自动解析为ID）")]
    public string? ChapterId { get; init; }

    [Description("场景序号（章节内排序，留空则自动递增）")]
    public int? Order { get; init; }

    [Description("地点ID，关联已有地点")]
    public string? LocationId { get; init; }

    [Description("时间描述，如「黄昏」「午夜」「三天后」")]
    public string? TimeDescription { get; init; }

    [Description("参与角色ID列表")]
    public List<string>? ParticipantIds { get; init; }

    [Description("推进的情节线ID列表。一个场景可同时推进多条情节线")]
    public List<string>? PlotlineIds { get; init; }

    [Description("场景冲突/核心张力")]
    public string? Conflict { get; init; }

    [Description("场景目标（此场景结束时达成什么）")]
    public string? Goal { get; init; }

    [Description("情绪弧线，如「紧张→释放→希望」")]
    public string? EmotionalArc { get; init; }

    [Description("场景类型：action / dialogue / reflection / transition")]
    public string? SceneType { get; init; }

    [Description("备注/写作提示")]
    public string? Notes { get; init; }
}

/// <summary>
/// 批量创建场景结果详情。
/// </summary>
public sealed record BatchCreateScenesDetails
{
    public int CreatedCount { get; init; } // 成功创建数量
    public string[] CreatedIds { get; init; } = []; // 成功创建的ID列表
    public int FailedCount { get; init; } // 失败数量
}
