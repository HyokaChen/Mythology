using Core.Data;

namespace Core.Evolution;

/// <summary>
/// as-of 派生视图（ENTITY-EVOLUTION §5）——「计算」要素的本地实现。
/// 内存构建：全量出场记录 + 变化事件按实体分组、按章序重放，不落盘（NKW 原则：
/// 派生视图每次现算，无失效同步问题）。
/// 数据量级：单部小说数千条，构建毫秒级。
/// </summary>
public sealed class EntityTimelineView
{
    private readonly List<AppearanceRecord> _appearances;
    private readonly List<EntityStateChange> _stateChanges;
    private readonly Dictionary<
        (EntityType Type, string Id),
        List<AppearanceRecord>
    > _appearanceIndex;
    private readonly Dictionary<(EntityType Type, string Id), List<EntityStateChange>> _changeIndex;
    private readonly Dictionary<string, List<AppearanceRecord>> _chapterAppearanceIndex;
    private readonly Dictionary<string, List<EntityStateChange>> _chapterChangeIndex;

    private EntityTimelineView(
        List<AppearanceRecord> appearances,
        List<EntityStateChange> stateChanges,
        Dictionary<(EntityType, string), List<AppearanceRecord>> appearanceIndex,
        Dictionary<(EntityType, string), List<EntityStateChange>> changeIndex
    )
    {
        _appearances = appearances;
        _stateChanges = stateChanges;
        _appearanceIndex = appearanceIndex;
        _changeIndex = changeIndex;
        _chapterAppearanceIndex = appearances
            .GroupBy(r => r.ChapterId)
            .ToDictionary(g => g.Key, g => g.ToList());
        _chapterChangeIndex = stateChanges
            .GroupBy(c => c.ChapterId)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>
    /// 从 store 全量构建视图。
    /// </summary>
    public static async Task<EntityTimelineView> BuildAsync(
        INovelStore store,
        string novelId,
        CancellationToken ct = default
    )
    {
        var appearances = await store.GetAppearances(novelId, ct);
        var changes = await store.GetEntityStateChanges(novelId, ct);

        var appearanceIndex = appearances
            .GroupBy(r => (r.EntityType, r.EntityId))
            .ToDictionary(g => g.Key, g => g.ToList());
        var changeIndex = changes
            .GroupBy(c => (c.TargetEntityType, c.TargetId))
            .ToDictionary(g => g.Key, g => g.ToList());

        return new EntityTimelineView(appearances, changes, appearanceIndex, changeIndex);
    }

    /// <summary>该小说全部出场记录（章序聚合前的原始列表）。</summary>
    public IReadOnlyList<AppearanceRecord> AllAppearances => _appearances;

    /// <summary>该小说全部变化事件。</summary>
    public IReadOnlyList<EntityStateChange> AllStateChanges => _stateChanges;

    /// <summary>本章全部出场记录（章节视图数据源；无记录返回空列表）。</summary>
    public List<AppearanceRecord> GetChapterAppearances(string chapterId) =>
        _chapterAppearanceIndex.TryGetValue(chapterId, out var records) ? records : [];

    /// <summary>本章全部变化事件（状态进展数据源；含待发布；无记录返回空列表）。</summary>
    public List<EntityStateChange> GetChapterStateChanges(string chapterId) =>
        _chapterChangeIndex.TryGetValue(chapterId, out var changes) ? changes : [];

    /// <summary>本章已生效变化（权威视图用；待发布不参与）。</summary>
    public List<EntityStateChange> GetChapterCommittedStateChanges(string chapterId) =>
        GetChapterStateChanges(chapterId)
            .Where(c => c.Lifecycle == ChangeLifecycle.Committed)
            .OrderBy(c => c.Sequence)
            .ToList();

    /// <summary>
    /// 出场频率曲线：实体出现过的章节 Order 序列（升序，含 mode）。
    /// 未出场实体返回空列表。
    /// </summary>
    public List<(int ChapterOrder, AppearanceMode Mode)> GetAppearancesAcrossChapters(
        EntityType entityType,
        string entityId,
        IReadOnlyDictionary<string, int> chapterIdToOrder
    )
    {
        if (!_appearanceIndex.TryGetValue((entityType, entityId), out var records))
            return [];
        return records
            .Where(r => chapterIdToOrder.TryGetValue(r.ChapterId, out _))
            .Select(r => (chapterIdToOrder[r.ChapterId], r.Mode))
            .OrderBy(t => t.Item1)
            .ToList();
    }

    /// <summary>
    /// as-of 状态查询：第 orderOf 章（含）时该实体的状态汇总——
    /// 重放全部 ≤ 该章的变化事件，返回每个 Aspect 的最新 ToSummary（NWM「取最新有效边」语义）。
    /// </summary>
    public Dictionary<EvolutionAspect, string?> GetStateAsOf(
        EntityType entityType,
        string entityId,
        IReadOnlyDictionary<string, int> chapterIdToOrder,
        int chapterOrder
    )
    {
        var result = new Dictionary<EvolutionAspect, string?>();
        if (!_changeIndex.TryGetValue((entityType, entityId), out var changes))
            return result;

        var committed = changes.Where(c => c.Lifecycle == ChangeLifecycle.Committed).ToList();

        // 叙事变化：按章序重放（每切面取最新）。
        var narrativeTime = new Dictionary<EvolutionAspect, DateTime>();
        foreach (
            var change in committed
                .Where(c =>
                    !string.IsNullOrEmpty(c.ChapterId)
                    && chapterIdToOrder.TryGetValue(c.ChapterId, out var o)
                    && o <= chapterOrder
                )
                .OrderBy(c => chapterIdToOrder[c.ChapterId])
                .ThenBy(c => c.CommittedAt ?? c.CreatedAt)
        )
        {
            result[change.Aspect] = change.ToSummary;
            narrativeTime[change.Aspect] = change.CommittedAt ?? change.CreatedAt;
        }

        // 设定变更（manual）：只有晚于该切面最新叙事变化时才覆盖。
        foreach (
            var manual in committed
                .Where(c => string.IsNullOrEmpty(c.ChapterId))
                .GroupBy(c => c.Aspect)
                .Select(g => g.OrderByDescending(c => c.CommittedAt ?? c.CreatedAt).First())
        )
        {
            var manualTime = manual.CommittedAt ?? manual.CreatedAt;
            if (!narrativeTime.TryGetValue(manual.Aspect, out var latest) || manualTime > latest)
            {
                result[manual.Aspect] = manual.ToSummary;
                narrativeTime[manual.Aspect] = manualTime;
            }
        }
        return result;
    }

    /// <summary>
    /// 近 window 章未出场的配角（精确版——取代 FindAbsentSupportingCandidates 的字符串扫描）。
    /// 无出场记录的实体不计（未启用 L0 打底的小说保持空结果，调用方回退扫描）。
    /// </summary>
    public List<string> FindAbsentSupporting(
        IReadOnlyList<Character> supportingCharacters,
        IReadOnlyDictionary<string, int> chapterIdToOrder,
        int latestOrder,
        int window
    )
    {
        if (window <= 0)
            return [];

        var windowStart = latestOrder - window + 1;
        var result = new List<string>();
        foreach (var c in supportingCharacters)
        {
            if (string.IsNullOrWhiteSpace(c.Name))
                continue;
            if (!_appearanceIndex.TryGetValue((EntityType.Character, c.Id), out var records))
                continue;

            var recentOrders = records
                .Where(r =>
                    chapterIdToOrder.TryGetValue(r.ChapterId, out var o) && o >= windowStart
                )
                .Select(r => chapterIdToOrder[r.ChapterId])
                .ToList();
            var appearedEarlier = records.Any(r =>
                chapterIdToOrder.TryGetValue(r.ChapterId, out var o) && o < windowStart
            );
            if (appearedEarlier && recentOrders.Count == 0)
                result.Add(c.Name);
            if (result.Count >= 2)
                break;
        }
        return result;
    }
}
