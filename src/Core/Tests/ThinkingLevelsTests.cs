using Core.Shared;
using Xunit;

namespace Core.Tests;

/// <summary>
/// ThinkingLevelBudgets 映射完整性测试。
/// 验证每个级别的预算 token 数、reasoning effort 和字符串解析。
/// </summary>
public sealed class ThinkingLevelsTests
{
    // ══════════════════════════════════════════════════════
    //  GetBudgetTokens
    // ══════════════════════════════════════════════════════

    [Theory]
    [InlineData(ThinkingLevel.Off, 0)]
    [InlineData(ThinkingLevel.Minimal, 128)]
    [InlineData(ThinkingLevel.Low, 512)]
    [InlineData(ThinkingLevel.Medium, 1024)]
    [InlineData(ThinkingLevel.High, 4096)]
    [InlineData(ThinkingLevel.XHigh, 8192)]
    public void GetBudgetTokens_AllLevels_ReturnCorrectValue(ThinkingLevel level, int expected)
    {
        Assert.Equal(expected, ThinkingLevelBudgets.GetBudgetTokens(level));
    }

    // ══════════════════════════════════════════════════════
    //  GetReasoningEffort
    // ══════════════════════════════════════════════════════

    [Theory]
    [InlineData(ThinkingLevel.Minimal, "low")]
    [InlineData(ThinkingLevel.Low, "low")]
    [InlineData(ThinkingLevel.Medium, "medium")]
    [InlineData(ThinkingLevel.High, "high")]
    [InlineData(ThinkingLevel.XHigh, "high")]
    public void GetReasoningEffort_AllLevels_ReturnCorrectValue(
        ThinkingLevel level,
        string expected
    )
    {
        Assert.Equal(expected, ThinkingLevelBudgets.GetReasoningEffort(level));
    }

    // ══════════════════════════════════════════════════════
    //  FromString
    // ══════════════════════════════════════════════════════

    [Theory]
    [InlineData("off", ThinkingLevel.Off)]
    [InlineData("minimal", ThinkingLevel.Minimal)]
    [InlineData("low", ThinkingLevel.Low)]
    [InlineData("medium", ThinkingLevel.Medium)]
    [InlineData("high", ThinkingLevel.High)]
    [InlineData("xhigh", ThinkingLevel.XHigh)]
    public void FromString_KnownValues_ReturnCorrectLevel(string value, ThinkingLevel expected)
    {
        Assert.Equal(expected, ThinkingLevelBudgets.FromString(value));
    }

    [Theory]
    [InlineData("OFF", ThinkingLevel.Off)]
    [InlineData("HIGH", ThinkingLevel.High)]
    [InlineData("Medium", ThinkingLevel.Medium)]
    [InlineData("LOW", ThinkingLevel.Low)]
    public void FromString_CaseInsensitive_ReturnsCorrectLevel(string value, ThinkingLevel expected)
    {
        Assert.Equal(expected, ThinkingLevelBudgets.FromString(value));
    }

    [Fact]
    public void FromString_Null_ReturnsMedium()
    {
        Assert.Equal(ThinkingLevel.Medium, ThinkingLevelBudgets.FromString(null));
    }

    [Fact]
    public void FromString_Unknown_ReturnsMedium()
    {
        Assert.Equal(ThinkingLevel.Medium, ThinkingLevelBudgets.FromString("nonexistent"));
    }

    [Fact]
    public void FromString_Empty_ReturnsMedium()
    {
        Assert.Equal(ThinkingLevel.Medium, ThinkingLevelBudgets.FromString(""));
    }

    // ══════════════════════════════════════════════════════
    //  Options 列表
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Options_ExcludesOff()
    {
        Assert.DoesNotContain(ThinkingLevelBudgets.Options, o => o.Key == "off");
    }

    [Fact]
    public void Options_HasFiveEntries()
    {
        Assert.Equal(5, ThinkingLevelBudgets.Options.Length);
    }

    [Fact]
    public void Options_EachHasNonEmptyKeyAndLabel()
    {
        Assert.All(
            ThinkingLevelBudgets.Options,
            o =>
            {
                Assert.NotEmpty(o.Key);
                Assert.NotEmpty(o.Label);
            }
        );
    }

    // ══════════════════════════════════════════════════════
    //  预算单调递增
    // ══════════════════════════════════════════════════════

    [Fact]
    public void GetBudgetTokens_IncreasingWithLevel()
    {
        var levels = new[]
        {
            ThinkingLevel.Off,
            ThinkingLevel.Minimal,
            ThinkingLevel.Low,
            ThinkingLevel.Medium,
            ThinkingLevel.High,
            ThinkingLevel.XHigh,
        };
        for (var i = 1; i < levels.Length; i++)
        {
            Assert.True(
                ThinkingLevelBudgets.GetBudgetTokens(levels[i])
                    > ThinkingLevelBudgets.GetBudgetTokens(levels[i - 1]),
                $"{levels[i]} budget should be > {levels[i - 1]} budget"
            );
        }
    }
}
