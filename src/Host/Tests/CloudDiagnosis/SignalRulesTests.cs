using CloudDiagnosis;
using Xunit;

namespace Host.Tests.CloudDiagnosis;

public sealed class SignalRulesTests
{
    private static LocalFacts Facts(
        int maxOrder = 10,
        IReadOnlyList<PlotlineFact>? plotlines = null,
        int activeInfoGaps = 1,
        double? entropyDelta = null
    ) =>
        new()
        {
            MaxChapterOrder = maxOrder,
            OpenPlotlines = plotlines ?? [],
            ActiveInfoGaps = activeInfoGaps,
            EntropyDelta = entropyDelta,
        };

    [Fact]
    public void OverduePlotline_ProducesLocalFinding()
    {
        var findings = SignalRules.Evaluate(
            Facts(maxOrder: 12, plotlines: [new PlotlineFact("师门血案", PlannedRevealOrder: 10)])
        );

        var finding = Assert.Single(findings, f => f.Trigger == "A-3");
        Assert.Equal("情节", finding.Category);
        Assert.Contains("师门血案", finding.Problem);
        Assert.DoesNotContain("S-1", finding.Problem);
    }

    [Fact]
    public void TooFewOpenPlotlines_ProducesDensityFinding()
    {
        var findings = SignalRules.Evaluate(
            Facts(
                plotlines: [new PlotlineFact("唯一暗线", PlannedRevealOrder: 20)],
                entropyDelta: 0.2
            )
        );

        Assert.Contains(findings, f => f.Trigger == "A-4" && f.Problem.Contains("仅 1 条"));
    }

    [Fact]
    public void NoInfoAsymmetryWithOpenPlotlines_ProducesWarning()
    {
        var findings = SignalRules.Evaluate(
            Facts(
                plotlines:
                [
                    new PlotlineFact("暗线一", PlannedRevealOrder: 20),
                    new PlotlineFact("暗线二", PlannedRevealOrder: 21),
                ],
                activeInfoGaps: 0
            )
        );

        Assert.Contains(findings, f => f.Trigger == "A-5");
    }

    [Fact]
    public void EvaluatedFindings_UseOnlyLocalTriggers()
    {
        var findings = SignalRules.Evaluate(
            Facts(
                maxOrder: 40,
                plotlines:
                [
                    new PlotlineFact("旧伏笔", null, FirstAppearOrder: 1),
                    new PlotlineFact("新伏笔", PlannedRevealOrder: 50),
                ],
                activeInfoGaps: 0
            )
        );

        Assert.All(findings, f => Assert.Contains(f.Trigger, new[] { "A-3", "A-4", "A-5" }));
        Assert.All(findings, f => Assert.DoesNotContain("fiction.", f.Problem));
        Assert.All(findings, f => Assert.Equal("", f.Id));
    }
}
