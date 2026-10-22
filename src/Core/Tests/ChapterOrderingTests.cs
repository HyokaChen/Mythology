using Core.Data;
using Xunit;

namespace Core.Tests;

/// <summary>
/// ChapterOrdering 测试：大纲「卷 → 章」结构操作用的纯逻辑（排序 / 新建序号 / 同组判定 /
/// 换序相邻计算 / 移动到卷后的序号）。两端 UI（TUI + Desktop）共用，故在 Core 侧覆盖边界。
/// </summary>
public class ChapterOrderingTests
{
    private static Chapter Ch(string id, int order, string? volumeId = null, string title = "") =>
        new()
        {
            Id = id,
            Order = order,
            VolumeId = volumeId,
            Title = title,
            NovelId = "n1",
        };

    private static Volume Vol(string id, int order, string name = "") =>
        new()
        {
            Id = id,
            Order = order,
            Name = name,
            NovelId = "n1",
        };

    [Fact]
    public void NextChapterOrder_Empty_ReturnsOne()
    {
        Assert.Equal(1, ChapterOrdering.NextChapterOrder([]));
    }

    [Fact]
    public void NextChapterOrder_ReturnsMaxPlusOne()
    {
        var all = new List<Chapter> { Ch("a", 3), Ch("b", 7), Ch("c", 1) };

        Assert.Equal(8, ChapterOrdering.NextChapterOrder(all));
    }

    [Fact]
    public void NextVolumeOrder_Empty_ReturnsOne()
    {
        Assert.Equal(1, ChapterOrdering.NextVolumeOrder([]));
    }

    [Fact]
    public void NextVolumeOrder_ReturnsMaxPlusOne()
    {
        var all = new List<Volume> { Vol("v1", 1), Vol("v2", 2) };

        Assert.Equal(3, ChapterOrdering.NextVolumeOrder(all));
    }

    [Fact]
    public void SameGroup_NullAndEmptyVolumeId_AreSameGroup()
    {
        Assert.True(ChapterOrdering.SameGroup(Ch("a", 1, null), Ch("b", 2, "")));
    }

    [Fact]
    public void SameGroup_DifferentVolumes_AreDifferent()
    {
        Assert.False(ChapterOrdering.SameGroup(Ch("a", 1, "v1"), Ch("b", 2, "v2")));
    }

    [Fact]
    public void SortChapters_TieOnOrder_BreaksByTitle()
    {
        // Ordinal 比较（确定性优先，非语言序）：A 码点小于 B → A 在前。
        var all = new List<Chapter> { Ch("b", 1, null, "B章"), Ch("a", 1, null, "A章") };

        var sorted = ChapterOrdering.SortChapters(all);

        Assert.Equal(["a", "b"], sorted.Select(c => c.Id));
    }

    [Fact]
    public void ReorderSwap_FirstInGroupMovingUp_ReturnsNull()
    {
        var all = new List<Chapter> { Ch("a", 1, "v1"), Ch("b", 2, "v1") };

        Assert.Null(ChapterOrdering.ReorderSwap(all, "a", -1));
    }

    [Fact]
    public void ReorderSwap_LastInGroupMovingDown_ReturnsNull()
    {
        var all = new List<Chapter> { Ch("a", 1, "v1"), Ch("b", 2, "v1") };

        Assert.Null(ChapterOrdering.ReorderSwap(all, "b", +1));
    }

    [Fact]
    public void ReorderSwap_Middle_ReturnsAdjacentPair()
    {
        var all = new List<Chapter> { Ch("a", 1, "v1"), Ch("b", 2, "v1"), Ch("c", 3, "v1") };

        var swap = ChapterOrdering.ReorderSwap(all, "b", -1);

        Assert.NotNull(swap);
        Assert.Equal("b", swap!.Value.First.Id);
        Assert.Equal("a", swap.Value.Second.Id);
    }

    [Fact]
    public void ReorderSwap_DoesNotCrossVolumes()
    {
        // v1 只有 a、b；c 在 v2。b 下移不能与 c 交换 → 组末 → null。
        var all = new List<Chapter> { Ch("a", 1, "v1"), Ch("b", 2, "v1"), Ch("c", 3, "v2") };

        Assert.Null(ChapterOrdering.ReorderSwap(all, "b", +1));
    }

    [Fact]
    public void ReorderSwap_UnassignedGroup_IsItsOwnGroup()
    {
        var all = new List<Chapter> { Ch("a", 1, "v1"), Ch("b", 5, null), Ch("c", 6, "") };

        var swap = ChapterOrdering.ReorderSwap(all, "c", -1);

        Assert.NotNull(swap);
        Assert.Equal("c", swap!.Value.First.Id);
        Assert.Equal("b", swap.Value.Second.Id);
    }

    [Fact]
    public void ReorderSwap_UnknownChapter_ReturnsNull()
    {
        Assert.Null(ChapterOrdering.ReorderSwap([Ch("a", 1, "v1")], "missing", -1));
    }

    [Fact]
    public void ReorderSwap_InvalidDelta_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ChapterOrdering.ReorderSwap([Ch("a", 1, "v1")], "a", 2)
        );
    }

    // ── PlanInsert：新建章「插入到某章之后 / 某卷末尾」的序号规划 ──

    [Fact]
    public void PlanInsert_AfterChapterWithGap_TakesMidpointWithoutReorder()
    {
        var all = new List<Chapter> { Ch("a", 1, "v1"), Ch("b", 5, "v1") };

        var plan = ChapterOrdering.PlanInsert(all, "a", null);

        Assert.Equal(3, plan.Order);
        Assert.False(plan.RequiresReorder);
    }

    [Fact]
    public void PlanInsert_AfterChapterAdjacentToSuccessor_RequiresReorder()
    {
        // 组内后继紧邻（4 与 5）→ 中点仍等于 4，会撞上本章 → 必须重排
        var all = new List<Chapter> { Ch("a", 4, "v1"), Ch("b", 5, "v1") };

        var plan = ChapterOrdering.PlanInsert(all, "a", null);

        Assert.True(plan.RequiresReorder);
    }

    [Fact]
    public void PlanInsert_AfterLastChapter_AppendsToBookEnd()
    {
        var all = new List<Chapter> { Ch("a", 1, "v1"), Ch("b", 2, "v1"), Ch("c", 3, "v2") };

        var plan = ChapterOrdering.PlanInsert(all, "c", null);

        Assert.Equal(4, plan.Order);
        Assert.False(plan.RequiresReorder);
    }

    [Fact]
    public void PlanInsert_AtVolumeTail_WhenNextOrderBelongsToOtherVolume_RequiresReorder()
    {
        // 第 1 卷尾是 2，而 3 属于第 2 卷 → 「插到第 1 卷末尾」不能取 3
        var all = new List<Chapter> { Ch("a", 1, "v1"), Ch("b", 2, "v1"), Ch("c", 3, "v2") };

        var plan = ChapterOrdering.PlanInsert(all, null, "v1");

        Assert.True(plan.RequiresReorder);
    }

    [Fact]
    public void PlanInsert_AtLastVolumeTail_AppendsWithoutReorder()
    {
        var all = new List<Chapter> { Ch("a", 1, "v1"), Ch("b", 2, "v2") };

        var plan = ChapterOrdering.PlanInsert(all, null, "v2");

        Assert.Equal(3, plan.Order);
        Assert.False(plan.RequiresReorder);
    }

    [Fact]
    public void PlanInsert_EmptyBook_ReturnsOne()
    {
        var plan = ChapterOrdering.PlanInsert([], null, "v1");

        Assert.Equal(1, plan.Order);
        Assert.False(plan.RequiresReorder);
    }

    [Fact]
    public void PlanInsert_EmptyVolume_AppendsToBookEnd()
    {
        var all = new List<Chapter> { Ch("a", 1, "v1") };

        var plan = ChapterOrdering.PlanInsert(all, null, "v9");

        Assert.Equal(2, plan.Order);
        Assert.False(plan.RequiresReorder);
    }

    // ── PlanFullReorder：连续重排 + 哨兵预留号 ──

    [Fact]
    public void PlanFullReorder_ProducesContiguousOrdersAndReservesSlot()
    {
        var all = new List<Chapter> { Ch("a", 1, "v1"), Ch("b", 2, "v1"), Ch("c", 7, "v2") };

        var plan = ChapterOrdering.PlanFullReorder(all, "b", null);

        // b 之后立刻给哨兵留号 → a=1, b=2, insert=3, c=4
        Assert.Equal(
            new (string, int)[] { ("a", 1), ("b", 2), (ChapterOrdering.InsertSlotId, 3), ("c", 4) },
            plan.Select(a => (a.ChapterId, a.Order)).ToArray()
        );
    }

    [Fact]
    public void PlanFullReorder_AtGroupEnd_ReservesSlotAfterGroup()
    {
        var all = new List<Chapter> { Ch("a", 1, "v1"), Ch("b", 2, "v1"), Ch("c", 3, "v2") };

        var plan = ChapterOrdering.PlanFullReorder(all, null, "v1");

        Assert.Equal(
            new (string, int)[] { ("a", 1), ("b", 2), (ChapterOrdering.InsertSlotId, 3), ("c", 4) },
            plan.Select(a => (a.ChapterId, a.Order)).ToArray()
        );
    }

    [Fact]
    public void PlanFullReorder_StaleAnchor_FallsBackToBookEndSlot()
    {
        var all = new List<Chapter> { Ch("a", 1, "v1") };

        var plan = ChapterOrdering.PlanFullReorder(all, "deleted", null);

        Assert.Equal(
            new (string, int)[] { ("a", 1), (ChapterOrdering.InsertSlotId, 2) },
            plan.Select(a => (a.ChapterId, a.Order)).ToArray()
        );
    }

    [Fact]
    public void PlanFullReorder_PreservesGroupAndRelativeOrder()
    {
        var all = new List<Chapter>
        {
            Ch("a", 5, "v2"),
            Ch("b", 1, "v1"),
            Ch("c", 3, "v1"),
            Ch("d", 9, "v2"),
        };

        var plan = ChapterOrdering.PlanFullReorder(all, null, null);

        // 分组顺序按各组最小 Order（v1=1 在前）；组内保持原相对序
        Assert.Equal(
            new (string, int)[]
            {
                ("b", 1),
                ("c", 2),
                ("a", 3),
                ("d", 4),
                (ChapterOrdering.InsertSlotId, 5),
            },
            plan.Select(a => (a.ChapterId, a.Order)).ToArray()
        );
    }

    [Fact]
    public void ReorderVolumeSwap_Middle_ReturnsAdjacentPair()
    {
        var all = new List<Volume> { Vol("v1", 1), Vol("v2", 2), Vol("v3", 3) };

        var swap = ChapterOrdering.ReorderVolumeSwap(all, "v2", +1);

        Assert.NotNull(swap);
        Assert.Equal("v2", swap!.Value.First.Id);
        Assert.Equal("v3", swap.Value.Second.Id);
    }

    [Fact]
    public void ReorderVolumeSwap_FirstAndLast_ReturnNull()
    {
        var all = new List<Volume> { Vol("v1", 1), Vol("v2", 2) };

        Assert.Null(ChapterOrdering.ReorderVolumeSwap(all, "v1", -1));
        Assert.Null(ChapterOrdering.ReorderVolumeSwap(all, "v2", +1));
    }

    [Fact]
    public void OrderForVolumeChange_ReturnsGlobalMaxPlusOne()
    {
        // Order 同时是章节文件名 → 必须全局唯一（不能只取目标组 max+1，否则撞其它卷的路径）。
        var all = new List<Chapter> { Ch("a", 4, "v2"), Ch("b", 9, "v2"), Ch("c", 20, "v1") };

        Assert.Equal(21, ChapterOrdering.OrderForVolumeChange(all, "v2"));
    }

    [Fact]
    public void OrderForVolumeChange_EmptyTargetVolume_StillGlobalMaxPlusOne()
    {
        var all = new List<Chapter> { Ch("a", 1, "v1"), Ch("b", 7, "v1") };

        Assert.Equal(8, ChapterOrdering.OrderForVolumeChange(all, "v2"));
    }

    [Fact]
    public void OrderForVolumeChange_EmptyNovel_ReturnsOne()
    {
        Assert.Equal(1, ChapterOrdering.OrderForVolumeChange([], "v1"));
        Assert.Equal(1, ChapterOrdering.OrderForVolumeChange([], null));
    }

    [Fact]
    public void SortVolumes_TieOnOrder_BreaksByName()
    {
        var all = new List<Volume> { Vol("b", 1, "B卷"), Vol("a", 1, "A卷") };

        var sorted = ChapterOrdering.SortVolumes(all);

        Assert.Equal(["a", "b"], sorted.Select(v => v.Id));
    }
}
