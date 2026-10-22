using System.ComponentModel;
using Core.Data;
using Core.Shared;
using Core.Tools;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 登记出场记录（entity-evolution-lifecycle §4.3 L2）——AI 写作时显式登记实体在某章的出场，
/// Source=Ai，与 L0 扫描记录并存（System 打底不覆盖 Ai/User 记录）。
/// </summary>
public sealed class RecordAppearanceTool(INovelStore store)
    : AgentTool<RecordAppearanceParams, RecordAppearanceDetails>
{
    /// <summary>工具唯一标识符</summary>
    public override string Name => "record_appearance";
    public override string[] Aliases => ["登记出场", "记录出场", "标记登场"];
    public override string[] ExampleQueries => ["林长歌在第三章首次登场"];

    /// <summary>工具显示名称</summary>
    public override string Label => "登记出场";
    public override string Description =>
        "登记实体（角色/地点/组织/道具）在某章的出场或被提及，带证据引文。用于人物跨章出现追踪";
    public override ToolExecutionMode ExecutionMode => ToolExecutionMode.Sequential; // 写入类：强制串行防并发写冲突
    public override ToolCategory Category => ToolCategory.EntityCrud;

    public override async Task<AgentToolResult<RecordAppearanceDetails>> ExecuteAsync(
        string toolCallId,
        RecordAppearanceParams args,
        CancellationToken ct,
        IProgress<AgentToolResult<RecordAppearanceDetails>>? onUpdate = null
    )
    {
        ThrowIfCancelled(ct);
        if (string.IsNullOrWhiteSpace(args.EntityId))
            return Error("必须提供实体 ID（entity_id）");
        if (string.IsNullOrWhiteSpace(args.ChapterId))
            return Error("必须提供章节 ID（chapter_id）");

        var chapter = await store.GetChapter(args.NovelId, args.ChapterId, ct);
        ThrowIfCancelled(ct);
        if (chapter is null)
            return Error($"章节 {args.ChapterId} 不存在");

        var record = new AppearanceRecord
        {
            Id = string.IsNullOrWhiteSpace(args.RecordId)
                ? $"ap_{chapter.Id}_{args.EntityId}_{Guid.NewGuid().ToString("N")[..6]}"
                : args.RecordId,
            NovelId = args.NovelId,
            EntityType = args.EntityType,
            EntityId = args.EntityId,
            ChapterId = chapter.Id,
            Mode = args.Mode,
            SceneIds = args.SceneIds is { Count: > 0 } ? args.SceneIds.ToList() : null,
            EvidenceSpan = args.Evidence,
            Source = AuditSource.Ai,
        };

        // 合并写入：同章 System 打底的该实体旧记录被本条替换（Ai 优先），其余 Ai/User 记录保留
        var existing = await store.GetAppearancesByChapter(args.NovelId, chapter.Id, ct);
        var merged = existing
            .Where(r => r.Source != AuditSource.System || r.EntityId != args.EntityId)
            .ToList();
        merged.Add(record);
        await store.SaveChapterAppearances(args.NovelId, chapter.Id, merged, ct);
        ThrowIfCancelled(ct);

        return new AgentToolResult<RecordAppearanceDetails>
        {
            Content =
            [
                new TextContent(
                    $"已登记出场：{args.EntityType} {args.EntityId} @ {chapter.Title}（{record.Mode}）"
                ),
            ],
            Details = new RecordAppearanceDetails
            {
                RecordId = record.Id,
                ChapterId = chapter.Id,
                EntityId = record.EntityId,
                Mode = record.Mode,
            },
        };
    }

    private static AgentToolResult<RecordAppearanceDetails> Error(string message) =>
        new() { Content = [new TextContent(message)], IsError = true };
}

public sealed record RecordAppearanceParams
{
    [Description("小说项目ID，留空则自动使用当前项目")]
    public string NovelId { get; init; } = "";

    [Description("实体 ID（角色/地点/组织/道具的 GUID）")]
    public string EntityId { get; init; } = "";

    [Description("实体类型：character/location/faction/item/plotline/scene")]
    public EntityType EntityType { get; init; } = EntityType.Character;

    [Description("章节 ID（GUID 或章节号）")]
    public string ChapterId { get; init; } = "";

    [Description("出场强度：present=在场有戏份 / mentioned=被提及")]
    public AppearanceMode Mode { get; init; } = AppearanceMode.Present;

    [Description("关联场景 ID 列表（可选，逗号分隔）")]
    public List<string>? SceneIds { get; init; }

    [Description("正文证据引文（可选，首次出现的原句片段）")]
    public string? Evidence { get; init; }

    [Description("记录 ID（可选，覆盖更新已有记录时提供）")]
    public string? RecordId { get; init; }
}

public sealed record RecordAppearanceDetails
{
    public string RecordId { get; init; } = "";
    public string ChapterId { get; init; } = "";
    public string EntityId { get; init; } = "";
    public AppearanceMode Mode { get; init; }
}
