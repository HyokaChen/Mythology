using Core.Data;

namespace Core.Evolution;

/// <summary>发布预检问题（BlocksPublish=true 阻止发布）。</summary>
public sealed record ValidationIssue(string Code, string Message, bool BlocksPublish);

/// <summary>发布预览：待生效变化 + 正文检测 + 联动建议 + 预检问题。</summary>
public sealed record PublishPreview(
    string NovelId,
    string ChapterId,
    int ChapterOrder,
    string ChapterTitle,
    IReadOnlyList<EntityStateChange> PendingChanges,
    IReadOnlyList<DetectedDeathCandidate> DetectedDeaths,
    IReadOnlyList<CascadeSuggestion> Suggestions,
    IReadOnlyList<ValidationIssue> ValidationIssues
)
{
    /// <summary>是否存在阻止发布的问题。</summary>
    public bool HasBlockingIssues => ValidationIssues.Any(i => i.BlocksPublish);
}

/// <summary>联动建议（自动建议不可取消；可选建议默认按 <see cref="DefaultSelected"/>）。</summary>
public sealed record CascadeSuggestion(
    string Id,
    EntityType TargetType,
    string TargetId,
    string TargetName,
    string Field,
    StateEffectOperation Operation,
    string? Value,
    string Summary,
    bool DefaultSelected,
    bool IsAutomatic,
    string? SourceChangeId,
    bool IsCreate = false
);

/// <summary>发布选项：null = 全部默认（检测到的死亡全选、默认选中的联动全选）。</summary>
public sealed record PublishOptions(
    IReadOnlyCollection<string>? SelectedDetectedDeathIds = null,
    IReadOnlyCollection<string>? SelectedSuggestionIds = null
);

/// <summary>发布/补记结果。</summary>
public sealed record PublishResult(
    bool Success,
    string? ChangeSetId,
    int AppliedSuggestionCount,
    int CreatedEntityCount,
    IReadOnlyList<string> Warnings,
    string? Error
);

/// <summary>撤回冲突（实体已被发布后修改）。</summary>
public sealed record ChangeConflict(EntityType EntityType, string EntityId, string Reason);

/// <summary>撤回预览。</summary>
public sealed record UnpublishPreview(
    bool HasAppliedChangeSets,
    IReadOnlyList<string> LaterPublishedChapters,
    IReadOnlyList<ChangeConflict> Conflicts
)
{
    /// <summary>是否有阻止撤回的问题。</summary>
    public bool CanUnpublish =>
        HasAppliedChangeSets && LaterPublishedChapters.Count == 0 && Conflicts.Count == 0;
}

/// <summary>撤回/删除结果。</summary>
public sealed record UnpublishResult(
    bool Success,
    int RevertedEntries,
    IReadOnlyList<string> Warnings,
    string? Error
);

/// <summary>正文检测到的未登记死亡。</summary>
public sealed record DetectedDeathCandidate(
    string Id,
    string CharacterId,
    string CharacterName,
    string Evidence,
    string Summary
);
