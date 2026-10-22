using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text.Json;
using Core.Cloud;
using Microsoft.Extensions.Logging;

namespace ClientPlatform;

/// <summary>
/// HTTP 实现 — 通过 Cloud API 的 /v1/agent/runs + harness-invocations 协议执行。
/// 请求携带 X-API-Key 与 Idempotency-Key；SSE 优先读取结果，旧服务器回退到轮询。
/// run 为调用容器：同一 API Key 下多处调用复用一个 run（P1-6），免除每次调用一次
/// CreateRun 往返/写库与孤儿 run；Key 变更（账号切换）自动弃用缓存。
/// </summary>
public sealed class HttpFictionHarnessClient : IFictionHarnessClient
{
    // 流式客户端模式：客户端超时设为无限（SSE 长连接不能被 HttpClient.Timeout 掐断），
    // 单请求超时由按请求的链接 CTS 安全网承担——两者均由
    // CloudRestTransport(streamingClient: true) 施加（CloudTimings 单点收口）。
    private readonly CloudRestTransport _transport;
    private readonly JsonSerializerOptions _json = ClientJsonDefaults.Cloud;
    private readonly TimeProvider _clock;
    private readonly Func<string?> _apiKeyProvider;
    private readonly ILogger? _logger;

    /// <summary>
    /// 复用中的 Agent run（API Key → runId）。run 是调用容器，服务端允许一 run 多 invocation；
    /// 复用免除每次调用一次 CreateRun 往返/写库。Key 变更（账号切换）→ 缓存失效必须新建 run，
    /// 否则会打到别的 workspace 的 run 上（404）。
    /// </summary>
    private volatile ReusedRun? _reusedRun;

    /// <summary>run 解析并发闸门：并发首调不得各自新建 run。</summary>
    private readonly SemaphoreSlim _runGate = new(1, 1);

    /// <summary>SSE 单次尝试上限（服务端执行硬超时 30s；到点弃流回退轮询，见 CloudTimings）。</summary>
    private readonly TimeSpan _sseAttemptTimeout;

    /// <summary>固定 API Key 构造。</summary>
    public HttpFictionHarnessClient(
        HttpClient http,
        string apiKey,
        string baseUrl,
        TimeProvider? timeProvider = null,
        CloudAuthInvalidationBus? invalidationBus = null,
        ILogger? logger = null,
        TimeSpan? sseAttemptTimeout = null
    )
        : this(
            http,
            () => apiKey,
            baseUrl,
            timeProvider,
            invalidationBus,
            logger,
            sseAttemptTimeout
        ) { }

    /// <summary>
    /// 动态 API Key 构造 — 每次请求求值（登录/登出后无需重建客户端）。
    /// <paramref name="sseAttemptTimeout"/>：SSE 单次尝试上限（缺省 CloudTimings.SseAttemptTimeout；
    /// 仅测试注入更短值以覆盖回退路径）。
    /// </summary>
    public HttpFictionHarnessClient(
        HttpClient http,
        Func<string?> apiKeyProvider,
        string baseUrl,
        TimeProvider? timeProvider = null,
        CloudAuthInvalidationBus? invalidationBus = null,
        ILogger? logger = null,
        TimeSpan? sseAttemptTimeout = null
    )
    {
        _transport = new CloudRestTransport(
            baseUrl,
            apiKeyProvider,
            http,
            invalidationBus,
            logger,
            streamingClient: true
        );
        _apiKeyProvider = apiKeyProvider;
        _clock = timeProvider ?? TimeProvider.System;
        _sseAttemptTimeout = sseAttemptTimeout ?? CloudTimings.SseAttemptTimeout;
        _logger = logger; // §13.6 DI 单例注入；null（测试/未接线）时跳过吊销通知
    }

    public async Task<HarnessResult> ExecuteAsync(
        HarnessRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken
    )
    {
        // 本地快速失败：无 API Key（未登录 / 凭据已被吊销清除）时不发 HTTP——避免凭据清除后
        // 的后续工具调用反复命中服务端 401（重复告警 + 无谓网络往返），改为确定性本地拒绝。
        if (string.IsNullOrWhiteSpace(_apiKeyProvider()))
        {
            _logger?.LogWarning(
                "Cloud Harness 调用被跳过（本地无 API Key — 未登录或凭据已清除）: {Capability}",
                request.Capability
            );
            throw new CloudApiException(
                "unauthorized",
                "云端未登录，请使用 :cloud login 登录后重试。"
            );
        }
        var runId = await ResolveRunIdAsync(request, cancellationToken);
        InvocationEnvelope invocation;
        try
        {
            invocation = await CreateInvocationAsync(
                runId,
                request,
                idempotencyKey,
                cancellationToken
            );
        }
        catch (CloudApiException exception) when (exception.Code == "not_found")
        {
            // 缓存 run 在服务端不存在（数据重置 / 跨 workspace 残留）→ 弃用缓存并以新 run 重试一次；
            // 404 在 invocation 创建前发生，服务端未落任何调用，重试安全（幂等键不变）。
            _reusedRun = null;
            var retryRunId = await ResolveRunIdAsync(request, cancellationToken);
            invocation = await CreateInvocationAsync(
                retryRunId,
                request,
                idempotencyKey,
                cancellationToken
            );
        }
        // 幂等命中时服务端返回旧调用真实所属的 run（agentRunId）；用它取结果，
        // 否则用新 run 去拉旧调用会因 run-id 不匹配而 404。
        var effectiveRunId = invocation.AgentRunId ?? runId;
        return await WaitForResultAsync(
            effectiveRunId,
            invocation.InvocationId,
            request.Capability,
            invocation.EstimatedUnits ?? 0, // 提交报价（202）：随结果一并回传给调用方对账
            cancellationToken
        );
    }

    public async Task<AgentContextSnapshot> GetContextAsync(
        HarnessRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken
    )
    {
        var withCapability = request with { Capability = "fiction.context.retrieve" };
        var result = await ExecuteAsync(withCapability, idempotencyKey, cancellationToken);
        return ToSnapshot(result);
    }

    /// <summary>上报设定检查 finding 反馈（CHANGE_10 §4.3）。批量，最多 200 条。</summary>
    public async Task ReportSetupFeedbackAsync(
        IReadOnlyList<(string FindingId, string Decision)> items,
        CancellationToken cancellationToken
    )
    {
        if (items.Count == 0)
            return;
        if (string.IsNullOrWhiteSpace(_apiKeyProvider()))
            throw new CloudApiException(
                "unauthorized",
                "云端未登录，请使用 :cloud login 登录后重试。"
            );
        using var content = JsonContent.Create(
            new Dictionary<string, object?>
            {
                ["items"] = items
                    .Select(i => new Dictionary<string, object?>
                    {
                        ["finding-id"] = i.FindingId,
                        ["decision"] = i.Decision,
                    })
                    .ToList(),
            },
            options: _json
        );
        using var requestMessage = _transport.NewRequest(HttpMethod.Post, "/v1/setup-feedback");
        requestMessage.Content = content;
        using var response = await _transport.SendAsync(requestMessage, cancellationToken);
        var body = await CloudRestTransport.ReadBodyAsync(response, cancellationToken);
        _transport.EnsureSuccess(response, body);
    }

    private async Task<RunEnvelope> CreateRunAsync(
        HarnessRequest request,
        CancellationToken cancellationToken
    )
    {
        using var content = JsonContent.Create(
            new Dictionary<string, object?>
            {
                ["contract-version"] = request.ContractVersion,
                ["goal"] = "Agent harness invocation",
            },
            options: _json
        );
        using var requestMessage = _transport.NewRequest(HttpMethod.Post, "/v1/agent/runs");
        requestMessage.Content = content;
        using var response = await _transport.SendAsync(requestMessage, cancellationToken);
        var body = await CloudRestTransport.ReadBodyAsync(response, cancellationToken);
        _transport.EnsureSuccess(response, body);

        var run = JsonSerializer.Deserialize<RunEnvelope>(body, _json);
        return run ?? throw new CloudApiException("invalid_response", "Empty run response.");
    }

    /// <summary>
    /// 解析本次调用所用的 Agent run：同 Key 命中缓存直接返回；未命中（首调 / Key 变更）
    /// 在闸门内新建并缓存。run 是调用容器（服务端允许一 run 多 invocation）。
    /// </summary>
    private async Task<string> ResolveRunIdAsync(
        HarnessRequest request,
        CancellationToken cancellationToken
    )
    {
        var apiKey = _apiKeyProvider() ?? "";
        if (
            _reusedRun is { } cached
            && string.Equals(cached.ApiKey, apiKey, StringComparison.Ordinal)
        )
        {
            return cached.RunId;
        }

        await _runGate.WaitAsync(cancellationToken);
        try
        {
            if (
                _reusedRun is { } doubleChecked
                && string.Equals(doubleChecked.ApiKey, apiKey, StringComparison.Ordinal)
            )
            {
                return doubleChecked.RunId;
            }
            var run = await CreateRunAsync(request, cancellationToken);
            _reusedRun = new ReusedRun(apiKey, run.RunId);
            return run.RunId;
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task<InvocationEnvelope> CreateInvocationAsync(
        string runId,
        HarnessRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken
    )
    {
        // 出站 payload：显式 DTO（FictionHarnessRequestDto）+ kebab 命名策略序列化。
        // 此前是 78 行匿名对象（camelCase 键）；契约统一 kebab 后匿名键无法携带连字符，
        // 借机重构为可测试的 DTO（云端 CreateHarnessInvocationRequest 的客户端镜像）。
        var payload = FictionHarnessRequestDto.From(request);
        using var content = JsonContent.Create(payload, options: _json);
        using var requestMessage = _transport.NewRequest(
            HttpMethod.Post,
            $"/v1/agent/runs/{runId}/harness-invocations",
            idempotencyKey
        );
        requestMessage.Content = content;
        using var response = await _transport.SendAsync(requestMessage, cancellationToken);
        var body = await CloudRestTransport.ReadBodyAsync(response, cancellationToken);
        _transport.EnsureSuccess(response, body);

        var invocation = JsonSerializer.Deserialize<InvocationEnvelope>(body, _json);
        return invocation
            ?? throw new CloudApiException("invalid_response", "Empty invocation response.");
    }

    private async Task<HarnessResult> WaitForResultAsync(
        string runId,
        string invocationId,
        string capability,
        int estimatedUnits,
        CancellationToken cancellationToken
    )
    {
        var deadline = _clock.GetUtcNow().AddMinutes(2);
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCts.CancelAfter(deadline - _clock.GetUtcNow());

        try
        {
            var sseResult = await TryReadSseResultAsync(
                runId,
                invocationId,
                capability,
                estimatedUnits,
                attemptCts.Token
            );
            if (sseResult is not null)
                return LogChargeDrift(sseResult);

            // SSE 不可用（404 / 非 SSE / 流无终态结束 / 尝试超时）→ 回退到轮询协议
            return LogChargeDrift(
                await PollForResultAsync(
                    runId,
                    invocationId,
                    capability,
                    estimatedUnits,
                    deadline,
                    cancellationToken
                )
            );
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 2 分钟 deadline 或 transport 的 60s 单请求 CTS 触发 → 与轮询路径同码
            throw new CloudApiException(
                "result_timeout",
                "云端分析超时：单次请求超过 60 秒（整体等待上限 2 分钟）。请稍后重试。"
            );
        }
    }

    /// <summary>
    /// 计费对账提示（P2-14）：结算值（cloud-work-units）与提交报价（estimated-units）不一致时留痕——
    /// 服务端账本为准（余额可由账本重建），此日志供用户/运维观察报价偏差，不改变调用结果。
    /// </summary>
    private HarnessResult LogChargeDrift(HarnessResult result)
    {
        if (result.CloudWorkUnits != result.EstimatedUnits)
        {
            _logger?.LogInformation(
                "云端计费结算与提交报价不一致（以账本为准）：{Capability} {InvocationId} 报价 {EstimatedUnits} / 结算 {ChargedUnits}",
                result.Capability,
                result.InvocationId,
                result.EstimatedUnits,
                result.CloudWorkUnits
            );
        }
        return result;
    }

    /// <summary>
    /// SSE 读取结果事件流。返回 null 表示应回退轮询（404 / 非 SSE / 流结束无终态 /
    /// SSE 尝试超时——见 <see cref="CloudTimings.SseAttemptTimeout"/>）。
    /// </summary>
    private async Task<HarnessResult?> TryReadSseResultAsync(
        string runId,
        string invocationId,
        string capability,
        int estimatedUnits,
        CancellationToken attemptToken
    )
    {
        // P2-15：SSE 尝试上限。服务端执行硬超时 30s，但排队可能更久；连接挂死/半开时
        // 不让 2 分钟整体预算被一个死流吃满——到点弃流回退轮询（轮询承接剩余预算）。
        using var sseAttemptCts = CancellationTokenSource.CreateLinkedTokenSource(attemptToken);
        sseAttemptCts.CancelAfter(_sseAttemptTimeout);
        var sseToken = sseAttemptCts.Token;
        try
        {
            using var requestMessage = _transport.NewRequest(
                HttpMethod.Get,
                $"/v1/agent/runs/{runId}/harness-invocations/{invocationId}/events"
            );
            requestMessage.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("text/event-stream")
            );
            using var response = await _transport.SendAsync(
                requestMessage,
                sseToken,
                HttpCompletionOption.ResponseHeadersRead,
                requestTimeout: false
            );

            // 未知调用（404）或旧服务器（非 SSE）→ 回退轮询
            if (
                response.StatusCode == System.Net.HttpStatusCode.NotFound
                || response.Content.Headers.ContentType?.MediaType != "text/event-stream"
            )
            {
                _logger?.LogDebug(
                    "SSE endpoint not available ({Status}), falling back to polling for {RunId}/{InvocationId}",
                    (int)response.StatusCode,
                    runId,
                    invocationId
                );
                return null;
            }

            var stream = await response.Content.ReadAsStreamAsync(sseToken);
            var parser = SseParser.Create(stream);
            await foreach (var item in parser.EnumerateAsync(sseToken))
            {
                switch (item.EventType)
                {
                    case "completed":
                    {
                        var envelope = JsonSerializer.Deserialize<SseResultEnvelope>(
                            item.Data,
                            _json
                        );
                        if (envelope is null)
                            throw new CloudApiException(
                                "invalid_response",
                                "Empty SSE result event."
                            );

                        object? result = null;
                        if (envelope.Result is { } element)
                            result = element;

                        return new HarnessResult
                        {
                            InvocationId = envelope.InvocationId,
                            Status = envelope.Status,
                            Capability = capability,
                            CloudWorkUnits = envelope.CloudWorkUnits,
                            EstimatedUnits = estimatedUnits,
                            Result = result,
                            Warnings = envelope.Warnings ?? [],
                        };
                    }
                    case "failed":
                    {
                        var failed = JsonSerializer.Deserialize<SseFailedEvent>(item.Data, _json);
                        throw new CloudApiException(
                            failed?.FailureCode ?? "analysis_failed",
                            string.IsNullOrWhiteSpace(failed?.FailureDetail)
                                ? "Harness invocation failed."
                                : failed.FailureDetail
                        );
                    }
                    default:
                        continue;
                }
            }

            // 流结束且无终态事件 → 回退轮询兜底
            return null;
        }
        catch (OperationCanceledException) when (!attemptToken.IsCancellationRequested)
        {
            _logger?.LogDebug(
                "SSE attempt exceeded {Timeout}, falling back to polling for {RunId}/{InvocationId}",
                _sseAttemptTimeout,
                runId,
                invocationId
            );
            return null;
        }
    }

    /// <summary>
    /// 轮询 /result 协议（旧服务器回退路径）— 由 _clock 守护 2 分钟 deadline。
    /// </summary>
    private async Task<HarnessResult> PollForResultAsync(
        string runId,
        string invocationId,
        string capability,
        int estimatedUnits,
        DateTimeOffset deadline,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_clock.GetUtcNow() > deadline)
                throw new CloudApiException(
                    "result_timeout",
                    "云端分析超时：等待结果超过 2 分钟仍未返回。请稍后重试。"
                );

            using var requestMessage = _transport.NewRequest(
                HttpMethod.Get,
                $"/v1/agent/runs/{runId}/harness-invocations/{invocationId}/result"
            );

            // 瞬态失败（429/502/503/504 + 网络异常）已收口到 命名云端客户端的
            // 官方 Resilience 流水线（CloudHttpResilience.ConfigureRetry，每请求 ≤3 次尝试）；
            // 轮询层只保留协议语义：409（进行中）与未完成状态 → 低频继续轮询。
            // 单次请求 60s 上限由 transport 的按请求 CTS 承担（此前该 GET 无任何超时）。
            using var response = await _transport.SendAsync(requestMessage, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                await Task.Delay(500, cancellationToken);
                continue;
            }

            var body = await CloudRestTransport.ReadBodyAsync(response, cancellationToken);
            _transport.EnsureSuccess(response, body);

            var envelope = JsonSerializer.Deserialize<ResultEnvelope>(body, _json);
            if (envelope is null)
                throw new CloudApiException("invalid_response", "Empty result response.");

            if (envelope.Status != "completed")
            {
                await Task.Delay(500, cancellationToken);
                continue;
            }

            object? result = null;
            if (envelope.Result is { } element)
                result = element;

            return new HarnessResult
            {
                InvocationId = envelope.InvocationId,
                Status = envelope.Status,
                Capability = capability,
                CloudWorkUnits = envelope.CloudWorkUnits,
                EstimatedUnits = estimatedUnits,
                Result = result,
                Warnings = envelope.Warnings ?? [],
            };
        }
    }

    private static AgentContextSnapshot ToSnapshot(HarnessResult result)
    {
        var snapshot = new AgentContextSnapshot();
        if (result.Result is not JsonElement element)
            return snapshot;

        if (
            element.TryGetProperty("context", out var context)
            && context.ValueKind == JsonValueKind.Object
        )
        {
            snapshot = snapshot with
            {
                TargetChapterRef = context.TryGetProperty("target-chapter", out var refProp)
                    ? refProp.GetString() ?? ""
                    : "",
                ChapterCount = context.TryGetProperty("chapter-count", out var cc)
                    ? cc.GetInt32()
                    : 0,
                PreviousChapterCount = context.TryGetProperty("previous-chapter-count", out var pc)
                    ? pc.GetInt32()
                    : 0,
                ParagraphCount = context.TryGetProperty("paragraph-count", out var para)
                    ? para.GetInt32()
                    : 0,
            };
        }

        if (element.TryGetProperty("features", out var features))
            snapshot = snapshot with { Features = features };
        // 跨仓库缺口 1.5：透传 signals[] + dimensionScores（§6.1），
        // 供基础档 S-1/F-1~F-5 规则与 M-3 爽感单调判定消费。
        if (element.TryGetProperty("signals", out var signals))
            snapshot = snapshot with { Signals = signals };
        if (element.TryGetProperty("dimension-scores", out var dimensionScores))
            snapshot = snapshot with { DimensionScores = dimensionScores };
        if (element.TryGetProperty("material-hints", out var materialHints))
            snapshot = snapshot with { MaterialHints = materialHints };
        return snapshot;
    }

    private sealed record RunEnvelope(
        [property: System.Text.Json.Serialization.JsonPropertyName("run-id")] string RunId
    );

    /// <summary>复用的 run 缓存项（引用类型：赋值原子，配合 volatile 跨线程可见）。</summary>
    private sealed record ReusedRun(string ApiKey, string RunId);

    private sealed record InvocationEnvelope(
        [property: System.Text.Json.Serialization.JsonPropertyName("invocation-id")]
            string InvocationId,
        [property: System.Text.Json.Serialization.JsonPropertyName("agent-run-id")]
            string? AgentRunId,
        string Status,
        [property: System.Text.Json.Serialization.JsonPropertyName("estimated-units")]
            int? EstimatedUnits
    );

    private sealed record ResultEnvelope(
        [property: System.Text.Json.Serialization.JsonPropertyName("invocation-id")]
            string InvocationId,
        string Status,
        [property: System.Text.Json.Serialization.JsonPropertyName("cloud-work-units")]
            int CloudWorkUnits,
        JsonElement? Result,
        IReadOnlyList<string>? Warnings
    );

    private sealed record SseResultEnvelope(
        [property: System.Text.Json.Serialization.JsonPropertyName("invocation-id")]
            string InvocationId,
        [property: System.Text.Json.Serialization.JsonPropertyName("agent-run-id")]
            string AgentRunId,
        string Capability,
        string Status,
        [property: System.Text.Json.Serialization.JsonPropertyName("contract-version")]
            string ContractVersion,
        [property: System.Text.Json.Serialization.JsonPropertyName("engine-version")]
            string EngineVersion,
        [property: System.Text.Json.Serialization.JsonPropertyName("baseline-version")]
            string BaselineVersion,
        [property: System.Text.Json.Serialization.JsonPropertyName("cloud-work-units")]
            int CloudWorkUnits,
        JsonElement? Result,
        IReadOnlyList<string>? Warnings
    );

    private sealed record SseFailedEvent(
        string Status,
        [property: System.Text.Json.Serialization.JsonPropertyName("failure-code")]
            string? FailureCode,
        [property: System.Text.Json.Serialization.JsonPropertyName("failure-detail")]
            string? FailureDetail
    );
}
