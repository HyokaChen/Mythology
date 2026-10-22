namespace Core.Data;

/// <summary>
/// 章节 / 分卷的排序与结构操作（纯函数，UI-agnostic，可单测）。
///
/// 规则：
/// - <see cref="Chapter.Order"/> 是小说内全局序号，<see cref="Volume.Order"/> 是卷序；
///   分组靠 <see cref="Chapter.VolumeId"/>（null 与 "" 同为「未分卷」）。
/// - 同组内排序：Order 升序，并列按标题（序号化，避免历史数据并列时抖动）。
/// - 换序只交换相邻两项的 Order（不做全局重排 → 允许出现空号）。
/// </summary>
public static class ChapterOrdering
{
    /// <summary>组归属键（null 与 "" 归一为未分卷）。</summary>
    public static string? GroupKey(string? volumeId) =>
        string.IsNullOrEmpty(volumeId) ? null : volumeId;

    /// <summary>章节排序：Order 升序，并列按标题（Ordinal）。</summary>
    public static List<Chapter> SortChapters(IEnumerable<Chapter> chapters) =>
        [.. chapters.OrderBy(c => c.Order).ThenBy(c => c.Title, StringComparer.Ordinal)];

    /// <summary>分卷排序：Order 升序，并列按卷名（Ordinal）。</summary>
    public static List<Volume> SortVolumes(IEnumerable<Volume> volumes) =>
        [.. volumes.OrderBy(v => v.Order).ThenBy(v => v.Name, StringComparer.Ordinal)];

    /// <summary>新建章节的下一个全局序号（max+1；空集 → 1）。</summary>
    public static int NextChapterOrder(IReadOnlyCollection<Chapter> all) =>
        all.Count == 0 ? 1 : all.Max(c => c.Order) + 1;

    /// <summary>新建分卷的下一个卷序（max+1；空集 → 1）。</summary>
    public static int NextVolumeOrder(IReadOnlyCollection<Volume> all) =>
        all.Count == 0 ? 1 : all.Max(v => v.Order) + 1;

    /// <summary>是否同组（未分卷自成一组）。</summary>
    public static bool SameGroup(Chapter a, Chapter b) =>
        GroupKey(a.VolumeId) == GroupKey(b.VolumeId);

    /// <summary>
    /// 上移 / 下移：返回需要交换 Order 的两章；组首上移、组末下移、目标章不存在 → null。
    /// </summary>
    /// <param name="delta">-1 上移 / +1 下移（其它值抛 <see cref="ArgumentOutOfRangeException"/>）。</param>
    public static (Chapter First, Chapter Second)? ReorderSwap(
        IReadOnlyCollection<Chapter> all,
        string chapterId,
        int delta
    )
    {
        if (delta is not (-1 or 1))
            throw new ArgumentOutOfRangeException(
                nameof(delta),
                delta,
                "只支持 -1（上移）/ +1（下移）"
            );

        var target = all.FirstOrDefault(c => c.Id == chapterId);
        if (target is null)
            return null;

        var siblings = SortChapters(all.Where(c => SameGroup(c, target)));
        var index = siblings.FindIndex(c => c.Id == chapterId);
        var neighbor = index + delta;
        if (index < 0 || neighbor < 0 || neighbor >= siblings.Count)
            return null;

        return (siblings[index], siblings[neighbor]);
    }

    /// <summary>分卷上移 / 下移：返回需要交换 Order 的两卷；首、末、目标卷不存在 → null。</summary>
    /// <param name="delta">-1 上移 / +1 下移（其它值抛 <see cref="ArgumentOutOfRangeException"/>）。</param>
    public static (Volume First, Volume Second)? ReorderVolumeSwap(
        IReadOnlyCollection<Volume> all,
        string volumeId,
        int delta
    )
    {
        if (delta is not (-1 or 1))
            throw new ArgumentOutOfRangeException(
                nameof(delta),
                delta,
                "只支持 -1（上移）/ +1（下移）"
            );

        var sorted = SortVolumes(all);
        var index = sorted.FindIndex(v => v.Id == volumeId);
        var neighbor = index + delta;
        if (index < 0 || neighbor < 0 || neighbor >= sorted.Count)
            return null;

        return (sorted[index], sorted[neighbor]);
    }

    /// <summary>
    /// 移动到目标卷后应取的序号：**全局** max+1（不是目标组 max+1）。
    /// Order 在 FileNovelStore 里同时是章节文件名（ch{Order:D3}.md），必须全局唯一，
    /// 否则会与其它卷的章节撞路径、互相覆盖；取全局最大值仍保证该章排在目标组末尾。
    /// </summary>
    public static int OrderForVolumeChange(IReadOnlyCollection<Chapter> all, string? targetVolumeId)
    {
        _ = targetVolumeId; // 组归属只影响渲染分组，序号必须全局唯一
        return NextChapterOrder(all);
    }

    /// <summary>
    /// 在 <paramref name="afterChapterId"/>（或目标组末尾）插入一章所需的序号规划。
    /// 纯函数，不触碰文件系统。
    /// </summary>
    /// <param name="afterChapterId">插入到该章之后；null / 空 / 未找到 → 追加到 <paramref name="targetVolumeId"/> 组末尾。</param>
    /// <param name="targetVolumeId">目标组。</param>
    public static ChapterInsertPlan PlanInsert(
        IReadOnlyCollection<Chapter> all,
        string? afterChapterId,
        string? targetVolumeId
    )
    {
        // ① 组内后继存在且中间有空号 → 取中点。不动任何既有文件，也不制造新的空号。
        // ② 该组已到全书末位 → 追加到全书末尾（Order 全局唯一，只能取 max+1）。
        // ③ 组末 +1 已被别的组占用（或与后继相邻无空号）→ 必须先全章重排才能原地插入。
        if (afterChapterId is { Length: > 0 })
        {
            var after = all.FirstOrDefault(c => c.Id == afterChapterId);
            if (after is not null)
            {
                var successor = SortChapters(
                        all.Where(c => SameGroup(c, after) && c.Order > after.Order)
                    )
                    .FirstOrDefault();
                if (successor is null)
                    return new ChapterInsertPlan(NextChapterOrder(all), false);

                var order = (after.Order + successor.Order) / 2;
                return new ChapterInsertPlan(order, order == after.Order);
            }
        }

        // 追加到目标组末尾：新章必须紧贴组末，且不能挤进别的组。
        var groupKey = GroupKey(targetVolumeId);
        var inGroup = all.Where(c => GroupKey(c.VolumeId) == groupKey).ToList();
        if (inGroup.Count == 0)
            return new ChapterInsertPlan(NextChapterOrder(all), false);

        var nextOrder = inGroup.Max(c => c.Order) + 1;
        var occupiedByOtherGroup = all.Any(c =>
            GroupKey(c.VolumeId) != groupKey && c.Order >= nextOrder
        );
        return new ChapterInsertPlan(nextOrder, occupiedByOtherGroup);
    }

    /// <summary>
    /// 无可用空号时的全章重排方案：按 (分组, 组内原序) 排好后重排为连续序号 1..N，
    /// 并用哨兵 <see cref="InsertSlotId"/> 为新章预留一个号。
    /// 分组顺序 = 各组最小 Order 的升序（与渲染时的分组顺序一致）。
    /// </summary>
    /// <returns>每行是一个 (章 id 或哨兵, 新序号)，序号连续且无空号。</returns>
    public static IReadOnlyList<ChapterOrderAssignment> PlanFullReorder(
        IReadOnlyCollection<Chapter> all,
        string? insertAfterChapterId,
        string? targetGroupKey
    )
    {
        var groups = all.GroupBy(c => GroupKey(c.VolumeId))
            .OrderBy(g => g.Min(c => c.Order))
            .ToList();

        var assignments = new List<ChapterOrderAssignment>(all.Count + 1);
        var next = 1;
        var reserved = false;

        var insertAfterOrder = insertAfterChapterId is { Length: > 0 } anchor
            ? all.FirstOrDefault(c => c.Id == anchor)?.Order
            : null;
        // 插入点在目标组末尾：只在指定章不存在（或未给）时才按组尾处理。
        var insertAtGroupEnd = insertAfterOrder is null && targetGroupKey is not null;

        foreach (var group in groups)
        {
            foreach (var chapter in SortChapters(group))
            {
                assignments.Add(new ChapterOrderAssignment(chapter.Id, next++));
                if (insertAfterOrder is { } after && chapter.Order == after)
                {
                    assignments.Add(new ChapterOrderAssignment(InsertSlotId, next++));
                    reserved = true;
                }
            }

            if (insertAtGroupEnd && !reserved && GroupKey(group.Key) == GroupKey(targetGroupKey))
            {
                assignments.Add(new ChapterOrderAssignment(InsertSlotId, next++));
                reserved = true;
            }
        }

        // 插入点已失效（章被删等）→ 预留号退到全书末尾，不丢号。
        if (!reserved)
            assignments.Add(new ChapterOrderAssignment(InsertSlotId, next++));
        return assignments;
    }

    /// <summary>
    /// <see cref="PlanFullReorder"/> 结果里代表「新章占位」的哨兵章 id。
    /// store 层据此判断该行是新章，从而创建它而不是搬移既有章。
    /// </summary>
    public const string InsertSlotId = "￿insert";
}

/// <summary>插入一章的序号规划结果。</summary>
/// <param name="Order">新章应取的全局序号。</param>
/// <param name="RequiresReorder">true = 该处无可用空号，必须先全章重排。</param>
public readonly record struct ChapterInsertPlan(int Order, bool RequiresReorder)
{
    /// <summary>无需重排即可写入（空号或末尾追加）。</summary>
    public static ChapterInsertPlan Direct(int order) => new(order, false);
}

/// <summary>全章重排中某一章的新序号。</summary>
public readonly record struct ChapterOrderAssignment(string ChapterId, int Order);
