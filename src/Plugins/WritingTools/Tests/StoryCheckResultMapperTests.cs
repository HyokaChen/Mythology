using System.Text.Json;
using Core.Shared;
using Plugins.WritingTools;
using Xunit;

namespace WritingTools.Tests;

public sealed class StoryCheckResultMapperTests
{
    [Fact]
    public void Map_HumanizerDimension_DoesNotChangeLegacyOverallScore()
    {
        using var document = JsonDocument.Parse(
            """
            {"dimensions":{
              "immersion":{"overall-score":80},
              "humanizer":{"overall-score":20,"issues":[
                {"severity":"high","sub-type":"formulaic_pattern","description":"公式化句式","evidence":"不是A而是B","suggestion":"直接说B","paragraph-index":1}
              ]}
            }}
            """
        );

        var report = StoryCheckResultMapper.Map(document.RootElement, DiagnosticDepth.Quick);

        Assert.Equal(80, report.OverallScore, precision: 6);
        Assert.Equal(20, report.DimensionScores[DimensionKeys.Humanizer], precision: 6);
        Assert.DoesNotContain(report.DimensionScores.Keys, key => key == "humanizer");
        var issue = Assert.Single(report.Issues);
        Assert.Equal("formulaic_pattern", issue.SubType);
    }
}
