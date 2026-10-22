using System.Net;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace ClientPlatform;

/// <summary>
/// 云端 HTTP 瞬态失败退避（C4 核心收口）：
/// - IsTransient：429 / 502 / 503 / 504 视为瞬态（5xx 中只取网关类，500 属服务端缺陷应直接上抛）；
/// - DelayFor：指数退避 + 20% jitter，上限 4s；优先采用 Retry-After 头。
/// - CreateRetryOptions / BuildPipeline：官方 Microsoft.Extensions.Http.Resilience 唯一配置源。
/// 轮询协议的预期中间态（如 409 未完成）不适用本工具——那是正常等待，不是失败。
/// </summary>
public static class CloudHttpResilience
{
    public static bool IsTransient(HttpStatusCode status) =>
        status
            is HttpStatusCode.TooManyRequests
                or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout;

    /// <param name="attempt">从 1 开始的尝试序号。</param>
    /// <param name="retryAfterSeconds">Retry-After 头解析值（秒）；非正数忽略。</param>
    public static TimeSpan DelayFor(int attempt, int? retryAfterSeconds = null)
    {
        if (retryAfterSeconds is > 0)
        {
            return TimeSpan.FromSeconds(Math.Min(retryAfterSeconds.Value, 30));
        }

        var baseMs = Math.Min(500d * Math.Pow(2, Math.Max(attempt - 1, 0)), 4000d);
        var jitterMs = Random.Shared.NextDouble() * baseMs * 0.2;
        return TimeSpan.FromMilliseconds(baseMs + jitterMs);
    }

    /// <summary>安全解析 Retry-After 头（秒制）；缺失或非正数返回 null。</summary>
    public static int? ParseRetryAfter(HttpResponseMessage response)
    {
        var delta = response.Headers.RetryAfter?.Delta;
        return delta is { } value && value > TimeSpan.Zero
            ? (int)Math.Ceiling(value.TotalSeconds)
            : null;
    }

    /// <summary>
    /// 官方 Microsoft.Extensions.Http.Resilience 重试策略选项（单一配置源）。
    /// 语义对齐手写版：只重试 429/502/503/504 + HttpRequestException（500 属服务端缺陷，不重试）；
    /// 延迟优先 Retry-After（封顶 30s），否则 500ms 指数退避（封顶 4s）+ 20% jitter。
    /// MaxRetryAttempts=2 → 1 次原始 + 2 次重试。
    /// </summary>
    public static HttpRetryStrategyOptions CreateRetryOptions() =>
        new()
        {
            // 关闭库自带的 Retry-After 自动解析，改由下方 DelayGenerator 精确复用 ParseRetryAfter/DelayFor 语义。
            ShouldRetryAfterHeader = false,
            MaxRetryAttempts = 2,
            ShouldHandle = static args =>
            {
                if (args.Outcome.Exception is HttpRequestException)
                    return new ValueTask<bool>(true);

                var result = args.Outcome.Result;
                return new ValueTask<bool>(result is not null && IsTransient(result.StatusCode));
            },
            DelayGenerator = static args =>
            {
                var result = args.Outcome.Result;
                var retryAfterSeconds = result is not null ? ParseRetryAfter(result) : null;
                // Polly AttemptNumber 为 0 基；手写版 DelayFor 从 1 起 → +1。
                var delay = DelayFor(args.AttemptNumber + 1, retryAfterSeconds);
                return new ValueTask<TimeSpan?>(delay);
            },
        };

    /// <summary>构建可直接包裹 StubHandler 的官方重试流水线（测试用）。</summary>
    public static ResiliencePipeline<HttpResponseMessage> BuildPipeline() =>
        new ResiliencePipelineBuilder<HttpResponseMessage>().AddRetry(CreateRetryOptions()).Build();

    /// <summary>把重试策略挂到 DI 的 <c>AddResilienceHandler</c> 流水线上（Host/Desktop 共用单一配置源）。</summary>
    public static void ConfigureRetry(ResiliencePipelineBuilder<HttpResponseMessage> builder) =>
        builder.AddRetry(CreateRetryOptions());
}
