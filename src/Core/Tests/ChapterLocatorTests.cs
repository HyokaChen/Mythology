using Core.Data;
using Core.Novels;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 章节定位与翻页（两宿主共用）：序号 → ID → 标题子串；歧义给候选；翻页边界返回 null。
/// </summary>
public sealed class ChapterLocatorTests
{
    private static List<Chapter> ThreeChapters() =>
        [
            new()
            {
                Id = "c1",
                NovelId = "n1",
                Title = "雨夜寄北",
                Order = 1,
            },
            new()
            {
                Id = "c2",
                NovelId = "n1",
                Title = "进城",
                Order = 2,
            },
            new()
            {
                Id = "c3",
                NovelId = "n1",
                Title = "雨夜再会",
                Order = 3,
            },
        ];

    [Fact]
    public void Resolve_ByOrderAndById()
    {
        Assert.Equal("c2", ChapterLocator.Resolve(ThreeChapters(), "2").Chapter!.Id);
        Assert.Equal("c2", ChapterLocator.Resolve(ThreeChapters(), "c2").Chapter!.Id);
        Assert.Equal("c2", ChapterLocator.Resolve(ThreeChapters(), " c2 ").Chapter!.Id);
    }

    [Fact]
    public void Resolve_ByTitleSubstring_UniqueMatch()
    {
        var result = ChapterLocator.Resolve(ThreeChapters(), "进城");

        Assert.Equal("c2", result.Chapter!.Id);
        Assert.Null(result.Message);
    }

    [Fact]
    public void Resolve_AmbiguousTitle_ListsCandidatesInsteadOfGuessing()
    {
        var result = ChapterLocator.Resolve(ThreeChapters(), "雨夜");

        Assert.Null(result.Chapter);
        Assert.Contains("匹配到 2 章", result.Message);
        Assert.Contains("第1章 雨夜寄北", result.Message);
        Assert.Contains("第3章 雨夜再会", result.Message);
    }

    [Fact]
    public void Resolve_MissingAndEmptyInput_GiveActionableMessages()
    {
        Assert.Contains("没有第 9 章", ChapterLocator.Resolve(ThreeChapters(), "9").Message);
        Assert.Contains("没有标题包含", ChapterLocator.Resolve(ThreeChapters(), "无此章").Message);
        Assert.Equal(
            ChapterLocator.UsageHint,
            ChapterLocator.Resolve(ThreeChapters(), null).Message
        );
        Assert.Contains("还没有章节", ChapterLocator.Resolve([], "1").Message);
    }

    // ── 阅读器翻页（←/→、n/p） ──

    [Fact]
    public void MoveBy_WalksChapters_AndStopsAtEdges()
    {
        var chapters = ThreeChapters();

        Assert.Equal("c2", ChapterLocator.MoveBy(chapters, "c1", 1)!.Id);
        Assert.Equal("c1", ChapterLocator.MoveBy(chapters, "c2", -1)!.Id);
        Assert.Null(ChapterLocator.MoveBy(chapters, "c1", -1)); // 第一章再往前
        Assert.Null(ChapterLocator.MoveBy(chapters, "c3", 1)); // 最后一章再往后
        Assert.Null(ChapterLocator.MoveBy(chapters, "missing", 1)); // 找不到当前位置
        Assert.Null(ChapterLocator.MoveBy([], "c1", 1));
    }

    [Fact]
    public void DescribePosition_ReportsIndexAndTotal()
    {
        var chapters = ThreeChapters();

        Assert.Equal("第 2 / 3 章", ChapterLocator.DescribePosition(chapters, "c2"));
        Assert.Equal("共 3 章", ChapterLocator.DescribePosition(chapters, "missing"));
        Assert.Equal("0 章", ChapterLocator.DescribePosition([], null));
    }
}
