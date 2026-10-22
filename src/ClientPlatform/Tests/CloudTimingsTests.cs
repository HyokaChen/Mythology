using ClientPlatform;
using Xunit;

namespace ClientPlatform.Tests;

/// <summary>
/// CloudTimings 共享时序常量测试（Desktop / TUI 共用收口）：
/// 编排层（探活超时 10s、心跳间隔 5 分钟、滑动续期 30 天与 slack 1 天、Toast 节流冷却 60s、
/// 登录 HTTP 无限超时）与协议层（OAuth 回调 120s、非流式 API 60s、流式 SSE 客户端无限）
/// 全部云端时序——此前两端/各客户端各自内联，收口后单点可配；此处锁定值防漂移。
/// </summary>
public sealed class CloudTimingsTests
{
    [Fact]
    public void ProbeTimeout_IsTenSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), CloudTimings.ProbeTimeout);
    }

    [Fact]
    public void HeartbeatInterval_IsFiveMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), CloudTimings.HeartbeatInterval);
    }

    [Fact]
    public void SessionLifetime_IsThirtyDays()
    {
        Assert.Equal(TimeSpan.FromDays(30), CloudTimings.SessionLifetime);
    }

    [Fact]
    public void RenewalSlack_IsOneDay()
    {
        Assert.Equal(TimeSpan.FromDays(1), CloudTimings.RenewalSlack);
    }

    [Fact]
    public void ToastCooldown_IsSixtySeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), CloudTimings.ToastCooldown);
    }

    [Fact]
    public void LoginHttpTimeout_IsInfinite()
    {
        Assert.Equal(Timeout.InfiniteTimeSpan, CloudTimings.LoginHttpTimeout);
    }

    [Fact]
    public void OAuthCallbackTimeout_IsTwoMinutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), CloudTimings.OAuthCallbackTimeout);
    }

    [Fact]
    public void CloudApiTimeout_IsSixtySeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), CloudTimings.CloudApiTimeout);
    }

    [Fact]
    public void StreamingClientTimeout_IsInfinite()
    {
        Assert.Equal(Timeout.InfiniteTimeSpan, CloudTimings.StreamingClientTimeout);
    }
}
