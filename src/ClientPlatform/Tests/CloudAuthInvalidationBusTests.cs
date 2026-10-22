using System.Net;
using ClientPlatform;
using Xunit;

namespace ClientPlatform.Tests;

/// <summary>
/// CloudAuthInvalidationBus 直接测试（§13.4 事件驱动失效检测）：
/// 401/403 判定 + 事件触发 + 路径上下文传递；非吊销状态不触发。
/// </summary>
public sealed class CloudAuthInvalidationBusTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void IsCredentialInvalid_401Or403_ReturnsTrue_AndRaises(HttpStatusCode status)
    {
        var bus = new CloudAuthInvalidationBus();
        var raised = 0;
        string? lastContext = null;
        bus.Invalidated += ctx =>
        {
            raised++;
            lastContext = ctx;
        };

        using var response = new HttpResponseMessage(status)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://cloud/v1/agent/runs"),
        };

        Assert.True(bus.IsCredentialInvalid(response));
        Assert.Equal(1, raised);
        Assert.Equal("/v1/agent/runs", lastContext);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public void IsCredentialInvalid_NonAuthStatus_ReturnsFalse_NoRaise(HttpStatusCode status)
    {
        var bus = new CloudAuthInvalidationBus();
        var raised = 0;
        bus.Invalidated += _ => raised++;

        using var response = new HttpResponseMessage(status)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://cloud/v1/agent/runs"),
        };

        Assert.False(bus.IsCredentialInvalid(response));
        Assert.Equal(0, raised);
    }

    [Fact]
    public void IsCredentialInvalid_NoRequestUri_RaisesWithNullContext()
    {
        var bus = new CloudAuthInvalidationBus();
        string? context = "unset";
        bus.Invalidated += ctx => context = ctx;

        using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);

        Assert.True(bus.IsCredentialInvalid(response));
        Assert.Null(context);
    }

    [Fact]
    public void Raise_Manually_NotifiesSubscribers()
    {
        var bus = new CloudAuthInvalidationBus();
        var raised = 0;
        string? lastContext = null;
        bus.Invalidated += ctx =>
        {
            raised++;
            lastContext = ctx;
        };

        bus.Raise("manual-context");

        Assert.Equal(1, raised);
        Assert.Equal("manual-context", lastContext);
    }

    [Fact]
    public void Raise_NoSubscribers_DoesNotThrow()
    {
        var bus = new CloudAuthInvalidationBus();
        bus.Raise("no-subscribers");
    }
}
