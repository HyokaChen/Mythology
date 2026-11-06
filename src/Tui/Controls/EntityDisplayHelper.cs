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
        EntityChangeSummary.DisplayName(entityType);

    /// <summary>哈希显示类型列表（与 Desktop EntityRefParser 一致）。</summary>
    public static readonly string[] HashDisplayTypes =
    [
        "角色",
        "组织",
        "地点",
        "情节",
        "场景",
        "道具",
        "关系",
        "章节",
        "分卷",
        "小说",
    ];

    /// <summary>哈希显示类型到 EntityType 的映射。</summary>
    private static readonly Dictionary<string, EntityType> HashDisplayToEntityTypeMap = new()
    {
        ["角色"] = EntityType.Character,
        ["组织"] = EntityType.Faction,
        ["地点"] = EntityType.Location,
        ["情节"] = EntityType.Plotline,
        ["场景"] = EntityType.Scene,
        ["道具"] = EntityType.Item,
        ["关系"] = EntityType.Relationship,
        ["章节"] = EntityType.Chapter,
        ["大纲"] = EntityType.Chapter, // 旧语法兼容
        ["分卷"] = EntityType.Volume,
        ["小说"] = EntityType.Novel,
    };

    /// <summary>
    /// 根据显示名称获取实体类型。
    /// </summary>
    /// <param name="display">显示名称</param>
    /// <returns>实体类型，未找到则返回 null</returns>
    public static EntityType? ToEntityType(string display) =>
        HashDisplayToEntityTypeMap.TryGetValue(display, out var et) ? et : null;

    /// <summary>实体类型 → 插入引用时使用的规范显示名（与 Desktop EntityRefParser 一致）。</summary>
    public static string HashDisplayName(EntityType type) =>
        type switch
        {
            EntityType.Character => "角色",
            EntityType.Faction => "组织",
            EntityType.Location => "地点",
            EntityType.Plotline => "情节",
            EntityType.Scene => "场景",
            EntityType.Item => "道具",
            EntityType.Relationship => "关系",
            EntityType.Chapter => "章节",
            EntityType.Volume => "分卷",
            EntityType.Novel => "小说",
            _ => DisplayName(type),
        };
}
