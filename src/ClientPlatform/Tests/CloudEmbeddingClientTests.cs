using System.Net;
using System.Text;
using Microsoft.Extensions.Http.Resilience;
using Xunit;

namespace ClientPlatform.Tests;

/// <summary>
/// CloudEmbeddingClient 协议测试（Stub handler）：
/// POST /v1/embedding + X-API-Key / {texts} 请求体 / 向量解析与顺序 /
/// available=false 显式失败 / 维度不一致显式失败 / 空输入不发 HTTP / 401 → 吊销总线。
/// </summary>
public sealed class CloudEmbeddingClientTests
{
    private sealed record Captured(HttpMethod Method, string Path, string? Body, string? ApiKey);

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static (CloudEmbeddingClient Client, List<Captured> Requests) CreateClient(
        Func<Captured, HttpResponseMessage> responder,
        int dimension = 4,
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
                    : null
            );
            requests.Add(captured);
            return responder(captured);
        });
        var client = new CloudEmbeddingClient(
            "http://localhost",
            () => "mk_test_public-secret",
            dimension,
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
    public async Task EmbedBatch_SendsTextsWithApiKey_AndParsesVectorsInOrder()
    {
        var (client, requests) = CreateClient(_ =>
            Json(
                """{"vectors":[[1,0,0,0],[0,1,0,0]],"dimension":4,"model":"bge-m3","available":true}"""
            )
        );

        var vectors = await client.EmbedBatchAsync(
            ["第一段", "第二段"],
            TestContext.Current.CancellationToken
        );

        Assert.Equal(2, vectors.Length);
        Assert.Equal(4, vectors[0].Length);
        Assert.Equal(1f, vectors[0][0]);
        Assert.Equal(1f, vectors[1][1]);
        Assert.Equal(4, client.Dimension);
        var request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/embedding", request.Path);
        Assert.Equal("mk_test_public-secret", request.ApiKey);
        var body = System.Text.Json.JsonDocument.Parse(request.Body!).RootElement;
        Assert.Equal(2, body.GetProperty("texts").GetArrayLength());
    }

    [Fact]
    public async Task EmbedBatch_Over100Texts_SplitsIntoChunks_PreservingOrder()
    {
        // 服务端单请求 texts ≤ 100：>100 条必须在客户端切批（每批一次往返），
        // 否则整批 400 → RetrievalKernel 回退逐条 HTTP（N 次往返 + 限流压力）。
        var (client, requests) = CreateClient(captured =>
        {
            var count = System
                .Text.Json.JsonDocument.Parse(captured.Body!)
                .RootElement.GetProperty("texts")
                .GetArrayLength();
            var vectors = string.Join(",", Enumerable.Range(0, count).Select(i => $"[{i},0,0,0]"));
            return Json(
                $"{{\"vectors\":[{vectors}],\"dimension\":4,\"model\":\"m\",\"available\":true}}"
            );
        });

        var texts = Enumerable.Range(0, 150).Select(i => $"t{i}").ToArray();
        var vectors = await client.EmbedBatchAsync(texts, TestContext.Current.CancellationToken);

        Assert.Equal(150, vectors.Length);
        Assert.Equal(2, requests.Count);
        Assert.Equal(
            100,
            System
                .Text.Json.JsonDocument.Parse(requests[0].Body!)
                .RootElement.GetProperty("texts")
                .GetArrayLength()
        );
        Assert.Equal(
            50,
            System
                .Text.Json.JsonDocument.Parse(requests[1].Body!)
                .RootElement.GetProperty("texts")
                .GetArrayLength()
        );
        // 顺序拼接：第 1 批 0..99，第 2 批在批内序号 0..49
        Assert.Equal(0f, vectors[0][0]);
        Assert.Equal(99f, vectors[99][0]);
        Assert.Equal(0f, vectors[100][0]);
        Assert.Equal(49f, vectors[149][0]);
    }

    [Fact]
    public async Task EmbedAsync_SingleText_UsesBatchEndpoint()
    {
        var (client, requests) = CreateClient(_ =>
            Json("""{"vectors":[[0.5,0.5,0.5,0.5]],"dimension":4,"model":"m","available":true}""")
        );

        var vector = await client.EmbedAsync("单条", TestContext.Current.CancellationToken);

        Assert.Equal(4, vector.Length);
        Assert.Single(requests);
        var body = System.Text.Json.JsonDocument.Parse(requests[0].Body!).RootElement;
        Assert.Equal(1, body.GetProperty("texts").GetArrayLength());
    }

    [Fact]
    public async Task Unavailable_Throws_InsteadOfSilentlyReturningZeroVectors()
    {
        var (client, _) = CreateClient(_ =>
            Json("""{"vectors":[[0,0,0,0]],"dimension":4,"model":"m","available":false}""")
        );

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.EmbedAsync("文本", TestContext.Current.CancellationToken)
        );

        Assert.Contains("不可用", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DimensionMismatch_Throws_WithActionableMessage()
    {
        var (client, _) = CreateClient(_ =>
            Json("""{"vectors":[[1,0,0,0,0,0]],"dimension":6,"model":"m","available":true}""")
        );

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.EmbedAsync("文本", TestContext.Current.CancellationToken)
        );

        Assert.Contains("维度", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyTexts_SkipsHttpCall()
    {
        var (client, requests) = CreateClient(_ => Json("{}"));

        var vectors = await client.EmbedBatchAsync([], TestContext.Current.CancellationToken);

        Assert.Empty(vectors);
        Assert.Empty(requests);
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
                    """{"code":"invalid_api_key"}""",
                    Encoding.UTF8,
                    "application/json"
                ),
            },
            bus: bus
        );

        var ex = await Assert.ThrowsAsync<CloudApiException>(() =>
            client.EmbedAsync("文本", TestContext.Current.CancellationToken)
        );

        // body-first：服务端 code 为权威
        Assert.Equal("invalid_api_key", ex.Code);
        Assert.Equal(1, raised);
    }
}
