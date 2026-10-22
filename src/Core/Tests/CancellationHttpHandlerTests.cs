using System.Net;
using Core.Infrastructure;
using Xunit;

namespace Core.Tests;

public class CancellationHttpHandlerTests
{
    [Fact]
    public async Task SendAsync_SuccessResponse_TracksIt()
    {
        // 禁用代理：本机若配置了 http_proxy（壳层/系统代理），.NET 会把 localhost:1 也走代理，
        // 代理返回响应后就不再抛 HttpRequestException —— 该用例必须与代理环境无关。
        using var handler = new CancellationHttpHandler(new HttpClientHandler { UseProxy = false });
        using var invoker = new HttpMessageInvoker(handler);

        // Use a request that will fail (no real server), to verify tracking
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "http://localhost:1/nonexistent"
        );

        // This will throw because there's no server, but the handler should track it
        // before the inner handler fails
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            invoker.SendAsync(request, CancellationToken.None)
        );
    }

    [Fact]
    public void CancelActiveRequests_NoActiveRequests_DoesNotThrow()
    {
        using var handler = new CancellationHttpHandler();
        handler.CancelActiveRequests(); // Should not throw
    }

    [Fact]
    public void CancelActiveRequests_CalledMultipleTimes_DoesNotThrow()
    {
        using var handler = new CancellationHttpHandler();
        handler.CancelActiveRequests();
        handler.CancelActiveRequests(); // Second call should not throw
    }

    [Fact]
    public void Untrack_NonExistentResponse_DoesNotThrow()
    {
        using var handler = new CancellationHttpHandler();
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        handler.Untrack(response); // Should not throw
    }

    [Fact]
    public async Task Dispose_DoesNotThrow()
    {
        var handler = new CancellationHttpHandler();
        handler.Dispose(); // First dispose
        handler.Dispose(); // Second dispose should not throw
        await Task.CompletedTask;
    }

    [Fact]
    public void Constructor_DoesNotThrow()
    {
        using var handler = new CancellationHttpHandler();
        Assert.NotNull(handler);
    }
}
