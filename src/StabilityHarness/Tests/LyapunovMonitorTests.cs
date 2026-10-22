using Core.Stability;
using StabilityHarness.Lyapunov;
using Xunit;

namespace StabilityHarness.Tests;

public class LyapunovMonitorTests
{
    [Fact]
    public void RecordStep_StableWhenEnergyDecreases()
    {
        var monitor = new LyapunovMonitor(lambda: 1.0, window: 3);
        // Step 1: tokens=100, errors=0 → energy=100
        var s1 = monitor.RecordStep(100);
        Assert.Equal(StabilityStatus.Stable, s1);

        // Step 2: tokens=50, errors=0 → energy=50 (decreasing → stable)
        var s2 = monitor.RecordStep(50);
        Assert.Equal(StabilityStatus.Stable, s2);
    }

    [Fact]
    public void RecordStep_WarningWhenEnergyIncreases()
    {
        var monitor = new LyapunovMonitor(lambda: 1.0, window: 3);
        monitor.RecordStep(10); // energy=10
        var s = monitor.RecordStep(50); // energy=50 (increasing → warning)
        Assert.Equal(StabilityStatus.Warning, s);
    }

    [Fact]
    public void RecordStep_TripsAfterConsecutiveIncreases()
    {
        var monitor = new LyapunovMonitor(lambda: 1.0, window: 3);
        monitor.RecordStep(10); // energy=10
        monitor.RecordStep(20); // energy=20, consecutive=1
        monitor.RecordStep(30); // energy=30, consecutive=2

        // 3rd consecutive increase → trips
        Assert.Throws<StabilityViolationException>(() => monitor.RecordStep(40));
        Assert.True(monitor.IsTripped);
    }

    [Fact]
    public void RecordStep_ResetsConsecutiveOnDecrease()
    {
        var monitor = new LyapunovMonitor(lambda: 1.0, window: 3);
        monitor.RecordStep(10); // energy=10
        monitor.RecordStep(20); // energy=20, consecutive=1
        monitor.RecordStep(5); // energy=5, consecutive=0 (decrease resets)

        var s = monitor.RecordStep(3); // energy=3, still decreasing
        Assert.Equal(StabilityStatus.Stable, s);
    }

    [Fact]
    public void RecordStep_FrozenWhenBudgetExhausted()
    {
        var monitor = new LyapunovMonitor(lambda: 1.0, window: 3, budgetCeiling: 100);
        monitor.RecordStep(60);
        Assert.Throws<Lyapunov.BudgetExhaustedException>(() => monitor.RecordStep(50));
        Assert.True(monitor.IsFrozen);
    }

    [Fact]
    public void RecordStep_ReturnsTrippedAfterTrip()
    {
        var monitor = new LyapunovMonitor(lambda: 1.0, window: 2);
        monitor.RecordStep(10); // energy=10
        monitor.RecordStep(20); // energy=20, consecutive=1
        Assert.Throws<StabilityViolationException>(() => monitor.RecordStep(30)); // energy=30, consecutive=2 → trip
        var s = monitor.RecordStep(5);
        Assert.Equal(StabilityStatus.Tripped, s);
    }

    [Fact]
    public void ResetEscalation_ClearsTripState()
    {
        var monitor = new LyapunovMonitor(lambda: 1.0, window: 3);
        monitor.RecordStep(10); // energy=10
        monitor.RecordStep(20); // energy=20, consecutive=1
        monitor.RecordStep(30); // energy=30, consecutive=2
        Assert.Throws<StabilityViolationException>(() => monitor.RecordStep(40)); // energy=40, consecutive=3 → trip
        Assert.True(monitor.IsTripped);

        monitor.ResetEscalation();
        Assert.False(monitor.IsTripped);
        Assert.Equal(StabilityStatus.Stable, monitor.CheckStability());

        var after = monitor.RecordStep(5); // 能量回落 → 稳定，不 Tripped
        Assert.NotEqual(StabilityStatus.Tripped, after);
    }

    [Fact]
    public void RecordStep_ErrorIncreasesEnergy()
    {
        var monitor = new LyapunovMonitor(lambda: 10.0, window: 3);
        monitor.RecordStep(10, errors: 0); // energy=10
        var s = monitor.RecordStep(5, errors: 2); // energy=5 + 10*2 = 25 (increasing)
        Assert.Equal(StabilityStatus.Warning, s);
    }

    [Fact]
    public void CheckStability_ReflectsCurrentState()
    {
        var monitor = new LyapunovMonitor(lambda: 1.0, window: 3);
        Assert.Equal(StabilityStatus.Stable, monitor.CheckStability());

        monitor.RecordStep(10);
        monitor.RecordStep(20); // warning
        Assert.Equal(StabilityStatus.Warning, monitor.CheckStability());
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        var monitor = new LyapunovMonitor(lambda: 1.0, window: 3);
        monitor.RecordStep(10);
        monitor.RecordStep(20);
        monitor.Reset();

        Assert.Equal(0, monitor.TotalTokens);
        Assert.Equal(0, monitor.TotalErrors);
        Assert.Equal(0, monitor.TotalSteps);
        Assert.False(monitor.IsTripped);
        Assert.False(monitor.IsFrozen);
        Assert.Empty(monitor.EnergyHistory);
    }

    [Fact]
    public void Constructor_ThrowsOnInvalidLambda()
    {
        Assert.Throws<ArgumentException>(() => new LyapunovMonitor(lambda: 0));
        Assert.Throws<ArgumentException>(() => new LyapunovMonitor(lambda: -1));
    }

    [Fact]
    public void Constructor_ThrowsOnInvalidWindow()
    {
        Assert.Throws<ArgumentException>(() => new LyapunovMonitor(lambda: 1.0, window: 0));
    }

    [Fact]
    public void TotalTokens_Accumulates()
    {
        var monitor = new LyapunovMonitor(lambda: 1.0, window: 3);
        monitor.RecordStep(100);
        monitor.RecordStep(50);
        Assert.Equal(150, monitor.TotalTokens);
    }

    [Fact]
    public void TotalErrors_Accumulates()
    {
        var monitor = new LyapunovMonitor(lambda: 1.0, window: 3);
        monitor.RecordStep(10, errors: 1);
        monitor.RecordStep(5, errors: 2);
        Assert.Equal(3, monitor.TotalErrors);
    }

    [Fact]
    public void EnergyHistory_TracksAllSteps()
    {
        var monitor = new LyapunovMonitor(lambda: 2.0, window: 3);
        monitor.RecordStep(10, errors: 0); // energy=10
        monitor.RecordStep(5, errors: 1); // energy=5+2=7

        Assert.Equal(2, monitor.EnergyHistory.Count);
        Assert.Equal(10.0, monitor.EnergyHistory[0]);
        Assert.Equal(7.0, monitor.EnergyHistory[1]);
    }
}
