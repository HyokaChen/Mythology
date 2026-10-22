using System.Net;
using System.Text;
using Microsoft.Extensions.Http.Resilience;
using Xunit;

namespace ClientPlatform.Tests;

/// <summary>
/// UsageRestClient 协议测试（Stub handler）：
/// GET /v1/agent/usage（kebab 载荷 → 强类型，含 breakdown 字典）/ period 查询串编码 /
/// GET /v1/agent/usage/ledger（entries + next-cursor、limit/capability/cursor 组合）/
/// X-API-Key 鉴权 / 402 → usage_limit_exceeded / 401 → 吊销总线。
/// </summary>
public sealed class UsageRestClientTests
{
    private sealed record Captured(HttpMethod Method, string PathAndQuery, string? ApiKey);

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage Problem(HttpStatusCode status, string code) =>
        new(status)
        {
            Content = new StringContent(
                $$"""{"type":"about:blank","title":"{{code}}","status":{{(int)status}},"code":"{{code}}"}""",
                Encoding.UTF8,
                "application/problem+json"
            ),
        };

    private static (UsageRestClient Client, List<Captured> Requests) CreateClient(
        Func<Captured, HttpResponseMessage> responder,
        CloudAuthInvalidationBus? bus = null,
        Func<string?>? apiKeyProvider = null
    )
    {
        var requests = new List<Captured>();
        var handler = new StubHandler(request =>
        {
            var captured = new Captured(
                request.Method,
                request.RequestUri!.PathAndQuery,
                request.Headers.TryGetValues("X-API-Key", out var values)
                    ? values.FirstOrDefault()
                    : null
            );
            requests.Add(captured);
            return responder(captured);
        });
        var client = new UsageRestClient(
            "http://localhost/",
            apiKeyProvider ?? (() => "mk_test_public-secret"),
            http: ResilientClient(handler),
            invalidationBus: bus
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
    public async Task GetUsage_ParsesKebabPayload_IncludingBreakdown()
    {
        var (client, requests) = CreateClient(_ =>
            Json(
                """
                {
                  "period":"2026-09","plan":"creator",
                  "included-units":5000,"used-units":87,
                  "reserved-units":6,"remaining-units":4907,
                  "breakdown":{"fiction.story.check":81,"retrieval.crawl":6}
                }
                """
            )
        );

        var usage = await client.GetUsageAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal("2026-09", usage.Period);
        Assert.Equal("creator", usage.Plan);
        Assert.Equal(5000, usage.IncludedUnits);
        Assert.Equal(87, usage.UsedUnits);
        Assert.Equal(6, usage.ReservedUnits);
        Assert.Equal(4907, usage.RemainingUnits);
        Assert.Equal(81, usage.Breakdown["fiction.story.check"]);
        Assert.Equal(6, usage.Breakdown["retrieval.crawl"]);

        var request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/v1/agent/usage", request.PathAndQuery); // 无 period → 服务端缺省当月
        Assert.Equal("mk_test_public-secret", request.ApiKey);
    }

    [Fact]
    public async Task GetUsage_EscapesPeriodQuery()
    {
        var (client, requests) = CreateClient(_ => Json("""{"period":"2026-08"}"""));

        await client.GetUsageAsync("2026-08", TestContext.Current.CancellationToken);

        var request = Assert.Single(requests);
        Assert.Equal("/v1/agent/usage?period=2026-08", request.PathAndQuery);
    }

    [Fact]
    public async Task GetLedger_ParsesEntriesAndNextCursor_AndBuildsQuery()
    {
        var (client, requests) = CreateClient(_ =>
            Json(
                """
                {
                  "period":"2026-09",
                  "entries":[
                    {"invocation-id":"11111111-1111-1111-1111-111111111111",
                     "entry-type":"charge","capability":"fiction.story.check",
                     "status":"settled","units":6,"idempotency-key-hash":"abc123",
                     "created-at":"2026-09-13T10:00:00+00:00"},
                    {"invocation-id":null,"entry-type":"release","capability":"retrieval.crawl",
                     "status":"released","units":-4,"idempotency-key-hash":null,
                     "created-at":"2026-09-13T10:01:00+00:00"}
                  ],
                  "next-cursor":"2026-09-13T10:01:00.0000000+00:00"
                }
                """
            )
        );

        var page = await client.GetLedgerAsync(
            period: "2026-09",
            capability: "fiction.story.check",
            limit: 2,
            cursor: "cursor==1",
            ct: TestContext.Current.CancellationToken
        );

        Assert.Equal("2026-09", page.Period);
        Assert.Equal(2, page.Entries.Count);
        Assert.Equal(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            page.Entries[0].InvocationId
        );
        Assert.Equal("charge", page.Entries[0].EntryType);
        Assert.Equal("settled", page.Entries[0].Status);
        Assert.Equal(6, page.Entries[0].Units);
        Assert.Equal("abc123", page.Entries[0].IdempotencyKeyHash);
        Assert.Null(page.Entries[1].InvocationId);
        Assert.Equal(-4, page.Entries[1].Units); // 释放行以负数返还
        Assert.Equal("2026-09-13T10:01:00.0000000+00:00", page.NextCursor);

        var request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.StartsWith(
            "/v1/agent/usage/ledger?",
            request.PathAndQuery,
            StringComparison.Ordinal
        );
        Assert.Contains("limit=2", request.PathAndQuery, StringComparison.Ordinal);
        Assert.Contains("period=2026-09", request.PathAndQuery, StringComparison.Ordinal);
        Assert.Contains(
            "capability=fiction.story.check",
            request.PathAndQuery,
            StringComparison.Ordinal
        );
        Assert.Contains("cursor=cursor%3D%3D1", request.PathAndQuery, StringComparison.Ordinal);
        Assert.Equal("mk_test_public-secret", request.ApiKey);
    }

    [Fact]
    public async Task GetLedger_ClampsLimitIntoServerRange()
    {
        var (client, requests) = CreateClient(_ => Json("""{"period":"2026-09","entries":[]}"""));

        await client.GetLedgerAsync(limit: 9999, ct: TestContext.Current.CancellationToken);

        Assert.Contains("limit=500", requests[0].PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PaymentRequired_MapsToUsageLimitExceeded()
    {
        var (client, _) = CreateClient(_ =>
            Problem(HttpStatusCode.PaymentRequired, "usage_limit_exceeded")
        );

        var ex = await Assert.ThrowsAsync<CloudApiException>(() =>
            client.GetUsageAsync(ct: TestContext.Current.CancellationToken)
        );

        Assert.Equal("usage_limit_exceeded", ex.Code);
    }

    [Fact]
    public async Task BadRequest_MapsToInvalidRequest_ForMalformedPeriod()
    {
        var (client, _) = CreateClient(_ => Problem(HttpStatusCode.BadRequest, "invalid_request"));

        var ex = await Assert.ThrowsAsync<CloudApiException>(() =>
            client.GetUsageAsync("2026/09", TestContext.Current.CancellationToken)
        );

        Assert.Equal("invalid_request", ex.Code);
    }

    [Fact]
    public async Task Unauthorized_RaisesInvalidationBus()
    {
        var bus = new CloudAuthInvalidationBus();
        var raised = 0;
        bus.Invalidated += _ => raised++;
        var (client, _) = CreateClient(
            _ => Problem(HttpStatusCode.Unauthorized, "invalid_api_key"),
            bus
        );

        var ex = await Assert.ThrowsAsync<CloudApiException>(() =>
            client.GetUsageAsync(ct: TestContext.Current.CancellationToken)
        );

        // body-first：服务端 code 为权威
        Assert.Equal("invalid_api_key", ex.Code);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task EmptyApiKey_OmitsHeader()
    {
        var (client, requests) = CreateClient(
            _ => Json("""{"period":"2026-09"}"""),
            apiKeyProvider: () => null
        );

        await client.GetUsageAsync(ct: TestContext.Current.CancellationToken);

        var request = Assert.Single(requests);
        Assert.Equal("/v1/agent/usage", request.PathAndQuery); // baseUrl 尾斜杠归一
        Assert.Null(request.ApiKey); // 未登录：不加 X-API-Key 头
    }
}
