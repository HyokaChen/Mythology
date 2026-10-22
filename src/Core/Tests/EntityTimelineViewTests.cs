using Core.Data;
using Core.Evolution;
using Xunit;

namespace Core.Tests;

/// <summary>
/// as-of 派生视图测试（entity-evolution-lifecycle §8 E3 验收）：
/// 出场曲线 / as-of 状态重放 / 合并时间线 / 缺席配角精确版。
/// 用 FileNovelStore（真实 YAML 往返）保证与存储层契约一致。
/// </summary>
[Collection("FileNovelStore")]
public sealed class EntityTimelineViewTests : IAsyncDisposable
{
    private readonly string _tempRoot;
    private readonly string _mythologyDir;
    private readonly FileNovelStore _store;

    public EntityTimelineViewTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"timeline-{Guid.NewGuid():N}");
        _mythologyDir = Path.Combine(_tempRoot, ".mythology");
        Directory.CreateDirectory(_tempRoot);
        _store = new FileNovelStore(_tempRoot, _mythologyDir);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_tempRoot, true);
        }
        catch { }
        return default;
    }

    private async Task<(string NovelId, Dictionary<string, int> OrderMap)> SeedNovelAsync()
    {
        var novel = new Novel { Id = "nov001", Title = "时间线测试" };
        await _store.SaveNovel(novel);
        var orderMap = new Dictionary<string, int>();
        for (var i = 1; i <= 5; i++)
        {
            var ch = new Chapter
            {
                Id = $"ch{i}",
                NovelId = novel.Id,
                Title = $"第{i}章",
                Order = i,
                Content = $"第{i}章正文",
            };
            await _store.SaveChapter(ch);
            orderMap[ch.Id] = i;
        }
        return (novel.Id, orderMap);
    }

    [Fact]
    public async Task GetAppearancesAcrossChapters_OrdersAndModes()
    {
        var (novelId, orderMap) = await SeedNovelAsync();
        await _store.SaveChapterAppearances(
            novelId,
            "ch1",
            [
                new()
                {
                    Id = "a1",
                    NovelId = novelId,
                    EntityType = EntityType.Character,
                    EntityId = "c1",
                    ChapterId = "ch1",
                    Mode = AppearanceMode.Present,
                    Source = AuditSource.System,
                },
            ]
        );
        await _store.SaveChapterAppearances(
            novelId,
            "ch3",
            [
                new()
                {
                    Id = "a2",
                    NovelId = novelId,
                    EntityType = EntityType.Character,
                    EntityId = "c1",
                    ChapterId = "ch3",
                    Mode = AppearanceMode.Mentioned,
                    Source = AuditSource.Ai,
                },
            ]
        );

        var view = await EntityTimelineView.BuildAsync(_store, novelId);
        var curve = view.GetAppearancesAcrossChapters(EntityType.Character, "c1", orderMap);

        Assert.Equal(2, curve.Count);
        Assert.Equal((1, AppearanceMode.Present), curve[0]);
        Assert.Equal((3, AppearanceMode.Mentioned), curve[1]);
    }

    [Fact]
    public async Task GetStateAsOf_ReplaysUpToChapter()
    {
        var (novelId, orderMap) = await SeedNovelAsync();
        // c1 关系演化：ch2 敌视 → ch4 信任
        await _store.SaveEntityStateChanges(
            novelId,
            "ch2",
            [
                new()
                {
                    Id = "e1",
                    NovelId = novelId,
                    TargetEntityType = EntityType.Character,
                    TargetId = "c1",
                    ChapterId = "ch2",
                    Aspect = EvolutionAspect.Relationship,
                    ToSummary = "敌视",
                    Summary = "结仇",
                    Source = AuditSource.Ai,
                },
            ]
        );
        await _store.SaveEntityStateChanges(
            novelId,
            "ch4",
            [
                new()
                {
                    Id = "e2",
                    NovelId = novelId,
                    TargetEntityType = EntityType.Character,
                    TargetId = "c1",
                    ChapterId = "ch4",
                    Aspect = EvolutionAspect.Relationship,
                    ToSummary = "信任",
                    Summary = "化敌为友",
                    Source = AuditSource.Ai,
                },
                new()
                {
                    Id = "e3",
                    NovelId = novelId,
                    TargetEntityType = EntityType.Character,
                    TargetId = "c1",
                    ChapterId = "ch4",
                    Aspect = EvolutionAspect.Status,
                    ToSummary = "Transformed",
                    Summary = "突破境界",
                    Source = AuditSource.Ai,
                },
            ]
        );

        var view = await EntityTimelineView.BuildAsync(_store, novelId);

        // ch3 时点：只看到 ch2 的敌视
        var asOf3 = view.GetStateAsOf(EntityType.Character, "c1", orderMap, 3);
        Assert.Equal("敌视", asOf3[EvolutionAspect.Relationship]);
        Assert.False(asOf3.ContainsKey(EvolutionAspect.Status));

        // ch5 时点：关系=信任（ch4 覆盖 ch2），Status=Transformed
        var asOf5 = view.GetStateAsOf(EntityType.Character, "c1", orderMap, 5);
        Assert.Equal("信任", asOf5[EvolutionAspect.Relationship]);
        Assert.Equal("Transformed", asOf5[EvolutionAspect.Status]);
    }

    [Fact]
    public async Task FindAbsentSupporting_RecordBased()
    {
        var (novelId, orderMap) = await SeedNovelAsync();
        // 冷月 ch1/ch2 出场后消失；青禾 ch1 出场、ch3 提及（仍在窗口）；阿星从未出场
        await _store.SaveChapterAppearances(
            novelId,
            "ch1",
            [
                new()
                {
                    Id = "a1",
                    NovelId = novelId,
                    EntityType = EntityType.Character,
                    EntityId = "cold",
                    ChapterId = "ch1",
                    Source = AuditSource.System,
                },
                new()
                {
                    Id = "a2",
                    NovelId = novelId,
                    EntityType = EntityType.Character,
                    EntityId = "qing",
                    ChapterId = "ch1",
                    Source = AuditSource.System,
                },
            ]
        );
        await _store.SaveChapterAppearances(
            novelId,
            "ch2",
            [
                new()
                {
                    Id = "a3",
                    NovelId = novelId,
                    EntityType = EntityType.Character,
                    EntityId = "cold",
                    ChapterId = "ch2",
                    Source = AuditSource.System,
                },
            ]
        );
        await _store.SaveChapterAppearances(
            novelId,
            "ch3",
            [
                new()
                {
                    Id = "a4",
                    NovelId = novelId,
                    EntityType = EntityType.Character,
                    EntityId = "qing",
                    ChapterId = "ch3",
                    Source = AuditSource.System,
                },
            ]
        );

        var view = await EntityTimelineView.BuildAsync(_store, novelId);
        var supporting = new List<Character>
        {
            new()
            {
                Id = "cold",
                Name = "冷月",
                Role = CharacterRole.Supporting,
            },
            new()
            {
                Id = "qing",
                Name = "青禾",
                Role = CharacterRole.Supporting,
            },
            new()
            {
                Id = "axing",
                Name = "阿星",
                Role = CharacterRole.Supporting,
            },
        };

        // 最近 2 章（ch4/ch5）：冷月近窗缺席（且早出现过）→ 命中；青禾 ch3 在窗口外也缺席 →
        // 早出现过 + 近窗无 → 也会命中，但取前 2；阿星从未出现 → 不猜（跳过）
        var absent = view.FindAbsentSupporting(supporting, orderMap, latestOrder: 5, window: 2);
        Assert.Contains("冷月", absent);
        Assert.Contains("青禾", absent);
        Assert.DoesNotContain("阿星", absent);
    }

    [Fact]
    public async Task FindAbsentSupporting_NoRecords_ReturnsEmpty()
    {
        var (novelId, orderMap) = await SeedNovelAsync();
        var view = await EntityTimelineView.BuildAsync(_store, novelId);
        var supporting = new List<Character>
        {
            new()
            {
                Id = "c1",
                Name = "冷月",
                Role = CharacterRole.Supporting,
            },
        };
        // 无任何出场记录 → 返回空（调用方回退扫描，不做猜测）
        var absent = view.FindAbsentSupporting(supporting, orderMap, latestOrder: 5, window: 2);
        Assert.Empty(absent);
    }
}
