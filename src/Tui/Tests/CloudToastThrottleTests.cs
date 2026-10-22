using Core.Cloud;
using Tui.Services;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// CloudToastThrottle 单元测试（云端状态变更 Toast 节流）：
/// - 同一降级原因（CloudConnectionState 键）在冷却窗口内只提示一次——登录后探活降级 /
///   心跳 401 / 吊销 401（同一 Authenticating 键）/ 登录失败（Unavailable 键）共用闸门；
/// - 冷却窗口过后同一原因重新提示；
/// - 不同降级原因互不节流；
/// - Connected（恢复）重置闸门——恢复后的新降级事件立即重新提示；
/// - Connected 本身永不抑制（成功静默）。
/// </summary>
public sealed class CloudToastThrottleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SameState_WithinCooldown_Suppressed()
    {
        var throttle = new CloudToastThrottle(TimeSpan.FromSeconds(60));

        Assert.False(throttle.ShouldSuppress(CloudConnectionState.Unavailable, Now));
        Assert.True(throttle.ShouldSuppress(CloudConnectionState.Unavailable, Now.AddSeconds(30)));
    }

    [Fact]
    public void SameState_AfterCooldown_NotSuppressed()
    {
        var throttle = new CloudToastThrottle(TimeSpan.FromSeconds(60));

        Assert.False(throttle.ShouldSuppress(CloudConnectionState.Unavailable, Now));
        Assert.False(throttle.ShouldSuppress(CloudConnectionState.Unavailable, Now.AddSeconds(61)));
    }

    [Fact]
    public void DifferentState_Immediately_NotSuppressed()
    {
        var throttle = new CloudToastThrottle(TimeSpan.FromSeconds(60));

        Assert.False(throttle.ShouldSuppress(CloudConnectionState.RateLimited, Now));
        // 不同降级原因 → 不节流（限流提示后立即出现不可达，是新的问题，值得再次提示）
        Assert.False(throttle.ShouldSuppress(CloudConnectionState.Unavailable, Now.AddSeconds(1)));
    }

    [Fact]
    public void Connected_ResetsGate_NewDegradeReNotified()
    {
        var throttle = new CloudToastThrottle(TimeSpan.FromSeconds(60));

        Assert.False(throttle.ShouldSuppress(CloudConnectionState.Unavailable, Now));
        Assert.True(throttle.ShouldSuppress(CloudConnectionState.Unavailable, Now.AddSeconds(10)));

        // 恢复 → 闸门重置
        Assert.False(throttle.ShouldSuppress(CloudConnectionState.Connected, Now.AddSeconds(20)));

        // 恢复后的新降级立即重新提示（即便仍落在原冷却窗口内）
        Assert.False(throttle.ShouldSuppress(CloudConnectionState.Unavailable, Now.AddSeconds(21)));
    }

    [Fact]
    public void Connected_IsNeverSuppressed()
    {
        var throttle = new CloudToastThrottle(TimeSpan.FromSeconds(60));

        Assert.False(throttle.ShouldSuppress(CloudConnectionState.Connected, Now));
        Assert.False(throttle.ShouldSuppress(CloudConnectionState.Connected, Now.AddSeconds(5)));
    }
}
