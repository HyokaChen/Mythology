using Core.Data;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 章节改序落盘回归（FileNovelStore）：章节文件名由 <see cref="Chapter.Order"/> 决定
/// （ch{Order:D3}.md），因此交换 / 移动序号必须清理旧序文件——否则同一章留下两份文件
/// （读回来出现重复章），并可能覆盖其它章的正文（历史上已发生过）。
/// </summary>
public sealed class FileNovelStoreChapterReorderTests : IDisposable
{
    private const string NovelId = "reorder-novel";
    private readonly string _root;
    private readonly string _myth;
    private readonly FileNovelStore _store;

    public FileNovelStoreChapterReorderTests()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"chapter-reorder-{Guid.NewGuid():N}");
        _root = Path.Combine(tmp, "novels");
        _myth = Path.Combine(tmp, "mythology");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_myth);
        _store = new FileNovelStore(_root, _myth);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_root)!, true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    private async Task SeedAsync(params (string Id, int Order, string Title)[] chapters)
    {
        await _store.SaveNovel(new Novel { Id = NovelId, Title = "改序校验" });
        foreach (var (id, order, title) in chapters)
        {
            await _store.SaveChapter(
                new Chapter
                {
                    Id = id,
                    NovelId = NovelId,
                    Title = title,
                    Order = order,
                    Content = $"正文-{title}",
                }
            );
        }
    }

    /// <summary>章节目录由存储层布局决定 → 动态定位（不硬编码 novelId 目录名）。</summary>
    private int ChapterFileCount() =>
        Directory
            .GetDirectories(_root, "chapters", SearchOption.AllDirectories)
            .SelectMany(d => Directory.GetFiles(d, "*.md"))
            .Count();

    [Fact]
    public async Task SwapOrders_LeavesNoStaleFileAndKeepsContent()
    {
        await SeedAsync(("c1", 1, "开局"), ("c2", 2, "承接"), ("c3", 3, "转折"));

        var a = await _store.GetChapter(NovelId, "c1");
        var b = await _store.GetChapter(NovelId, "c2");
        (a!.Order, b!.Order) = (b.Order, a.Order);
        await _store.SaveChapter(a);
        await _store.SaveChapter(b);

        var all = await _store.GetChapters(NovelId);
        Assert.Equal(3, all.Count);
        Assert.Equal(3, all.Select(c => c.Id).Distinct().Count());
        Assert.Equal(2, all.Single(c => c.Id == "c1").Order);
        Assert.Equal(1, all.Single(c => c.Id == "c2").Order);
        Assert.Equal(3, ChapterFileCount());
        // 正文随章走（换序不得丢内容）
        Assert.Equal("正文-开局", all.Single(c => c.Id == "c1").Content);
        Assert.Equal("正文-承接", all.Single(c => c.Id == "c2").Content);
    }

    [Fact]
    public async Task MoveOrderBeyondExistingRange_DoesNotOrphanOrClobber()
    {
        await SeedAsync(("c1", 1, "开局"), ("c2", 2, "承接"), ("c3", 3, "转折"));

        // 移到全局 max+1（移动到卷的序号规则）：不得覆盖 ch001/ch002/ch003，也不得留下 ch003 旧文件
        var c3 = await _store.GetChapter(NovelId, "c3");
        c3!.Order = ChapterOrdering.NextChapterOrder(await _store.GetChapters(NovelId));
        await _store.SaveChapter(c3);

        var all = await _store.GetChapters(NovelId);
        Assert.Equal(3, all.Count);
        Assert.Equal(3, ChapterFileCount());
        Assert.Equal(4, all.Single(c => c.Id == "c3").Order);
        Assert.Equal("正文-开局", all.Single(c => c.Id == "c1").Content);
        Assert.Equal("正文-承接", all.Single(c => c.Id == "c2").Content);
        Assert.Equal("正文-转折", all.Single(c => c.Id == "c3").Content);
    }

    [Fact]
    public async Task RepeatedSaves_KeepSingleFile()
    {
        await SeedAsync(("c1", 1, "开局"));

        var chapter = await _store.GetChapter(NovelId, "c1");
        for (var order = 2; order <= 5; order++)
        {
            chapter!.Order = order;
            await _store.SaveChapter(chapter);
        }

        Assert.Equal(1, ChapterFileCount());
        var all = await _store.GetChapters(NovelId);
        Assert.Single(all);
        Assert.Equal(5, all[0].Order);
    }

    [Fact]
    public async Task ReorderAllChapters_Permutation_KeepsEveryChapterOnceWithOwnContent()
    {
        await SeedAsync(("c1", 1, "开局"), ("c2", 2, "承接"), ("c3", 3, "转折"));

        // 刻意做「就地插入」式重排：c3 → 1，会与 c1/c2 互换位置（置换式改写最易互相覆盖）
        await _store.ReorderAllChapters(
            NovelId,
            [
                new ChapterOrderAssignment("c3", 1),
                new ChapterOrderAssignment("c1", 2),
                new ChapterOrderAssignment("c2", 3),
            ]
        );

        var all = await _store.GetChapters(NovelId);
        Assert.Equal(3, all.Count);
        Assert.Equal(3, all.Select(c => c.Id).Distinct().Count());
        Assert.Equal(3, ChapterFileCount()); // 临时位必须已清理，不能留残file
        Assert.Equal(1, all.Single(c => c.Id == "c3").Order);
        Assert.Equal(2, all.Single(c => c.Id == "c1").Order);
        Assert.Equal(3, all.Single(c => c.Id == "c2").Order);
        // 正文逐章对应：置换不得把 A 的正文写到 B 的文件里
        Assert.Equal("正文-开局", all.Single(c => c.Id == "c1").Content);
        Assert.Equal("正文-承接", all.Single(c => c.Id == "c2").Content);
        Assert.Equal("正文-转折", all.Single(c => c.Id == "c3").Content);
    }

    [Fact]
    public async Task ReorderAllChapters_MigratesEvolutionShardsToNewOrder()
    {
        await SeedAsync(("c1", 1, "开局"), ("c2", 2, "承接"));
        await _store.SaveChapterAppearances(
            NovelId,
            "c1",
            [
                new AppearanceRecord
                {
                    Id = "ap1",
                    NovelId = NovelId,
                    ChapterId = "c1",
                    EntityId = "e1",
                    EntityType = EntityType.Character,
                },
            ]
        );

        await _store.ReorderAllChapters(
            NovelId,
            [new ChapterOrderAssignment("c1", 2), new ChapterOrderAssignment("c2", 1)]
        );

        // 分片按 id → 新 Order 解析：换序后必须还能读回 c1 的出场记录，且不再留在旧位
        var appearances = await _store.GetAppearancesByChapter(NovelId, "c1");
        Assert.Single(appearances);
        Assert.Equal("e1", appearances[0].EntityId);

        var shardDir = Directory
            .GetDirectories(_root, "appearances", SearchOption.AllDirectories)
            .First();
        Assert.Equal(
            ["ch002.yaml"],
            Directory.GetFiles(shardDir, "*.yaml").Select(Path.GetFileName).ToArray()
        );
    }

    [Fact]
    public async Task ReorderAllChapters_PreservesCreatedAt()
    {
        await SeedAsync(("c1", 1, "开局"), ("c2", 2, "承接"));
        var before = await _store.GetChapter(NovelId, "c1");
        await Task.Delay(5);

        await _store.ReorderAllChapters(
            NovelId,
            [new ChapterOrderAssignment("c1", 2), new ChapterOrderAssignment("c2", 1)]
        );

        var after = await _store.GetChapter(NovelId, "c1");
        Assert.Equal(before!.CreatedAt, after!.CreatedAt);
    }

    [Fact]
    public async Task ReorderAllChapters_InsertAfterPlan_LeavesSlotFreeForNewChapter()
    {
        await SeedAsync(("c1", 1, "开局"), ("c2", 2, "承接"), ("c3", 3, "转折"));

        // 「插到 c1 之后」在无空号时 → 先重排腾位，再由调用方写入新章
        var all = await _store.GetChapters(NovelId);
        var plan = ChapterOrdering.PlanInsert(all, "c1", null);
        Assert.True(plan.RequiresReorder);

        var assignments = ChapterOrdering.PlanFullReorder(all, "c1", null);
        await _store.ReorderAllChapters(NovelId, assignments);

        var slot = assignments.First(a => a.ChapterId == ChapterOrdering.InsertSlotId).Order;
        await _store.SaveChapter(
            new Chapter
            {
                Id = "new",
                NovelId = NovelId,
                Title = "新章",
                Order = slot,
                Content = "正文-新章",
            }
        );

        var after = ChapterOrdering.SortChapters(await _store.GetChapters(NovelId));
        Assert.Equal(["c1", "new", "c2", "c3"], after.Select(c => c.Id).ToArray());
        Assert.Equal(4, ChapterFileCount());
        Assert.Equal("正文-新章", after.Single(c => c.Id == "new").Content);
        Assert.Equal("正文-承接", after.Single(c => c.Id == "c2").Content);
    }
}
