using Core.Data;

namespace Core.Evolution;

/// <summary>全实体时间线的一行（状态变化 / 设定动作 / 旧字段审计）。</summary>
public sealed record EntityLogEntry(
    EntityType EntityType,
    string EntityId,
    string EntityName,
    int ChapterOrder,
    string? ChapterId,
    string? ChapterTitle,
    string? LocationId,
    string? LocationName,
    string? SceneId,
    string? SceneTitle,
    IReadOnlyList<string> PlotlineNames,
    string? TimeDescription,
    string? Cause,
    IReadOnlyList<StateEffect> Effects,
    ChangeVisibility Visibility,
    IReadOnlyList<string> WitnessIds,
    int Sequence,
    DateTime Timestamp,
    OntologyElement Element,
    OntologyOperation Operation,
    string Kind,
    string? Field,
    string? From,
    string? To,
    string Summary,
    AuditSource Source,
    string? SourceSessionId,
    ChangeLifecycle Lifecycle,
    bool IsCascade,
    string? ChangeSetId
);

/// <summary>
/// 全实体时间线（纯派生视图）：状态变化 + 本体动作日志按「章 → 章内顺序 → 时间」混排。
/// 设定变更（ChapterId="" 的 manual 记录）不单独渲染，它的字段差异已由 journal 的 User 行表示。
/// </summary>
public sealed class EntityLogView
{
    private readonly List<EntityLogEntry> _entries;

    private EntityLogView(List<EntityLogEntry> entries) => _entries = entries;

    /// <summary>该小说全部时间线行。</summary>
    public IReadOnlyList<EntityLogEntry> All => _entries;

    /// <summary>从 store 全量构建（毫秒级；派生视图每次现算）。</summary>
    public static async Task<EntityLogView> BuildAsync(
        INovelStore store,
        string novelId,
        CancellationToken ct = default
    )
    {
        var chapters = await store.GetChapters(novelId, ct);
        var scenes = await store.GetScenesByNovel(novelId, ct);
        var locations = await store.GetLocations(novelId, ct);
        var plotlines = await store.GetPlotlines(novelId, ct);
        var characters = await store.GetCharacters(novelId, ct);
        var changes = await store.GetEntityStateChanges(novelId, ct);
        var actions = await store.GetOntologyActions(novelId, ct);

        var chapterOrder = chapters.ToDictionary(c => c.Id, c => c.Order, StringComparer.Ordinal);
        var chapterTitle = chapters.ToDictionary(c => c.Id, c => c.Title, StringComparer.Ordinal);
        var locationName = locations.ToDictionary(l => l.Id, l => l.Name, StringComparer.Ordinal);
        var plotlineName = plotlines.ToDictionary(p => p.Id, p => p.Name, StringComparer.Ordinal);
        var sceneTitle = scenes.ToDictionary(
            s => s.Id,
            s => string.IsNullOrWhiteSpace(s.Title) ? s.Id : s.Title,
            StringComparer.Ordinal
        );
        var characterName = characters.ToDictionary(c => c.Id, c => c.Name, StringComparer.Ordinal);

        var names = new Dictionary<(EntityType, string), string>();
        foreach (var character in characters)
            names[(EntityType.Character, character.Id)] = character.Name;
        foreach (var faction in await store.GetFactions(novelId, ct))
            names[(EntityType.Faction, faction.Id)] = faction.Name;
        foreach (var location in locations)
            names[(EntityType.Location, location.Id)] = location.Name;
        foreach (var item in await store.GetItems(novelId, ct))
            names[(EntityType.Item, item.Id)] = item.Name;
        foreach (var plotline in plotlines)
            names[(EntityType.Plotline, plotline.Id)] = plotline.Name;
        foreach (var scene in scenes)
            names[(EntityType.Scene, scene.Id)] = scene.Title;
        foreach (var chapter in chapters)
            names[(EntityType.Chapter, chapter.Id)] = chapter.Title;

        var entries = new List<EntityLogEntry>();

        // 状态变化行：只渲染有章节归属的记录（manual 只供 as-of 视图）。
        foreach (var change in changes.Where(c => !string.IsNullOrEmpty(c.ChapterId)))
        {
            var order = chapterOrder.GetValueOrDefault(change.ChapterId);
            entries.Add(
                new EntityLogEntry(
                    change.TargetEntityType,
                    change.TargetId,
                    NameOf(names, change.TargetEntityType, change.TargetId, change.Summary),
                    order,
                    change.ChapterId,
                    chapterTitle.GetValueOrDefault(change.ChapterId),
                    change.LocationId,
                    change.LocationId is null
                        ? null
                        : locationName.GetValueOrDefault(change.LocationId),
                    change.SceneId,
                    change.SceneId is null ? null : sceneTitle.GetValueOrDefault(change.SceneId),
                    change
                        .PlotlineIds.Select(id => plotlineName.GetValueOrDefault(id, id))
                        .ToList(),
                    change.TimeDescription,
                    change.Cause,
                    change.Effects,
                    change.Visibility,
                    change
                        .WitnessIds.Select(id => characterName.GetValueOrDefault(id, id))
                        .ToList(),
                    change.Sequence,
                    change.CommittedAt ?? change.CreatedAt,
                    OntologyElement.State,
                    OntologyOperation.Update,
                    "state",
                    "state-change",
                    change.FromSummary,
                    change.ToSummary,
                    string.IsNullOrWhiteSpace(change.Summary)
                        ? $"{change.FromSummary ?? "?"} → {change.ToSummary ?? "?"}"
                        : change.Summary,
                    change.Source,
                    change.SessionId,
                    change.Lifecycle,
                    change.IsCascade,
                    change.ChangeSetId
                )
            );
        }

        // 动作行（state-change 已由状态行渲染，跳过避免重复）
        foreach (
            var action in actions.Where(a =>
                !string.Equals(a.Field, "state-change", StringComparison.Ordinal)
            )
        )
        {
            var order = action.ChapterId is not null
                ? chapterOrder.GetValueOrDefault(action.ChapterId)
                : 0;
            entries.Add(
                new EntityLogEntry(
                    action.TargetEntityType,
                    action.TargetId,
                    action.TargetName,
                    order,
                    action.ChapterId,
                    action.ChapterId is null
                        ? null
                        : chapterTitle.GetValueOrDefault(action.ChapterId),
                    action.LocationId,
                    action.LocationId is null
                        ? null
                        : locationName.GetValueOrDefault(action.LocationId),
                    action.SceneId,
                    action.SceneId is null ? null : sceneTitle.GetValueOrDefault(action.SceneId),
                    action
                        .PlotlineIds.Select(id => plotlineName.GetValueOrDefault(id, id))
                        .ToList(),
                    null,
                    null,
                    [],
                    ChangeVisibility.Public,
                    [],
                    action.Sequence,
                    action.CreatedAt,
                    action.Element,
                    action.Operation,
                    "Element",
                    action.Field,
                    action.From,
                    action.To,
                    action.Summary,
                    action.Source,
                    action.SessionId,
                    ChangeLifecycle.Committed,
                    action.IsCascade,
                    action.ChangeSetId
                )
            );
        }

        return new EntityLogView(Sort(entries));
    }

    /// <summary>某实体的时间线行。</summary>
    public IReadOnlyList<EntityLogEntry> Get(EntityType type, string entityId) =>
        _entries
            .Where(e =>
                e.EntityType == type
                && string.Equals(e.EntityId, entityId, StringComparison.Ordinal)
            )
            .ToList();

    /// <summary>某章的全部时间线行（章节阅读器）。</summary>
    public IReadOnlyList<EntityLogEntry> GetByChapter(string chapterId) =>
        _entries
            .Where(e => string.Equals(e.ChapterId, chapterId, StringComparison.Ordinal))
            .ToList();

    /// <summary>排序：章序 → 章内顺序 → 时间；待发布排在同章末尾；设定组（0 章）按时间。</summary>
    private static List<EntityLogEntry> Sort(List<EntityLogEntry> entries) =>
        entries
            .OrderBy(e => e.ChapterOrder)
            .ThenBy(e =>
                e.ChapterOrder == 0 ? 1
                : e.Lifecycle == ChangeLifecycle.Pending ? 1
                : 0
            )
            .ThenBy(e => e.ChapterOrder == 0 ? 0 : e.Sequence)
            .ThenBy(e => e.Timestamp)
            .ToList();

    /// <summary>
    /// 单实体时间线（含旧审计字段行，只读合并）。按 (field,from,to,source) 2 分钟内去重。
    /// </summary>
    public static async Task<IReadOnlyList<EntityLogEntry>> BuildForEntityAsync(
        INovelStore store,
        string novelId,
        EntityType type,
        string entityId,
        CancellationToken ct = default
    )
    {
        var view = await BuildAsync(store, novelId, ct);
        var entries = view.Get(type, entityId).ToList();

        var audit = await store.GetEntityAuditLog(entityId, ct);
        var legacy = audit
            .Where(a => a.FieldName is not null)
            .Select(a => new EntityLogEntry(
                type,
                entityId,
                entityId,
                entries.FirstOrDefault()?.ChapterOrder ?? 0,
                null,
                null,
                null,
                null,
                null,
                null,
                [],
                null,
                null,
                [],
                ChangeVisibility.Public,
                [],
                0,
                a.CreatedAt,
                OntologyFieldMap.Classify(type, a.FieldName!).Element,
                a.Action switch
                {
                    AuditAction.Create => OntologyOperation.Create,
                    AuditAction.Delete => OntologyOperation.Delete,
                    _ => OntologyOperation.Update,
                },
                "字段",
                a.FieldName,
                a.OldValue,
                a.NewValue,
                $"{a.FieldName}: {a.OldValue ?? ""} → {a.NewValue ?? ""}",
                a.Source,
                a.SessionId,
                ChangeLifecycle.Committed,
                false,
                null
            ))
            .ToList();

        var merged = new List<EntityLogEntry>(entries);
        foreach (var candidate in legacy)
        {
            var duplicate = merged.Any(e =>
                e.Kind == "字段"
                && string.Equals(e.Field, candidate.Field, StringComparison.Ordinal)
                && string.Equals(e.From, candidate.From, StringComparison.Ordinal)
                && string.Equals(e.To, candidate.To, StringComparison.Ordinal)
                && e.Source == candidate.Source
                && Math.Abs((e.Timestamp - candidate.Timestamp).TotalMinutes) <= 2
            );
            if (!duplicate)
                merged.Add(candidate);
        }

        return merged
            .OrderBy(e => e.ChapterOrder)
            .ThenBy(e => e.Sequence)
            .ThenBy(e => e.Timestamp)
            .ToList();
    }

    private static string NameOf(
        Dictionary<(EntityType, string), string> names,
        EntityType type,
        string id,
        string fallback
    ) => names.TryGetValue((type, id), out var name) ? name : fallback;
}

/// <summary>实体时间线的 Markdown 表格渲染。</summary>
public static class EntityLogFormatter
{
    public static string ToMarkdown(string entityName, IReadOnlyList<EntityLogEntry> entries)
    {
        if (entries.Count == 0)
            return $"「{entityName}」暂无时间线记录。";

        var text = new System.Text.StringBuilder();
        text.AppendLine($"## 「{entityName}」时间线");
        text.AppendLine();
        text.AppendLine("| 章 | 时间/地点 | 情节线 | 变化 | 原因/在场 | 来源 | 状态 |");
        text.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var entry in entries)
        {
            text.AppendLine(
                $"| {ChapterLabel(entry)} | {TimeAndPlace(entry)} | {Plotlines(entry)} | {Change(entry)} | {CauseAndWitnesses(entry)} | {Source(entry)} | {Status(entry)} |"
            );
        }
        return text.ToString().TrimEnd();
    }

    private static string ChapterLabel(EntityLogEntry entry) =>
        entry.ChapterOrder <= 0 ? "设定" : $"第{entry.ChapterOrder}章";

    private static string TimeAndPlace(EntityLogEntry entry)
    {
        var parts = new[] { entry.TimeDescription, entry.LocationName }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
        return parts.Count == 0 ? "—" : Escape(string.Join(" · ", parts));
    }

    private static string Plotlines(EntityLogEntry entry) =>
        entry.PlotlineNames.Count == 0 ? "—" : Escape(string.Join("、", entry.PlotlineNames));

    private static string Change(EntityLogEntry entry)
    {
        var text = string.IsNullOrWhiteSpace(entry.Summary)
            ? entry.Field is null
                ? ""
                : $"{entry.Field}: {entry.From ?? ""} → {entry.To ?? ""}"
            : entry.Summary;
        if (entry.Effects.Count > 0)
        {
            var effects = string.Join(
                "；",
                entry.Effects.Select(e => $"{e.Field}: {e.Operation} {e.Value ?? ""}".TrimEnd())
            );
            text = string.IsNullOrWhiteSpace(text) ? effects : $"{text}（{effects}）";
        }
        return Escape(text.Length == 0 ? "—" : text);
    }

    private static string CauseAndWitnesses(EntityLogEntry entry)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.Cause))
            parts.Add(entry.Cause!);
        if (entry.WitnessIds.Count > 0)
            parts.Add($"在场：{string.Join("、", entry.WitnessIds)}");
        return parts.Count == 0 ? "—" : Escape(string.Join(" / ", parts));
    }

    private static string Source(EntityLogEntry entry)
    {
        var icon = entry.Source switch
        {
            AuditSource.User => "👤",
            AuditSource.Ai => "🤖",
            _ => "🔧",
        };
        var text = icon;
        if (!string.IsNullOrWhiteSpace(entry.SourceSessionId))
            text +=
                $"（会话 {entry.SourceSessionId![..Math.Min(6, entry.SourceSessionId.Length)]}）";
        if (string.IsNullOrEmpty(entry.ChapterId))
            text += "（设定）";
        return text;
    }

    private static string Status(EntityLogEntry entry)
    {
        var text =
            entry.Operation == OntologyOperation.Revert ? "已回撤"
            : entry.Lifecycle == ChangeLifecycle.Pending ? "待发布"
            : "已生效";
        if (entry.IsCascade)
            text = "🔗" + text;
        if (entry.Visibility != ChangeVisibility.Public)
            text = "🔒" + text;
        return text;
    }

    private static string Escape(string text) =>
        text.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");
}
