using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;

namespace ClientPlatform;

/// <summary>
/// 云端 REST 传输唯一收口：baseUrl 归一化 · <c>X-API-Key</c> / <c>Idempotency-Key</c> ·
/// 默认 HttpClient（自带官方 Resilience 重试流水线）· 按请求 60s 安全网 ·
/// 401/403 吊销总线 · 失败日志 · <see cref="CloudErrorMapper"/> 异常映射
/// （服务端 <c>code</c>/<c>detail</c> 为权威）。
///
/// <para>
/// 端点路径、请求体与响应形态**留在各自客户端**（wire 语义显式可见、可被 StubHandler
/// 直测）；<see cref="HttpResponseMessage"/> 由调用方 <c>using</c>——Harness 的 SSE
/// 要在返回后继续读流。刻意不做泛型/源生成/attribute 驱动（见 ARCHITECTURE §7.1）。
/// </para>
/// </summary>
internal sealed class CloudRestTransport
{
    private readonly string _baseUrl;
    private readonly Func<string?> _apiKeyProvider;
    private readonly CloudAuthInvalidationBus? _invalidationBus;
    private readonly ILogger? _logger;
    private readonly HttpClient _client;
    private readonly TimeSpan _requestTimeout;

    /// <param name="baseUrl">云端基地址（尾斜杠在此归一，避免拼出 <c>//v1/...</c>）。</param>
    /// <param name="apiKeyProvider">每次请求求值的 API Key（null/空 → 不发 <c>X-API-Key</c> 头）。</param>
    /// <param name="http">
    /// 注入实例（DI 路径：命名云端客户端）；null → 自建带同款重试流水线的客户端。
    /// </param>
    /// <param name="invalidationBus">401/403 吊销总线（null = 测试/未接线）。</param>
    /// <param name="logger">失败日志通道（原始响应体在此进日志，异常消息用服务端 detail）。</param>
    /// <param name="streamingClient">
    /// 流式（SSE）客户端：Timeout 取 <see cref="CloudTimings.StreamingClientTimeout"/>（无限），
    /// 单请求超时改由 <see cref="SendAsync"/> 的按请求 CTS 承担。注入实例仅在 true 时改
    /// Timeout（DI 每次 <c>CreateClient</c> 返回独立包装实例，不污染其他消费者）。
    /// </param>
    /// <param name="requestTimeout">
    /// 按请求安全网（缺省 <see cref="CloudTimings.CloudApiTimeout"/>）；仅测试注入更短值以覆盖超时路径。
    /// </param>
    public CloudRestTransport(
        string baseUrl,
        Func<string?> apiKeyProvider,
        HttpClient? http = null,
        CloudAuthInvalidationBus? invalidationBus = null,
        ILogger? logger = null,
        bool streamingClient = false,
        TimeSpan? requestTimeout = null
    )
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKeyProvider = apiKeyProvider;
        _invalidationBus = invalidationBus;
        _logger = logger;
        _requestTimeout = requestTimeout ?? CloudTimings.CloudApiTimeout;

        if (http is null)
        {
            _client = new HttpClient(
                new ResilienceHandler(CloudHttpResilience.BuildPipeline())
                {
                    InnerHandler = new HttpClientHandler(),
                }
            )
            {
                Timeout = streamingClient ? CloudTimings.StreamingClientTimeout : _requestTimeout,
            };
        }
        else
        {
            if (streamingClient)
            {
                http.Timeout = CloudTimings.StreamingClientTimeout;
            }
            _client = http;
        }
    }

    /// <summary>构造请求：绝对 URL + <c>X-API-Key</c>（非空时）+ <c>Idempotency-Key</c>（非空时）。</summary>
    public HttpRequestMessage NewRequest(
        HttpMethod method,
        string path,
        string? idempotencyKey = null
    )
    {
        var request = new HttpRequestMessage(method, _baseUrl + path);
        var apiKey = _apiKeyProvider();
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Add("X-API-Key", apiKey);
        }
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }
        return request;
    }

    /// <summary>
    /// 发送请求。<paramref name="requestTimeout"/> 为 true 时按请求 CTS 只覆盖到**本方法返回**
    /// （缓冲读：体已随响应读完；头读：体仍未读）——头读调用点因此必须自持覆盖到体的 CTS，
    /// 见 <see cref="SendForStringAsync"/>（SSE 由调用方 token 管生命周期，传 false）。
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken ct,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
        bool requestTimeout = true
    )
    {
        if (!requestTimeout)
        {
            return await _client.SendAsync(request, completionOption, ct);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_requestTimeout);
        return await _client.SendAsync(request, completionOption, timeoutCts.Token);
    }

    /// <summary>
    /// 一次完整的「发请求 → 读体 → 判失败 → 返回响应体原文」：透传型端点（响应由调用方
    /// 原样消费或自行反序列化）的唯一入口。请求体用 <paramref name="bodyJson"/> 序列化
    /// （camelCase 与 kebab 两条 wire 约定由调用方显式选择）。
    ///
    /// 按请求安全网在本方法内自持、覆盖到响应体读完为止：头读模式下响应体不在任何超时内
    ///（<c>HttpClient.Timeout</c> 不覆盖头读后的体读取，实测亦然），若交给 <see cref="SendAsync"/>
    /// 内部那个「收到响应头即释放」的 CTS，服务端发完头后卡死将无限挂起。
    /// </summary>
    public async Task<string> SendForStringAsync(
        HttpMethod method,
        string path,
        object? body,
        JsonSerializerOptions bodyJson,
        CancellationToken ct,
        string? idempotencyKey = null,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead
    )
    {
        using var request = NewRequest(method, path, idempotencyKey);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: bodyJson);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_requestTimeout);

        using var response = await SendAsync(
            request,
            timeoutCts.Token,
            completionOption,
            requestTimeout: false
        );
        var responseText = await ReadBodyAsync(response, timeoutCts.Token);
        EnsureSuccess(response, responseText);
        return responseText;
    }

    /// <summary>
    /// 失败路径统一裁决：401/403 → 吊销总线；原始体（截断 500）→ 日志；
    /// 异常一律 <see cref="CloudErrorMapper.Map"/>（服务端 code/detail 为权威）。
    /// 同步：响应体已由调用方读毕，每次失败只读一次体。
    /// </summary>
    public void EnsureSuccess(HttpResponseMessage response, string body)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        _invalidationBus?.IsCredentialInvalid(response);
        _logger?.LogWarning(
            "Cloud API error: {Method} {Path} → {Status} [{Body}]",
            response.RequestMessage?.Method,
            response.RequestMessage?.RequestUri?.AbsolutePath,
            (int)response.StatusCode,
            body.Length > 500 ? body[..500] : body
        );
        throw CloudErrorMapper.Map(response.StatusCode, body);
    }

    public static Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct) =>
        response.Content.ReadAsStringAsync(ct);
}
