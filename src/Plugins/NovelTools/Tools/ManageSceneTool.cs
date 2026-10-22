using System.ComponentModel;
using Core.Data;
using Core.Shared;
using Core.Tools;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 场景管理工具：场景的增删改查（CRUD）操作
/// </summary>
public sealed class ManageSceneTool(INovelStore store)
    : AgentTool<ManageSceneParams, ManageSceneDetails>
{
    /// <summary>工具唯一标识符</summary>
    public override string Name => "manage_scene";
    public override string[] Aliases => ["场景", "场景管理", "新建场景"];
    public override string[] ExampleQueries => ["为第五章新建一个雨夜场景"];

    /// <summary>工具显示名称</summary>
    public override string Label => "场景管理";
    public override string Description =>
        "场景 CRUD：create（创建场景）/ update（更新场景）/ delete（删除场景）/ get（获取场景详情）/ list（列出章节所有场景）"
        + BatchToolHints.PreferBatchForMultiple;
    public override ToolCategory Category => ToolCategory.EntityCrud;
    public override string? EntityType => "scene";
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突

    /// <summary>
    /// 执行场景管理操作
    /// </summary>
    /// <param name="toolCallId">工具调用ID</param>
    /// <param name="args">管理参数</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="onUpdate">进度更新回调</param>
    /// <returns>操作结果</returns>
    public override async Task<AgentToolResult<ManageSceneDetails>> ExecuteAsync(
        string toolCallId,
        ManageSceneParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<ManageSceneDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        return args.Action.ToLowerInvariant() switch
        {
            "create" => await Create(args),
            "update" => await Update(args),
            "delete" => await Delete(args),
            "get" => await Get(args),
            "list" => await List(args),
            _ => Error($"未知操作: {args.Action}。支持: create/update/delete/get/list"),
        };
    }

    /// <summary>
    /// 将章节序号或 ID 解析为真实的章节 ID。
    /// AI 可能传入序号（如 "2"）而非 ID，需要转换为实际 ID 以保证关联正确。
    /// 返回 null 表示未指定章节。
    /// </summary>
    private async Task<string?> ResolveChapterId(string novelId, string? chapterId)
    {
        if (string.IsNullOrEmpty(chapterId))
            return null;

        // 如果不是纯数字，假设已经是 ID 格式，直接返回
        if (!int.TryParse(chapterId, out var order))
            return chapterId;

        // 按序号查找章节的真实 ID
        var chapters = await store.GetChapters(novelId);
        var chapter = chapters.Find(c => c.Order == order);
        return chapter?.Id; // 找不到则返回 null，而非原始序号字符串
    }

    /// <summary>
    /// 将场景 ID 或章节内序号解析为真实场景。
    /// AI 可能传序号（如 "12"，来自 list 输出的 Order）而非真实 ID，
    /// 与章节 ID 的序号解析（ResolveChapterId）保持一致。
    /// </summary>
    private async Task<SceneDefinition?> ResolveScene(ManageSceneParams args)
    {
        if (string.IsNullOrEmpty(args.SceneId))
            return null;

        // 非纯数字 → 假设已是真实 ID
        if (!int.TryParse(args.SceneId, out var order))
            return await store.GetScene(args.SceneId);

        // 纯数字 → 按序号解析（可同时提供 chapter_id 缩小范围）
        var resolvedChapterId = await ResolveChapterId(args.NovelId, args.ChapterId);
        if (resolvedChapterId is not null)
        {
            var byChapter = await store.GetScenesByChapter(resolvedChapterId);
            return byChapter.Find(s => s.Order == order);
        }
        if (!string.IsNullOrEmpty(args.NovelId))
        {
            var byNovel = await store.GetScenesByNovel(args.NovelId);
            return byNovel.Find(s => s.Order == order);
        }
        return null;
    }

    /// <summary>
    /// 创建新场景
    /// </summary>
    private async Task<AgentToolResult<ManageSceneDetails>> Create(ManageSceneParams args)
    {
        if (string.IsNullOrEmpty(args.Title))
            return Error("创建场景需要 title");

        var resolvedChapterId = await ResolveChapterId(args.NovelId, args.ChapterId);

        var scene = new SceneDefinition
        {
            NovelId = args.NovelId,
            ChapterId = resolvedChapterId,
            Title = args.Title,
            Order = args.Order ?? 1,
            LocationId = args.LocationId,
            TimeDescription = args.TimeDescription ?? "",
            ParticipantIds = args.ParticipantIds ?? [],
            PlotlineIds = args.PlotlineIds ?? [],
            Conflict = args.Conflict ?? "",
            Goal = args.Goal ?? "",
            EmotionalArc = args.EmotionalArc ?? "",
            SceneType = ParseSceneType(args.SceneType),
            Notes = args.Notes ?? "",
        };
        await store.SaveScene(scene);
        return Ok($"场景「{scene.Title}」创建成功 (ID: {scene.Id})", scene.Id, scene.Title);
    }

    /// <summary>
    /// 更新场景信息
    /// </summary>
    private async Task<AgentToolResult<ManageSceneDetails>> Update(ManageSceneParams args)
    {
        if (string.IsNullOrEmpty(args.SceneId))
            return Error("更新场景需要 scene-id");

        var scene = await ResolveScene(args);
        if (scene is null)
            return Error($"场景 {args.SceneId} 不存在（可用 list 查看序号或 ID）");

        if (args.Title is not null)
            scene.Title = args.Title;
        if (args.Order is not null)
            scene.Order = args.Order.Value;
        if (args.ChapterId is not null)
            scene.ChapterId =
                await ResolveChapterId(args.NovelId, args.ChapterId) ?? scene.ChapterId; // 序号解析失败时保留原章节，避免误清空
        if (args.LocationId is not null)
            scene.LocationId = args.LocationId;
        if (args.TimeDescription is not null)
            scene.TimeDescription = args.TimeDescription;
        if (args.ParticipantIds is not null)
            scene.ParticipantIds = args.ParticipantIds;
        if (args.PlotlineIds is not null)
            scene.PlotlineIds = args.PlotlineIds;
        if (args.Conflict is not null)
            scene.Conflict = args.Conflict;
        if (args.Goal is not null)
            scene.Goal = args.Goal;
        if (args.EmotionalArc is not null)
            scene.EmotionalArc = args.EmotionalArc;
        if (args.SceneType is not null)
            scene.SceneType = ParseSceneType(args.SceneType);
        if (args.Notes is not null)
            scene.Notes = args.Notes;

        await store.SaveScene(scene);
        return Ok($"场景「{scene.Title}」已更新", scene.Id, scene.Title);
    }

    /// <summary>
    /// 删除场景
    /// </summary>
    private async Task<AgentToolResult<ManageSceneDetails>> Delete(ManageSceneParams args)
    {
        if (string.IsNullOrEmpty(args.SceneId))
            return Error("删除场景需要 scene-id");

        var scene = await ResolveScene(args);
        if (scene is null)
            return Error($"场景 {args.SceneId} 不存在（可用 list 查看序号或 ID）");

        var title = scene.Title;
        await store.DeleteScene(scene.Id);
        return Ok($"场景「{title}」已删除", scene.Id);
    }

    /// <summary>
    /// 获取场景详情
    /// </summary>
    private async Task<AgentToolResult<ManageSceneDetails>> Get(ManageSceneParams args)
    {
        if (string.IsNullOrEmpty(args.SceneId))
            return Error("获取场景需要 scene-id");

        var scene = await ResolveScene(args);
        if (scene is null)
            return Error($"场景 {args.SceneId} 不存在（可用 list 查看序号或 ID）");

        var participantIds = scene.ParticipantIds;
        var participants = new List<string>();
        foreach (var pid in participantIds)
        {
            var ch = await store.GetCharacter(pid);
            participants.Add(ch?.Name ?? pid);
        }

        var text = $"""
场景: {scene.Title}
章节: {scene.ChapterId ?? "(未分配)"}
序号: {scene.Order}
时间: {scene.TimeDescription}
地点ID: {scene.LocationId ?? "(未指定)"}
参与角色: {string.Join(", ", participants)}
推进情节线: [{string.Join(", ", scene.PlotlineIds)}]
冲突: {scene.Conflict}
目标: {scene.Goal}
情绪弧: {scene.EmotionalArc}
类型: {scene.SceneType}
备注: {scene.Notes}
""";
        return Ok(text, scene.Id, scene.Title);
    }

    /// <summary>
    /// 列出章节下的所有场景
    /// </summary>
    private async Task<AgentToolResult<ManageSceneDetails>> List(ManageSceneParams args)
    {
        List<SceneDefinition> scenes;
        if (!string.IsNullOrEmpty(args.ChapterId))
        {
            scenes = await store.GetScenesByChapter(args.ChapterId);
        }
        else if (!string.IsNullOrEmpty(args.NovelId))
        {
            scenes = await store.GetScenesByNovel(args.NovelId);
        }
        else
        {
            return Error("列出场景需要 chapter_id 或 novel-id");
        }

        var lines = scenes.Select(s =>
        {
            var emoji = s.SceneType switch
            {
                SceneType.Action => "⚔",
                SceneType.Dialogue => "💬",
                SceneType.Reflection => "💭",
                SceneType.Transition => "➡",
                _ => "🎬",
            };
            return $"{emoji} {s.Order}. {s.Title} (ID: {s.Id}) — {s.Conflict}";
        });
        var text =
            scenes.Count == 0
                ? string.IsNullOrEmpty(args.ChapterId)
                    ? "暂无场景"
                    : "本章暂无场景"
                : string.Join("\n", lines);
        return Ok(text, count: scenes.Count);
    }

    /// <summary>
    /// 解析场景类型字符串
    /// </summary>
    /// <param name="type">场景类型字符串</param>
    /// <returns>场景类型枚举</returns>
    private static SceneType ParseSceneType(string? type) =>
        type?.ToLowerInvariant() switch
        {
            "action" => SceneType.Action,
            "dialogue" => SceneType.Dialogue,
            "reflection" => SceneType.Reflection,
            "transition" => SceneType.Transition,
            _ => SceneType.Dialogue,
        };

    /// <summary>
    /// 构建成功响应
    /// </summary>
    private static AgentToolResult<ManageSceneDetails> Ok(
        string text,
        string? id = null,
        string? title = null,
        int count = 0
    ) =>
        new()
        {
            Content = [new TextContent(text)],
            Details = new ManageSceneDetails
            {
                Id = id ?? "",
                Title = title ?? "",
                Count = count,
            },
        };

    /// <summary>
    /// 构建错误响应
    /// </summary>
    private static AgentToolResult<ManageSceneDetails> Error(string msg) =>
        new() { Content = [new TextContent(msg)], IsError = true };
}

public sealed record ManageSceneParams
{
    [Description("操作: create / update / delete / get / list")]
    public string Action { get; init; } = "list";

    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("场景ID（update/delete/get 时必填；也支持章节内序号，如 12，自动解析为真实 ID）")]
    public string? SceneId { get; init; }

    [Description("章节ID（GUID格式，如 7b2be66d2f56；也支持章节序号如 1、2，会自动解析为ID）")]
    public string? ChapterId { get; init; }

    [Description("场景标题，如「悬崖对峙」「酒馆密谋」")]
    public string? Title { get; init; }

    [Description("场景序号（章节内排序，从1开始）")]
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

public sealed record ManageSceneDetails
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public int Count { get; init; }
}
