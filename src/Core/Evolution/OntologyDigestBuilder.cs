using System.Text;
using Core.Data;

namespace Core.Evolution;

/// <summary>跨会话摘要：注入文本 + 注入成功后的日志游标。</summary>
public sealed record OntologyDigest(string Text, long NextOffset);

/// <summary>
/// 跨会话本体变化摘要：从 ontology/actions.jsonl 的游标位置读取「上次会话以来」的变化，
/// 附带各章待发布变化，组成一段主动注入新 session 的上下文。
/// 磁盘是唯一真源；注入成功才推进游标。
/// </summary>
public static class OntologyDigestBuilder
{
    private const int MaxActions = 20;
    private const int MaxPending = 5;

    public static async Task<OntologyDigest> BuildAsync(
        INovelStore store,
        string novelId,
        string? currentSessionId,
        CancellationToken ct = default
    )
    {
        var stamp = await store.GetOntologyJournalStamp(novelId, ct);
        var cursor = await store.GetOntologyCursorAsync(novelId, ct);
        if (cursor > stamp)
            cursor = 0;

        var actions = await store.GetOntologyActionsAfterOffset(novelId, cursor, ct);
        var firstRun = cursor == 0;

        var chapters = await store.GetChapters(novelId, ct);
        var chapterById = chapters.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var pending = (await store.GetEntityStateChanges(novelId, ct))
            .Where(c =>
                c.Lifecycle == ChangeLifecycle.Pending && !string.IsNullOrEmpty(c.ChapterId)
            )
            .OrderBy(c => chapterById.GetValueOrDefault(c.ChapterId)?.Order ?? int.MaxValue)
            .ThenBy(c => c.Sequence)
            .ToList();

        // 没有任何新动作（游标已到文件末尾）→ 本轮不注入；待发布变化不重复刷屏。
        if (actions.Count == 0)
            return new OntologyDigest("", stamp);

        var pendingKeys = pending.Select(p => (p.TargetId, p.Summary)).ToHashSet();

        // 待发布变化优先：同 (target, summary) 的动作不再重复展示
        var shownActions = actions
            .Where(a => !pendingKeys.Contains((a.TargetId, a.Summary)))
            .ToList();
        if (shownActions.Count > MaxActions)
            shownActions = shownActions[^MaxActions..];

        if (shownActions.Count == 0 && pending.Count == 0)
            return new OntologyDigest("", stamp);

        var text = new StringBuilder();
        text.AppendLine(
            firstRun && actions.Count > MaxActions
                ? $"{OntologyHarnessPolicy.Prefix} 最近本体变更"
                : $"{OntologyHarnessPolicy.Prefix} 上次会话以来的本体变化"
        );

        foreach (var action in shownActions)
        {
            var time = action.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            var icon = action.Source switch
            {
                AuditSource.User => "👤",
                AuditSource.Ai => "🤖",
                _ => "🔧",
            };
            var place =
                string.IsNullOrEmpty(action.ChapterId) ? "设定"
                : chapterById.TryGetValue(action.ChapterId, out var chapter)
                    ? $"第{chapter.Order}章"
                : "章节";
            var lifecycle = action.Operation == OntologyOperation.Revert ? "已回撤" : "已生效";
            var session = string.Equals(
                action.SessionId,
                currentSessionId,
                StringComparison.Ordinal
            )
                ? "本会话"
                : "上次会话";

            var change =
                action.From is null && action.To is null
                    ? action.Summary
                    : $"{action.Field}: {action.From ?? ""} → {action.To ?? ""}";
            text.AppendLine(
                $"- {time} {icon} {EntityFieldRegistry.Label(action.TargetEntityType)}「{action.TargetName}」{change}（{place}，{lifecycle}，{session}）"
            );
        }

        if (pending.Count > 0)
        {
            foreach (var group in pending.Take(MaxPending).GroupBy(p => p.ChapterId))
            {
                var order = chapterById.GetValueOrDefault(group.Key)?.Order ?? 0;
                var items = string.Join("；", group.Select(p => p.Summary));
                text.AppendLine($"- 第{order}章 有 {group.Count()} 条待发布变化：{items}");
            }
        }

        text.AppendLine(
            "边界：这些是已记录的本体事实；如与正文冲突，以正文证据为准并调用 record_evolution 修正。"
        );
        return new OntologyDigest(text.ToString().TrimEnd(), stamp);
    }
}
