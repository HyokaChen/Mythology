using Core.Agent;
using Core.Data;
using Core.Novels;
using Xunit;

namespace Core.Tests;

/// <summary>compact 摘要必须把作者任务范围当成硬约束，不能把全局写作前沿当成任务进度。</summary>
public sealed class CompactionTaskScopeTests
{
    private const string RevisionInstruction =
        "修正优化整个第二卷 分卷[ID:v2]:夏夜的萤火 的所有章节内容！！";

    [Fact]
    public void TryCreate_RevisionVolume_RejectsGlobalNext()
    {
        var scope = CompactionTaskScope.TryCreate([RevisionInstruction], BuildStructure());

        Assert.NotNull(scope);
        Assert.Equal(CompactionTaskMode.Revise, scope!.Mode);
        Assert.Contains(21, scope.ChapterOrders);
        Assert.Contains(40, scope.ChapterOrders);
        Assert.DoesNotContain(54, scope.ChapterOrders);
        Assert.False(scope.IsNextWithinScope("写第54章《皮皮龙的灵觉与恶意的瞥见》"));
        Assert.True(scope.IsNextWithinScope("继续优化第30章《萤火30》"));

        var safeNext = scope.BuildSafeNext();
        Assert.DoesNotContain("第54章", safeNext);
        Assert.Contains("第23章", safeNext);

        var section = scope.FormatSection();
        Assert.Contains("允许范围", section);
        Assert.Contains("第2卷《夏夜的萤火》", section);
        Assert.Contains("全局写作前沿", section);
    }

    [Fact]
    public void TryCreate_WriteChapter_AllowsOnlyThatChapter()
    {
        var scope = CompactionTaskScope.TryCreate(["写第54章"], BuildStructure());

        Assert.NotNull(scope);
        Assert.Equal(CompactionTaskMode.Write, scope!.Mode);
        Assert.Contains(54, scope.ChapterOrders);
        Assert.True(scope.IsNextWithinScope("写第54章《恶意的瞥见》"));
        Assert.False(scope.IsNextWithinScope("写第55章"));
        Assert.Contains("第54章", scope.BuildSafeNext());
    }

    [Fact]
    public void TryCreate_ContinuesPreviousRevisionScope()
    {
        var first = CompactionTaskScope.TryCreate([RevisionInstruction], BuildStructure());
        Assert.NotNull(first);
        var firstScope = first!;

        var second = CompactionTaskScope.TryCreate(
            ["继续"],
            BuildStructure(),
            firstScope.FormatBody()
        );

        Assert.NotNull(second);
        Assert.Equal(CompactionTaskMode.Revise, second!.Mode);
        Assert.Contains(21, second.ChapterOrders);
        Assert.Contains(40, second.ChapterOrders);
        Assert.Equal(firstScope.Instruction, second.Instruction);
        Assert.DoesNotContain("允许范围", second.Instruction);
    }

    [Fact]
    public void TryCreate_SkipsContinuationOnlyMessage()
    {
        var scope = CompactionTaskScope.TryCreate(["继续", RevisionInstruction], BuildStructure());

        Assert.NotNull(scope);
        Assert.Contains(21, scope!.ChapterOrders);
        Assert.Contains(40, scope.ChapterOrders);
        Assert.DoesNotContain(54, scope.ChapterOrders);
    }

    [Fact]
    public void EnforceTaskScope_RepairsOutOfScopeNext()
    {
        var scope = CompactionTaskScope.TryCreate([RevisionInstruction], BuildStructure())!;
        const string summary =
            "## Scope / 任务边界\n"
            + "- 未记录\n\n"
            + "## Next / 下一步\n"
            + "- 写第54章《皮皮龙的灵觉与恶意的瞥见》";

        var repaired = CompactionEngine.EnforceTaskScope(summary, scope, null);

        Assert.Contains("## Scope / 任务边界", repaired);
        Assert.Contains("允许范围", repaired);
        Assert.DoesNotContain("写第54章", repaired);
        Assert.Contains("优化/补写第23章", repaired);
    }

    [Fact]
    public void EnforceTaskScope_ReplacesWhereProgressAndNext()
    {
        var scope = CompactionTaskScope.TryCreate([RevisionInstruction], BuildStructure())!;
        var progress = CompactionTaskProgress.Create(
            [21, 22, 24, 30],
            [25],
            currentOrder: 30,
            nextOrder: 23,
            outOfScopeOrders: [54]
        );
        const string summary =
            "## Scope / 任务边界\n- 未记录\n\n"
            + "## Progress / 进度\n- 未记录\n\n"
            + "## Where / 写到哪里\n- 先推进第54章\n\n"
            + "## Next / 下一步\n- 写第54章《恶意的瞥见》";
        const string baseCheckpoint =
            "## Where / 写到哪里\n"
            + "- 当前章（任务焦点）：第30章《萤火30》\n"
            + "- 下一章（任务焦点）：第23章《萤火23》\n"
            + "- 全局下一章（不是本任务 Next）：第54章《恶意的瞥见》";

        var repaired = CompactionEngine.EnforceTaskScope(
            summary,
            scope,
            previousSummary: null,
            progress,
            baseCheckpoint
        );

        Assert.Contains("已实际编辑：21、22、24、30", repaired);
        Assert.Contains("当前：第30章", repaired);
        Assert.Contains("下一章：第23章", repaired);
        Assert.Contains("当前章（任务焦点）：第30章", repaired);
        var nextIndex = repaired.IndexOf("## Next / 下一步", StringComparison.Ordinal);
        Assert.True(nextIndex >= 0);
        Assert.DoesNotContain("写第54章", repaired[nextIndex..]);
        Assert.DoesNotContain("先推进第54章", repaired);
    }

    [Fact]
    public void EnforceTaskScope_PrefersPreviousInScopeNext()
    {
        var scope = CompactionTaskScope.TryCreate([RevisionInstruction], BuildStructure())!;
        const string summary =
            "## Scope / 任务边界\n"
            + "- 未记录\n\n"
            + "## Next / 下一步\n"
            + "- 写第54章《皮皮龙的灵觉与恶意的瞥见》";
        const string previous = "## Next / 下一步\n" + "- 继续优化第30章《萤火30》";

        var repaired = CompactionEngine.EnforceTaskScope(summary, scope, previous);

        Assert.Contains("继续优化第30章", repaired);
        Assert.DoesNotContain("第54章", repaired);
    }

    private static NovelStructure BuildStructure()
    {
        const string novelId = "n-scope";
        var novel = new Novel
        {
            Id = novelId,
            NovelId = novelId,
            Title = "Scope 测试",
        };
        var volume1 = new Volume
        {
            Id = "v1",
            NovelId = novelId,
            Name = "第一卷",
            Order = 1,
        };
        var volume2 = new Volume
        {
            Id = "v2",
            NovelId = novelId,
            Name = "夏夜的萤火",
            Order = 2,
        };
        var volume3 = new Volume
        {
            Id = "v3",
            NovelId = novelId,
            Name = "第三卷",
            Order = 3,
        };

        var chapters = new List<Chapter>();
        for (var order = 1; order <= 20; order++)
            chapters.Add(ChapterAt(novelId, volume1, order, $"卷一{order}", 100));
        for (var order = 21; order <= 40; order++)
            chapters.Add(ChapterAt(novelId, volume2, order, $"萤火{order}", order <= 22 ? 100 : 0));
        chapters.Add(ChapterAt(novelId, volume3, 53, "皮皮龙的灵觉", 500));
        chapters.Add(ChapterAt(novelId, volume3, 54, "恶意的瞥见", 0));

        return new NovelStructure(novel, [volume1, volume2, volume3], chapters, []);
    }

    private static Chapter ChapterAt(
        string novelId,
        Volume volume,
        int order,
        string title,
        int charCount
    ) =>
        new()
        {
            Id = $"{volume.Id}-c{order}",
            NovelId = novelId,
            Title = title,
            Order = order,
            VolumeId = volume.Id,
            CharCount = charCount,
        };
}
