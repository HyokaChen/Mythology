using System.Net;
using System.Text;
using Microsoft.Extensions.Http.Resilience;
using Xunit;

namespace ClientPlatform.Tests;

/// <summary>
/// CrawlRestClient 协议测试（Stub handler）：
/// 提交携带 Idempotency-Key + camelCase 请求体 / 202 kebab 载荷解析 / 快照 include 查询 /
/// 取消 DELETE / Idempotency-Key 前置校验（不发 HTTP）/ 402 → usage_limit_exceeded。
/// </summary>
public sealed class CrawlRestClientTests
{
    private sealed record Captured(
        HttpMethod Method,
        string PathAndQuery,
        string? Body,
        string? ApiKey,
        string? IdempotencyKey
    );

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (CrawlRestClient Client, List<Captured> Requests) CreateClient(
        Func<Captured, HttpResponseMessage> responder
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
                request.RequestUri!.PathAndQuery,
                body,
                request.Headers.TryGetValues("X-API-Key", out var values)
                    ? values.FirstOrDefault()
                    : null,
                request.Headers.TryGetValues("Idempotency-Key", out var idem)
                    ? idem.FirstOrDefault()
                    : null
            );
            requests.Add(captured);
            return responder(captured);
        });
        var client = new CrawlRestClient(
            "http://localhost",
            () => "mk_test_public-secret",
            http: ResilientClient(handler)
        );
        return (client, requests);
    }

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
    public async Task Start_SendsIdempotencyKey_AndParsesKebabStartPayload()
    {
        var (client, requests) = CreateClient(_ =>
            Json(
                HttpStatusCode.Accepted,
                """{"job-id":"job_abc","status":"queued","reserved-units":25}"""
            )
        );

        var start = await client.StartAsync(
            new
            {
                url = "https://example.com",
                limit = 25,
                maxDepth = 2,
                perHostDelayMs = 500,
                maxConcurrency = 2,
                import = false,
            },
            "crawl-key-1",
            TestContext.Current.CancellationToken
        );

        Assert.Equal("job_abc", start.JobId);
        Assert.Equal("queued", start.Status);
        Assert.Equal(25, start.ReservedUnits);
        var request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/retrieval/crawl", request.PathAndQuery);
        Assert.Equal("mk_test_public-secret", request.ApiKey);
        Assert.Equal("crawl-key-1", request.IdempotencyKey);
        var body = System.Text.Json.JsonDocument.Parse(request.Body!).RootElement;
        Assert.Equal("https://example.com", body.GetProperty("url").GetString());
        Assert.Equal(2, body.GetProperty("maxDepth").GetInt32());
        Assert.Equal(500, body.GetProperty("perHostDelayMs").GetInt32());
        Assert.False(body.TryGetProperty("max-depth", out _));
    }

    [Fact]
    public async Task Start_InvalidIdempotencyKey_ThrowsWithoutHttpCall()
    {
        var (client, requests) = CreateClient(_ => Json(HttpStatusCode.Accepted, "{}"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.StartAsync(
                new { url = "https://example.com" },
                "",
                TestContext.Current.CancellationToken
            )
        );
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.StartAsync(
                new { url = "https://example.com" },
                new string('k', 201),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Empty(requests);
    }

    [Fact]
    public async Task Snapshot_UsesIncludeQuery_AndCancelUsesDelete()
    {
        var (client, requests) = CreateClient(_ =>
            Json(HttpStatusCode.OK, """{"job-id":"j1","status":"running"}""")
        );

        var snapshot = await client.GetSnapshotAsync(
            "j1",
            includeMarkdown: true,
            TestContext.Current.CancellationToken
        );
        await client.CancelAsync("j1", TestContext.Current.CancellationToken);

        Assert.Contains("running", snapshot, StringComparison.Ordinal);
        Assert.Equal(2, requests.Count);
        Assert.Equal(HttpMethod.Get, requests[0].Method);
        Assert.Equal("/v1/retrieval/crawl/j1?include=markdown", requests[0].PathAndQuery);
        Assert.Equal(HttpMethod.Delete, requests[1].Method);
        Assert.Equal("/v1/retrieval/crawl/j1", requests[1].PathAndQuery);
        Assert.Equal("mk_test_public-secret", requests[1].ApiKey);
    }

    [Fact]
    public async Task PaymentRequired_MapsToUsageLimitExceeded()
    {
        var (client, _) = CreateClient(_ =>
            Json(HttpStatusCode.PaymentRequired, """{"code":"usage_limit_exceeded","status":402}""")
        );

        var ex = await Assert.ThrowsAsync<CloudApiException>(() =>
            client.StartAsync(
                new { url = "https://example.com" },
                "crawl-key-402",
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal("usage_limit_exceeded", ex.Code);
    }

    [Fact]
    public async Task NotFound_MapsToNotFound()
    {
        var (client, _) = CreateClient(_ =>
            Json(HttpStatusCode.NotFound, """{"code":"not_found"}""")
        );

        var ex = await Assert.ThrowsAsync<CloudApiException>(() =>
            client.GetSnapshotAsync("missing", ct: TestContext.Current.CancellationToken)
        );

        Assert.Equal("not_found", ex.Code);
    }
}
