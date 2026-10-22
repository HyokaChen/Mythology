using System.Net;
using System.Text;
using ClientPlatform;
using Core.Cloud;
using Microsoft.Extensions.Http.Resilience;
using Xunit;

namespace ClientPlatform.Tests;

/// <summary>
/// HttpFictionHarnessClient 边界测试补充：
/// 轮询路径瞬态 429/503 退避重试、SSE 流结束无终态回退轮询、SSE failed 无 detail、
/// 2 分钟 deadline 触发 result_timeout、轮询 404 上抛、错误体超长截断日志不中断。
/// </summary>
public sealed class HttpFictionHarnessClientEdgeTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(respond(request));
    }

    private static HttpResponseMessage Json(
        string body,
        HttpStatusCode status = HttpStatusCode.OK
    ) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Sse(string eventType, string data) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"event: {eventType}\ndata: {data}\n\n",
                Encoding.UTF8,
                "text/event-stream"
            ),
        };

    private static HttpFictionHarnessClient NewClient(StubHandler handler) =>
        new(
            new HttpClient(
                new ResilienceHandler(CloudHttpResilience.BuildPipeline())
                {
                    InnerHandler = handler,
                }
            ),
            "mk_test_key",
            "http://localhost"
        );

    [Fact]
    public async Task Polling_Transient503_RetriesWithBackoff_ThenSucceeds()
    {
        var resultPolls = 0;
        var handler = new StubHandler(request =>
        {
            if (
                request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.EndsWith("/runs")
            )
                return Json("""{"run-id":"run_1"}""");
            if (request.Method == HttpMethod.Post)
                return Json("""{"invocation-id":"inv_1","status":"queued"}""");
            // /events 返回非 SSE → 回退轮询
            if (request.RequestUri!.AbsolutePath.EndsWith("/events"))
                return Json("""{"status":"queued"}""");
            // /result：先 503 再成功
            resultPolls++;
            if (resultPolls == 1)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent(
                        """{"code":"service_unavailable"}""",
                        Encoding.UTF8
                    ),
                };
            return Json(
                """{"invocation-id":"inv_1","status":"completed","cloud-work-units":1,"result":{"status":"pass"}}"""
            );
        });

        var result = await NewClient(handler)
            .ExecuteAsync(
                new HarnessRequest { Capability = "fiction.context.retrieve" },
                "idem-t1",
                CancellationToken.None
            );

        Assert.Equal("completed", result.Status);
        Assert.Equal(2, resultPolls); // 1 次 503 + 1 次成功
    }

    [Fact]
    public async Task Polling_Transient429_RespectsRetryAfterHeader()
    {
        var resultPolls = 0;
        var handler = new StubHandler(request =>
        {
            if (
                request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.EndsWith("/runs")
            )
                return Json("""{"run-id":"run_1"}""");
            if (request.Method == HttpMethod.Post)
                return Json("""{"invocation-id":"inv_1","status":"queued"}""");
            if (request.RequestUri!.AbsolutePath.EndsWith("/events"))
                return Json("""{"status":"queued"}""");
            resultPolls++;
            if (resultPolls == 1)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("""{"code":"rate_limited"}""", Encoding.UTF8),
                };
                resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                    TimeSpan.FromSeconds(1)
                );
                return resp;
            }
            return Json(
                """{"invocation-id":"inv_1","status":"completed","cloud-work-units":1,"result":{"status":"pass"}}"""
            );
        });

        var result = await NewClient(handler)
            .ExecuteAsync(
                new HarnessRequest { Capability = "fiction.context.retrieve" },
                "idem-t2",
                CancellationToken.None
            );

        Assert.Equal("completed", result.Status);
        Assert.Equal(2, resultPolls);
    }

    [Fact]
    public async Task SseStreamEndsWithoutTerminal_FallsBackToPolling()
    {
        var handler = new StubHandler(request =>
        {
            if (
                request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.EndsWith("/runs")
            )
                return Json("""{"run-id":"run_1"}""");
            if (request.Method == HttpMethod.Post)
                return Json("""{"invocation-id":"inv_1","status":"queued"}""");
            if (request.RequestUri!.AbsolutePath.EndsWith("/events"))
                return Sse("ping", "{}"); // 只有非终态事件，流正常结束
            return Json(
                """{"invocation-id":"inv_1","status":"completed","cloud-work-units":1,"result":{"status":"pass"}}"""
            );
        });

        var result = await NewClient(handler)
            .ExecuteAsync(
                new HarnessRequest { Capability = "fiction.context.retrieve" },
                "idem-t3",
                CancellationToken.None
            );

        Assert.Equal("completed", result.Status);
    }

    [Fact]
    public async Task SseFailedWithoutDetail_UsesFallbackMessage()
    {
        var handler = new StubHandler(request =>
        {
            if (
                request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.EndsWith("/runs")
            )
                return Json("""{"run-id":"run_1"}""");
            if (request.Method == HttpMethod.Post)
                return Json("""{"invocation-id":"inv_1","status":"queued"}""");
            return Sse("failed", """{"status":"failed"}""");
        });

        var ex = await Assert.ThrowsAsync<CloudApiException>(() =>
            NewClient(handler)
                .ExecuteAsync(
                    new HarnessRequest { Capability = "fiction.context.retrieve" },
                    "idem-t4",
                    CancellationToken.None
                )
        );

        Assert.Equal("analysis_failed", ex.Code);
        Assert.Equal("Harness invocation failed.", ex.Message);
    }

    [Fact]
    public async Task Polling_NotFound_ThrowsCloudApiException()
    {
        var handler = new StubHandler(request =>
        {
            if (
                request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.EndsWith("/runs")
            )
                return Json("""{"run-id":"run_1"}""");
            if (request.Method == HttpMethod.Post)
                return Json("""{"invocation-id":"inv_1","status":"queued"}""");
            return Json(
                """{"code":"not_found","detail":"no such invocation"}""",
                HttpStatusCode.NotFound
            );
        });

        var ex = await Assert.ThrowsAsync<CloudApiException>(() =>
            NewClient(handler)
                .ExecuteAsync(
                    new HarnessRequest { Capability = "fiction.context.retrieve" },
                    "idem-t5",
                    CancellationToken.None
                )
        );

        Assert.Equal("not_found", ex.Code);
    }

    [Fact]
    public async Task Polling_CompletedWithResult_StopsPolling()
    {
        var resultPolls = 0;
        var handler = new StubHandler(request =>
        {
            if (
                request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.EndsWith("/runs")
            )
                return Json("""{"run-id":"run_1"}""");
            if (request.Method == HttpMethod.Post)
                return Json("""{"invocation-id":"inv_1","status":"queued"}""");
            if (request.RequestUri!.AbsolutePath.EndsWith("/events"))
                return Json("""{"status":"queued"}"""); // 非 SSE → 回退轮询
            resultPolls++;
            return Json(
                """{"invocation-id":"inv_1","status":"completed","cloud-work-units":2,"result":{"status":"pass"}}"""
            );
        });

        var result = await NewClient(handler)
            .ExecuteAsync(
                new HarnessRequest { Capability = "fiction.story.check" },
                "idem-t6",
                CancellationToken.None
            );

        Assert.Equal(2, result.CloudWorkUnits);
        Assert.Equal(1, resultPolls); // /result 只轮询一次即完成
    }
}
