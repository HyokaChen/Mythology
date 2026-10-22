using System.ComponentModel;
using Core.Data;
using Core.Shared;
using Core.Tools;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 小说项目管理工具：小说项目的增删改查（CRUD）操作
/// </summary>
public sealed class ManageNovelTool(INovelStore store)
    : AgentTool<ManageNovelParams, ManageNovelDetails>
{
    /// <summary>工具唯一标识符</summary>
    public override string Name => "manage_novel";
    public override string[] Aliases => ["小说", "项目", "创建小说", "打开小说"];
    public override string[] ExampleQueries =>
        ["创建一本新小说《雾都孤影》", "修改小说简介", "把总纲写进小说"];

    /// <summary>工具显示名称</summary>
    public override string Label => "小说项目管理";
    public override string Description =>
        "小说项目 CRUD：create（创建）、update（修改标题/类型/简介/风格/总纲）、get（加载）、delete（删除）、list（列出）";
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突
    public override ToolCategory Category => ToolCategory.ChapterManagement;

    /// <summary>
    /// 执行小说项目管理操作
    /// </summary>
    /// <param name="toolCallId">工具调用ID</param>
    /// <param name="args">管理参数</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="onUpdate">进度更新回调</param>
    /// <returns>操作结果</returns>
    public override async Task<AgentToolResult<ManageNovelDetails>> ExecuteAsync(
        string toolCallId,
        ManageNovelParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<ManageNovelDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        return args.Action.ToLowerInvariant() switch
        {
            "create" => await Create(args),
            "update" => await Update(args),
            "get" => await Get(args),
            "delete" => await Delete(args),
            "list" => await List(args),
            _ => Error($"未知操作: {args.Action}。支持: create/update/get/delete/list"),
        };
    }

    /// <summary>
    /// 创建新小说项目
    /// </summary>
    private async Task<AgentToolResult<ManageNovelDetails>> Create(ManageNovelParams args)
    {
        if (string.IsNullOrEmpty(args.Title))
            return Error("创建小说需要 title");

        var novel = new Novel
        {
            Title = args.Title,
            Genre = args.Genre ?? "东方玄幻",
            Description = args.Description ?? "",
            WritingStyle = args.WritingStyle ?? "",
        };
        await store.SaveNovel(novel);
        return Ok($"小说「{novel.Title}」创建成功 (ID: {novel.Id})", novel.Id, novel.Title);
    }

    /// <summary>
    /// 修改小说项目（部分更新）：只写传入的字段，未传字段保持原值。
    /// 总纲（outline）是「大纲一层」——全书走向/主题/核心冲突/结局方向，只有本操作用户/模型能写。
    /// </summary>
    private async Task<AgentToolResult<ManageNovelDetails>> Update(ManageNovelParams args)
    {
        if (string.IsNullOrEmpty(args.NovelId))
            return Error("修改小说需要 novel-id");

        var novel = await ResolveNovel(args);
        if (novel is null)
            return Error($"小说 {args.NovelId} 不存在（可用 list 查看 ID 或直接传标题）");

        var changed = new List<string>();
        if (!string.IsNullOrWhiteSpace(args.Title))
        {
            novel.Title = args.Title!;
            changed.Add("标题");
        }
        if (!string.IsNullOrWhiteSpace(args.Genre))
        {
            novel.Genre = args.Genre!;
            changed.Add("类型");
        }
        if (args.Description is not null)
        {
            novel.Description = args.Description;
            changed.Add("简介");
        }
        if (args.WritingStyle is not null)
        {
            novel.WritingStyle = args.WritingStyle;
            changed.Add("风格");
        }
        if (args.Outline is not null)
        {
            novel.Outline = args.Outline;
            changed.Add($"总纲({args.Outline.Length} 字)");
        }

        if (changed.Count == 0)
            return Error(
                "修改小说需要至少一个字段（title/genre/description/writing_style/outline）"
            );

        await store.SaveNovel(novel);
        return Ok(
            $"小说「{novel.Title}」已更新: {string.Join("、", changed)}",
            novel.Id,
            novel.Title
        );
    }

    /// <summary>
    /// 获取小说项目详情
    /// </summary>
    private async Task<AgentToolResult<ManageNovelDetails>> Get(ManageNovelParams args)
    {
        if (string.IsNullOrEmpty(args.NovelId))
            return Error("加载小说需要 novel-id");

        var novel = await ResolveNovel(args);
        if (novel is null)
            return Error($"小说 {args.NovelId} 不存在（可用 list 查看 ID 或直接传标题）");

        var outlineState = string.IsNullOrWhiteSpace(novel.Outline)
            ? "总纲未填写（可用 update 写入）"
            : $"总纲已填写 {novel.Outline.Length} 字";
        var text =
            $"已加载小说「{novel.Title}」(ID: {novel.Id}, 类型: {novel.Genre}, {outlineState})";
        return Ok(text, novel.Id, novel.Title);
    }

    /// <summary>
    /// 删除小说项目
    /// </summary>
    private async Task<AgentToolResult<ManageNovelDetails>> Delete(ManageNovelParams args)
    {
        if (string.IsNullOrEmpty(args.NovelId))
            return Error("删除小说需要 novel-id");

        var novel = await ResolveNovel(args);
        if (novel is null)
            return Error($"小说 {args.NovelId} 不存在（可用 list 查看 ID 或直接传标题）");

        var title = novel.Title;
        await store.DeleteNovel(novel.Id);
        return Ok($"小说「{title}」已删除", novel.Id);
    }

    /// <summary>
    /// 列出所有小说项目
    /// </summary>
    private async Task<AgentToolResult<ManageNovelDetails>> List(ManageNovelParams args)
    {
        var novels = await store.GetAllNovels();
        var lines = novels.Select(n => $"- {n.Title} (ID: {n.Id}, 类型: {n.Genre})");
        var text = novels.Count == 0 ? "暂无小说项目" : string.Join("\n", lines);
        return Ok(text, count: novels.Count);
    }

    /// <summary>
    /// 将小说 ID 或标题解析为真实小说。
    /// </summary>
    private async Task<Novel?> ResolveNovel(ManageNovelParams args)
    {
        if (string.IsNullOrEmpty(args.NovelId))
            return null;
        var novels = await store.GetAllNovels();
        return ManageHelper.ResolveByIdOrName(novels, args.NovelId, n => n.Id, n => n.Title);
    }

    /// <summary>
    /// 构建成功响应
    /// </summary>
    private static AgentToolResult<ManageNovelDetails> Ok(
        string text,
        string? id = null,
        string? name = null,
        int count = 0
    ) =>
        new()
        {
            Content = [new TextContent(text)],
            Details = new ManageNovelDetails
            {
                Id = id ?? "",
                Title = name ?? "",
                Count = count,
            },
        };

    /// <summary>
    /// 构建错误响应
    /// </summary>
    private static AgentToolResult<ManageNovelDetails> Error(string msg) =>
        new() { Content = [new TextContent(msg)], IsError = true };
}

public sealed record ManageNovelParams
{
    [Description("操作: create / update / get / delete / list")]
    public string Action { get; init; } = "list";

    [Description("小说项目ID（update/get/delete 时必填；也支持直接传小说标题）")]
    public string? NovelId { get; init; }

    [Description("小说标题（create 时必填）")]
    public string? Title { get; init; }

    [Description("小说类型/流派，如「科幻」「奇幻」「推理」")]
    public string? Genre { get; init; }

    [Description("小说简介/概要描述。写具体设定和核心冲突")]
    public string? Description { get; init; }

    [Description("写作风格描述，如「古龙风格」「村上春树风格」")]
    public string? WritingStyle { get; init; }

    /// <summary>
    /// 总纲（大纲一层）：全书走向/主题/核心冲突/结局方向。作者内部规划，可含剧透。
    /// 只有 update 会写入；卷纲（二层）与章节细纲（三层）分别走 manage_volume / write_chapter。
    /// </summary>
    [Description("总纲（大纲一层）：全书走向/主题/核心冲突/结局方向；update 时写入，可含剧透")]
    public string? Outline { get; init; }
}

public sealed record ManageNovelDetails : INovelDetails
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public int Count { get; init; }
}
