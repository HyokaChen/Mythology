namespace Core.Data;

/// <summary>
/// 实体变更的界面文案：TUI 对话面板与 Desktop 系统状态行共用同一套映射/摘要格式。
/// </summary>
public static class EntityChangeSummary
{
    /// <summary>实体类型的中文显示名称。</summary>
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

    /// <summary>
    /// 生成实体变更摘要；小说级变更不显示，未知操作返回 null。
    /// </summary>
    public static string? Describe(EntityChangeEvent evt)
    {
        if (evt.EntityType == EntityType.Novel)
            return null;

        var label = DisplayName(evt.EntityType);
        return evt.Action switch
        {
            AuditAction.Create => $"新建{label}「{evt.EntityName}」",
            AuditAction.Update => evt.FieldChanges is { Count: > 0 }
                ? $"修改{label}「{evt.EntityName}」: {string.Join(", ", evt.FieldChanges.Keys)}"
                : $"修改{label}「{evt.EntityName}」",
            AuditAction.Delete => $"删除{label}「{evt.EntityName}」",
            _ => null,
        };
    }
}
