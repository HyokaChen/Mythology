using Core.Agent;
using Core.Data;
using Core.Novels;
using Xunit;

namespace Core.Tests;

public sealed class CompactionScopeExtractorTests
{
    [Fact]
    public void ParseSelection_UsesLlmVolumeAndChapterSelection()
    {
        var structure = BuildStructure();
        const string json =
            """{"mode":"revise","instruction":"修正优化整个第二卷","volume_ids":["v2"],"chapter_orders":[21,22]}""";

        var result = CompactionScopeExtractor.ParseSelection(json, structure, null);

        Assert.True(result.Completed);
        Assert.NotNull(result.Scope);
        Assert.Equal(CompactionTaskMode.Revise, result.Scope!.Mode);
        Assert.Contains(21, result.Scope.ChapterOrders);
        Assert.Contains(22, result.Scope.ChapterOrders);
        Assert.DoesNotContain(54, result.Scope.ChapterOrders);
        Assert.Contains("第2卷《夏夜的萤火》", result.Scope.FormatBody());
    }

    [Fact]
    public void ParseSelection_NoneMeansNoScope()
    {
        var result = CompactionScopeExtractor.ParseSelection(
            """{"mode":"none"}""",
            BuildStructure(),
            "继续"
        );

        Assert.True(result.Completed);
        Assert.Null(result.Scope);
    }

    [Fact]
    public void ParseSelection_UnresolvableSelectionFallsBack()
    {
        var result = CompactionScopeExtractor.ParseSelection(
            """{"mode":"revise","volume_ids":[],"chapter_orders":[]}""",
            BuildStructure(),
            "修正优化整个第二卷"
        );

        // 半成品回答（认出任务却选不出卷/章）必须按解析失败处理，
        // 让上游走 TryCreate 兜底；mode=none 才表示真的没有任务。
        Assert.False(result.Completed);
        Assert.Null(result.Scope);
    }

    [Fact]
    public void ParseSelection_InvalidJsonFallsBack()
    {
        var result = CompactionScopeExtractor.ParseSelection("这不是 JSON", BuildStructure(), null);

        Assert.False(result.Completed);
        Assert.Null(result.Scope);
    }

    private static NovelStructure BuildStructure()
    {
        var novel = new Novel
        {
            Id = "n",
            NovelId = "n",
            Title = "测试",
            Genre = "玄幻",
            WritingStyle = "冷峻",
        };
        var volume2 = new Volume
        {
            Id = "v2",
            NovelId = "n",
            Name = "夏夜的萤火",
            Order = 2,
        };
        var volume3 = new Volume
        {
            Id = "v3",
            NovelId = "n",
            Name = "第三卷",
            Order = 3,
        };
        var chapters = new List<Chapter>();
        for (var order = 21; order <= 40; order++)
        {
            chapters.Add(
                new Chapter
                {
                    Id = $"c{order}",
                    NovelId = "n",
                    Title = $"萤火{order}",
                    Order = order,
                    VolumeId = "v2",
                }
            );
        }
        chapters.Add(
            new Chapter
            {
                Id = "c54",
                NovelId = "n",
                Title = "恶意的瞥见",
                Order = 54,
                VolumeId = "v3",
            }
        );
        return new NovelStructure(novel, [volume2, volume3], chapters, []);
    }
}
