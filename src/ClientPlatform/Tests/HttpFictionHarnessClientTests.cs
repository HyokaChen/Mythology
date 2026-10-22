using System.Net;
using System.Text;
using ClientPlatform;
using Core.Cloud;
using Xunit;

namespace ClientPlatform.Tests;

public sealed class HttpFictionHarnessClientTests
{
    [Fact]
    public async Task ExecuteAsync_FollowsRunInvocationResultProtocol()
    {
        var requests = new List<(HttpMethod Method, string Path, string? Body)>();
        var handler = new StubHandler(async request =>
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
            if (
                request.Method == HttpMethod.Post
                && request.RequestUri.AbsolutePath.EndsWith("/runs")
            )
                return JsonResponse("""{"run-id":"run_1","status":"queued"}""");
            if (
                request.Method == HttpMethod.Post
                && request.RequestUri.AbsolutePath.EndsWith("/harness-invocations")
            )
            {
                Assert.True(request.Headers.Contains("Idempotency-Key"));
                return JsonResponse(
                    """{"invocation-id":"inv_1","status":"queued","estimated-units":2}"""
                );
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/events"))
            {
                Assert.Contains(request.Headers.Accept, h => h.MediaType == "text/event-stream");
                return SseResponse(
                    "completed",
                    """{"invocation-id":"inv_1","status":"completed","cloud-work-units":2,"result":{"status":"pass","features":{"mattr":0.83}}}"""
                );
            }
            return JsonResponse(
                """{"invocation-id":"inv_1","status":"completed","cloud-work-units":2,"result":{"status":"pass","features":{"mattr":0.83}}}"""
            );
        });

        var client = new HttpFictionHarnessClient(
            new HttpClient(handler),
            "mk_test_public-secret",
            "http://localhost"
        );
        var request = new HarnessRequest
        {
            Capability = "fiction.context.retrieve",
            Chapters = [new ChapterInput("ch_1", 1, "第一章", "target", "内容")],
        };

        var result = await client.ExecuteAsync(request, "idem-1", CancellationToken.None);

        Assert.Equal("completed", result.Status);
        Assert.Equal(2, result.CloudWorkUnits);
        Assert.Equal(2, result.EstimatedUnits); // 提交报价（202 estimated-units）随结果回传
        Assert.NotNull(result.Result);
        Assert.Equal("/v1/agent/runs", requests[0].Path);
        Assert.Equal("/v1/agent/runs/run_1/harness-invocations", requests[1].Path);
        Assert.Equal("/v1/agent/runs/run_1/harness-invocations/inv_1/events", requests[2].Path);
    }

    [Fact]
    public async Task ExecuteAsync_UsesInvocationAgentRunId_WhenDedupAcrossRuns()
    {
        // 回归：幂等命中时服务端返回旧调用真实所属的 run（agentRunId），
        // 客户端必须用它取结果；若仍用本次新建的 run，会因 run-id 不匹配而 404。
        var paths = new List<string>();
        var handler = new StubHandler(async request =>
        {
            if (
                request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.EndsWith("/runs")
            )
                return JsonResponse("""{"run-id":"run_new"}""");
            if (request.Method == HttpMethod.Post)
                // 幂等命中：返回旧 run_old 的调用，而非本次新建的 run_new
                return JsonResponse(
                    """{"invocation-id":"inv_old","agent-run-id":"run_old","status":"completed"}"""
                );
            paths.Add(request.RequestUri!.AbsolutePath);
            return SseResponse(
                "completed",
                """{"invocation-id":"inv_old","status":"completed","cloud-work-units":1,"result":{"status":"pass"}}"""
            );
        });

        var client = new HttpFictionHarnessClient(
            new HttpClient(handler),
            "mk_test_public-secret",
            "http://localhost"
        );
        var request = new HarnessRequest
        {
            Capability = "fiction.story.check",
            Chapters = [new ChapterInput("ch_1", 1, "第一章", "target", "内容")],
        };

        var result = await client.ExecuteAsync(request, "idem-shared", CancellationToken.None);

        Assert.Equal("completed", result.Status);
        // 取结果必须打在旧 run_old 上，而不是新建的 run_new
        Assert.Contains(
            paths,
            p => p == "/v1/agent/runs/run_old/harness-invocations/inv_old/events"
        );
        Assert.DoesNotContain(paths, p => p.Contains("run_new"));
    }

    [Fact]
    public async Task ExecuteAsync_ReusesRun_AcrossCallsWithSameKey()
    {
        // P1-6：run 是调用容器，同一 Key 下多次调用复用同一 run（少一次 CreateRun 往返/写库）。
        var runCreations = 0;
        var invocationPaths = new List<string>();
        var handler = new StubHandler(async request =>
        {
            if (
                request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.EndsWith("/runs")
            )
            {
                runCreations++;
                return JsonResponse("""{"run-id":"run_1"}""");
            }
            if (request.Method == HttpMethod.Post)
            {
                invocationPaths.Add(request.RequestUri!.AbsolutePath);
                return JsonResponse("""{"invocation-id":"inv_1","status":"queued"}""");
            }
            return SseResponse(
                "completed",
                """{"invocation-id":"inv_1","status":"completed","cloud-work-units":1,"result":{"status":"pass"}}"""
            );
        });
        var client = new HttpFictionHarnessClient(
            new HttpClient(handler),
            "mk_test_public-secret",
            "http://localhost"
        );
        var request = new HarnessRequest
        {
            Capability = "fiction.context.retrieve",
            Chapters = [new ChapterInput("ch_1", 1, "第一章", "target", "内容")],
        };

        await client.ExecuteAsync(request, "idem-reuse-1", CancellationToken.None);
        await client.ExecuteAsync(request, "idem-reuse-2", CancellationToken.None);

        Assert.Equal(1, runCreations);
        Assert.Equal(2, invocationPaths.Count);
        Assert.All(
            invocationPaths,
            path => Assert.Equal("/v1/agent/runs/run_1/harness-invocations", path)
        );
    }

    [Fact]
    public async Task ExecuteAsync_StaleCachedRun_NotFound_RecreatesRunAndRetriesOnce()
    {
        // 缓存 run 在服务端不存在（数据重置 / 跨 workspace 残留）→ 404 → 弃用缓存、新建 run 重试一次。
        var runCreations = 0;
        var run1Exists = true; // 首调成功后模拟服务端 run_1 消失（数据重置）
        var handler = new StubHandler(async request =>
        {
            if (
                request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.EndsWith("/runs")
            )
            {
                runCreations++;
                return JsonResponse($$"""{"run-id":"run_{{runCreations}}"}""");
            }
            if (request.Method == HttpMethod.Post)
            {
                var runId = request.RequestUri!.AbsolutePath.Split('/')[4];
                if (runId == "run_1" && !run1Exists)
                    return new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent(
                            """{"code":"not_found"}""",
                            Encoding.UTF8,
                            "application/json"
                        ),
                    };
                return JsonResponse("""{"invocation-id":"inv_1","status":"queued"}""");
            }
            return SseResponse(
                "completed",
                """{"invocation-id":"inv_1","status":"completed","cloud-work-units":1,"result":{"status":"pass"}}"""
            );
        });
        var client = new HttpFictionHarnessClient(
            new HttpClient(handler),
            "mk_test_public-secret",
            "http://localhost"
        );
        var request = new HarnessRequest { Capability = "fiction.context.retrieve" };

        var first = await client.ExecuteAsync(request, "idem-stale-1", CancellationToken.None);
        Assert.Equal("completed", first.Status);
        Assert.Equal(1, runCreations);

        run1Exists = false; // run_1 在服务端消失（数据重置）
        var second = await client.ExecuteAsync(request, "idem-stale-2", CancellationToken.None);

        Assert.Equal("completed", second.Status);
        Assert.Equal(2, runCreations); // run_1 404 → 重建 run_2
    }

    [Fact]
    public async Task ExecuteAsync_ApiKeyChanged_CreatesNewRun()
    {
        // 账号切换（Key 变更）→ 旧 run 属别的 workspace：必须新建 run，不得复用缓存。
        var runCreations = 0;
        var key = "mk_live_first-key";
        var handler = new StubHandler(async request =>
        {
            if (
                request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.EndsWith("/runs")
            )
            {
                runCreations++;
                return JsonResponse($$"""{"run-id":"run_{{runCreations}}"}""");
            }
            if (request.Method == HttpMethod.Post)
                return JsonResponse("""{"invocation-id":"inv_1","status":"queued"}""");
            return SseResponse(
                "completed",
                """{"invocation-id":"inv_1","status":"completed","cloud-work-units":1,"result":{"status":"pass"}}"""
            );
        });
        var client = new HttpFictionHarnessClient(
            new HttpClient(handler),
            () => key,
            "http://localhost"
        );
        var request = new HarnessRequest { Capability = "fiction.context.retrieve" };

        await client.ExecuteAsync(request, "idem-switch-1", CancellationToken.None);
        key = "mk_live_second-key";
        await client.ExecuteAsync(request, "idem-switch-2", CancellationToken.None);

        Assert.Equal(2, runCreations);
    }

    [Fact]
    public async Task ExecuteAsync_SseAttemptTimeout_FallsBackToPolling()
    {
        // P2-15：SSE 连接挂死（headers 已到、body 永不来）→ 到点弃流回退轮询，
        // 轮询承接剩余预算并拿到结果；不把 2 分钟整体预算耗在死流上。
        var polled = false;
        var handler = new StubHandler(async request =>
        {
            if (
                request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.EndsWith("/runs")
            )
                return JsonResponse("""{"run-id":"run_1"}""");
            if (request.Method == HttpMethod.Post)
                return JsonResponse("""{"invocation-id":"inv_1","status":"queued"}""");
            if (request.RequestUri!.AbsolutePath.EndsWith("/events"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    // SSE 头已到，body 永不下发（挂死流）
                    Content = new StreamContent(new HangingStream())
                    {
                        Headers =
                        {
                            ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                                "text/event-stream"
                            ),
                        },
                    },
                };
            polled = true;
            return JsonResponse(
                """{"invocation-id":"inv_1","status":"completed","cloud-work-units":1,"result":{"status":"pass"}}"""
            );
        });
        var client = new HttpFictionHarnessClient(
            new HttpClient(handler),
            "mk_test_public-secret",
            "http://localhost",
            sseAttemptTimeout: TimeSpan.FromMilliseconds(200)
        );

        var result = await client.ExecuteAsync(
            new HarnessRequest { Capability = "fiction.context.retrieve" },
            "idem-sse-cap",
            CancellationToken.None
        );

        Assert.Equal("completed", result.Status);
        Assert.True(polled, "SSE 尝试上限后必须回退轮询");
    }

    [Fact]
    public async Task ExecuteAsync_ConflictWhileQueued_RetriesUntilCompleted()
    {
        var polls = 0;
        var handler = new StubHandler(request =>
        {
            if (
                request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.EndsWith("/runs")
            )
                return JsonResponse("""{"run-id":"run_1"}""");
            if (request.Method == HttpMethod.Post)
                return JsonResponse("""{"invocation-id":"inv_1","status":"queued"}""");
            polls++;
            if (polls == 1)
                return new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = new StringContent(
                        """{"code":"not_completed"}""",
                        Encoding.UTF8,
                        "application/json"
                    ),
                };
            return JsonResponse(
                """{"invocation-id":"inv_1","status":"completed","cloud-work-units":1,"result":{"status":"pass"}}"""
            );
        });

        var client = new HttpFictionHarnessClient(
            new HttpClient(handler),
            "mk_test_public-secret",
            "http://localhost"
        );

        var result = await client.ExecuteAsync(
            new HarnessRequest { Capability = "fiction.context.retrieve" },
            "idem-2",
            CancellationToken.None
        );

        Assert.Equal("completed", result.Status);
        // /events 尝试（1 次，返回 JSON 而非 SSE）→ 回退轮询：/result 1 次即 completed
        Assert.Equal(2, polls);
    }

    [Fact]
    public void CloudErrorMapper_MapsProblemDetails()
    {
        var exception = CloudErrorMapper.Map(
            HttpStatusCode.TooManyRequests,
            """{"code":"rate_limited","title":"Too many requests"}"""
        );
        Assert.Equal("rate_limited", exception.Code);
        Assert.Equal(
            CloudConnectionState.RateLimited,
            CloudErrorMapper.ToConnectionState(exception.Code)
        );

        var quota = CloudErrorMapper.Map(
            HttpStatusCode.PaymentRequired,
            """{"code":"quota_exceeded","title":"Credits exhausted"}"""
        );
        Assert.Equal(
            CloudConnectionState.QuotaExceeded,
            CloudErrorMapper.ToConnectionState(quota.Code)
        );

        Assert.Equal(
            CloudConnectionState.Authenticating,
            CloudErrorMapper.ToConnectionState("invalid_api_key")
        );
        Assert.Equal(
            CloudConnectionState.Unavailable,
            CloudErrorMapper.ToConnectionState("analysis_unavailable")
        );

        // 服务端 CloudErrors.cs 的真实错误码必须正确归类（否则误报「云端不可用」）
        Assert.Equal(
            CloudConnectionState.QuotaExceeded,
            CloudErrorMapper.ToConnectionState("usage_limit_exceeded")
        );
        Assert.Equal(
            CloudConnectionState.RateLimited,
            CloudErrorMapper.ToConnectionState("rate_limit_exceeded")
        );

        // 业务拒绝（能力/契约/载荷）与连接故障区分，detail 可直接呈现
        Assert.True(CloudErrorMapper.IsBusinessRejection("invalid_analysis_input"));
        Assert.True(CloudErrorMapper.IsBusinessRejection("capability_not_entitled"));
        Assert.True(CloudErrorMapper.IsBusinessRejection("payload_too_large"));
        Assert.False(CloudErrorMapper.IsBusinessRejection("analysis_unavailable"));
        Assert.False(CloudErrorMapper.IsBusinessRejection("invalid_api_key"));
        // 服务端能力/额度口径调整后，capability_not_entitled 不再由服务端产生；
        // 旧服务端返回时按一次性业务拒绝处理，不粘滞整个会话。
        Assert.False(CloudErrorMapper.IsEntitlementDenial("capability_not_entitled"));
        Assert.True(CloudErrorMapper.IsEntitlementDenial("invalid_api_key"));
    }

    [Fact]
    public void CloudErrorMapper_PrefersDetailOverTitle()
    {
        // 服务端 CloudProblems.From 把 title 设为错误码（not_found）、detail 设为可读描述。
        // 客户端必须优先读 detail，避免把错误码当消息（not_found：not_found）。
        var exception = CloudErrorMapper.Map(
            HttpStatusCode.NotFound,
            """{"code":"not_found","title":"not_found","detail":"The requested resource was not found."}"""
        );
        Assert.Equal("not_found", exception.Code);
        Assert.Equal("The requested resource was not found.", exception.Message);
    }

    [Fact]
    public async Task GetContextAsync_ParsesSnapshot()
    {
        var handler = new StubHandler(request =>
        {
            if (
                request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.EndsWith("/runs")
            )
                return JsonResponse("""{"run-id":"run_1"}""");
            if (request.Method == HttpMethod.Post)
                return JsonResponse("""{"invocation-id":"inv_1","status":"queued"}""");
            return JsonResponse(
                """{"invocation-id":"inv_1","status":"completed","cloud-work-units":1,"result":{"status":"pass","context":{"target-chapter":"ch_3","chapter-count":5,"previous-chapter-count":2,"paragraph-count":12},"features":{"mattr":0.7},"material-hints":{"status":"ok","prompt-block":"素材参考A","hints":[]}}}"""
            );
        });

        var client = new HttpFictionHarnessClient(
            new HttpClient(handler),
            "mk_test_public-secret",
            "http://localhost"
        );

        var snapshot = await client.GetContextAsync(
            new HarnessRequest(),
            "idem-3",
            CancellationToken.None
        );

        Assert.Equal("ch_3", snapshot.TargetChapterRef);
        Assert.Equal(5, snapshot.ChapterCount);
        Assert.Equal(2, snapshot.PreviousChapterCount);
        Assert.Equal(12, snapshot.ParagraphCount);
        Assert.NotNull(snapshot.Features);
        var materialHints = Assert.IsType<System.Text.Json.JsonElement>(snapshot.MaterialHints);
        Assert.Equal("ok", materialHints.GetProperty("status").GetString());
        Assert.Equal("素材参考A", materialHints.GetProperty("prompt-block").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_SseFailedEvent_ThrowsCloudApiException()
    {
        var handler = new StubHandler(request =>
        {
            if (
                request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.EndsWith("/runs")
            )
                return JsonResponse("""{"run-id":"run_1"}""");
            if (request.Method == HttpMethod.Post)
                return JsonResponse("""{"invocation-id":"inv_1","status":"queued"}""");
            if (request.RequestUri!.AbsolutePath.EndsWith("/events"))
                return SseResponse(
                    "failed",
                    """{"status":"failed","failure-code":"analysis_failed"}"""
                );
            return JsonResponse(
                """{"invocation-id":"inv_1","status":"completed","cloud-work-units":1}"""
            );
        });

        var client = new HttpFictionHarnessClient(
            new HttpClient(handler),
            "mk_test_public-secret",
            "http://localhost"
        );

        var exception = await Assert.ThrowsAsync<CloudApiException>(() =>
            client.ExecuteAsync(
                new HarnessRequest { Capability = "fiction.context.retrieve" },
                "idem-4",
                CancellationToken.None
            )
        );

        Assert.Equal("analysis_failed", exception.Code);
    }

    [Fact]
    public async Task ExecuteAsync_EventsNotFound_FallsBackToPolling()
    {
        var handler = new StubHandler(request =>
        {
            if (
                request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.EndsWith("/runs")
            )
                return JsonResponse("""{"run-id":"run_1"}""");
            if (request.Method == HttpMethod.Post)
                return JsonResponse(
                    """{"invocation-id":"inv_1","status":"queued","estimated-units":1}"""
                );
            if (request.RequestUri!.AbsolutePath.EndsWith("/events"))
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(
                        """{"code":"not_found","title":"Invocation not found"}""",
                        Encoding.UTF8,
                        "application/json"
                    ),
                };
            return JsonResponse(
                """{"invocation-id":"inv_1","status":"completed","cloud-work-units":1,"result":{"status":"pass"}}"""
            );
        });

        var client = new HttpFictionHarnessClient(
            new HttpClient(handler),
            "mk_test_public-secret",
            "http://localhost"
        );

        var result = await client.ExecuteAsync(
            new HarnessRequest { Capability = "fiction.context.retrieve" },
            "idem-5",
            CancellationToken.None
        );

        Assert.Equal("completed", result.Status);
        Assert.Equal(1, result.CloudWorkUnits);
        // 轮询回退路径同样回传提交报价（两条结果构造点均已接线）
        Assert.Equal(1, result.EstimatedUnits);
    }

    [Fact]
    public async Task ExecuteAsync_RunRejected401_RaisesInvalidationBus()
    {
        var handler = new StubHandler(request => new HttpResponseMessage(
            HttpStatusCode.Unauthorized
        )
        {
            Content = new StringContent(
                """{"code":"invalid_api_key"}""",
                Encoding.UTF8,
                "application/json"
            ),
            // 生产环境由 HttpClient 管线自动填充；stub 中手动补上以校验失败路径上下文
            RequestMessage = request,
        });
        var bus = new CloudAuthInvalidationBus();
        var client = new HttpFictionHarnessClient(
            new HttpClient(handler),
            "mk_test_public-secret",
            "http://localhost",
            invalidationBus: bus
        );

        var raised = 0;
        string? lastContext = null;
        void Handler(string? ctx)
        {
            raised++;
            lastContext = ctx;
        }

        bus.Invalidated += Handler;
        try
        {
            await Assert.ThrowsAsync<CloudApiException>(() =>
                client.ExecuteAsync(
                    new HarnessRequest { Capability = "fiction.context.retrieve" },
                    "idem-401",
                    CancellationToken.None
                )
            );
        }
        finally
        {
            bus.Invalidated -= Handler;
        }

        // §13.4：401 → 真实 API 调用立即触发吊销事件（不等 5 分钟心跳），上下文为失败路径
        Assert.Equal(1, raised);
        Assert.Equal("/v1/agent/runs", lastContext);
    }

    [Fact]
    public async Task ExecuteAsync_RunRejected500_DoesNotRaiseInvalidationBus()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(
            HttpStatusCode.InternalServerError
        )
        {
            Content = new StringContent(
                """{"code":"server_error"}""",
                Encoding.UTF8,
                "application/json"
            ),
        });
        var bus = new CloudAuthInvalidationBus();
        var client = new HttpFictionHarnessClient(
            new HttpClient(handler),
            "mk_test_public-secret",
            "http://localhost",
            invalidationBus: bus
        );

        var raised = 0;
        void Handler(string? ctx) => raised++;
        bus.Invalidated += Handler;
        try
        {
            await Assert.ThrowsAsync<CloudApiException>(() =>
                client.ExecuteAsync(
                    new HarnessRequest { Capability = "fiction.context.retrieve" },
                    "idem-500",
                    CancellationToken.None
                )
            );
        }
        finally
        {
            bus.Invalidated -= Handler;
        }

        // 服务端错误（非凭据吊销）→ 不触发吊销事件
        Assert.Equal(0, raised);
    }

    private static HttpResponseMessage JsonResponse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage SseResponse(string eventType, string data) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"event: {eventType}\ndata: {data}\n\n",
                Encoding.UTF8,
                "text/event-stream"
            ),
        };

    /// <summary>挂死流：读取永不返回数据，直到 CancellationToken 取消（模拟 SSE 连接半开）。</summary>
    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0; // 不可达（Task.Delay 仅以取消结束）
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respondAsync)
            : this(request => respondAsync(request).GetAwaiter().GetResult()) { }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(respond(request));
    }
}
