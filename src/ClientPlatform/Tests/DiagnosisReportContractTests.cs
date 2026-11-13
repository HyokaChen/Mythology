using System.Text.Json;
using Core.Cloud;
using Xunit;

namespace ClientPlatform.Tests;

public sealed class DiagnosisReportContractTests
{
    private const string ReportJson = """
        {"verdict":"需要修改","action":"revise","summary":"主角没有主动选择。","score":82,
         "findings":[{"id":"f_8f3a2c9d1e4b7a60","category":"角色","severity":"high",
           "severity-label":"严重","action":"fix-now","action-label":"直接修改",
           "location":"第 2 章 · 第 3 段","problem":"主角没有主动选择。",
           "evidence":"他站在门口。","suggestion":"让主角做一个选择。"}],
         "decision-question":null,"decision-options":null,
         "snapshot":{"target-chapter-ref":"ch_2","chapter-count":2,"previous-chapter-count":1,
           "paragraph-count":10,"narrative-entropy":1.4,"satisfaction-score":66,
           "trends":["爽感 连续 3 章走低"],"absent-characters":["冷月"],
           "material-hint":"素材参考"},
         "strengths":["节奏紧凑"]}
        """;

    [Fact]
    public void HarnessResult_ResultCarriesFullReport()
    {
        var json =
            $$"""{"invocation-id":"inv_1","status":"completed","capability":"fiction.story.check","cloud-work-units":2,"estimated-units":2,"result":{{ReportJson}},"warnings":[]}""";
        var result = JsonSerializer.Deserialize<HarnessResult>(json, ClientJsonDefaults.Cloud);

        Assert.NotNull(result);
        var report = JsonSerializer.Deserialize<DiagnosisReport>(
            result!.Result is JsonElement element ? element.GetRawText() : "",
            ClientJsonDefaults.Cloud
        );
        Assert.NotNull(report);
        Assert.Equal("需要修改", report!.Verdict);
        Assert.Equal("revise", report.Action);
        Assert.Equal(82, report.Score);
        Assert.Matches("^f_[0-9a-f]{16}$", report.Findings[0].Id);
        Assert.Equal("角色", report.Findings[0].Category);
        Assert.Equal("严重", report.Findings[0].SeverityLabel);
        Assert.Equal("素材参考", report.Snapshot!.MaterialHint);
        Assert.Equal(66, report.Snapshot.SatisfactionScore);
    }

    [Fact]
    public void HarnessResult_HasNoParallelReportOrDataFields()
    {
        var names = typeof(HarnessResult)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal(
            [
                "InvocationId",
                "Status",
                "Capability",
                "CloudWorkUnits",
                "EstimatedUnits",
                "Result",
                "Warnings",
            ],
            names
        );
        Assert.Contains("Result", names);
        Assert.DoesNotContain("Report", names);
        Assert.DoesNotContain("Data", names);
    }
}
