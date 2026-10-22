using System.Net;
using System.Text;
using ClientPlatform;
using Xunit;

namespace ClientPlatform.Tests;

/// <summary>
/// DeviceAuthorizationClient 边界测试补充：
/// authorize 失败映射 / discovery 网络异常回退 / SSE error 非 expired 返回 null /
/// SSE 流结束无终态返回 null / 轮询 409 非 JSON body 继续轮询 / 轮询 401 错误映射 /
/// 空响应体 / SSE 乱序字段容错。
/// </summary>
public sealed class DeviceAuthorizationClientEdgeTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(respond(request));
    }

    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        };

    [Fact]
    public async Task AuthorizeAsync_ServerError_MapsProblemDetails()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(
            HttpStatusCode.ServiceUnavailable
        )
        {
            Content = new StringContent(
                """{"code":"service_unavailable","detail":"cloud down"}""",
                Encoding.UTF8,
                "application/json"
            ),
        });
        var client = new DeviceAuthorizationClient(new HttpClient(handler), "http://localhost");

        var ex = await Assert.ThrowsAsync<CloudApiException>(() =>
            client.AuthorizeAsync("TUI", CancellationToken.None)
        );

        Assert.Equal("service_unavailable", ex.Code);
        Assert.Equal("cloud down", ex.Message);
    }

    [Fact]
    public async Task AuthorizeAsync_DiscoveryHttpFailure_FallsBackToDefaultPath()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/.well-known/oauth-authorization-server")
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            Assert.Equal("/v1/device/authorize", request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"device-code":"d1","user-code":"AAAA1111","verification-uri":"https://x","expires-in-seconds":600,"poll-interval-seconds":3}""",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        });
        var client = new DeviceAuthorizationClient(new HttpClient(handler), "http://localhost");

        var auth = await client.AuthorizeAsync("TUI", CancellationToken.None);

        Assert.Equal("d1", auth.DeviceCode);
    }

    [Fact]
    public async Task AuthorizeAsync_EmptyBody_ThrowsInvalidResponse()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("", Encoding.UTF8, "application/json"),
        });
        var client = new DeviceAuthorizationClient(new HttpClient(handler), "http://localhost");

        var ex = await Assert.ThrowsAsync<CloudApiException>(() =>
            client.AuthorizeAsync("TUI", CancellationToken.None)
        );

        Assert.Equal("invalid_response", ex.Code);
    }

    [Fact]
    public async Task PollToken_SseErrorUnknownCode_ReturnsNull()
    {
        var handler = new StubHandler(_ =>
            Sse("event: error\ndata: {\"code\":\"unknown_code\",\"detail\":\"x\"}\n\n")
        );
        var client = new DeviceAuthorizationClient(new HttpClient(handler), "http://localhost");

        var result = await client.PollTokenAsync("abc", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task PollToken_SseStreamEndsWithoutTerminal_ReturnsNull()
    {
        var handler = new StubHandler(_ =>
            Sse("event: pending\ndata: {\"status\":\"waiting\"}\n\n")
        );
        var client = new DeviceAuthorizationClient(new HttpClient(handler), "http://localhost");

        var result = await client.PollTokenAsync("abc", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task PollToken_Legacy409NonJsonBody_TreatedAsPending()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("conflict", Encoding.UTF8, "text/plain"),
        });
        var client = new DeviceAuthorizationClient(new HttpClient(handler), "http://localhost");

        var result = await client.PollTokenAsync("abc", CancellationToken.None);

        Assert.Null(result); // 非结构化 body → pending，继续轮询
    }

    [Fact]
    public async Task PollToken_Legacy401_MapsError()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                """{"error":"invalid_grant","error_description":"bad device code"}""",
                Encoding.UTF8,
                "application/json"
            ),
        });
        var client = new DeviceAuthorizationClient(new HttpClient(handler), "http://localhost");

        var ex = await Assert.ThrowsAsync<CloudApiException>(() =>
            client.PollTokenAsync("abc", CancellationToken.None)
        );

        Assert.Equal("invalid_grant", ex.Code);
        Assert.Equal("bad device code", ex.Message);
    }

    [Fact]
    public async Task PollToken_SseError_ExpiredGrantWithoutDetail_UsesFallbackMessage()
    {
        var handler = new StubHandler(_ =>
            Sse("event: error\ndata: {\"code\":\"expired_grant\"}\n\n")
        );
        var client = new DeviceAuthorizationClient(new HttpClient(handler), "http://localhost");

        var ex = await Assert.ThrowsAsync<CloudApiException>(() =>
            client.PollTokenAsync("abc", CancellationToken.None)
        );

        Assert.Equal("expired_grant", ex.Code);
        Assert.Contains("请重新发起登录", ex.Message);
    }

    [Fact]
    public async Task PollToken_SseApproved_MissingDisplayName_DefaultsEmpty()
    {
        var handler = new StubHandler(_ =>
            Sse(
                "event: approved\ndata: {\"api-key\":\"mk_live_dev123_secret\",\"public-id\":\"dev123\",\"workspace-id\":\"ws1\",\"plan\":\"free\"}\n\n"
            )
        );
        var client = new DeviceAuthorizationClient(new HttpClient(handler), "http://localhost");

        var token = await client.PollTokenAsync("abc", CancellationToken.None);

        Assert.NotNull(token);
        Assert.Equal("", token!.DisplayName);
        Assert.Null(token.ExpiresAt);
    }

    [Fact]
    public async Task PollToken_SseApproved_InvalidJsonData_ThrowsInvalidResponse()
    {
        var handler = new StubHandler(_ => Sse("event: approved\ndata: not-json\n\n"));
        var client = new DeviceAuthorizationClient(new HttpClient(handler), "http://localhost");

        var ex = await Assert.ThrowsAsync<CloudApiException>(() =>
            client.PollTokenAsync("abc", CancellationToken.None)
        );

        Assert.Equal("invalid_response", ex.Code);
    }
}
