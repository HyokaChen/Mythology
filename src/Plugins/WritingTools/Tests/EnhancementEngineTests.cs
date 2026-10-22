using Core.Cloud;
using Core.Shared;
using Plugins.WritingTools;
using Xunit;

namespace WritingTools.Tests;

public sealed class EnhancementEngineTests
{
    // ── Locate ──

    [Fact]
    public void Locate_HighSeverityIssues_ReturnsLocations()
    {
        var engine = CreateEngine();
        var report = new DiagnosticReport
        {
            Issues =
            [
                new DiagnosticIssue
                {
                    Dimension = DimensionKeys.Immersion,
                    Severity = Severity.High,
                    Description = "精确身高",
                    ParagraphIndex = 0,
                    ChapterId = "ch1",
                },
            ],
        };

        var locations = engine.Locate(report, "第一段。\n\n第二段。");
        Assert.Single(locations);
        Assert.Equal(DimensionKeys.Immersion, locations[0].Dimension);
        Assert.Equal(0, locations[0].ParagraphIndex);
    }

    [Fact]
    public void Locate_LowSeverityIssues_FilteredOut()
    {
        var engine = CreateEngine();
        var report = new DiagnosticReport
        {
            Issues =
            [
                new DiagnosticIssue
                {
                    Dimension = DimensionKeys.Immersion,
                    Severity = Severity.Low,
                    Description = "轻微问题",
                    ParagraphIndex = 0,
                    ChapterId = "ch1",
                },
            ],
        };

        var locations = engine.Locate(report, "第一段。");
        Assert.Empty(locations);
    }

    [Fact]
    public void Locate_MoreThanThreeIssues_CappedAtThree()
    {
        var engine = CreateEngine();
        var issues = Enumerable
            .Range(0, 5)
            .Select(i => new DiagnosticIssue
            {
                Dimension = DimensionKeys.Immersion,
                Severity = Severity.High,
                Description = $"问题{i}",
                ParagraphIndex = i,
                ChapterId = "ch1",
            })
            .ToList();

        var report = new DiagnosticReport { Issues = issues };
        var paragraphs = string.Join(
            "\n\n",
            Enumerable.Range(0, 5).Select(i => $"第{i + 1}段内容。")
        );
        var locations = engine.Locate(report, paragraphs);

        Assert.Equal(3, locations.Count);
    }

    [Fact]
    public void Locate_InvalidParagraphIndex_DefaultsToZero()
    {
        var engine = CreateEngine();
        var report = new DiagnosticReport
        {
            Issues =
            [
                new DiagnosticIssue
                {
                    Dimension = DimensionKeys.Immersion,
                    Severity = Severity.High,
                    Description = "问题",
                    ParagraphIndex = -1,
                    ChapterId = "ch1",
                },
            ],
        };

        var locations = engine.Locate(report, "唯一段落。");
        Assert.Single(locations);
        Assert.Equal(0, locations[0].ParagraphIndex);
    }

    // ── Apply ──

    [Fact]
    public void Apply_GeneratesExploreDetails()
    {
        var engine = CreateEngine();
        var candidates = new List<EnhancementCandidate>
        {
            new()
            {
                Id = "c1",
                Label = "保守版",
                Content = "改写内容1",
                Description = "最小改动",
            },
            new()
            {
                Id = "c2",
                Label = "适度版",
                Content = "改写内容2",
                Description = "适度增强",
            },
        };

        var location = new EnhancementLocation
        {
            ChapterId = "ch1",
            ParagraphIndex = 0,
            Dimension = DimensionKeys.Immersion,
            IssueDescription = "精确身高",
        };

        var result = engine.Apply(candidates, location, "novel1");
        var details = result.TypedDetails;
        Assert.NotNull(details);
        Assert.Equal(2, details.Variants.Count);
        Assert.Equal("enhancement", details.ExploreType);
    }

    private static EnhancementEngine CreateEngine()
        // Locate/Apply are pure logic — store, agent and cloud harness are never invoked
        =>
        new(null!, null!, new EnhancementSnapshot(), new NeverCalledHarness());

    /// <summary>Locate/Apply 测试用：任何云端调用都应触发失败（证明纯逻辑不触云端）。</summary>
    private sealed class NeverCalledHarness : IFictionHarnessClient
    {
        public Task<HarnessResult> ExecuteAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("Locate/Apply must not call the cloud harness.");

        public Task<AgentContextSnapshot> GetContextAsync(
            HarnessRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("Locate/Apply must not call the cloud harness.");

        public Task ReportSetupFeedbackAsync(
            IReadOnlyList<(string FindingId, string Decision)> items,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }
}
