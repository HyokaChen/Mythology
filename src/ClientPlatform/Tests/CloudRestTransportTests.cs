using System.Net;
using Xunit;

namespace ClientPlatform.Tests;

/// <summary>
/// CloudRestTransport 的两条自身不变量（其余传输语义由 6 个客户端套件经 StubHandler 传递覆盖）：
/// baseUrl 尾斜杠归一（此前 <c>HttpFictionHarnessClient</c> 会拼出 <c>//v1/...</c>）、
/// 空 Key 不发 X-API-Key 头。
/// </summary>
public sealed class CloudRestTransportTests
{
    private sealed class CapturingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(request);
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task BaseUrlWithTrailingSlash_ProducesSingleSlashPath()
    {
        var handler = new CapturingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}"""),
            }
        );
        var transport = new CloudRestTransport(
            "http://localhost/",
            () => "mk_test_key",
            http: new HttpClient(handler)
        );

        await transport.SendForStringAsync(
            HttpMethod.Get,
            "/v1/retrieval/scrape",
            body: null,
            ClientJsonDefaults.CamelCaseRequest,
            TestContext.Current.CancellationToken
        );

        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://localhost/v1/retrieval/scrape", request.RequestUri!.ToString());
        Assert.Equal("mk_test_key", Assert.Single(request.Headers.GetValues("X-API-Key")));
    }

    [Fact]
    public async Task EmptyApiKey_OmitsHeader_AndIdempotencyKeyIsSent()
    {
        var handler = new CapturingHandler(
            new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("{}") }
        );
        var transport = new CloudRestTransport(
            "http://localhost",
            () => null,
            http: new HttpClient(handler)
        );

        await transport.SendForStringAsync(
            HttpMethod.Post,
            "/v1/retrieval/crawl",
            new { url = "https://example.com" },
            ClientJsonDefaults.CamelCaseRequest,
            TestContext.Current.CancellationToken,
            idempotencyKey: "key-1"
        );

        var request = Assert.Single(handler.Requests);
        Assert.False(request.Headers.Contains("X-API-Key"));
        Assert.Equal("key-1", Assert.Single(request.Headers.GetValues("Idempotency-Key")));
    }

    /// <summary>
    /// 透传端点（头读模式）的响应体读取必须落在按请求安全网内：服务端发完响应头后卡死时，
    /// 体读取此前不在任何超时内（<c>HttpClient.Timeout</c> 同样不覆盖头读后的体读取，实测
    /// 2s 超时在 10s 卡体下未触发），仅剩调用方 token 兜底 → 传 <c>None</c> 即无限挂起。
    /// 修复前本用例不抛异常（读到桩内容自然结束）。
    /// </summary>
    [Fact]
    public async Task HeaderRead_BodyReadIsCoveredByRequestTimeout()
    {
        var transport = new CloudRestTransport(
            "http://localhost",
            () => "mk_test_key",
            http: new HttpClient(new StallingBodyHandler(TimeSpan.FromSeconds(2))),
            requestTimeout: TimeSpan.FromMilliseconds(150)
        );

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transport.SendForStringAsync(
                HttpMethod.Get,
                "/v1/agent/usage",
                body: null,
                ClientJsonDefaults.CamelCaseRequest,
                CancellationToken.None,
                completionOption: HttpCompletionOption.ResponseHeadersRead
            )
        );
    }

    /// <summary>回完响应头即卡住响应体（模拟服务端发头后不再推体）。</summary>
    private sealed class StallingBodyHandler(TimeSpan stall) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StallingContent(stall) }
            );
    }

    private sealed class StallingContent(TimeSpan stall) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken
        )
        {
            await Task.Delay(stall, cancellationToken);
            await stream.WriteAsync("{}"u8.ToArray(), cancellationToken);
        }
    }
}
