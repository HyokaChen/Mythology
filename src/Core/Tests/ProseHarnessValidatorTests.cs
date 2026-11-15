using Core.Prose;
using Xunit;

namespace Core.Tests;

public sealed class ProseHarnessValidatorTests
{
    [Fact]
    public void TryValidate_AcceptsOnlySeparatorChanges()
    {
        var original = "甲\n\n---\n\n乙";
        var revised = "甲\n\n***\n\n乙";

        Assert.True(
            ProseHarnessValidator.TryValidate(
                original,
                revised,
                out var content,
                out var summary,
                out var error
            )
        );
        Assert.Null(error);
        Assert.Equal(revised, content);
        Assert.Equal(1, summary.OriginalSeparators);
        Assert.Equal(1, summary.RevisedSeparators);
    }

    [Fact]
    public void TryValidate_AcceptsManySeparators_NoDensityPolicy()
    {
        var original = BuildSeparatorContent(20);
        var revised = original.Replace("---", "***", StringComparison.Ordinal);

        Assert.True(
            ProseHarnessValidator.TryValidate(
                original,
                revised,
                out var content,
                out var summary,
                out var error
            )
        );
        Assert.Null(error);
        Assert.Equal(revised, content);
        Assert.Equal(20, summary.OriginalSeparators);
        Assert.Equal(20, summary.RevisedSeparators);
    }

    [Fact]
    public void TryValidate_AcceptsInsertionAndRemoval()
    {
        var plain = "甲\n\n乙";
        var withSeparator = "甲\n\n---\n\n乙";

        Assert.True(
            ProseHarnessValidator.TryValidate(plain, withSeparator, out _, out var inserted, out _)
        );
        Assert.Equal(0, inserted.OriginalSeparators);
        Assert.Equal(1, inserted.RevisedSeparators);

        Assert.True(
            ProseHarnessValidator.TryValidate(withSeparator, plain, out _, out var removed, out _)
        );
        Assert.Equal(1, removed.OriginalSeparators);
        Assert.Equal(0, removed.RevisedSeparators);
    }

    [Fact]
    public void TryValidate_RejectsProseChange()
    {
        var original = "甲\n\n乙";
        var revised = "甲\n\n丙";

        Assert.False(
            ProseHarnessValidator.TryValidate(
                original,
                revised,
                out var content,
                out _,
                out var error
            )
        );
        Assert.Equal(original, content);
        Assert.NotNull(error);
        Assert.Contains("正文第", error);
    }

    [Fact]
    public void TryValidate_RejectsParagraphMerge()
    {
        var original = "甲\n\n乙";
        var revised = "甲\n乙";

        Assert.False(
            ProseHarnessValidator.TryValidate(
                original,
                revised,
                out var content,
                out _,
                out var error
            )
        );
        Assert.Equal(original, content);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryValidate_PreservesCrLf()
    {
        var original = "甲\r\n\r\n---\r\n\r\n乙";
        var revised = "甲\r\n\r\n***\r\n\r\n乙";

        Assert.True(
            ProseHarnessValidator.TryValidate(
                original,
                revised,
                out var content,
                out _,
                out var error
            )
        );
        Assert.Null(error);
        Assert.Contains("\r\n", content);
        Assert.DoesNotContain("\n", content.Replace("\r\n", ""));
    }

    [Fact]
    public void IsStandaloneSeparatorLine_RequiresAtLeastTwoSymbols()
    {
        Assert.True(ProseHarnessValidator.IsStandaloneSeparatorLine("---"));
        Assert.True(ProseHarnessValidator.IsStandaloneSeparatorLine("- - -"));
        Assert.True(ProseHarnessValidator.IsStandaloneSeparatorLine("——"));
        Assert.True(ProseHarnessValidator.IsStandaloneSeparatorLine("＊＊＊"));
        Assert.False(ProseHarnessValidator.IsStandaloneSeparatorLine("—"));
        Assert.False(ProseHarnessValidator.IsStandaloneSeparatorLine("「你——」"));
    }

    [Fact]
    public void CountSeparatorLines_CountsOnlyStandaloneLines()
    {
        Assert.Equal(3, ProseHarnessValidator.CountSeparatorLines("---\n\n——\n\n「你——」\n\n***"));
    }

    [Fact]
    public void TryValidate_PreservesInlineDashAsProse()
    {
        var original = "他喊：「你——」\n\n---\n\n她停下。";
        var revised = "他喊：「你——」\n\n***\n\n她停下。";

        Assert.True(
            ProseHarnessValidator.TryValidate(
                original,
                revised,
                out var content,
                out var summary,
                out var error
            )
        );
        Assert.Null(error);
        Assert.Equal(revised, content);
        Assert.Equal(1, summary.OriginalSeparators);
        Assert.Equal(1, summary.RevisedSeparators);
    }

    [Fact]
    public void TryValidate_RejectsInlineDashRewrite()
    {
        var original = "他喊：「你——」";
        var revised = "他喊：「你—」";

        Assert.False(
            ProseHarnessValidator.TryValidate(
                original,
                revised,
                out var content,
                out _,
                out var error
            )
        );
        Assert.Equal(original, content);
        Assert.NotNull(error);
    }

    private static string BuildSeparatorContent(int separatorCount) =>
        string.Join(
            "\n\n---\n\n",
            Enumerable.Range(0, separatorCount + 1).Select(i => $"{new string('甲', 100)}{i}")
        );
}
