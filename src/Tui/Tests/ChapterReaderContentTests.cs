using Core.Data;
using Tui.Controls;
using Tui.Services;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// 章节阅读器（/read 与面板 📖）的两块纯逻辑：
/// ① 内容装配（细纲带 + 正文，缺失时给可行动占位）；
/// ② 章节定位（序号 → id → 标题子串；歧义给候选而不是猜）。
/// 长篇不以列表铺细节，正文与细纲在这里按需展开。
/// </summary>
public sealed class ChapterReaderContentTests
{
    private static Chapter BuildChapter(
        string content = "雨落下来，他把信折好。",
        string summary = "发现密信",
        ChapterStatus status = ChapterStatus.Draft
    ) =>
        new()
        {
            Id = "c1",
            NovelId = "n1",
            Title = "雨夜寄北",
            Order = 1,
            Status = status,
            Summary = summary,
            Notes = "结尾留钩子",
            Content = content,
            PlotlineIds = ["p1"],
            CharacterIds = ["ch1"],
            VolumeId = "v1",
        };

    [Fact]
    public void Build_IncludesSynopsisBandAndContent()
    {
        var chapter = BuildChapter();
        var volume = new Volume
        {
            Id = "v1",
            NovelId = "n1",
            Name = "风起",
            Order = 1,
        };
        var appearances = new List<AppearanceRecord>
        {
            new()
            {
                Id = "a1",
                NovelId = "n1",
                ChapterId = "c1",
                EntityId = "ch1",
                EntityType = EntityType.Character,
                Mode = AppearanceMode.Present,
            },
        };
        var changes = new List<EntityStateChange>
        {
            new()
            {
                Id = "s1",
                NovelId = "n1",
                ChapterId = "c1",
                TargetId = "ch1",
                TargetEntityType = EntityType.Character,
                FromSummary = "警惕",
                ToSummary = "信任",
            },
        };

        var md = ChapterReaderContent.Build(
            chapter,
            volume,
            id => id == "p1" ? "主线" : id,
            id => id == "ch1" ? "林晚" : id,
            appearances,
            changes
        );

        Assert.Contains("# 第1章 雨夜寄北", md);
        Assert.Contains("`草稿`", md);
        Assert.Contains("`第1卷 风起`", md);
        Assert.Contains("## 细纲", md);
        Assert.Contains("发现密信", md);
        Assert.Contains("**计划推进**: 主线", md);
        Assert.Contains("**计划出场**: 林晚", md);
        Assert.Contains("**备注**: 结尾留钩子", md);
        Assert.Contains("**实际出场**: 林晚(在场)", md);
        Assert.Contains("**状态变化**: 林晚: 警惕 → 信任", md);
        Assert.Contains("## 正文", md);
        Assert.Contains("雨落下来，他把信折好。", md);
    }

    [Fact]
    public void Build_PendingChanges_AnnotatePendingLocationAndPlotline()
    {
        var chapter = BuildChapter();
        var changes = new List<EntityStateChange>
        {
            new()
            {
                Id = "s1",
                NovelId = "n1",
                ChapterId = "c1",
                TargetId = "ch1",
                TargetEntityType = EntityType.Character,
                Summary = "在破庙许愿",
                Lifecycle = ChangeLifecycle.Pending,
                LocationId = "loc1",
                PlotlineIds = ["p1"],
            },
        };

        var md = ChapterReaderContent.Build(
            chapter,
            null,
            id => id == "p1" ? "主线" : id,
            id => id == "ch1" ? "林晚" : id,
            null,
            changes,
            id => id == "loc1" ? "破庙" : id
        );

        // 待发布（草稿章登记未生效）与地点/情节线一并标注，读者能分清"将要生效的"
        Assert.Contains("**状态变化**: 林晚: 在破庙许愿（待发布 · 地点 破庙 · 情节线 主线）", md);
    }

    [Fact]
    public void Build_EmptyContentAndSummary_UseActionablePlaceholders()
    {
        // 全新空章：无细纲、无正文、无关联实体
        var bare = new Chapter
        {
            Id = "c9",
            NovelId = "n1",
            Title = "新章",
            Order = 9,
        };
        var md = ChapterReaderContent.Build(bare, null);

        Assert.Contains("# 第9章 新章", md);
        Assert.Contains(ChapterReaderContent.EmptySummaryPlaceholder, md);
        Assert.Contains(ChapterReaderContent.EmptyContentPlaceholder, md);
        // 无卷/无关联实体时不产生空行噪音
        Assert.DoesNotContain("**计划推进**", md);
        Assert.DoesNotContain("**计划出场**", md);
        Assert.DoesNotContain("**实际出场**", md);
        Assert.DoesNotContain("**状态变化**", md);
    }

    // ── 筛选：标题关键字 + 状态 ──

    [Fact]
    public void Filter_ByKeywordAndStatus()
    {
        var chapters = new List<Chapter>
        {
            new()
            {
                Id = "c1",
                NovelId = "n1",
                Title = "雨夜寄北",
                Order = 1,
                Status = ChapterStatus.Draft,
            },
            new()
            {
                Id = "c2",
                NovelId = "n1",
                Title = "雨夜再会",
                Order = 2,
                Status = ChapterStatus.Published,
            },
            new()
            {
                Id = "c3",
                NovelId = "n1",
                Title = "进城",
                Order = 3,
                Status = ChapterStatus.Draft,
            },
        };

        var keywordOnly = new ChapterListFilter("雨夜", null);
        Assert.Equal(["c1", "c2"], chapters.Where(keywordOnly.Matches).Select(c => c.Id));
        Assert.True(keywordOnly.IsActive);
        Assert.Contains("标题含「雨夜」", keywordOnly.Describe());

        var statusOnly = new ChapterListFilter(null, ChapterStatus.Draft);
        Assert.Equal(["c1", "c3"], chapters.Where(statusOnly.Matches).Select(c => c.Id));

        var both = new ChapterListFilter("雨夜", ChapterStatus.Draft);
        Assert.Equal(["c1"], chapters.Where(both.Matches).Select(c => c.Id));
        Assert.Contains("草稿", both.Describe());

        Assert.False(ChapterListFilter.None.IsActive);
        Assert.Equal(chapters.Count, chapters.Count(ChapterListFilter.None.Matches));
    }
}
