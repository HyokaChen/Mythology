using Core.Data;
using Core.Forecast;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 远期推演纯计算：用 CharCount 判已写、窗口/义务/转折/播种缺口/风险/查询文本。
/// </summary>
public sealed class PlotForecastBuilderTests
{
    private const string NovelId = "forecast-novel";

    private static Novel Novel() =>
        new()
        {
            Id = NovelId,
            Title = "推演测试",
            Outline = "从微末到巅峰：青铜钥匙开启北境之门",
        };

    private static List<Volume> Volumes() =>
        [
            new()
            {
                Id = "v1",
                NovelId = NovelId,
                Name = "风起",
                Order = 1,
                Goal = "让主角拿到青铜钥匙",
            },
        ];

    private static List<Chapter> Chapters()
    {
        var list = new List<Chapter>();
        for (var order = 1; order <= 5; order++)
        {
            list.Add(
                new Chapter
                {
                    Id = $"ch{order}",
                    NovelId = NovelId,
                    Order = order,
                    Title = $"第{order}章",
                    Content = order == 1 ? "青铜钥匙的传闻" : "正文",
                    VolumeId = "v1",
                }
            );
        }
        list.Add(
            new Chapter
            {
                Id = "ch6",
                NovelId = NovelId,
                Order = 6,
                Title = "钥匙归位",
                Summary = "回收青铜钥匙",
                VolumeId = "v1",
            }
        );
        list.Add(
            new Chapter
            {
                Id = "ch7",
                NovelId = NovelId,
                Order = 7,
                Title = "使者",
                Summary = "北境使者抵达",
                CharacterIds = ["c-envoy"],
                PlotlineIds = ["p-bronze", "p-rebellion"],
                VolumeId = "v1",
            }
        );
        list.Add(
            new Chapter
            {
                Id = "ch8",
                NovelId = NovelId,
                Order = 8,
                Title = "决战",
                Summary = "决战",
                VolumeId = "v1",
            }
        );
        return list;
    }

    private static List<Plotline> Plotlines() =>
        [
            new()
            {
                Id = "p-bronze",
                NovelId = NovelId,
                Name = "青铜钥匙",
                Type = PlotlineType.Foreshadowing,
                PlannedRevealChapterId = "ch6",
                Turnpoints = "第2章：埋下青铜钥匙\n第7章：真相揭露",
            },
            new()
            {
                Id = "p-rebellion",
                NovelId = NovelId,
                Name = "北境叛乱",
                Type = PlotlineType.Side,
            },
        ];

    private static List<Character> Characters() =>
        [
            new()
            {
                Id = "c-hero",
                NovelId = NovelId,
                Name = "主角",
            },
            new()
            {
                Id = "c-envoy",
                NovelId = NovelId,
                Name = "北境使者",
            },
        ];

    private static List<AppearanceRecord> Appearances() =>
        [
            new()
            {
                Id = "ap1",
                NovelId = NovelId,
                EntityType = EntityType.Character,
                EntityId = "c-hero",
                ChapterId = "ch1",
            },
        ];

    private static PlotForecast Build(
        IReadOnlyList<Chapter>? chapters = null,
        IReadOnlyList<AppearanceRecord>? appearances = null,
        IReadOnlyList<Plotline>? plotlines = null,
        int window = 5
    ) =>
        PlotForecastBuilder.Build(
            Novel(),
            chapters ?? Chapters(),
            Volumes(),
            plotlines ?? Plotlines(),
            Characters(),
            appearances ?? Appearances(),
            window
        );

    [Fact]
    public void EmptyBook_HasNoPlanData()
    {
        var forecast = PlotForecastBuilder.Build(
            Novel(),
            [],
            [],
            [],
            [],
            [],
            PlotForecastBuilder.DefaultWindow
        );

        Assert.Equal(0, forecast.CurrentOrder);
        Assert.Equal(1, forecast.TargetOrder);
        Assert.Empty(forecast.Window);
        Assert.False(forecast.HasPlanData);
        Assert.Contains(ForecastRiskKind.NoAppearanceRecords, forecast.Risks.Select(r => r.Kind));
    }

    [Fact]
    public void CurrentOrder_UsesCharCountOnly()
    {
        var chapters = Chapters();
        chapters[4].Content = ""; // 第5章只有细纲、没有正文
        var forecast = Build(chapters);

        Assert.Equal(4, forecast.CurrentOrder);
        Assert.Equal(5, forecast.TargetOrder);
        Assert.Equal("ch5", forecast.TargetChapterId);
    }

    [Fact]
    public void Window_IsTargetToTargetPlusWindow()
    {
        var forecast = Build(window: 2);

        Assert.Equal([6, 7], forecast.Window.Select(w => w.Order));
        Assert.Equal("回收青铜钥匙", forecast.Window[0].Summary);
    }

    [Fact]
    public void VolumeGoal_FallsBackToSummary()
    {
        var volumes = Volumes();
        volumes[0].Goal = "";
        volumes[0].Summary = "开篇铺垫";

        var forecast = PlotForecastBuilder.Build(
            Novel(),
            Chapters(),
            volumes,
            Plotlines(),
            Characters(),
            Appearances(),
            5
        );

        Assert.Equal("开篇铺垫", forecast.VolumeGoal);
    }

    [Fact]
    public void Obligations_OverdueAndDue()
    {
        var plotlines = Plotlines();
        plotlines[0].PlannedRevealChapterId = "ch4"; // 已过（current=5）
        plotlines[1].PlannedRevealChapterId = "ch7";

        var forecast = Build(plotlines: plotlines);

        var bronze = Assert.Single(forecast.Obligations, o => o.PlotlineId == "p-bronze");
        Assert.True(bronze.Overdue);
        Assert.Equal(-1, bronze.RemainingChapters);
        Assert.Empty(bronze.AdvanceOrders);

        var rebellion = Assert.Single(forecast.Obligations, o => o.PlotlineId == "p-rebellion");
        Assert.False(rebellion.Overdue);
        Assert.Equal(2, rebellion.RemainingChapters);
        Assert.Equal("p-bronze", forecast.Obligations[0].PlotlineId); // 逾期优先
    }

    [Fact]
    public void Obligations_AdvanceOrders_ComeFromWrittenChapters()
    {
        var chapters = Chapters();
        chapters[1].PlotlineIds = ["p-bronze"]; // 第2章推进过
        var forecast = Build(chapters);

        var bronze = Assert.Single(forecast.Obligations, o => o.PlotlineId == "p-bronze");
        Assert.Equal([2], bronze.AdvanceOrders);
    }

    [Fact]
    public void Obligations_SkipUnresolvableRevealChapter()
    {
        var plotlines = Plotlines();
        plotlines[0].PlannedRevealChapterId = "missing-chapter";

        var forecast = Build(plotlines: plotlines);

        Assert.DoesNotContain(forecast.Obligations, o => o.PlotlineId == "p-bronze");
    }

    [Fact]
    public void Beats_OnlyFutureSortedAndCapped()
    {
        var plotlines = Plotlines();
        plotlines[0].Turnpoints =
            "第2章：埋下青铜钥匙\n第8章：门开\n第7章：真相揭露\n第7章：使者出手";
        var forecast = Build(plotlines: plotlines);

        Assert.Equal([7, 7, 8], forecast.Beats.Select(b => b.ChapterOrder));
        Assert.DoesNotContain(forecast.Beats, b => b.ChapterOrder <= forecast.CurrentOrder);
        Assert.All(forecast.Beats, b => Assert.Equal("青铜钥匙", b.PlotlineName));
    }

    [Fact]
    public void SeedGaps_CharacterWithoutAppearance_AndUnseededPlotline()
    {
        var forecast = Build();

        var characterGap = Assert.Single(
            forecast.SeedGaps,
            g => g.Kind == "Character" && g.EntityId == "c-envoy"
        );
        Assert.Equal("北境使者", characterGap.Name);
        Assert.Equal(7, characterGap.ReferencedOrder);

        Assert.Contains(
            forecast.SeedGaps,
            g => g.Kind == "Plotline" && g.EntityId == "p-rebellion"
        );
        // 青铜钥匙在第2章转折里已播种 → 不算缺口
        Assert.DoesNotContain(
            forecast.SeedGaps,
            g => g.Kind == "Plotline" && g.EntityId == "p-bronze"
        );
    }

    [Fact]
    public void SeedGaps_DedupeSameEntityAcrossWindow()
    {
        var chapters = Chapters();
        chapters[7].CharacterIds = ["c-envoy"]; // 第8章也提到同一角色
        chapters[7].PlotlineIds = ["p-rebellion"];

        var forecast = Build(chapters);

        Assert.Single(forecast.SeedGaps, g => g.Kind == "Character" && g.EntityId == "c-envoy");
        Assert.Single(forecast.SeedGaps, g => g.Kind == "Plotline" && g.EntityId == "p-rebellion");
        Assert.Equal(7, forecast.SeedGaps.Single(g => g.EntityId == "c-envoy").ReferencedOrder);
    }

    [Fact]
    public void SeedGaps_SkipCharacters_WhenNoAppearanceRecords()
    {
        var forecast = Build(appearances: []);

        Assert.DoesNotContain(forecast.SeedGaps, g => g.Kind == "Character");
        Assert.Contains(forecast.Risks, r => r.Kind == ForecastRiskKind.NoAppearanceRecords);
    }

    [Fact]
    public void Risks_CoverOverdueDueWithoutAdvanceNoPlanAndBeatAfterReveal()
    {
        var plotlines = Plotlines();
        plotlines[0].PlannedRevealChapterId = "ch4";
        var forecast = Build(plotlines: plotlines);

        Assert.Contains(
            forecast.Risks,
            r =>
                r.Kind == ForecastRiskKind.Overdue
                && r.Message.Contains("已逾期 1 章", StringComparison.Ordinal)
        );
        Assert.Contains(
            forecast.Risks,
            r =>
                r.Kind == ForecastRiskKind.NoPlanOnWindowLine
                && r.Message.Contains("《北境叛乱》出现在第7章细纲中", StringComparison.Ordinal)
        );
        Assert.Contains(
            forecast.Risks,
            r => r.Kind == ForecastRiskKind.BeatAfterReveal && r.ChapterOrder == 7
        );
    }

    [Fact]
    public void Risks_DueWithoutAdvance_Message()
    {
        var forecast = Build();

        Assert.Contains(
            forecast.Risks,
            r =>
                r.Kind == ForecastRiskKind.DueWithoutAdvance
                && r.Message.Contains(
                    "《青铜钥匙》计划第6章揭示（还有 1 章），但已写章节中从未推进过该线",
                    StringComparison.Ordinal
                )
        );
    }

    [Fact]
    public void BeatAfterReveal_ScansBeatsBeyondRenderCap()
    {
        var chapters = Chapters();
        chapters.Add(
            new Chapter
            {
                Id = "ch9",
                NovelId = NovelId,
                Order = 9,
                Title = "九",
            }
        );
        chapters.Add(
            new Chapter
            {
                Id = "ch10",
                NovelId = NovelId,
                Order = 10,
                Title = "十",
            }
        );
        var plotlines = Plotlines();
        plotlines[0].PlannedRevealChapterId = "ch9";
        plotlines[0].Turnpoints =
            "第6章：铺垫一\n第6章：铺垫二\n第6章：铺垫三\n第6章：铺垫四\n"
            + "第7章：铺垫五\n第7章：铺垫六\n第8章：铺垫七\n第8章：铺垫八\n第10章：真相晚了";
        plotlines.RemoveAt(1);

        var forecast = Build(chapters, plotlines: plotlines);

        Assert.Equal(PlotForecastBuilder.MaxBeats, forecast.Beats.Count); // 渲染只取前 8
        Assert.Contains(
            forecast.Risks,
            r =>
                r.Kind == ForecastRiskKind.BeatAfterReveal
                && r.Message.Contains("第10章", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void Risks_UnwrittenHole()
    {
        var chapters = Chapters();
        chapters[2].Content = ""; // 第3章未写
        chapters[3].Content = "";
        chapters[3].Order = 4;
        var forecast = Build(chapters);

        Assert.Contains(
            forecast.Risks,
            r => r.Kind == ForecastRiskKind.UnwrittenHole && r.ChapterOrder == 3
        );
    }

    [Fact]
    public void Queries_ContainPlanAndLongRangeContext()
    {
        var forecast = Build();

        Assert.Contains("回收青铜钥匙", forecast.QueryText, StringComparison.Ordinal);
        Assert.Contains(
            "青铜钥匙：第2章：埋下青铜钥匙",
            forecast.QueryText,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "第1卷《风起》目标：让主角拿到青铜钥匙",
            forecast.QueryText,
            StringComparison.Ordinal
        );
        Assert.Contains("从微末到巅峰", forecast.LongRangeQueryText, StringComparison.Ordinal);
        Assert.True(forecast.QueryText.Length <= PlotForecastBuilder.MaxQueryChars);
        Assert.True(forecast.LongRangeQueryText.Length <= PlotForecastBuilder.MaxQueryChars);
    }

    [Fact]
    public void HasPlanData_TrueWhenWindowSummaryExists()
    {
        Assert.True(Build().HasPlanData);
        Assert.True(Build().Window.Count > 0);
    }
}
