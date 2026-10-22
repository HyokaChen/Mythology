using Core.Stability;
using StabilityHarness.Lyapunov;
using Xunit;

namespace StabilityHarness.Tests;

public class CompositeStabilityMonitorTests
{
    [Fact]
    public void RecordStep_StableWhenBothMonitorsStable()
    {
        var config = new StabilityConfig { TokenBudget = 100_000 };
        config.ApplyProfile(new ModelContextProfile { ContextWindowTokens = 128_000 });
        var monitor = new CompositeStabilityMonitor(config);

        // Warmup: fill warmup turns with decreasing tokens
        for (var i = 0; i < config.EffectiveWarmupTurns; i++)
            monitor.RecordStep(100);

        // Post-warmup: stable step
        var status = monitor.RecordStep(50);
        Assert.Equal(StabilityStatus.Stable, status);
    }

    [Fact]
    public void RecordStep_TripsWhenGuardTrips()
    {
        var config = new StabilityConfig { TokenBudget = 1_000_000 };
        config.ApplyProfile(new ModelContextProfile { ContextWindowTokens = 128_000 });
        var monitor = new CompositeStabilityMonitor(config);

        // Warmup with small tokens
        for (var i = 0; i < config.EffectiveWarmupTurns; i++)
            monitor.RecordStep(100);

        // Exceed budget gate + ratio threshold for consecutive turns
        for (var i = 0; i < config.EffectiveWindow + 1; i++)
        {
            try
            {
                monitor.RecordStep(10_000);
            }
            catch (StabilityViolationException)
            {
                break;
            }
        }

        Assert.True(monitor.IsTripped);
    }

    [Fact]
    public void RecordStep_FrozenWhenBudgetExhausted()
    {
        var config = new StabilityConfig { TokenBudget = 500 };
        var monitor = new CompositeStabilityMonitor(config);

        try
        {
            monitor.RecordStep(600);
        }
        catch (Core.Stability.BudgetExhaustedException) { }

        Assert.True(monitor.IsFrozen);
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        var config = new StabilityConfig { TokenBudget = 100_000 };
        var monitor = new CompositeStabilityMonitor(config);

        monitor.RecordStep(100);
        monitor.RecordStep(200);
        monitor.Reset();

        Assert.Equal(0, monitor.TotalTokens);
        Assert.Equal(0, monitor.TotalSteps);
        Assert.False(monitor.IsTripped);
        Assert.False(monitor.IsFrozen);
    }

    [Fact]
    public void Unfreeze_ResetsBothMonitors()
    {
        var config = new StabilityConfig { TokenBudget = 500 };
        var monitor = new CompositeStabilityMonitor(config);

        try
        {
            monitor.RecordStep(600);
        }
        catch (Core.Stability.BudgetExhaustedException) { }
        Assert.True(monitor.IsFrozen);

        monitor.Unfreeze(100);
        Assert.False(monitor.IsFrozen);
    }

    [Fact]
    public void ResetBaseline_ResetsBothMonitors()
    {
        var config = new StabilityConfig { TokenBudget = 100_000 };
        config.ApplyProfile(new ModelContextProfile { ContextWindowTokens = 128_000 });
        var monitor = new CompositeStabilityMonitor(config);

        // Warmup
        for (var i = 0; i < config.EffectiveWarmupTurns; i++)
            monitor.RecordStep(100);

        Assert.NotNull(monitor.Baseline);
        monitor.ResetBaseline();
        Assert.Null(monitor.Baseline);
    }

    [Fact]
    public void ApplyProfile_PropagatesToBothMonitors()
    {
        var config = new StabilityConfig();
        var monitor = new CompositeStabilityMonitor(config);

        var profile = new ModelContextProfile { ContextWindowTokens = 1_000_000 };
        monitor.ApplyProfile(profile);

        Assert.Equal(1_000_000, monitor.GetTokenBudget());
    }

    [Fact]
    public void GuardAndLyapunov_AreAccessible()
    {
        var config = new StabilityConfig();
        var monitor = new CompositeStabilityMonitor(config);

        Assert.NotNull(monitor.Guard);
        Assert.NotNull(monitor.Lyapunov);
    }

    [Fact]
    public void IsStable_RequiresBothStable()
    {
        var config = new StabilityConfig { TokenBudget = 100_000 };
        config.ApplyProfile(new ModelContextProfile { ContextWindowTokens = 128_000 });
        var monitor = new CompositeStabilityMonitor(config);

        // Warmup
        for (var i = 0; i < config.EffectiveWarmupTurns; i++)
            monitor.RecordStep(100);

        // GRG is stable, Lyapunov is stable → composite is stable
        Assert.True(monitor.IsStable);
    }

    [Fact]
    public void ThinkingTokens_ExcludedFromRatioButIncludedInBudget()
    {
        var config = new StabilityConfig { TokenBudget = 100_000 };
        config.ApplyProfile(new ModelContextProfile { ContextWindowTokens = 128_000 });
        var monitor = new CompositeStabilityMonitor(config);

        // Warmup with thinking tokens
        for (var i = 0; i < config.EffectiveWarmupTurns; i++)
            monitor.RecordStep(500, thinkingTokens: 400, errors: 0);

        // Baseline should be based on non-thinking tokens (100)
        Assert.NotNull(monitor.Baseline);
        Assert.True(monitor.Baseline < 200); // ~100, not 500
    }

    [Fact]
    public void ResetEscalation_ClearsLyapunovTrip_NextRunDoesNotImmediatelyBreak()
    {
        // 默认配置：window=3, DeltaVTolerance=0.10, warmup=3, RatioThreshold=2.0, BudgetGate=0
        var monitor = new CompositeStabilityMonitor(new StabilityConfig());

        // 连续增长驱动 Lyapunov Tripped（复合层吞异常，只返回状态不抛出；
        // 注意 CheckStability 对 Lyapunov Tripped 的语义是 Unstable，循环只对 Tripped/Frozen 中断）
        monitor.RecordStep(100);
        monitor.RecordStep(130);
        monitor.RecordStep(170);
        var status = monitor.RecordStep(220);
        Assert.Equal(StabilityStatus.Tripped, status);
        Assert.True(monitor.IsTripped);

        // 新任务边界重置 → 不再 Tripped，且后续 RecordStep 不会立刻 Tripped
        monitor.ResetEscalation();
        Assert.False(monitor.IsTripped);
        Assert.NotEqual(StabilityStatus.Tripped, monitor.CheckStability());

        var after = monitor.RecordStep(300);
        Assert.NotEqual(StabilityStatus.Tripped, after);
        Assert.NotEqual(StabilityStatus.Tripped, monitor.CheckStability());
    }

    [Fact]
    public void ResetEscalation_ClearsGuardTrip()
    {
        var monitor = new CompositeStabilityMonitor(new StabilityConfig());

        // 3 轮 warmup 建立 baseline=100
        for (var i = 0; i < 3; i++)
            monitor.RecordStep(100);

        // 连续 3 轮 ratio=3.0 > 2.0 → GRG Tripped（第 6 步抛 StabilityViolationException，GRG 异常不被复合层吞掉）
        for (var i = 0; i < 3; i++)
        {
            try
            {
                monitor.RecordStep(300);
            }
            catch (StabilityViolationException)
            {
                break;
            }
        }
        Assert.True(monitor.IsTripped);
        Assert.Equal(StabilityStatus.Tripped, monitor.CheckStability());

        monitor.ResetEscalation();
        Assert.NotEqual(StabilityStatus.Tripped, monitor.CheckStability());

        var after = monitor.RecordStep(200);
        Assert.NotEqual(StabilityStatus.Tripped, after);
        Assert.NotEqual(StabilityStatus.Tripped, monitor.CheckStability());
    }
}

public class TypicalTurnTokensInterpolationTests
{
    [Theory]
    [InlineData(8_000, 800)]
    [InlineData(1_000_000, 5_000)]
    public void TypicalTurn_AnchorPoints_MatchExactly(long contextWindow, long expected)
    {
        var profile = new ModelContextProfile { ContextWindowTokens = contextWindow };
        Assert.Equal(expected, profile.TypicalTurnTokens);
    }

    [Theory]
    [InlineData(4_000, 800)] // Below minimum anchor → clamp to 800
    [InlineData(2_000_000, 5_000)] // Above maximum anchor → clamp to 5000
    public void TypicalTurn_OutOfRange_ClampsToAnchor(long contextWindow, long expected)
    {
        var profile = new ModelContextProfile { ContextWindowTokens = contextWindow };
        Assert.Equal(expected, profile.TypicalTurnTokens);
    }

    [Fact]
    public void TypicalTurn_Midpoint_BetweenAnchors()
    {
        // Midpoint between 8K (800) and 32K (1200) = 20K
        // Expected: 800 + (20000-8000)/(32000-8000) * (1200-800) = 800 + 0.5 * 400 = 1000
        var profile = new ModelContextProfile { ContextWindowTokens = 20_000 };
        Assert.Equal(1_000, profile.TypicalTurnTokens);
    }

    [Fact]
    public void TypicalTurn_NoJumpAtBoundary()
    {
        // Old behavior: 99_999 → 2000, 100_000 → 3000 (jump of 1000!)
        // New behavior: smooth interpolation
        var profile99 = new ModelContextProfile { ContextWindowTokens = 99_999 };
        var profile100 = new ModelContextProfile { ContextWindowTokens = 100_000 };

        var diff = Math.Abs(profile100.TypicalTurnTokens - profile99.TypicalTurnTokens);
        Assert.True(diff < 100, $"Jump at 100K boundary: {diff} tokens (should be < 100)");
    }

    [Fact]
    public void TypicalTurn_AdaptiveThinking_Multiplied()
    {
        var profile = new ModelContextProfile
        {
            ContextWindowTokens = 128_000,
            IsAdaptiveThinking = true,
        };
        var profileNormal = new ModelContextProfile
        {
            ContextWindowTokens = 128_000,
            IsAdaptiveThinking = false,
        };

        Assert.Equal(profileNormal.TypicalTurnTokens * 3 / 2, profile.TypicalTurnTokens);
    }

    [Fact]
    public void TypicalTurn_MonotonicallyIncreasing()
    {
        // Verify monotonicity across the full range
        long prev = 0;
        for (var ctx = 8_000L; ctx <= 1_000_000; ctx += 8_000)
        {
            var profile = new ModelContextProfile { ContextWindowTokens = ctx };
            var current = profile.TypicalTurnTokens;
            Assert.True(current >= prev, $"Non-monotonic at {ctx}: {current} < {prev}");
            prev = current;
        }
    }
}
