using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Core.Data;

namespace Plugins.NovelTools.Tools;

/// <summary>
/// 候选目录条目 — 提及匹配的实体（角色/组织/地点/道具/情节线/场景）。
/// 非角色实体同样参与占用区间（避免「青云宗」里的「青云」被当成角色），
/// 但只有本体允许的类型对才会成为候选。
/// </summary>
public sealed record CatalogEntry(string EntityId, EntityType EntityType, string Name);

/// <summary>类型限定的实体引用（无序对去重与既有关系排除的键）。</summary>
public sealed record EntityRef(EntityType Type, string Id);

/// <summary>
/// 关系候选对（同句封锁 + 域/值域过滤产物，含证据定位）。
/// <see cref="SourceEntityId"/> 为句内先出现者（决定问题方向）。
/// </summary>
public sealed record RelationCandidate(
    string CandidateId,
    string ChapterId,
    int ChapterOrder,
    string SourceEntityId,
    EntityType SourceEntityType,
    string SourceName,
    string TargetEntityId,
    EntityType TargetEntityType,
    string TargetName,
    string EvidenceText,
    int EvidenceStart,
    int EvidenceEnd
);

/// <summary>
/// 关系提案管线（提炼自 jevgraph <c>extract.py</c>）：
/// 全实体提及（最长优先 + 占用区间去重）→ 同句封锁（邻域上限 + 本体域/值域门 + 无序对去重取最近）
/// → 稳定 ID（novel/chapter/类型化端点/span 哈希）+ 证据原句与字符 span。
/// 纯函数、无 DI：可独立测试。
/// </summary>
public static class RelationshipScanPipeline
{
    /// <summary>中文句切分：句末标点/换行/省略号收尾。</summary>
    private static readonly Regex SentenceRe = new(
        @"[^。！？；…\n]+[。！？；…]*",
        RegexOptions.Compiled
    );

    /// <summary>扫描章节（按 Order 升序）并返回候选对（上限 <paramref name="maxCandidates"/>）。</summary>
    public static List<RelationCandidate> Find(
        string novelId,
        IReadOnlyList<Chapter> chapters,
        IReadOnlyList<CatalogEntry> catalog,
        IReadOnlySet<(EntityRef A, EntityRef B)> existingPairs,
        int maxNeighbors,
        int maxCandidates
    )
    {
        if (maxCandidates <= 0 || maxNeighbors <= 0)
            return [];

        // 实体薄：名称长度 ≥ 2（单字名误报率过高）；最长优先（「李寒山」先于「李寒」占用区间），
        // 同名跨类型时按（名称, 类型, Id）稳定排序，保证结果可复现
        var entries = catalog
            .Where(entry => entry.Name.Length >= 2)
            .OrderByDescending(entry => entry.Name.Length)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ThenBy(entry => entry.EntityType)
            .ThenBy(entry => entry.EntityId, StringComparer.Ordinal)
            .ToList();
        if (entries.Count < 2)
            return [];

        // 全篇按无序对去重：同对只保留距离最近的一次出现（跨句/跨章，稳定 ID 随证据 span 变化）
        var best =
            new Dictionary<
                (EntityRef A, EntityRef B),
                (
                    int Distance,
                    string ChapterId,
                    int ChapterOrder,
                    int Start,
                    int End,
                    Mention A,
                    Mention B,
                    string Evidence
                )
            >();

        foreach (var chapter in chapters.OrderBy(c => c.Order))
        {
            if (string.IsNullOrEmpty(chapter.Content))
                continue;

            foreach (Match sentence in SentenceRe.Matches(chapter.Content))
            {
                if (string.IsNullOrWhiteSpace(sentence.Value))
                    continue;

                var mentions = FindMentions(sentence.Value, entries);
                if (mentions.Count < 2)
                    continue;

                for (var i = 0; i < mentions.Count; i++)
                {
                    var source = mentions[i];
                    var neighbors = mentions
                        .Where((m, j) => j != i && m.Ref != source.Ref)
                        .OrderBy(m => Math.Abs(m.Start - source.Start))
                        .Take(maxNeighbors);

                    foreach (var target in neighbors)
                    {
                        var (a, b) =
                            source.Start <= target.Start ? (source, target) : (target, source);
                        // 域/值域门（本体）：没有关系允许该（源类型, 目标类型）→ 不成为候选
                        if (!RelationshipOntology.Allows(a.EntityType, b.EntityType))
                            continue;

                        var key = PairKey(a.Ref, b.Ref);
                        var distance = Math.Abs(target.Start - source.Start);
                        if (!best.TryGetValue(key, out var current) || distance < current.Distance)
                        {
                            best[key] = (
                                distance,
                                chapter.Id,
                                chapter.Order,
                                sentence.Index,
                                sentence.Index + sentence.Length,
                                a,
                                b,
                                sentence.Value
                            );
                        }
                    }
                }
            }
        }

        return best.Where(kv => !existingPairs.Contains(kv.Key))
            .OrderBy(kv => kv.Value.ChapterOrder)
            .ThenBy(kv => kv.Value.Start)
            .Take(maxCandidates)
            .Select(kv => new RelationCandidate(
                StableId(
                    novelId,
                    kv.Value.ChapterId,
                    kv.Value.A.Ref,
                    kv.Value.B.Ref,
                    kv.Value.Start,
                    kv.Value.End
                ),
                kv.Value.ChapterId,
                kv.Value.ChapterOrder,
                kv.Value.A.EntityId,
                kv.Value.A.EntityType,
                kv.Value.A.Name,
                kv.Value.B.EntityId,
                kv.Value.B.EntityType,
                kv.Value.B.Name,
                kv.Value.Evidence,
                kv.Value.Start,
                kv.Value.End
            ))
            .ToList();
    }

    /// <summary>句内提及：最长名优先、占用区间去重（重叠的短名让位；跨实体类型同样互斥）。</summary>
    private static List<Mention> FindMentions(string sentence, List<CatalogEntry> entries)
    {
        var mentions = new List<Mention>();
        var occupied = new List<(int Start, int End)>();
        foreach (var entry in entries)
        {
            var searchFrom = 0;
            while (searchFrom < sentence.Length)
            {
                var index = sentence.IndexOf(entry.Name, searchFrom, StringComparison.Ordinal);
                if (index < 0)
                    break;

                var end = index + entry.Name.Length;
                var overlaps = occupied.Any(o => index < o.End && end > o.Start);
                if (!overlaps)
                {
                    mentions.Add(
                        new Mention(entry.EntityId, entry.EntityType, entry.Name, index, end)
                    );
                    occupied.Add((index, end));
                }
                searchFrom = index + 1;
            }
        }
        return mentions.OrderBy(m => m.Start).ToList();
    }

    /// <summary>无序对归一化（A/B 与 B/A 同一键）。</summary>
    public static (EntityRef A, EntityRef B) PairKey(EntityRef a, EntityRef b) =>
        Compare(a, b) <= 0 ? (a, b) : (b, a);

    private static int Compare(EntityRef a, EntityRef b)
    {
        var byId = string.CompareOrdinal(a.Id, b.Id);
        return byId != 0 ? byId : a.Type.CompareTo(b.Type);
    }

    private static string StableId(
        string novelId,
        string chapterId,
        EntityRef a,
        EntityRef b,
        int start,
        int end
    )
    {
        var payload = string.Concat(
            novelId,
            "\0",
            chapterId,
            "\0",
            a.Type.ToString(),
            ":",
            a.Id,
            "\0",
            b.Type.ToString(),
            ":",
            b.Id,
            "\0",
            start,
            "\0",
            end
        );
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return "rel_" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private sealed record Mention(
        string EntityId,
        EntityType EntityType,
        string Name,
        int Start,
        int End
    )
    {
        public EntityRef Ref => new(EntityType, EntityId);
    }
}
