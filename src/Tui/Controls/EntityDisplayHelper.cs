using Core.Data;

namespace Tui.Controls;

/// <summary>
/// 实体显示辅助类 — 提供实体类型的中文显示名称和映射。
/// </summary>
public static class EntityDisplayHelper
{
    /// <summary>
    /// 根据实体类型返回中文显示名称。
    /// </summary>
    /// <param name="entityType">实体类型。</param>
    /// <returns>中文显示名称。</returns>
    public static string DisplayName(EntityType entityType) =>
        entityType switch
        {
            EntityType.Character => "角色",
            EntityType.Faction => "组织",
            EntityType.Location => "地点",
            EntityType.Plotline => "情节线",
            EntityType.Chapter => "章节",
            EntityType.Item => "道具",
            EntityType.TimelineEvent => "时间线事件",
            EntityType.Relationship => "关系",
            EntityType.FactionRelationship => "组织关系",
            EntityType.Novel => "小说",
            EntityType.Volume => "分卷",
            EntityType.Scene => "场景",
            EntityType.WorldRule => "世界规则",
            EntityType.Note => "笔记",
            _ => entityType.ToString(),
        };

    /// <summary>哈希显示类型列表。</summary>
    public static readonly string[] HashDisplayTypes =
    [
        "角色",
        "组织",
        "地点",
        "情节",
        "道具",
        "关系",
        "大纲",
    ];

    /// <summary>哈希显示类型到 EntityType 的映射。</summary>
    private static readonly Dictionary<string, EntityType> HashDisplayToEntityTypeMap = new()
    {
        ["角色"] = EntityType.Character,
        ["组织"] = EntityType.Faction,
        ["地点"] = EntityType.Location,
        ["情节"] = EntityType.Plotline,
        ["道具"] = EntityType.Item,
        ["关系"] = EntityType.Relationship,
        ["大纲"] = EntityType.Chapter,
    };

    /// <summary>
    /// 根据显示名称获取实体类型。
    /// </summary>
    /// <param name="display">显示名称。</param>
    /// <returns>实体类型，未找到则返回 null。</returns>
    public static EntityType? ToEntityType(string display) =>
        HashDisplayToEntityTypeMap.TryGetValue(display, out var et) ? et : null;
}
