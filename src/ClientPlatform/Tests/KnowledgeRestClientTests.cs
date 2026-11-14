using System.Net;
using System.Text;
using Core.Cloud;
using Microsoft.Extensions.Http.Resilience;
using Xunit;

namespace ClientPlatform.Tests;

/// <summary>
/// KnowledgeRestClient 协议测试（Stub handler）：
/// 路径/方法/查询串（含转义）/ X-API-Key 鉴权 / camelCase 请求体 /
/// 503 → knowledge_unavailable / 404 → not_found。
/// </summary>
public sealed class KnowledgeRestClientTests
{
    private sealed record Captured(
        HttpMethod Method,
        string PathAndQuery,
        string? Body,
        string? ApiKey
    );

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

    private static (KnowledgeRestClient Client, List<Captured> Requests) CreateClient(
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
                request.RequestUri!.PathAndQuery,
                body,
                request.Headers.TryGetValues("X-API-Key", out var values)
                    ? values.FirstOrDefault()
                    : null
            );
            requests.Add(captured);
            return responder(captured);
        });
        var client = new KnowledgeRestClient(
            "http://localhost",
            () => "mk_test_public-secret",
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
    public async Task Search_SendsCamelCaseBody_AndApiKey()
    {
        var (client, requests) = CreateClient(_ => Json("""{"hits":[],"mode":"hybrid"}"""));

        var result = await client.SearchAsync(
            new
            {
                query = "角色设计",
                limit = 5,
                category = "角色",
                subcategory = "主角",
                mode = "hybrid",
            },
            TestContext.Current.CancellationToken
        );

        Assert.Contains("hybrid", result, StringComparison.Ordinal);
        var request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/knowledge/search", request.PathAndQuery);
        Assert.Equal("mk_test_public-secret", request.ApiKey);
        var body = System.Text.Json.JsonDocument.Parse(request.Body!).RootElement;
        Assert.Equal("角色设计", body.GetProperty("query").GetString());
        Assert.Equal(5, body.GetProperty("limit").GetInt32());
        Assert.Equal("hybrid", body.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Hint_SendsCamelCaseBody_AndApiKey()
    {
        var (client, requests) = CreateClient(_ =>
            Json("""{"status":"ok","prompt-block":"素材参考A","hints":[],"vector-used":false}""")
        );

        var result = await client.HintAsync(
            new KnowledgeHintRequest(
                SceneGoal: "女主雪夜登场",
                CurrentText: "她推门而入。",
                Genre: "古言",
                Limit: 5,
                MaxChars: 600,
                Expand: false
            ),
            TestContext.Current.CancellationToken
        );

        Assert.Equal("ok", result.Status);
        Assert.Equal("素材参考A", result.PromptBlock);
        var request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/knowledge/hint", request.PathAndQuery);
        Assert.Equal("mk_test_public-secret", request.ApiKey);
        var body = System.Text.Json.JsonDocument.Parse(request.Body!).RootElement;
        Assert.Equal("女主雪夜登场", body.GetProperty("sceneGoal").GetString());
        Assert.Equal("她推门而入。", body.GetProperty("currentText").GetString());
        Assert.Equal("古言", body.GetProperty("genre").GetString());
        Assert.Equal(5, body.GetProperty("limit").GetInt32());
        Assert.Equal(600, body.GetProperty("maxChars").GetInt32());
        Assert.False(body.GetProperty("expand").GetBoolean());
    }

    [Fact]
    public async Task Hint_DeserializesTypedHits()
    {
        var cardId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var relatedId = Guid.NewGuid();
        var (client, _) = CreateClient(_ =>
            Json(
                $$"""
                {"status":"ok","prompt-block":"素材参考B","hints":[{"card-id":"{{cardId}}","document-id":"{{documentId}}","title":"卡片","kind":"technique","tags":["对话"],"excerpt":"摘录","suggestion":"建议","score":0.9,"source-title":"来源","source-url":null,"related-card-ids":["{{relatedId}}"],"matched-delta-ids":["d1"]}],"vector-used":true}
                """
            )
        );

        var result = await client.HintAsync(
            new KnowledgeHintRequest("女主雪夜登场"),
            TestContext.Current.CancellationToken
        );

        var hit = Assert.Single(result.Hints);
        Assert.Equal(cardId, hit.CardId);
        Assert.Equal(documentId, hit.DocumentId);
        Assert.Equal("卡片", hit.Title);
        Assert.Equal("technique", hit.Kind);
        Assert.Equal(["对话"], hit.Tags);
        Assert.Equal(0.9, hit.Score);
        Assert.Equal(relatedId, Assert.Single(hit.RelatedCardIds!));
        Assert.Equal(["d1"], hit.MatchedDeltaIds!);
        Assert.True(result.VectorUsed);
    }

    [Fact]
    public async Task Hint_ServiceUnavailable_MapsToKnowledgeUnavailable()
    {
        var (client, _) = CreateClient(_ =>
            Problem(HttpStatusCode.ServiceUnavailable, "knowledge_unavailable")
        );

        var ex = await Assert.ThrowsAsync<CloudApiException>(() =>
            client.HintAsync(
                new KnowledgeHintRequest("女主雪夜登场"),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal("knowledge_unavailable", ex.Code);
    }

    [Fact]
    public async Task Browse_BuildsEscapedQueryString()
    {
        var (client, requests) = CreateClient(_ => Json("""{"documents":[]}"""));

        await client.BrowseAsync(
            category: "写作 技巧",
            subcategory: "对话",
            limit: 30,
            cursor: "cursor==1",
            ct: TestContext.Current.CancellationToken
        );

        var request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.StartsWith(
            "/v1/knowledge/documents?",
            request.PathAndQuery,
            StringComparison.Ordinal
        );
        Assert.Contains("limit=30", request.PathAndQuery, StringComparison.Ordinal);
        Assert.Contains(
            $"category={Uri.EscapeDataString("写作 技巧")}",
            request.PathAndQuery,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "subcategory=%E5%AF%B9%E8%AF%9D",
            request.PathAndQuery,
            StringComparison.Ordinal
        );
        Assert.Contains("cursor=cursor%3D%3D1", request.PathAndQuery, StringComparison.Ordinal);
        Assert.Null(request.Body);
    }

    [Fact]
    public async Task Categories_And_Document_UseGetPaths()
    {
        var docs = Guid.NewGuid();
        var (client, requests) = CreateClient(_ => Json("{}"));

        await client.CategoriesAsync(TestContext.Current.CancellationToken);
        await client.GetDocumentAsync(docs, TestContext.Current.CancellationToken);

        Assert.Equal(2, requests.Count);
        Assert.Equal(HttpMethod.Get, requests[0].Method);
        Assert.Equal("/v1/knowledge/categories", requests[0].PathAndQuery);
        Assert.Equal($"/v1/knowledge/documents/{docs}", requests[1].PathAndQuery);
        Assert.Equal("mk_test_public-secret", requests[1].ApiKey);
    }

    [Fact]
    public async Task ServiceUnavailable_MapsToKnowledgeUnavailable()
    {
        var (client, _) = CreateClient(_ =>
            Problem(HttpStatusCode.ServiceUnavailable, "knowledge_unavailable")
        );

        var ex = await Assert.ThrowsAsync<CloudApiException>(() =>
            client.SearchAsync(new { query = "x" }, TestContext.Current.CancellationToken)
        );

        Assert.Equal("knowledge_unavailable", ex.Code);
    }

    [Fact]
    public async Task NotFound_MapsToNotFound_AndRaisesNoInvalidation()
    {
        var bus = new CloudAuthInvalidationBus();
        var raised = 0;
        bus.Invalidated += _ => raised++;
        var (client, _) = CreateClient(_ => Problem(HttpStatusCode.NotFound, "not_found"), bus);

        var ex = await Assert.ThrowsAsync<CloudApiException>(() =>
            client.GetDocumentAsync(Guid.NewGuid(), TestContext.Current.CancellationToken)
        );

        Assert.Equal("not_found", ex.Code);
        Assert.Equal(0, raised);
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
            client.CategoriesAsync(TestContext.Current.CancellationToken)
        );

        // body-first：服务端 code 为权威
        Assert.Equal("invalid_api_key", ex.Code);
        Assert.Equal(1, raised);
    }
}
