using System.Net;
using ClientPlatform;
using Microsoft.Extensions.Http.Resilience;
using Xunit;

namespace ClientPlatform.Tests;

/// <summary>
/// CloudHttpResilience 退避策略（C4）：瞬态判定集合、指数退避上限、Retry-After 优先与封顶。
/// </summary>
public sealed class CloudHttpResilienceTests
{
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public void IsTransient_GatewayAndRateLimitStatuses_True(HttpStatusCode status)
    {
        Assert.True(CloudHttpResilience.IsTransient(status));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.NotFound)]
    public void IsTransient_NonTransientStatuses_False(HttpStatusCode status)
    {
        Assert.False(CloudHttpResilience.IsTransient(status));
    }

    [Fact]
    public void DelayFor_RetryAfterTakesPrecedence()
    {
        var delay = CloudHttpResilience.DelayFor(3, retryAfterSeconds: 7);

        Assert.Equal(TimeSpan.FromSeconds(7), delay);
    }

    [Fact]
    public void DelayFor_RetryAfterCappedAt30Seconds()
    {
        var delay = CloudHttpResilience.DelayFor(1, retryAfterSeconds: 120);

        Assert.Equal(TimeSpan.FromSeconds(30), delay);
    }

    [Fact]
    public void DelayFor_ExponentialGrowthWithJitter_CappedAt4SecondsBase()
    {
        var first = CloudHttpResilience.DelayFor(1);
        var tenth = CloudHttpResilience.DelayFor(10);

        // attempt=1 基准 500ms + ≤20% jitter；attempt=10 已到 4s 上限 + ≤20% jitter。
        Assert.InRange(first.TotalMilliseconds, 500, 600);
        Assert.InRange(tenth.TotalMilliseconds, 4000, 4800);
    }

    [Fact]
    public void ParseRetryAfter_MissingOrNonPositive_ReturnsNull()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        Assert.Null(CloudHttpResilience.ParseRetryAfter(response));
    }

    [Fact]
    public void CreateRetryOptions_ThreeTotalAttempts_DisablesBuiltInRetryAfter()
    {
        var options = CloudHttpResilience.CreateRetryOptions();

        Assert.Equal(2, options.MaxRetryAttempts); // 1 原始 + 2 重试
        Assert.False(options.ShouldRetryAfterHeader); // Retry-After 由 DelayGenerator 精确解析
    }

    [Fact]
    public async Task Pipeline_Transient503_ThenSuccess_IsRetried()
    {
        var attempts = 0;
        using var client = new HttpClient(
            new ResilienceHandler(CloudHttpResilience.BuildPipeline())
            {
                InnerHandler = new FuncHandler(_ =>
                {
                    attempts++;
                    return attempts == 1
                        ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                        : new HttpResponseMessage(HttpStatusCode.OK);
                }),
            }
        );

        using var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "http://localhost/x")
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, attempts); // 503 → 退避重试 → 成功
    }

    [Fact]
    public async Task Pipeline_HttpRequestException_ThenSuccess_IsRetried()
    {
        var attempts = 0;
        using var client = new HttpClient(
            new ResilienceHandler(CloudHttpResilience.BuildPipeline())
            {
                InnerHandler = new FuncHandler(_ =>
                {
                    attempts++;
                    if (attempts == 1)
                        throw new HttpRequestException("connection reset");
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }),
            }
        );

        using var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "http://localhost/x")
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Pipeline_InternalServerError_NotRetried()
    {
        var attempts = 0;
        using var client = new HttpClient(
            new ResilienceHandler(CloudHttpResilience.BuildPipeline())
            {
                InnerHandler = new FuncHandler(_ =>
                {
                    attempts++;
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }),
            }
        );

        using var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "http://localhost/x")
        );

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(1, attempts); // 500 属服务端缺陷，不重试
    }

    [Fact]
    public async Task Pipeline_PersistentTransient_ExhaustsAfterThreeAttempts()
    {
        var attempts = 0;
        using var client = new HttpClient(
            new ResilienceHandler(CloudHttpResilience.BuildPipeline())
            {
                InnerHandler = new FuncHandler(_ =>
                {
                    attempts++;
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }),
            }
        );

        using var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "http://localhost/x")
        );

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(3, attempts); // 1 原始 + 2 重试后放弃
    }

    private sealed class FuncHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(respond(request));
    }
}
