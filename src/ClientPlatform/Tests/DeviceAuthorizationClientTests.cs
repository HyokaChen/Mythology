using System.Net;
using System.Text;
using ClientPlatform;
using Xunit;

namespace ClientPlatform.Tests;

public sealed class DeviceAuthorizationClientTests
{
    [Fact]
    public async Task AuthorizeAsync_DiscoveryWithoutDeviceEndpoint_FallsBackToHardcoded()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/.well-known/oauth-authorization-server")
            {
                // 旧服务器元数据：只有 authorization_endpoint（OAuth 授权页入口），
                // 无 device_authorization_endpoint（RFC 8628 字段）→ 必须回退硬编码路径，
                // 绝不能把设备流 payload POST 到 authorization_endpoint。
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"authorization_endpoint":"http://localhost/v1/oauth/authorize","token_endpoint":"http://localhost/v1/oauth/token"}""",
                        Encoding.UTF8,
                        "application/json"
                    ),
                };
            }
            Assert.Equal("/v1/device/authorize", request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"device-code":"abc123","user-code":"ABCDEFGH","verification-uri":"https://example.com/app/device?user_code=ABCDEFGH","expires-in-seconds":600,"poll-interval-seconds":3}""",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        });
        var client = new DeviceAuthorizationClient(new HttpClient(handler), "http://localhost");

        var auth = await client.AuthorizeAsync("TUI-iMac", CancellationToken.None);

        Assert.Equal("abc123", auth.DeviceCode);
        Assert.Equal("ABCDEFGH", auth.UserCode);
        Assert.Equal(600, auth.ExpiresInSeconds);
        Assert.Equal(3, auth.PollIntervalSeconds);
    }

    [Fact]
    public async Task AuthorizeAsync_UsesDeviceAuthorizationEndpointFromDiscovery()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/.well-known/oauth-authorization-server")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"device-authorization_endpoint":"http://cloud.test/v1/device/authorize"}""",
                        Encoding.UTF8,
                        "application/json"
                    ),
                };
            }
            // discovery 200 分支：必须 POST 到声明的 device_authorization_endpoint
            // （而不是 GET-only 的 authorization_endpoint /v1/oauth/authorize）
            Assert.Equal("/v1/device/authorize", request.RequestUri!.AbsolutePath);
            Assert.Equal(HttpMethod.Post, request.Method);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"device-code":"xyz789","user-code":"KLMN2345","verification-uri":"https://example.com/app/device?user_code=KLMN2345","expires-in-seconds":600,"poll-interval-seconds":3}""",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        });
        var client = new DeviceAuthorizationClient(new HttpClient(handler), "http://cloud.test");

        var auth = await client.AuthorizeAsync("TUI-iMac", CancellationToken.None);

        Assert.Equal("xyz789", auth.DeviceCode);
        Assert.Equal("KLMN2345", auth.UserCode);
        Assert.Equal(600, auth.ExpiresInSeconds);
    }

    [Fact]
    public async Task PollTokenAsync_Pending_ReturnsNull()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent(
                """{"status":"pending"}""",
                Encoding.UTF8,
                "application/json"
            ),
        });
        var client = new DeviceAuthorizationClient(new HttpClient(handler), "http://localhost");

        var result = await client.PollTokenAsync("abc123", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task PollTokenLegacy_ExpiredConflict_ThrowsExpiredGrant()
    {
        // 409 + 结构化 {status:"expired"} → 终止轮询并抛 expired_grant（区别于 pending 的继续轮询）
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent(
                """{"status":"expired","message":"This device grant has expired; please start a new one."}""",
                Encoding.UTF8,
                "application/json"
            ),
        });
        var client = new DeviceAuthorizationClient(new HttpClient(handler), "http://localhost");

        var ex = await Assert.ThrowsAsync<CloudApiException>(() =>
            client.PollTokenAsync("abc123", CancellationToken.None)
        );

        Assert.Equal("expired_grant", ex.Code);
    }

    [Fact]
    public async Task PollTokenAsync_SseExpiredGrant_ThrowsImmediately()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "event: error\n" + "data: {\"code\":\"expired_grant\",\"detail\":\"expired\"}\n\n",
                Encoding.UTF8,
                "text/event-stream"
            ),
        });
        var client = new DeviceAuthorizationClient(new HttpClient(handler), "http://localhost");

        var exception = await Assert.ThrowsAsync<CloudApiException>(() =>
            client.PollTokenAsync("abc123", CancellationToken.None)
        );

        Assert.Equal("expired_grant", exception.Code);
        Assert.Equal("expired", exception.Message);
    }

    [Fact]
    public async Task PollTokenAsync_Approved_ReturnsKey()
    {
        var handler = new StubHandler(request =>
        {
            // SSE 优先尝试 /token/stream（JSON → 回退），再走 /token
            Assert.Contains("/v1/device/token", request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"api-key":"mk_live_dev123_secret","public-id":"dev123","workspace-id":"ws1","plan":"free"}""",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        });
        var client = new DeviceAuthorizationClient(new HttpClient(handler), "http://localhost");

        var token = await client.PollTokenAsync("abc123", CancellationToken.None);

        Assert.NotNull(token);
        Assert.Equal("mk_live_dev123_secret", token!.ApiKey);
        Assert.Equal("dev123", token.PublicId);
    }

    [Fact]
    public async Task PollTokenAsync_Approved_ParsesServerExpiresAt()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"api-key":"mk_live_dev123_secret","public-id":"dev123","workspace-id":"ws1","plan":"free","expires-at":"2026-09-17T00:00:00Z"}""",
                Encoding.UTF8,
                "application/json"
            ),
        });
        var client = new DeviceAuthorizationClient(new HttpClient(handler), "http://localhost");

        var token = await client.PollTokenAsync("abc123", CancellationToken.None);

        // §13 Tier 2：服务端 expiresAt 可选字段被解析为 ExpiresAt
        Assert.NotNull(token);
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero), token!.ExpiresAt);
    }

    [Fact]
    public async Task PollTokenAsync_Approved_NoExpiresAt_IsNull()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"api-key":"mk_live_dev123_secret","public-id":"dev123","workspace-id":"ws1","plan":"free"}""",
                Encoding.UTF8,
                "application/json"
            ),
        });
        var client = new DeviceAuthorizationClient(new HttpClient(handler), "http://localhost");

        var token = await client.PollTokenAsync("abc123", CancellationToken.None);

        // 旧服务端不返回该字段 → null，客户端回退滑动续期（向后兼容）
        Assert.NotNull(token);
        Assert.Null(token!.ExpiresAt);
    }

    [Fact]
    public async Task PollTokenAsync_SseApproved_ReturnsToken()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal("/v1/device/token/stream", request.RequestUri!.AbsolutePath);
            Assert.Contains(request.Headers.Accept, h => h.MediaType == "text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "event: approved\ndata: {\"api-key\":\"mk_live_dev123_secret\",\"public-id\":\"dev123\",\"workspace-id\":\"ws1\",\"plan\":\"free\"}\n\n",
                    Encoding.UTF8,
                    "text/event-stream"
                ),
            };
        });
        var client = new DeviceAuthorizationClient(new HttpClient(handler), "http://localhost");

        var token = await client.PollTokenAsync("abc123", CancellationToken.None);

        Assert.NotNull(token);
        Assert.Equal("mk_live_dev123_secret", token!.ApiKey);
        Assert.Equal("dev123", token.PublicId);
        Assert.Equal("free", token!.Plan);
    }

    [Fact]
    public void CloudSessionStore_RoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"myth-cloud-session-{Guid.NewGuid():N}");
        try
        {
            var store = new CloudSessionStore(dir);
            store.Save(
                new CloudSession("mk_live_abc_secret", "abc", "ws1", "free", DateTimeOffset.UtcNow)
            );

            var loaded = store.Load();

            Assert.NotNull(loaded);
            Assert.Equal("mk_live_abc_secret", loaded!.ApiKey);
            Assert.Equal("abc", loaded.PublicId);
            Assert.Equal("ws1", loaded.WorkspaceId);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CloudSessionStore_MissingFile_ReturnsNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"myth-cloud-empty-{Guid.NewGuid():N}");
        try
        {
            var store = new CloudSessionStore(dir);
            Assert.Null(store.Load());
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(respond(request));
    }
}
