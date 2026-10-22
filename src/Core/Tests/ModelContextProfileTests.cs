using Core.Stability;
using Xunit;

namespace Core.Tests;

/// <summary>
/// ModelContextProfile 动态阈值测试（P0 nudge 阈值 + P1 工具结果截断阈值）。
/// </summary>
public sealed class ModelContextProfileTests
{
    // ── P0：nudge 分级提醒阈值必须严格低于 pre-flight 阈值 ──
    // 回归：原先硬编码 70%/85%/92% 与 pre-flight 动态阈值层级矛盾——
    // 8K 模型 pre-flight=85%，92% 提醒是死代码；1M 模型 92% 远早于 99.25% 过度打扰。

    [Theory]
    [InlineData(8_000)]
    [InlineData(32_000)]
    [InlineData(128_000)]
    [InlineData(200_000)]
    [InlineData(1_000_000)]
    public void NudgeThresholds_StrictlyOrderedBelowPreflight(long contextWindow)
    {
        var profile = new ModelContextProfile { ContextWindowTokens = contextWindow };

        Assert.True(profile.NudgeInfoPercent < profile.NudgeWarnPercent, "info 应 < warn");
        Assert.True(profile.NudgeWarnPercent < profile.NudgeCriticalPercent, "warn 应 < critical");
        Assert.True(
            profile.NudgeCriticalPercent < profile.PreflightThresholdPercent,
            "critical 应 < preflight（否则 critical 提醒是死代码）"
        );
    }

    // ── P0：接续换页阈值（建议 < 自动 < pre-flight）必须模型相对且严格有序 ──
    // 回归：绝对 token 阈值在 8K 上永远不可达、在 1M 上一开页就触发；
    // 且自动换页必须早于 pre-flight，否则先抛溢出再换页（白跑一轮重试）。

    [Theory]
    [InlineData(8_000)]
    [InlineData(32_000)]
    [InlineData(128_000)]
    [InlineData(200_000)]
    [InlineData(1_000_000)]
    public void ContextRollThresholds_StrictlyOrderedBelowPreflight(long contextWindow)
    {
        var profile = new ModelContextProfile { ContextWindowTokens = contextWindow };

        Assert.True(
            profile.RollAdvicePercent < profile.AutoRollPercent,
            "建议换页阈值应 < 自动换页阈值（模型才有机会挑自然接缝）"
        );
        Assert.True(
            profile.AutoRollPercent < profile.PreflightThresholdPercent,
            "自动换页阈值应 < pre-flight（否则先抛溢出）"
        );
    }

    [Fact]
    public void ContextRollThresholds_AreReachableOnAnyWindowSize()
    {
        foreach (var window in new long[] { 8_000, 128_000, 1_000_000 })
        {
            var profile = new ModelContextProfile { ContextWindowTokens = window };
            Assert.InRange(profile.RollAdvicePercent, 0.50, 0.85);
            Assert.InRange(profile.AutoRollPercent, 0.60, 0.95);
            // 一开页不会立刻触发（阈值不低于 50%）
            Assert.True(profile.RollAdvicePercent >= 0.50);
        }
    }

    [Fact]
    public void NudgeCritical_AlwaysReachableBeforePreflight_SmallWindow()
    {
        // 8K 模型：preflight=85%，critical 必须 < 85%（原先硬编码 92% 不可达）
        var small = new ModelContextProfile { ContextWindowTokens = 8_000 };
        Assert.True(small.NudgeCriticalPercent < 0.85);
        Assert.True(small.NudgeInfoPercent >= 0.50, "info 不低于下限 0.50");
    }

    // ── P1：工具结果截断阈值随窗口缩放 ──

    [Theory]
    [InlineData(8_000, 8_000)]
    [InlineData(20_000, 12_000)]
    [InlineData(50_000, 15_000)]
    [InlineData(128_000, 20_000)]
    [InlineData(200_000, 40_000)]
    [InlineData(1_000_000, 60_000)]
    public void ScaledToolResultMaxChars_ScalesWithWindow(long contextWindow, int expected)
    {
        var profile = new ModelContextProfile { ContextWindowTokens = contextWindow };
        Assert.Equal(expected, profile.ScaledToolResultMaxChars);
    }

    [Fact]
    public void ScaledToolResultMaxChars_LargeWindowMoreThanSmallWindow()
    {
        var small = new ModelContextProfile { ContextWindowTokens = 8_000 };
        var large = new ModelContextProfile { ContextWindowTokens = 1_000_000 };
        Assert.True(large.ScaledToolResultMaxChars > small.ScaledToolResultMaxChars);
    }
}
