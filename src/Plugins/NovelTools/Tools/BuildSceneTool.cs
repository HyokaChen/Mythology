using System.ComponentModel;
using Core.Data;
using Core.Shared;
using Core.Tools;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 场景构建助手 — 传入构思描述，自动推断冲突/情绪弧/场景类型，创建场景草稿。
/// LLM 在后续 turn 中基于草稿自动补充/修正场景细节。
/// </summary>
public sealed class BuildSceneTool(INovelStore store)
    : AgentTool<BuildSceneParams, BuildSceneDetails>
{
    public override string Name => "build_scene";
    public override string[] Aliases => ["场景构建", "场景助手"];
    public override string[] ExampleQueries => ["帮我构建一场对峙戏"]; // 工具唯一标识
    public override string Label => "场景构建助手"; // 工具显示名称
    public override string Description =>
        "基于构思描述快速构建场景草稿，自动推断冲突、情绪弧线、场景类型并创建";
    public override ToolCategory Category => ToolCategory.EntityCrud; // 分类：实体CRUD
    public override string EntityType => "scene"; // 实体类型：场景
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突

    public override async Task<AgentToolResult<BuildSceneDetails>> ExecuteAsync(
        string toolCallId,
        BuildSceneParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<BuildSceneDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        // 参数校验：构思描述为必填项
        if (string.IsNullOrEmpty(args.Concept))
            return new AgentToolResult<BuildSceneDetails>
            {
                Content = [new TextContent("请提供场景构思描述")],
                IsError = true,
            };

        // 获取已有场景数，确定新场景的序号
        var existingScenes = await store.GetScenesByNovel(args.NovelId, ct);
        ThrowIfCancelled(ct);
        var nextOrder = existingScenes.Count > 0 ? existingScenes.Max(s => s.Order) + 1 : 1;

        // 从构思描述中自动推断场景属性
        var title = InferTitle(args.Concept); // 推断标题
        var conflict = InferConflict(args.Concept); // 推断冲突
        var sceneType = InferSceneType(args.Concept); // 推断场景类型
        var emotionalArc = InferEmotionalArc(sceneType); // 推断情绪弧线

        // 组装参与角色列表
        var participantIds = args.ParticipantIds ?? [];

        // 构建场景定义实体
        var scene = new SceneDefinition
        {
            NovelId = args.NovelId,
            ChapterId = args.ChapterId,
            Title = title,
            Order = nextOrder,
            LocationId = args.LocationId,
            TimeDescription = "",
            ParticipantIds = participantIds,
            Conflict = conflict,
            Goal = "（待 LLM 补充场景目标）",
            EmotionalArc = emotionalArc,
            SceneType = sceneType,
            Notes = $"构思: {args.Concept}",
        };

        // 持久化场景草稿
        await store.SaveScene(scene, ct);

        // 查询参与角色的名称用于展示
        var participantNames = new List<string>();
        foreach (var pid in participantIds)
        {
            var ch = await store.GetCharacter(pid, ct);
            participantNames.Add(ch?.Name ?? pid);
        }

        // 构建场景创建结果摘要
        var summary = $"""
场景草稿已创建 (ID: {scene.Id})：

🎬 **{scene.Title}** (第{scene.Order}场)
📍 地点: {args.LocationId ?? "(待指定)"}
🧑 角色: {string.Join(", ", participantNames)}
🔥 冲突: {conflict}
😤 情绪弧: {emotionalArc}
🎭 类型: {sceneType}

请 LLM 在后续对话中补充时间描述、完善场景目标和冲突细节。
""";

        return new AgentToolResult<BuildSceneDetails>
        {
            Content = [new TextContent(summary)],
            Details = new BuildSceneDetails
            {
                SceneId = scene.Id,
                Title = scene.Title,
                ChapterId = scene.ChapterId,
            },
        };
    }

    /// <summary>
    /// 从构思描述中推断场景标题 — 取前12个字作为暂定标题。
    /// </summary>
    private static string InferTitle(string concept)
    {
        var trimmed = concept.Trim();
        return trimmed.Length <= 12 ? trimmed : trimmed[..12] + "…";
    }

    /// <summary>
    /// 从构思描述中推断冲突类型 — 基于关键词匹配。
    /// </summary>
    private static string InferConflict(string concept)
    {
        // 冲突关键词到默认冲突描述的映射
        var conflictKeywords = new Dictionary<string, string>
        {
            ["对峙"] = "双方立场对立，互不相让",
            ["争吵"] = "言语冲突，情绪升级",
            ["战斗"] = "武力冲突，生死相搏",
            ["追击"] = "追逐与逃避的紧张对峙",
            ["谈判"] = "利益博弈，各怀心思",
            ["告白"] = "情感坦露，关系面临转折",
            ["背叛"] = "信任破裂，关系逆转",
            ["密谋"] = "暗中策划，利益交换",
            ["重逢"] = "久别重逢，物是人非",
            ["诀别"] = "分离之痛，不舍与决绝",
        };

        // 按顺序匹配关键词，返回第一个匹配的冲突描述
        foreach (var (keyword, defaultConflict) in conflictKeywords)
        {
            if (concept.Contains(keyword))
                return defaultConflict;
        }

        return "（待 LLM 补充冲突描述）";
    }

    /// <summary>
    /// 从构思描述中推断场景类型 — 基于关键词匹配。
    /// </summary>
    private static SceneType InferSceneType(string concept)
    {
        if (concept.Contains("战斗") || concept.Contains("对峙") || concept.Contains("追击"))
            return SceneType.Action; // 动作场景
        if (concept.Contains("思考") || concept.Contains("回忆") || concept.Contains("独白"))
            return SceneType.Reflection; // 反思场景
        if (concept.Contains("过渡") || concept.Contains("前往") || concept.Contains("路上"))
            return SceneType.Transition; // 过渡场景
        return SceneType.Dialogue; // 默认：对话场景
    }

    /// <summary>
    /// 根据场景类型推断情绪弧线。
    /// </summary>
    private static string InferEmotionalArc(SceneType sceneType) =>
        sceneType switch
        {
            SceneType.Action => "紧张→高潮→转折",
            SceneType.Dialogue => "平和→张力升级→释放",
            SceneType.Reflection => "平静→回忆波动→沉淀",
            SceneType.Transition => "结束→过渡→新开始",
            _ => "（待 LLM 补充情绪弧线）",
        };
}

public sealed record BuildSceneParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("章节ID（可选，后续可通过 update 关联场景到章节）")]
    public string? ChapterId { get; init; }

    [Description("场景构思描述，如「两人在悬崖边对峙，月如质问逍遥为何隐瞒」")]
    public string Concept { get; init; } = "";

    [Description("参与角色ID列表（至少1个）")]
    public List<string>? ParticipantIds { get; init; }

    [Description("可选地点ID")]
    public string? LocationId { get; init; }
}

/// <summary>
/// 场景构建结果详情。
/// </summary>
public sealed record BuildSceneDetails
{
    public string SceneId { get; init; } = ""; // 场景ID
    public string Title { get; init; } = ""; // 场景标题
    public string? ChapterId { get; init; } = ""; // 所属章节ID
}
