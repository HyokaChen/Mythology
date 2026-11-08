using Core.Data;

namespace Core.Cloud;

/// <summary>
/// 设定联动输入构建器：把本体日志（<see cref="OntologyAction"/>）的最近修改折叠为
/// 「本轮被编辑的设定元素」（<see cref="SetupChangedElementInput"/>），供
/// <c>fiction.setup.check</c> 返回 change-impact 影响面。
///
/// 口径（与服务端 <c>SetupLinkageIndex</c> 同源）：
/// - 章节一律 ch_{Order}（客户端章节 ID 是 GUID，云端只认规范化形式）；
/// - 关系元素用 <c>source|type|target</c> 复合键（type = RelationshipOntology.CloudSetupToken），
///   并额外上报两端角色——即使复合键与服务端不一致，端点仍能命中影响面（防御性冗余）；
/// - 未映射的实体类型（时间线事件/出场记录/笔记/组织关系）跳过——云端无对应元素。
/// </summary>
public static class SetupChangedElementMapper
{
    /// <summary>默认取最近多少条本体动作（append-only 日志按时间倒序折叠）。</summary>
    public const int DefaultRecentActionCount = 50;

    /// <summary>
    /// 从本体动作日志折叠被编辑元素（最近的优先、去重）。
    /// 无可用动作 → 空列表（调用方按「不上报」处理）。
    /// </summary>
    public static List<SetupChangedElementInput> FromOntologyActions(
        Novel novel,
        IReadOnlyList<OntologyAction> actions,
        IReadOnlyList<Relationship> relationships,
        IReadOnlyList<Chapter> chapters,
        int take = DefaultRecentActionCount
    )
    {
        if (actions.Count == 0 || take <= 0)
            return [];

        var chapterRefMap = BuildChapterRefMap(chapters);
        var relationshipById = new Dictionary<string, Relationship>(StringComparer.Ordinal);
        foreach (var relationship in relationships)
        {
            if (!string.IsNullOrWhiteSpace(relationship.Id))
                relationshipById.TryAdd(relationship.Id, relationship);
        }

        var result = new List<SetupChangedElementInput>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var action in actions.OrderByDescending(a => a.CreatedAt).Take(take))
        {
            foreach (var element in MapAction(action, novel, relationshipById, chapterRefMap))
            {
                if (seen.Add($"{element.Kind}|{element.Id}"))
                    result.Add(element);
            }
        }
        return result;
    }

    private static IEnumerable<SetupChangedElementInput> MapAction(
        OntologyAction action,
        Novel novel,
        IReadOnlyDictionary<string, Relationship> relationshipById,
        IReadOnlyDictionary<string, string> chapterRefMap
    )
    {
        switch (action.TargetEntityType)
        {
            case EntityType.Character:
                yield return new SetupChangedElementInput("character", action.TargetId);
                break;
            case EntityType.Relationship:
                // 关系复合键 + 两端角色（端点上报保证即使键不一致仍有影响面）。
                if (relationshipById.TryGetValue(action.TargetId, out var relationship))
                {
                    yield return new SetupChangedElementInput(
                        "relation",
                        $"{relationship.CharacterAId}|{RelationshipOntology.CloudSetupToken(relationship.Type)}|{relationship.CharacterBId}"
                    );
                    yield return new SetupChangedElementInput(
                        "character",
                        relationship.CharacterAId
                    );
                    yield return new SetupChangedElementInput(
                        "character",
                        relationship.CharacterBId
                    );
                }
                break;
            case EntityType.Scene:
                yield return new SetupChangedElementInput("scene", action.TargetId);
                break;
            case EntityType.Plotline:
                yield return new SetupChangedElementInput("plotline", action.TargetId);
                break;
            case EntityType.Location:
                yield return new SetupChangedElementInput("location", action.TargetId);
                break;
            case EntityType.Item:
                yield return new SetupChangedElementInput("item", action.TargetId);
                break;
            case EntityType.Faction:
                yield return new SetupChangedElementInput("organization", action.TargetId);
                break;
            case EntityType.WorldRule:
                yield return new SetupChangedElementInput("worldrule", action.TargetId);
                break;
            case EntityType.Chapter:
                if (chapterRefMap.TryGetValue(action.TargetId, out var chapterRef))
                    yield return new SetupChangedElementInput("chapter", chapterRef);
                break;
            case EntityType.Volume:
                yield return new SetupChangedElementInput("volume", action.TargetId);
                break;
            case EntityType.Novel:
                yield return new SetupChangedElementInput("novel", novel.Id);
                break;
            default:
                // 时间线事件 / 出场记录 / 组织关系 / 笔记：云端设定图无对应元素。
                break;
        }
    }

    private static IReadOnlyDictionary<string, string> BuildChapterRefMap(
        IReadOnlyList<Chapter> chapters
    )
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chapter in chapters)
        {
            if (chapter.Order > 0 && !string.IsNullOrWhiteSpace(chapter.Id))
                map.TryAdd(chapter.Id, $"ch_{chapter.Order}");
        }
        return map;
    }
}
