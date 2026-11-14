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

        var chapterLookup = await NovelReferenceResolver.ChapterLookupAsync(
            store,
            args.NovelId,
            args.ChapterId,
            ct
        );
        var chapter = chapterLookup.Value;
        ThrowIfCancelled(ct);
        if (chapter is null)
            return Error(chapterLookup.Error ?? $"章节 {args.ChapterId} 不存在");

        var entityLookup = await NovelReferenceResolver.EntityIdAsync(
            store,
            args.NovelId,
            args.EntityType,
            args.EntityId,
            ct
        );
        if (entityLookup.Value is not { } entityId)
            return Error(entityLookup.Error ?? $"{args.EntityType} {args.EntityId} 不存在");

        List<string>? sceneIds = null;
        if (args.SceneIds is { Count: > 0 })
        {
            var scenes = await store.GetScenesByChapter(chapter.Id, ct);
            sceneIds = new List<string>(args.SceneIds.Count);
            foreach (var sceneRef in args.SceneIds)
            {
                if (string.IsNullOrWhiteSpace(sceneRef))
                    return Error("场景 ID 列表不能包含空值");
                var scene = NovelReferenceResolver.FindByIdOrName(
                    scenes,
                    sceneRef,
                    s => s.Id,
                    s => s.Title
                );
                if (
                    scene is null
                    && NovelReferenceResolver.TryParseOrder(sceneRef, out var sceneOrder)
                )
                {
                    var byOrder = scenes.Where(s => s.Order == sceneOrder).ToList();
                    if (byOrder.Count > 1)
                        return Error(
                            $"第 {sceneOrder} 场在本章有 {byOrder.Count} 个，请改用场景 ID"
                        );
                    scene = byOrder.FirstOrDefault();
                }
                if (scene is null)
                    return Error($"场景 {sceneRef} 不存在于本章");
                if (sceneIds.Contains(scene.Id, StringComparer.Ordinal))
                    return Error($"场景 {sceneRef} 在列表中重复");
                sceneIds.Add(scene.Id);
            }
        }

        var record = new AppearanceRecord
        {
            Id = string.IsNullOrWhiteSpace(args.RecordId)
                ? $"ap_{chapter.Id}_{entityId}_{Guid.NewGuid().ToString("N")[..6]}"
                : args.RecordId.Trim(),
            NovelId = args.NovelId,
            EntityType = args.EntityType,
            EntityId = entityId,
            ChapterId = chapter.Id,
            Mode = args.Mode,
            SceneIds = sceneIds,
            EvidenceSpan = args.Evidence,
            Source = AuditSource.Ai,
        };

        // 合并写入：同章同 ID 的旧记录被本条替换；System 打底的该实体旧记录也由 Ai 覆盖
        var existing = await store.GetAppearancesByChapter(args.NovelId, chapter.Id, ct);
        var merged = existing
            .Where(r =>
                !string.Equals(r.Id, record.Id, StringComparison.Ordinal)
                && (r.Source != AuditSource.System || r.EntityId != entityId)
            )
            .ToList();
        merged.Add(record);
        await store.SaveChapterAppearances(args.NovelId, chapter.Id, merged, ct);
        ThrowIfCancelled(ct);

        return new AgentToolResult<RecordAppearanceDetails>
        {
            Content =
            [
                new TextContent(
                    $"已登记出场：{args.EntityType} {entityId} @ {chapter.Title}（{record.Mode}）"
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

    [Description("实体 ID 或名称（角色/地点/组织/道具/情节线/场景）")]
    public string EntityId { get; init; } = "";

    [Description("实体类型：character/location/faction/item/plotline/scene")]
    public EntityType EntityType { get; init; } = EntityType.Character;

    [Description("章节 ID、章节号或标题")]
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
