using Core.Data;
using Core.Shared;

namespace Core.Evolution;

/// <summary>
/// 本体动作日志的纯函数 diff（不做 IO）：把「旧 YAML dict / 新 YAML dict」翻译为
/// 按字段的 <see cref="OntologyAction"/>；来源、工具、证据一律取 <see cref="OntologyWriteContext.Current"/>。
/// </summary>
public static class OntologyJournal
{
    private const int MaxValueChars = 200;
    private const int MaxSummaryChars = 240;
    private const int MaxNameChars = 60;

    private static readonly HashSet<string> ExcludedKeys = new(StringComparer.Ordinal)
    {
        "id",
        "novel-id",
        "created-at",
        "updated-at",
    };

    /// <summary>实体创建/更新 diff；无变化返回空列表。</summary>
    public static IReadOnlyList<OntologyAction> Diff(
        string novelId,
        EntityType entityType,
        string entityId,
        string entityName,
        IReadOnlyDictionary<string, object?>? oldValues,
        IReadOnlyDictionary<string, object?> newValues,
        bool isNew,
        string? chapterId = null
    )
    {
        var scope = OntologyWriteContext.Current;
        var name = Truncate(entityName, MaxNameChars);
        var label = EntityFieldRegistry.Label(entityType);

        if (isNew || oldValues is null)
        {
            return
            [
                CreateAction(
                    novelId,
                    entityType,
                    entityId,
                    name,
                    chapterId,
                    scope,
                    OntologyOperation.Create,
                    scope.Source,
                    $"创建{label}「{name}」"
                ),
            ];
        }

        var actions = new List<OntologyAction>();
        foreach (var (field, newValue) in newValues)
        {
            if (ExcludedKeys.Contains(field))
            {
                continue;
            }
            if (
                entityType == EntityType.Chapter
                && string.Equals(field, "order", StringComparison.Ordinal)
            )
            {
                continue;
            }

            var oldText = OntologyFieldMap.FormatValue(
                oldValues.TryGetValue(field, out var oldValue) ? oldValue : null
            );
            var newText = OntologyFieldMap.FormatValue(newValue);
            if (string.Equals(oldText, newText, StringComparison.Ordinal))
            {
                continue;
            }

            var from = Truncate(oldText, MaxValueChars);
            var to = Truncate(newText, MaxValueChars);
            var classification = OntologyFieldMap.Classify(entityType, field);
            actions.Add(
                CreateAction(
                    novelId,
                    entityType,
                    entityId,
                    name,
                    chapterId,
                    scope,
                    OntologyOperation.Update,
                    scope.Source,
                    Truncate($"{label}「{name}」{field}: {from} → {to}", MaxSummaryChars),
                    classification.Element,
                    field,
                    from,
                    to
                )
            );
        }
        return actions;
    }

    /// <summary>删除动作。</summary>
    public static OntologyAction Deleted(
        string novelId,
        EntityType entityType,
        string entityId,
        string entityName
    )
    {
        var scope = OntologyWriteContext.Current;
        var name = Truncate(entityName, MaxNameChars);
        return CreateAction(
            novelId,
            entityType,
            entityId,
            name,
            chapterId: null,
            scope,
            OntologyOperation.Delete,
            scope.Source,
            $"删除{EntityFieldRegistry.Label(entityType)}「{name}」"
        );
    }

    /// <summary>
    /// 状态分片整章替换的按 Id 三路 diff（新增/删除/字段变化）。
    /// 来源使用记录自身的 <see cref="EntityStateChange.Source"/>，不用环境来源。
    /// </summary>
    public static IReadOnlyList<OntologyAction> DiffStateChanges(
        string novelId,
        string chapterId,
        IReadOnlyList<EntityStateChange> oldRecords,
        IReadOnlyList<EntityStateChange> newRecords
    )
    {
        var scope = OntologyWriteContext.Current;
        var oldById = oldRecords.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var newById = newRecords.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var actions = new List<OntologyAction>();

        foreach (var record in newRecords)
        {
            var name = Truncate(record.Summary, MaxNameChars);
            if (!oldById.TryGetValue(record.Id, out var old))
            {
                actions.Add(
                    CreateStateAction(
                        novelId,
                        record,
                        chapterId,
                        scope,
                        OntologyOperation.Create,
                        name,
                        name
                    )
                );
                continue;
            }

            if (StateRecordEquals(old, record))
            {
                continue;
            }

            actions.Add(
                CreateStateAction(
                    novelId,
                    record,
                    chapterId,
                    scope,
                    OntologyOperation.Update,
                    name,
                    Truncate($"变化「{name}」: {old.Summary} → {record.Summary}", MaxSummaryChars),
                    Truncate(old.FromSummary ?? "", MaxValueChars),
                    Truncate(record.ToSummary ?? "", MaxValueChars)
                )
            );
        }

        foreach (var old in oldRecords)
        {
            if (newById.ContainsKey(old.Id))
            {
                continue;
            }
            var name = Truncate(old.Summary, MaxNameChars);
            actions.Add(
                CreateStateAction(
                    novelId,
                    old,
                    chapterId,
                    scope,
                    OntologyOperation.Delete,
                    name,
                    $"删除变化：{name}"
                )
            );
        }

        return actions;
    }

    private static bool StateRecordEquals(EntityStateChange a, EntityStateChange b) =>
        a.TargetEntityType == b.TargetEntityType
        && string.Equals(a.TargetId, b.TargetId, StringComparison.Ordinal)
        && string.Equals(a.ChapterId, b.ChapterId, StringComparison.Ordinal)
        && a.Aspect == b.Aspect
        && string.Equals(a.FromSummary, b.FromSummary, StringComparison.Ordinal)
        && string.Equals(a.ToSummary, b.ToSummary, StringComparison.Ordinal)
        && string.Equals(a.EvidenceQuote, b.EvidenceQuote, StringComparison.Ordinal)
        && string.Equals(a.Summary, b.Summary, StringComparison.Ordinal)
        && EffectsEqual(a.Effects, b.Effects)
        && a.Sequence == b.Sequence
        && string.Equals(a.Cause, b.Cause, StringComparison.Ordinal)
        && a.Visibility == b.Visibility
        && a.WitnessIds.SequenceEqual(b.WitnessIds, StringComparer.Ordinal)
        && string.Equals(a.LocationId, b.LocationId, StringComparison.Ordinal)
        && string.Equals(a.SceneId, b.SceneId, StringComparison.Ordinal)
        && string.Equals(a.TimeDescription, b.TimeDescription, StringComparison.Ordinal)
        && a.PlotlineIds.SequenceEqual(b.PlotlineIds, StringComparer.Ordinal)
        && a.Lifecycle == b.Lifecycle
        && a.CommittedAt == b.CommittedAt
        && string.Equals(a.ChangeSetId, b.ChangeSetId, StringComparison.Ordinal)
        && a.IsCascade == b.IsCascade
        && string.Equals(a.SourceChangeId, b.SourceChangeId, StringComparison.Ordinal)
        && string.Equals(a.SessionId, b.SessionId, StringComparison.Ordinal);

    private static bool EffectsEqual(IReadOnlyList<StateEffect> a, IReadOnlyList<StateEffect> b)
    {
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];
            if (
                x.TargetType != y.TargetType
                || !string.Equals(x.TargetId, y.TargetId, StringComparison.Ordinal)
                || !string.Equals(x.Field, y.Field, StringComparison.Ordinal)
                || x.Operation != y.Operation
                || !string.Equals(x.Value, y.Value, StringComparison.Ordinal)
                || !string.Equals(x.From, y.From, StringComparison.Ordinal)
                || x.Cascade != y.Cascade
                || !string.Equals(x.SourceChangeId, y.SourceChangeId, StringComparison.Ordinal)
            )
                return false;
        }
        return true;
    }

    private static OntologyAction CreateStateAction(
        string novelId,
        EntityStateChange record,
        string chapterId,
        OntologyWriteScope scope,
        OntologyOperation operation,
        string name,
        string summary,
        string? from = null,
        string? to = null
    ) =>
        new()
        {
            NovelId = novelId,
            Element = OntologyElement.State,
            Operation = operation,
            TargetEntityType = record.TargetEntityType,
            TargetId = record.TargetId,
            TargetName = name,
            Field = "state-change",
            From = from,
            To = to,
            Summary = summary,
            ChapterId = string.IsNullOrEmpty(chapterId) ? scope.ChapterId : chapterId,
            Evidence = record.EvidenceQuote,
            ProposalId = scope.ProposalId,
            Source = record.Source,
            ToolName = scope.ToolName,
            SessionId = scope.SessionId ?? record.SessionId,
            ChangeSetId = scope.ChangeSetId,
            LocationId = record.LocationId,
            SceneId = record.SceneId,
            PlotlineIds = record.PlotlineIds,
            Sequence = record.Sequence,
            IsCascade = record.IsCascade,
            SourceChangeId = record.SourceChangeId,
        };

    private static OntologyAction CreateAction(
        string novelId,
        EntityType entityType,
        string entityId,
        string name,
        string? chapterId,
        OntologyWriteScope scope,
        OntologyOperation operation,
        AuditSource source,
        string summary,
        OntologyElement? element = null,
        string? field = null,
        string? from = null,
        string? to = null
    ) =>
        new()
        {
            NovelId = novelId,
            Element = element ?? OntologyFieldMap.DefaultElement(entityType),
            Operation = operation,
            TargetEntityType = entityType,
            TargetId = entityId,
            TargetName = name,
            Field = field,
            From = from,
            To = to,
            Summary = summary,
            ChapterId = chapterId ?? scope.ChapterId,
            Evidence = scope.Evidence,
            ProposalId = scope.ProposalId,
            Source = source,
            ToolName = scope.ToolName,
            SessionId = scope.SessionId,
            ChangeSetId = scope.ChangeSetId,
            LocationId = scope.LocationId,
            SceneId = scope.SceneId,
            PlotlineIds = scope.PlotlineIds?.ToList() ?? [],
            Sequence = scope.Sequence,
            IsCascade = scope.IsCascade,
            SourceChangeId = scope.SourceChangeId,
        };

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }
        return text.Length <= max ? text : text[..max];
    }
}
