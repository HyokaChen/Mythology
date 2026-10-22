using System.Net;
using System.Text;
using Microsoft.Extensions.Http.Resilience;
using Xunit;

namespace ClientPlatform.Tests;

/// <summary>
/// CloudNovelSyncClient 协议测试（Stub handler）：
/// 请求路径/方法/X-API-Key 头 / kebab body / 分批（>500 keys）/ 401 → CloudApiException + 吊销总线。
/// </summary>
public sealed class CloudNovelSyncClientTests
{
    private sealed record Captured(HttpMethod Method, string Path, string? Body, string? ApiKey);

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static (CloudNovelSyncClient Client, List<Captured> Requests) CreateClient(
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
                    : null
            );
            requests.Add(captured);
            return responder(captured);
        });
        var client = new CloudNovelSyncClient(
            ResilientClient(handler),
            () => "mk_test_public-secret",
            "http://localhost",
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
    public async Task ListNovels_SendsPathAndApiKey_ParsesKebabFields()
    {
        var (client, requests) = CreateClient(_ =>
            Json(
                """[{"novel-id":"n1","title":"我的小说","directory-name":"我的小说","updated-at":"2026-01-01T00:00:00Z","document-count":3}]"""
            )
        );
        var novels = await client.ListNovelsAsync();
        var novel = Assert.Single(novels);
        Assert.Equal("n1", novel.NovelId);
        Assert.Equal("我的小说", novel.DirectoryName);
        Assert.Equal(3, novel.DocumentCount);
        var request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/v1/sync/novels", request.Path);
        Assert.Equal("mk_test_public-secret", request.ApiKey);
    }

    [Fact]
    public async Task Push_SendsKebabBody_AndParsesPerItemResults()
    {
        var (client, requests) = CreateClient(_ =>
            Json(
                """{"results":[{"document-type":"chapter","entity-id":"ch1","status":"conflict","revision":2,"current":{"document-type":"chapter","entity-id":"ch1","revision":2,"content-sha-256":"abc","title":"第一章","relative-path":"我的小说/chapters/ch001.md","content-encoding":"utf8","content":"云端现行","updated-at":"2026-01-01T00:00:00Z"},"error":null}]}"""
            )
        );
        var results = await client.PushDocumentsAsync(
            "n1",
            [
                new SyncPushItemDto(
                    "chapter",
                    "ch1",
                    1,
                    "sha",
                    "第一章",
                    "我的小说/chapters/ch001.md",
                    "utf8",
                    "本地内容"
                ),
            ]
        );
        var result = Assert.Single(results);
        Assert.Equal("conflict", result.Status);
        Assert.Equal(2, result.Revision);
        Assert.NotNull(result.Current);
        Assert.Equal("云端现行", result.Current!.Content);
        var request = Assert.Single(requests);
        var json = System.Text.Json.JsonDocument.Parse(request.Body!).RootElement;
        var sent = json.GetProperty("documents")[0];
        Assert.Equal(
            "content-sha-256",
            sent.EnumerateObject().First(p => p.Name.StartsWith("content-sha")).Name
        );
        Assert.Equal(1, sent.GetProperty("base-revision").GetInt32());
        Assert.Equal("我的小说/chapters/ch001.md", sent.GetProperty("relative-path").GetString());
    }

    [Fact]
    public async Task Fetch_ChunksKeysOver500()
    {
        var callCount = 0;
        var (client, requests) = CreateClient(_ =>
        {
            callCount++;
            return Json(
                """{"documents":[{"document-type":"chapter","entity-id":"x","revision":1,"content-sha-256":"h","title":null,"relative-path":"p","content-encoding":"utf8","content":"c","updated-at":"2026-01-01T00:00:00Z"}]}"""
            );
        });
        var keys = Enumerable.Range(0, 1201).Select(i => (("chapter"), $"ch-{i}")).ToList();
        var documents = await client.FetchDocumentsAsync("n1", keys);
        Assert.Equal(3, documents.Count); // 每批一个 stub 返回（500+500+201 = 3 批）
        Assert.Equal(3, requests.Count);
        // 第三批 keys 数 = 201
        var lastBody = System.Text.Json.JsonDocument.Parse(requests[2].Body!).RootElement;
        Assert.Equal(201, lastBody.GetProperty("keys").GetArrayLength());
    }

    [Fact]
    public async Task TransientFailure_IsRetried_ThenSucceeds()
    {
        var attempts = 0;
        var (client, requests) = CreateClient(_ =>
        {
            attempts++;
            return attempts == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                }
                : Json("""[]""");
        });
        var novels = await client.ListNovelsAsync();
        Assert.NotNull(novels);
        Assert.Equal(2, attempts); // 503 → 退避重试 → 成功
        Assert.Equal(2, requests.Count);
    }

    [Fact]
    public async Task Unauthorized_RaisesInvalidationBus_AndThrows()
    {
        var bus = new CloudAuthInvalidationBus();
        var raised = false;
        bus.Invalidated += _ => raised = true;
        var (client, _) = CreateClient(
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    """{"code":"invalid_api_key"}""",
                    Encoding.UTF8,
                    "application/json"
                ),
            },
            bus
        );
        var exception = await Assert.ThrowsAsync<CloudApiException>(() => client.ListNovelsAsync());
        Assert.Equal("invalid_api_key", exception.Code);
        Assert.True(raised);
    }
}
