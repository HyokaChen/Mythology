using System.ComponentModel;
using Core.Data;
using Core.Shared;
using Core.Tools;
using Core.Vector;

namespace Plugins.NovelTools.Tools;

// ═══════════════════════════════════════════════════════════════════════════════
// 统一 CRUD 管理工具 — 用 action 参数 (create/update/delete/get/list) 替代
// 每个实体拆 5 个独立工具类的旧模式。领域行为工具保留独立。
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 管理工具辅助类：提供字段对比和文本处理功能
/// </summary>
internal static class ManageHelper
{
    /// <summary>
    /// 截取字符串并添加省略号（用于 diff 显示）
    /// </summary>
    internal static string Diff(string? s, int max = 30) =>
        string.IsNullOrEmpty(s) ? "(空)" : s.Truncate(max);

    /// <summary>
    /// 截断字符串（使用统一的 StringHelper.Truncate）
    /// </summary>
    internal static string Truncate(string s, int max) => s.Truncate(max);

    /// <summary>
    /// 对比多个字段的变化
    /// </summary>
    /// <param name="fields">字段对比元组数组</param>
    /// <returns>变更描述列表</returns>
    internal static List<string> DiffFields(
        params (string Label, string? Old, string? New)[] fields
    )
    {
        var changes = new List<string>();
        foreach (var (label, old, @new) in fields)
        {
            if (@new is not null && @new != old)
                changes.Add($"{label}: {Diff(old)} → {Diff(@new)}");
        }
        return changes;
    }

    /// <summary>
    /// 将用户输入解析为章节序号。支持两种格式：
    /// <list type="bullet">
    ///   <item>12位 hex 章节ID（如 "a1b2c3d4e5f6"）→ 按 ID 查找对应序号</item>
    ///   <item>整数字符串（如 "3"）→ 直接作为序号</item>
    /// </list>
    /// 输入为 null/空时返回 <paramref name="defaultVal"/>。
    /// </summary>
    internal static int ResolveChapterOrder(List<Chapter> chapters, string? input, int defaultVal)
    {
        if (string.IsNullOrEmpty(input))
            return defaultVal;
        // 优先按 ID 匹配
        foreach (var ch in chapters.Where(ch => ch.Id == input))
        {
            return ch.Order;
        }

        // 回退按序号解析
        return int.TryParse(input, out var parsedOrder) ? parsedOrder : defaultVal;
    }

    /// <summary>
    /// 按 ID 或名称解析实体：优先精确 ID，其次在给定集合内按名称匹配。
    /// 与场景/章节的序号解析一样，容忍 LLM 传名称而非 ID。
    /// </summary>
    internal static T? ResolveByIdOrName<T>(
        IReadOnlyList<T> entities,
        string? idOrName,
        Func<T, string> getId,
        Func<T, string> getName
    )
        where T : class
    {
        if (string.IsNullOrEmpty(idOrName))
            return null;
        return entities.FirstOrDefault(e => getId(e) == idOrName)
            ?? entities.FirstOrDefault(e => getName(e) == idOrName);
    }
}

// ── 角色管理工具 ─────────────────────────────────────────────────────────

/// <summary>
/// 角色管理工具：角色的增删改查（CRUD）操作
/// </summary>
public sealed class ManageCharacterTool(INovelStore store, RetrievalKernel? kernel = null)
    : AgentTool<ManageCharacterParams, ManageCharacterDetails>
{
    /// <summary>工具唯一标识符</summary>
    public override string Name => "manage_character";
    public override string[] Aliases => ["角色", "人物", "创建角色", "修改角色", "角色设定"];
    public override string[] ExampleQueries => ["给主角林晚补充背景设定", "新建一个配角"];

    /// <summary>工具显示名称</summary>
    public override string Label => "角色管理";
    public override string Description =>
        "角色 CRUD：create（创建角色）、update（更新角色设定）、delete（删除角色）、get（获取角色详情）、list（列出所有角色）"
        + BatchToolHints.PreferBatchForMultiple;
    public override ToolCategory Category => ToolCategory.EntityCrud;
    public override string EntityType => "character";
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突

    /// <summary>
    /// 执行角色管理操作
    /// </summary>
    /// <param name="toolCallId">工具调用ID</param>
    /// <param name="args">管理参数</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="onUpdate">进度更新回调</param>
    /// <returns>操作结果</returns>
    public override async Task<AgentToolResult<ManageCharacterDetails>> ExecuteAsync(
        string toolCallId,
        ManageCharacterParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<ManageCharacterDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        return args.Action.ToLowerInvariant() switch
        {
            "create" => await Create(args, ct),
            "update" => await Update(args, ct),
            "delete" => await Delete(args),
            "get" => await Get(args),
            "list" => await List(args),
            _ => Error($"未知操作: {args.Action}。支持: create/update/delete/get/list"),
        };
    }

    /// <summary>
    /// 创建新角色
    /// </summary>
    private async Task<AgentToolResult<ManageCharacterDetails>> Create(
        ManageCharacterParams args,
        CancellationToken ct
    )
    {
        if (string.IsNullOrEmpty(args.Name))
            return Error("创建角色需要 name");

        var character = new Character
        {
            NovelId = args.NovelId,
            Name = args.Name,
            Gender = args.Gender ?? "",
            Age = args.Age ?? "",
            Role = Enum.TryParse<CharacterRole>(args.Role, ignoreCase: true, out var cr)
                ? cr
                : CharacterRole.Supporting,
            Personality = args.Personality ?? "",
            Appearance = args.Appearance ?? "",
            Background = args.Background ?? "",
            Motivation = args.Motivation ?? "",
            Habits = args.Habits ?? "",
            Weakness = args.Weakness ?? "",
            FactionId = args.FactionId,
        };
        await store.SaveCharacter(character, ct);
        ThrowIfCancelled(ct);
        if (kernel is not null)
        {
            var (doc, chunks) = kernel.Builder.BuildCharacter(character);
            await kernel.IndexDocumentAsync(doc, chunks, ct);
        }
        ThrowIfCancelled(ct);
        return Ok(
            $"角色「{character.Name}」创建成功 (ID: {character.Id})",
            character.Id,
            character.Name
        );
    }

    /// <summary>
    /// 更新角色信息
    /// </summary>
    private async Task<AgentToolResult<ManageCharacterDetails>> Update(
        ManageCharacterParams args,
        CancellationToken ct
    )
    {
        if (string.IsNullOrEmpty(args.CharacterId))
            return Error("更新角色需要 character-id");

        var character = await ResolveCharacter(args);
        if (character is null)
            return Error($"角色 {args.CharacterId} 不存在（可用 list 查看 ID 或直接传名称）");

        var changes = ManageHelper.DiffFields(
            ("姓名", character.Name, args.Name),
            ("性别", character.Gender, args.Gender),
            ("年龄", character.Age, args.Age),
            ("定位", character.Role.ToString(), args.Role),
            ("性格", character.Personality, args.Personality),
            ("外貌", character.Appearance, args.Appearance),
            ("背景", character.Background, args.Background),
            ("动机", character.Motivation, args.Motivation),
            ("习惯", character.Habits, args.Habits),
            ("弱点", character.Weakness, args.Weakness),
            ("组织", character.FactionId, args.FactionId)
        );

        if (args.Name is not null)
            character.Name = args.Name;
        if (args.Gender is not null)
            character.Gender = args.Gender;
        if (args.Age is not null)
            character.Age = args.Age;
        if (
            args.Role is not null
            && Enum.TryParse<CharacterRole>(args.Role, ignoreCase: true, out var parsedRole)
        )
            character.Role = parsedRole;
        if (args.Personality is not null)
            character.Personality = args.Personality;
        if (args.Appearance is not null)
            character.Appearance = args.Appearance;
        if (args.Background is not null)
            character.Background = args.Background;
        if (args.Motivation is not null)
            character.Motivation = args.Motivation;
        if (args.Habits is not null)
            character.Habits = args.Habits;
        if (args.Weakness is not null)
            character.Weakness = args.Weakness;
        if (args.FactionId is not null)
            character.FactionId = args.FactionId;

        await store.SaveCharacter(character, ct);
        ThrowIfCancelled(ct);
        if (kernel is not null)
        {
            var (doc, chunks) = kernel.Builder.BuildCharacter(character);
            await kernel.IndexDocumentAsync(doc, chunks, ct);
        }
        ThrowIfCancelled(ct);

        var summary =
            changes.Count > 0
                ? $"角色「{character.Name}」已更新:\n{string.Join("\n", changes)}"
                : $"角色「{character.Name}」无变更";
        return Ok(summary, character.Id, character.Name);
    }

    /// <summary>
    /// 删除角色
    /// </summary>
    private async Task<AgentToolResult<ManageCharacterDetails>> Delete(ManageCharacterParams args)
    {
        if (string.IsNullOrEmpty(args.CharacterId))
            return Error("删除角色需要 character-id");

        var character = await ResolveCharacter(args);
        if (character is null)
            return Error($"角色 {args.CharacterId} 不存在（可用 list 查看 ID 或直接传名称）");

        var name = character.Name;
        await store.DeleteCharacter(character.Id);
        return Ok($"角色「{name}」已删除", character.Id);
    }

    /// <summary>
    /// 获取角色详情
    /// </summary>
    private async Task<AgentToolResult<ManageCharacterDetails>> Get(ManageCharacterParams args)
    {
        if (string.IsNullOrEmpty(args.CharacterId))
            return Error("获取角色需要 character-id");

        var character = await ResolveCharacter(args);
        if (character is null)
            return Error($"角色 {args.CharacterId} 不存在（可用 list 查看 ID 或直接传名称）");

        var roleLabel = character.Role switch
        {
            CharacterRole.Protagonist => "主角",
            CharacterRole.Supporting => "配角",
            CharacterRole.Minor => "龙套",
            CharacterRole.Antagonist => "反派",
            _ => character.Role.ToString(),
        };
        var text =
            $"角色: {character.Name}\n性别: {(string.IsNullOrEmpty(character.Gender) ? "未设定" : character.Gender)}\n年龄: {(string.IsNullOrEmpty(character.Age) ? "未设定" : character.Age)}\n定位: {roleLabel}\n性格: {character.Personality}\n外貌: {character.Appearance}\n背景: {character.Background}\n动机: {character.Motivation}";
        return Ok(text, character.Id, character.Name);
    }

    /// <summary>
    /// 列出所有角色
    /// </summary>
    private async Task<AgentToolResult<ManageCharacterDetails>> List(ManageCharacterParams args)
    {
        var characters = await store.GetCharacters(args.NovelId);
        var lines = characters.Select(c =>
        {
            var roleLabel = c.Role switch
            {
                CharacterRole.Protagonist => "主角",
                CharacterRole.Antagonist => "反派",
                CharacterRole.Minor => "龙套",
                _ => "配角",
            };
            var gender = string.IsNullOrEmpty(c.Gender) ? "" : $", {c.Gender}";
            return $"- {c.Name} (ID: {c.Id}, 定位: {roleLabel}{gender}, 性格: {ManageHelper.Truncate(c.Personality, 30)})";
        });
        var text = characters.Count == 0 ? "暂无角色" : string.Join("\n", lines);
        return Ok(text, count: characters.Count);
    }

    /// <summary>
    /// 将角色 ID 或名称解析为真实角色（名称需配合 novel_id 缩小范围）。
    /// </summary>
    private async Task<Character?> ResolveCharacter(ManageCharacterParams args)
    {
        if (string.IsNullOrEmpty(args.CharacterId))
            return null;
        var characters = await store.GetCharacters(args.NovelId);
        return ManageHelper.ResolveByIdOrName(characters, args.CharacterId, c => c.Id, c => c.Name);
    }

    /// <summary>
    /// 构建成功响应
    /// </summary>
    private static AgentToolResult<ManageCharacterDetails> Ok(
        string text,
        string? id = null,
        string? name = null,
        int count = 0
    ) =>
        new()
        {
            Content = [new TextContent(text)],
            Details = new ManageCharacterDetails
            {
                Id = id ?? "",
                Name = name ?? "",
                Count = count,
            },
        };

    /// <summary>
    /// 构建错误响应
    /// </summary>
    private static AgentToolResult<ManageCharacterDetails> Error(string msg) =>
        new() { Content = [new TextContent(msg)], IsError = true };
}

public sealed record ManageCharacterParams
{
    [Description("操作: create / update / delete / get / list")]
    public string Action { get; init; } = "list";

    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("角色ID（update/delete/get 时必填；也支持直接传角色名称）")]
    public string? CharacterId { get; init; }

    [Description("角色姓名（create 时必填，update 时可选）")]
    public string? Name { get; init; }

    [Description("性别：男/女/其他。影响代词和关系逻辑")]
    public string? Gender { get; init; }

    [Description("年龄或年龄段，如「25」「少年」「中年」。影响行为合理性")]
    public string? Age { get; init; }

    [Description(
        "角色定位：protagonist（主角）/ supporting（配角）/ minor（龙套）/ antagonist（反派）"
    )]
    public string? Role { get; init; }

    [Description("性格描述，如「内向敏感、外冷内热」。不用 AI 词")]
    public string? Personality { get; init; }

    [Description("外貌描述，如「黑发碧眼、身材高挑」。写具体特征")]
    public string? Appearance { get; init; }

    [Description("角色背景故事。直接叙述，不用 AI 句式")]
    public string? Background { get; init; }

    [Description("角色核心动机/目标。写具体欲望")]
    public string? Motivation { get; init; }

    [Description("习惯/行为特点，如「晨练、喜好喝茶」")]
    public string? Habits { get; init; }

    [Description("弱点/缺陷，如「固执、过于自信」")]
    public string? Weakness { get; init; }

    [Description("所属组织/门派ID")]
    public string? FactionId { get; init; }
}

public sealed record ManageCharacterDetails
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public int Count { get; init; }
}
