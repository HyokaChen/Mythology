namespace Core.Data;

/// <summary>
/// 实体变更事件，由 FileNovelStore 写操作触发。
/// 用于 UI 刷新、对话提示、Agent 上下文同步。
/// </summary>
public record EntityChangeEvent(
    /// <summary>实体类型。</summary>
    EntityType EntityType,
    /// <summary>实体唯一标识符。</summary>
    string EntityId,
    /// <summary>操作类型：创建 / 更新 / 删除。</summary>
    AuditAction Action,
    /// <summary>所属小说 ID。</summary>
    string NovelId,
    /// <summary>变更后的名称（用于显示）。</summary>
    string EntityName,
    /// <summary>字段变更映射，如 { "Personality": "旧值 → 新值" }。</summary>
    Dictionary<string, string>? FieldChanges = null,
    /// <summary>变更来源，默认为 AI。</summary>
    AuditSource Source = AuditSource.Ai,
    /// <summary>触发工具名称（可选）。</summary>
    string? ToolName = null
);
