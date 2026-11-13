using Desktop.Models;
using Xunit;

namespace Desktop.Tests;

public sealed class AgentStepPreviewTests
{
    [Theory]
    [InlineData("## 🔍 设定检查报告\n- 发现 2 处问题", "↳ 🔍 设定检查报告")]
    [InlineData("- 亡妻的纸鸢 (ID: 8bcd242dec3b)", "↳ 亡妻的纸鸢 (ID: 8bcd242dec3b)")]
    [InlineData("**已更新** `chapter.md`", "↳ 已更新 chapter.md")]
    [InlineData("---\n| 项目 | 结果 |", "↳ | 项目 | 结果 |")]
    public void ResultPreview_StripsMarkdownMarkersAndKeepsFirstLine(
        string summary,
        string expected
    )
    {
        var step = new AgentStep { ResultSummary = summary };

        Assert.Equal(expected, step.ResultPreview);
    }

    [Fact]
    public void ResultPreview_NotifiesWhenSummaryChanges()
    {
        var step = new AgentStep();
        var changed = new List<string?>();
        step.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        step.ResultSummary = "完成";

        Assert.Contains(nameof(AgentStep.ResultPreview), changed);
    }
}
