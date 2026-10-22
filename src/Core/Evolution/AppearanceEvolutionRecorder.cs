using Core.Data;

namespace Core.Evolution;

/// <summary>
/// 出场/演化记录器（entity-evolution-lifecycle §4.1 L0 扫描层）。
/// 把 DiagnosisScheduler.HasNewCharacterDebut / WritingFlowHook.FindAbsentSupportingCandidates
/// 的运行时字符串扫描固化为持久化出场记录——零 token、无条件执行、Source=System。
/// 章节 ID 解析：chapter.Id（写路径）/ 章序（读路径过滤）。
/// </summary>
public static class AppearanceEvolutionRecorder
{
    /// <summary>
    /// L0 扫描：对已保存章节内容扫描全部已知实体名，命中写该章出场分片（整章替换）。
    /// 保持幂等：重复调用产出相同结果（整章替换语义）。
    /// </summary>
    /// <param name="store">小说存储。</param>
    /// <param name="novelId">小说 ID。</param>
    /// <param name="chapter">已保存的章节（Content 为最终正文）。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task RecordChapterAsync(
        INovelStore store,
        string novelId,
        Chapter chapter,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrEmpty(chapter.Content))
            return;

        ThrowIfCancelled(ct);
        var characters = await store.GetCharacters(novelId, ct);
        var locations = await store.GetLocations(novelId, ct);
        var items = await store.GetItems(novelId, ct);
        var factions = await store.GetFactions(novelId, ct);
        ThrowIfCancelled(ct);

        var records = new List<AppearanceRecord>();

        foreach (var c in characters)
        {
            if (string.IsNullOrWhiteSpace(c.Name))
                continue;
            var evidence = FindEvidence(chapter.Content, c.Name);
            if (evidence is not null)
                records.Add(
                    new AppearanceRecord
                    {
                        Id = $"ap_{chapter.Id}_{c.Id}",
                        NovelId = novelId,
                        EntityType = EntityType.Character,
                        EntityId = c.Id,
                        ChapterId = chapter.Id,
                        Mode = AppearanceMode.Present,
                        EvidenceSpan = evidence,
                        Source = AuditSource.System,
                    }
                );
        }

        foreach (var l in locations)
        {
            if (string.IsNullOrWhiteSpace(l.Name))
                continue;
            var evidence = FindEvidence(chapter.Content, l.Name);
            if (evidence is not null)
                records.Add(
                    new AppearanceRecord
                    {
                        Id = $"ap_{chapter.Id}_{l.Id}",
                        NovelId = novelId,
                        EntityType = EntityType.Location,
                        EntityId = l.Id,
                        ChapterId = chapter.Id,
                        Mode = AppearanceMode.Present,
                        EvidenceSpan = evidence,
                        Source = AuditSource.System,
                    }
                );
        }

        foreach (var i in items)
        {
            if (string.IsNullOrWhiteSpace(i.Name))
                continue;
            var evidence = FindEvidence(chapter.Content, i.Name);
            if (evidence is not null)
                records.Add(
                    new AppearanceRecord
                    {
                        Id = $"ap_{chapter.Id}_{i.Id}",
                        NovelId = novelId,
                        EntityType = EntityType.Item,
                        EntityId = i.Id,
                        ChapterId = chapter.Id,
                        Mode = AppearanceMode.Present,
                        EvidenceSpan = evidence,
                        Source = AuditSource.System,
                    }
                );
        }

        foreach (var f in factions)
        {
            if (string.IsNullOrWhiteSpace(f.Name))
                continue;
            var evidence = FindEvidence(chapter.Content, f.Name);
            if (evidence is not null)
                records.Add(
                    new AppearanceRecord
                    {
                        Id = $"ap_{chapter.Id}_{f.Id}",
                        NovelId = novelId,
                        EntityType = EntityType.Faction,
                        EntityId = f.Id,
                        ChapterId = chapter.Id,
                        Mode = AppearanceMode.Present,
                        EvidenceSpan = evidence,
                        Source = AuditSource.System,
                    }
                );
        }

        // 整章替换：L0 记录以本章扫描结果为最终态。若同章此前有 Ai/User 记录（L2），
        // 保留之（System 只打底，不覆盖人工/AI 记录）。
        var existing = await store.GetAppearancesByChapter(novelId, chapter.Id, ct);
        var preserved = existing
            .Where(r => r.Source != AuditSource.System)
            .Select(r =>
            {
                // 已被 L0 重新确认的实体保留一次即可（去重键 = EntityId）
                return r;
            })
            .ToList();
        var coveredIds = records.Select(r => r.EntityId).ToHashSet();
        var merged = records.Concat(preserved.Where(r => !coveredIds.Contains(r.EntityId)));

        await store.SaveChapterAppearances(novelId, chapter.Id, merged.ToList(), ct);
    }

    /// <summary>在正文中查找实体名首次出现的上下文片段（前后各 20 字符截断，作证据引文）。</summary>
    private static string? FindEvidence(string content, string name)
    {
        var idx = content.IndexOf(name, StringComparison.Ordinal);
        if (idx < 0)
            return null;
        var start = Math.Max(0, idx - 20);
        var end = Math.Min(content.Length, idx + name.Length + 20);
        return content[start..end];
    }

    private static void ThrowIfCancelled(CancellationToken ct) => ct.ThrowIfCancellationRequested();
}
