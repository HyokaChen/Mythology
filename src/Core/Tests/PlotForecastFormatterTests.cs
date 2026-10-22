using Core.Data;
using Core.Forecast;
using Core.Vector;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 远期推演注入块的逐字格式：首行 / 各节 / 证据两态 / 笔记四种来源 / 预算截断。
/// </summary>
public sealed class PlotForecastFormatterTests
{
    private static PlotForecast Forecast(
        bool hasPlanData = true,
        IReadOnlyList<ForecastChapterBrief>? window = null,
        IReadOnlyList<ForecastBeat>? beats = null,
        IReadOnlyList<ForecastObligation>? obligations = null,
        IReadOnlyList<ForecastSeedGap>? seedGaps = null,
        IReadOnlyList<ForecastRisk>? risks = null,
        string? volumeGoal = "让主角拿到青铜钥匙"
    ) =>
        new(
            CurrentOrder: 5,
            TargetOrder: 6,
            TargetChapterId: "ch6",
            TargetChapterTitle: "钥匙归位",
            VolumeGoal: volumeGoal,
            Window: window ?? [new ForecastChapterBrief(7, "使者", "北境使者抵达")],
            Beats: beats
                ?? [new ForecastBeat(7, "p-bronze", "青铜钥匙", "真相揭露", "reveal", 0.9)],
            Obligations: obligations
                ??
                [
                    new ForecastObligation(
                        "p-bronze",
                        "青铜钥匙",
                        PlotlineType.Foreshadowing,
                        PlannedOrder: 6,
                        RemainingChapters: 1,
                        Overdue: false,
                        AdvanceOrders: [2, 3],
                        Turnpoints: "第2章：埋下"
                    ),
                ],
            SeedGaps: seedGaps ?? [new ForecastSeedGap("Character", "c-envoy", "北境使者", 7)],
            Risks: risks
                ??
                [
                    new ForecastRisk(
                        ForecastRiskKind.DueWithoutAdvance,
                        "《青铜钥匙》计划第6章揭示（还有 1 章），但已写章节中从未推进过该线",
                        6
                    ),
                ],
            QueryText: "回收青铜钥匙",
            LongRangeQueryText: "总纲",
            HasPlanData: hasPlanData,
            WindowSize: 5
        );

    [Fact]
    public void NoPlanData_ReturnsGuideOnly()
    {
        var text = PlotForecastFormatter.Format(Forecast(hasPlanData: false), []);

        Assert.Equal(PlotForecastFormatter.NoPlanGuide, text);
    }

    [Fact]
    public void Head_HasProgressNextChapterAndVolumeGoal()
    {
        var text = PlotForecastFormatter.Format(Forecast(), []);

        Assert.StartsWith(
            "[远期推演] 已写至第5章，下一章：第6章《钥匙归位》（本卷目标：让主角拿到青铜钥匙）",
            text,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Head_EmptyBookAndMissingTitle()
    {
        var forecast = Forecast() with
        {
            CurrentOrder = 0,
            TargetChapterTitle = null,
            VolumeGoal = null,
        };

        var text = PlotForecastFormatter.Format(forecast, []);

        Assert.StartsWith(
            "[远期推演] 尚未写章，下一章：第6章《（未在细纲中登记）》",
            text,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Obligations_RenderTypeOverdueAndAdvance()
    {
        var text = PlotForecastFormatter.Format(Forecast(), []);

        Assert.Contains("到期义务：", text, StringComparison.Ordinal);
        Assert.Contains(
            "- 《青铜钥匙》（伏笔）计划第6章揭示，还有 1 章｜已推进 2 次（第2、3章）",
            text,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Obligations_OverdueAndNoAdvance()
    {
        var obligations = new List<ForecastObligation>
        {
            new(
                "p1",
                "甲线",
                PlotlineType.Main,
                PlannedOrder: 3,
                RemainingChapters: -2,
                Overdue: true,
                AdvanceOrders: [],
                Turnpoints: ""
            ),
        };

        var text = PlotForecastFormatter.Format(Forecast(obligations: obligations), []);

        Assert.Contains(
            "- 《甲线》（主线）计划第3章揭示，已逾期 2 章｜已推进 0 次（尚未推进）",
            text,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Sections_RenderBeatsWindowGapsRisks()
    {
        var text = PlotForecastFormatter.Format(Forecast(), []);

        Assert.Contains("风险：", text, StringComparison.Ordinal);
        Assert.Contains("- 《青铜钥匙》计划第6章揭示", text, StringComparison.Ordinal);
        Assert.Contains("计划转折：", text, StringComparison.Ordinal);
        Assert.Contains(
            "- 第7章 ·《青铜钥匙》[reveal 0.9] 真相揭露",
            text,
            StringComparison.Ordinal
        );
        Assert.Contains("计划远期（第7-7章）：", text, StringComparison.Ordinal);
        Assert.Contains("- 第7章《使者》：北境使者抵达", text, StringComparison.Ordinal);
        Assert.Contains("规划缺口：", text, StringComparison.Ordinal);
        Assert.Contains(
            "- 第7章细纲提到的角色「北境使者」在前文无任何出场/推进记录 → 需先铺垫",
            text,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void EmptySections_AreOmitted()
    {
        var forecast = Forecast(beats: [], seedGaps: [], risks: [], window: []);

        var text = PlotForecastFormatter.Format(forecast, []);

        Assert.DoesNotContain("计划转折：", text, StringComparison.Ordinal);
        Assert.DoesNotContain("风险：", text, StringComparison.Ordinal);
        Assert.DoesNotContain("规划缺口：", text, StringComparison.Ordinal);
        Assert.DoesNotContain("计划远期", text, StringComparison.Ordinal);
        Assert.DoesNotContain("前文证据：", text, StringComparison.Ordinal);
        Assert.DoesNotContain("笔记线索：", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Evidence_LocalAndCloud()
    {
        List<ForecastEvidence> evidence =
        [
            new(
                "local",
                "ch2",
                2,
                "井底",
                DocumentKind.ChapterBodyChunk,
                "青铜钥匙在井底\n发着微光",
                null,
                null,
                0.81
            ),
            new("cloud", null, 3, null, null, "玉佩（同源信物）", "bridge", "legacy-anchor", 0.44),
        ];

        var text = PlotForecastFormatter.Format(Forecast(), evidence);

        Assert.Contains("前文证据：", text, StringComparison.Ordinal);
        Assert.Contains(
            "- 第2章《井底》·章节正文：青铜钥匙在井底 发着微光",
            text,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "- 第3章·联想[强关联·遗留锚（契诃夫之枪）]：玉佩（同源信物）",
            text,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Notes_RenderFourSourcesAndInboxLine()
    {
        var notes = new NoteSelection(
            [
                new ForecastNote("n1", "半块玉的递法", "让使者递出半块玉", 7, "anchor"),
                new ForecastNote("n2", "决战前夜下雨", "雨一直下", 4, "past"),
                new ForecastNote("n3", "远处提醒", "北境有雪", null, "far"),
                new ForecastNote("n4", "近期灵感", "钥匙会发光", null, "near"),
            ],
            InboxCount: 2
        );

        var text = PlotForecastFormatter.Format(Forecast(), [], notes);

        Assert.Contains("笔记线索：", text, StringComparison.Ordinal);
        Assert.Contains(
            "- 《半块玉的递法》第7章 · 让使者递出半块玉",
            text,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "- 《决战前夜下雨》第4章（已写）· 雨一直下 → 确认是否已落地",
            text,
            StringComparison.Ordinal
        );
        Assert.Contains("- 《远处提醒》· 北境有雪（远期提醒）", text, StringComparison.Ordinal);
        Assert.Contains("- 《近期灵感》· 钥匙会发光", text, StringComparison.Ordinal);
        Assert.Contains("笔记待确认：2 条（确认后才参与推演）", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Budget_TruncatesAndMarks()
    {
        var longWindow = Enumerable
            .Range(7, 40)
            .Select(o => new ForecastChapterBrief(o, $"标题{o}", new string('长', 120)))
            .ToList();

        var text = PlotForecastFormatter.Format(Forecast(window: longWindow), []);

        Assert.Contains("…（已截断）", text, StringComparison.Ordinal);
        Assert.True(
            text.Length <= PlotForecastFormatter.MaxChars + "…（已截断）".Length + 4,
            $"块长度 {text.Length} 超预算"
        );
    }
}
