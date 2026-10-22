using StabilityHarness.Lyapunov;
using Xunit;

namespace StabilityHarness.Tests;

/// <summary>
/// 监控器归零语义（接续换页 / compact 后的公共修复）：
/// Unfreeze 在未冻结时直接返回（这是「pre-flight 溢出 → 换页 → 重试仍被拦截」的根因），
/// 而 Reset + SeedTokenCount 才能真正把 TotalTokens 拉回新基线。
/// </summary>
public sealed class MonitorResetSemanticsTests
{
    private static LyapunovMonitor CreateMonitor(long budget) =>
        new(lambda: 1.0, window: 3, budgetCeiling: budget);

    [Fact]
    public void Unfreeze_OnUnfrozenMonitor_KeepsTotalTokens()
    {
        var monitor = CreateMonitor(budget: 1_000_000);
        monitor.RecordStep(500_000);

        Assert.False(monitor.IsFrozen);
        monitor.Unfreeze(1_000); // 未冻结 → 早退，TotalTokens 不变

        Assert.Equal(500_000, monitor.TotalTokens);
    }

    [Fact]
    public void ResetThenSeed_SetsTotalTokensToSeed()
    {
        var monitor = CreateMonitor(budget: 1_000_000);
        monitor.RecordStep(500_000);

        monitor.Reset();
        Assert.Equal(0, monitor.TotalTokens);
        Assert.False(monitor.IsFrozen);

        monitor.SeedTokenCount(1_200); // 空页 ≈ 固定开销
        Assert.Equal(1_200, monitor.TotalTokens);
    }

    [Fact]
    public void ResetThenSeed_ZeroSeed_LeavesZero_ForFullPromptSeeding()
    {
        var monitor = CreateMonitor(budget: 1_000_000);
        monitor.RecordStep(900_000);

        monitor.Reset();
        monitor.SeedTokenCount(0); // 0 不生效 → 下一轮增量走 full prompt 分支

        Assert.Equal(0, monitor.TotalTokens);
        monitor.RecordStep(30_000);
        Assert.Equal(30_000, monitor.TotalTokens);
    }

    [Fact]
    public void Unfreeze_OnFrozenMonitor_ResetsFreezeButKeepsHistorySemantics()
    {
        var monitor = CreateMonitor(budget: 1_000);
        // 预算耗尽抛的是 StabilityHarness.Lyapunov.BudgetExhaustedException（本项目内的类型）
        Assert.Throws<BudgetExhaustedException>(() => monitor.RecordStep(1_500));
        Assert.True(monitor.IsFrozen);

        monitor.Unfreeze(10);
        Assert.False(monitor.IsFrozen);
        Assert.Equal(10, monitor.TotalTokens);
    }
}
