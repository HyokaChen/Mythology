using Core.Data;
using Xunit;

namespace Core.Tests;

/// <summary>
/// 正文格式硬约束：独立成行的分隔符必须清理，句内破折号必须保留。
/// 这组测试钉住“删除什么/保留什么/段落是否粘连”三个边界。
/// </summary>
public sealed class ProseFormatGuardTests
{
    [Fact]
    public void Normalize_RemovesStandaloneDashLine_AndKeepsParagraphs()
    {
        var result = ProseFormatGuard.Normalize("第一段。\n\n---\n\n第二段。");

        Assert.Equal("第一段。\n\n第二段。", result.Content);
        Assert.Equal(1, result.RemovedSeparatorLines);
        Assert.Equal("；已清理 1 处独立分隔符", result.CleanupNote);
    }

    [Fact]
    public void Normalize_RemovesEveryCommonSeparatorForm()
    {
        const string text = """
            甲。

            ---

            乙。

            ***

            丙。

            ___

            丁。

            ——

            戊。

            * * *

            己。

            — — —

            庚。

            --
            辛。
            """;

        var result = ProseFormatGuard.Normalize(text);

        Assert.Equal(7, result.RemovedSeparatorLines);
        Assert.DoesNotContain("---", result.Content);
        Assert.DoesNotContain("***", result.Content);
        Assert.DoesNotContain("___", result.Content);
        Assert.DoesNotContain("——", result.Content);
        Assert.DoesNotContain("* * *", result.Content);
        Assert.DoesNotContain("— — —", result.Content);
        Assert.DoesNotContain("\n--\n", result.Content);
        Assert.Contains("甲。\n\n乙。\n\n丙。\n\n丁。\n\n戊。\n\n己。\n\n庚。\n\n辛。", result.Content);
    }

    [Fact]
    public void Normalize_PreservesInlineEmDashDialogue()
    {
        const string text = "「你——」\n\n他走了。";

        var result = ProseFormatGuard.Normalize(text);

        Assert.Equal(text, result.Content);
        Assert.Equal(0, result.RemovedSeparatorLines);
        Assert.Equal("", result.CleanupNote);
    }

    [Fact]
    public void Normalize_DoesNotTreatSingleSymbolAsSeparator()
    {
        const string text = "-\n\n—\n\n*";

        var result = ProseFormatGuard.Normalize(text);

        Assert.Equal(text, result.Content);
        Assert.Equal(0, result.RemovedSeparatorLines);
    }

    [Fact]
    public void Normalize_CollapsesBlankRunsCreatedBySeparator()
    {
        var result = ProseFormatGuard.Normalize("甲。\n\n\n---\n\n\n乙。");

        Assert.Equal("甲。\n\n乙。", result.Content);
        Assert.Equal(1, result.RemovedSeparatorLines);
    }

    [Fact]
    public void Normalize_PreservesCrLf()
    {
        var result = ProseFormatGuard.Normalize("甲。\r\n\r\n---\r\n\r\n乙。");

        Assert.Equal("甲。\r\n\r\n乙。", result.Content);
        Assert.Equal(1, result.RemovedSeparatorLines);
    }

    [Fact]
    public void Normalize_RemovesEdgeSeparatorsWithoutLeavingBlankLines()
    {
        var result = ProseFormatGuard.Normalize("---\n\n正文\n\n***");

        Assert.Equal("正文", result.Content);
        Assert.Equal(2, result.RemovedSeparatorLines);
    }

    [Fact]
    public void CountSeparatorLines_CountsOnlyStandaloneLines()
    {
        const string text = "---\n句内——破折号\n***\n——\n不是分隔符 -";

        Assert.Equal(3, ProseFormatGuard.CountSeparatorLines(text));
    }

    [Fact]
    public void IsSeparatorLine_RecognizesSpacedAndFullWidthForms()
    {
        Assert.True(ProseFormatGuard.IsSeparatorLine("--"));
        Assert.True(ProseFormatGuard.IsSeparatorLine("* * *"));
        Assert.True(ProseFormatGuard.IsSeparatorLine("— — —"));
        Assert.True(ProseFormatGuard.IsSeparatorLine("＊＊＊"));
        Assert.False(ProseFormatGuard.IsSeparatorLine("——一句话"));
        Assert.False(ProseFormatGuard.IsSeparatorLine("「——」"));
        Assert.False(ProseFormatGuard.IsSeparatorLine("-"));
    }
}
