using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Http.Resilience;
using Xunit;

namespace ClientPlatform.Tests;

/// <summary>
/// RetrievalRestClient 协议测试（Stub handler）：
/// 路径/方法 / X-API-Key 鉴权（非 Bearer——服务端数据面只认 X-API-Key，API Key 走 Bearer 会被拒 401）/
/// camelCase 请求体（服务端 JsonDocument 手工读取字面量键，多词键 kebab 化会被静默丢弃）/
/// 402 → usage_limit_exceeded 且不重试 / 401 → 吊销总线。
/// </summary>
public sealed class RetrievalRestClientTests
{
    private sealed record Captured(
        HttpMethod Method,
        string Path,
        string? Body,
        string? ApiKey,
        bool HasAuthorization
    );

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static (RetrievalRestClient Client, List<Captured> Requests) CreateClient(
        Func<Captured, HttpResponseMessage> responder,
        CloudAuthInvalidationBus? bus = null
    )
    {
        var requests = new List<Captured>();
        var handler = new StubHandler(request =>
        {
            var body = request.Content is null
                ? null
                : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var captured = new Captured(
                request.Method,
                request.RequestUri!.AbsolutePath,
                body,
                request.Headers.TryGetValues("X-API-Key", out var values)
                    ? values.FirstOrDefault()
                    : null,
                request.Headers.Authorization is not null
            );
            requests.Add(captured);
            return responder(captured);
        });
        var client = new RetrievalRestClient(
            "http://localhost",
            () => "mk_test_public-secret",
            http: ResilientClient(handler),
            invalidationBus: bus
        );
        return (client, requests);
    }

    /// <summary>用官方重试流水线包裹 Stub（对齐 DI "MythologyCloud" 客户端语义）。</summary>
    private static HttpClient ResilientClient(StubHandler handler) =>
        new(new ResilienceHandler(CloudHttpResilience.BuildPipeline()) { InnerHandler = handler });

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(responder(request));
    }

    [Fact]
    public async Task Scrape_SendsXApiKey_AndCamelCaseBody()
    {
        var (client, requests) = CreateClient(_ => Json("""{"status":"success"}"""));

        var result = await client.ScrapeAsync(
            new
            {
                url = "https://example.com",
                formats = new[] { "markdown" },
                onlyMainContent = true,
                timeout = 30000,
            },
            TestContext.Current.CancellationToken
        );

        Assert.Contains("success", result, StringComparison.Ordinal);
        var request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/retrieval/scrape", request.Path);
        Assert.Equal("mk_test_public-secret", request.ApiKey);
        Assert.False(request.HasAuthorization); // API Key 不上 Bearer 通道（服务端只认 X-API-Key）
        var body = JsonDocument.Parse(request.Body!).RootElement;
        Assert.True(body.GetProperty("onlyMainContent").GetBoolean());
        Assert.False(body.TryGetProperty("only-main-content", out _));
    }

    [Fact]
    public async Task Search_SendsQueryAndLimit()
    {
        var (client, requests) = CreateClient(_ => Json("""{"hits":[]}"""));

        await client.SearchAsync(
            new { query = "测试查询", limit = 8 },
            TestContext.Current.CancellationToken
        );

        var request = Assert.Single(requests);
        Assert.Equal("/v1/retrieval/search", request.Path);
        Assert.Equal("mk_test_public-secret", request.ApiKey);
        var body = JsonDocument.Parse(request.Body!).RootElement;
        Assert.Equal("测试查询", body.GetProperty("query").GetString());
        Assert.Equal(8, body.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task MapAndDiscover_UseDedicatedPaths()
    {
        var (client, requests) = CreateClient(_ => Json("""{"links":[]}"""));

        await client.MapAsync(
            new { url = "https://example.com", limit = 100 },
            TestContext.Current.CancellationToken
        );
        await client.DiscoverAsync(
            new { url = "https://example.com" },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(2, requests.Count);
        Assert.Equal("/v1/retrieval/map", requests[0].Path);
        Assert.Equal("/v1/retrieval/discover", requests[1].Path);
        Assert.All(requests, request => Assert.Equal("mk_test_public-secret", request.ApiKey));
    }

    [Fact]
    public async Task PaymentRequired_MapsToUsageLimitExceeded_WithoutRetry()
    {
        var (client, requests) = CreateClient(_ => new HttpResponseMessage(
            HttpStatusCode.PaymentRequired
        )
        {
            Content = new StringContent(
                """{"type":"about:blank","title":"usage_limit_exceeded","status":402,"code":"usage_limit_exceeded"}""",
                Encoding.UTF8,
                "application/problem+json"
            ),
        });

        var ex = await Assert.ThrowsAsync<CloudApiException>(() =>
            client.ScrapeAsync(
                new { url = "https://example.com" },
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal("usage_limit_exceeded", ex.Code);
        // 402 非瞬态：不得重试（配额耗尽重试无意义，且会放大调用计数）。
        Assert.Single(requests);
    }

    [Fact]
    public async Task Unauthorized_RaisesInvalidationBus_AndThrows()
    {
        var bus = new CloudAuthInvalidationBus();
        var raised = 0;
        bus.Invalidated += _ => raised++;

        var (client, _) = CreateClient(
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    """{"code":"invalid_credentials"}""",
                    Encoding.UTF8,
                    "application/json"
                ),
            },
            bus: bus
        );

        var ex = await Assert.ThrowsAsync<CloudApiException>(() =>
            client.SearchAsync(new { query = "x" }, TestContext.Current.CancellationToken)
        );

        // body-first：服务端 code 为权威（真实数据面 401 回 invalid_credentials）
        Assert.Equal("invalid_credentials", ex.Code);
        Assert.Equal(1, raised);
    }
}
