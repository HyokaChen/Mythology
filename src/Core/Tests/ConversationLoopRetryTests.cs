using System.Net;
using Core.Agent;
using Xunit;

namespace Core.Tests;

/// <summary>
/// ConversationLoop 瞬态 LLM 调用重试策略测试。
/// 覆盖 CanRetryLlmTurn（429/5xx/网络瞬断 → 重试；OCE/溢出/终态/尝试上限/已产出内容 → 不重试）
/// 与 LlmRetryDelay（普通瞬态 1s→8s；429/TPM 1s→60s；均带 jitter）。
/// </summary>
public sealed class ConversationLoopRetryTests
{
    private static HttpRequestException Http(int status, string message = "boom") =>
        new(message, null, (HttpStatusCode)status);

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(555)]
    public void CanRetryLlmTurn_TransientHttpStatuses_True(int status)
    {
        Assert.True(
            ConversationLoop.CanRetryLlmTurn(Http(status), attempt: 1, anythingEmitted: false)
        );
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public void CanRetryLlmTurn_ClientErrorStatuses_False(int status)
    {
        Assert.False(
            ConversationLoop.CanRetryLlmTurn(Http(status), attempt: 1, anythingEmitted: false)
        );
    }

    [Fact]
    public void CanRetryLlmTurn_ContextOverflow400_NotRetried()
    {
        var overflow = Http(400, "maximum context length exceeded: prompt is too long");

        Assert.False(
            ConversationLoop.CanRetryLlmTurn(overflow, attempt: 1, anythingEmitted: false)
        );
    }

    [Fact]
    public void CanRetryLlmTurn_OperationCanceled_NotRetried()
    {
        Assert.False(
            ConversationLoop.CanRetryLlmTurn(
                new OperationCanceledException(),
                attempt: 1,
                anythingEmitted: false
            )
        );
    }

    [Fact]
    public void CanRetryLlmTurn_RateLimitHasLongerAttemptBudget()
    {
        // 429/TPM：1 次原始 + 7 次重试，覆盖常见 60s 配额窗口。
        Assert.True(
            ConversationLoop.CanRetryLlmTurn(Http(429), attempt: 7, anythingEmitted: false)
        );
        Assert.False(
            ConversationLoop.CanRetryLlmTurn(Http(429), attempt: 8, anythingEmitted: false)
        );

        // 5xx/网络瞬断仍是有界短重试：1 次原始 + 2 次重试。
        Assert.False(
            ConversationLoop.CanRetryLlmTurn(Http(503), attempt: 3, anythingEmitted: false)
        );
    }

    [Theory]
    [InlineData(true)]
    public void CanRetryLlmTurn_AnythingEmitted_NotRetried(bool anythingEmitted)
    {
        // 幂等保护：本轮已产出文本/推理/工具调用时，即使 429 也不重发（避免重复输出/重复工具调用）。
        Assert.False(ConversationLoop.CanRetryLlmTurn(Http(429), attempt: 1, anythingEmitted));
    }

    [Fact]
    public void CanRetryLlmTurn_IOException_IsTransientNetwork_True()
    {
        Assert.True(
            ConversationLoop.CanRetryLlmTurn(
                new IOException("connection reset"),
                attempt: 1,
                anythingEmitted: false
            )
        );
    }

    [Fact]
    public void CanRetryLlmTurn_HttpRequestExceptionNoStatus_WrappingIo_True()
    {
        // 无 HTTP 状态码的 HttpRequestException（未收到响应）+ 内层 IOException = 网络瞬断。
        Assert.True(
            ConversationLoop.CanRetryLlmTurn(
                new HttpRequestException("connection failed", new IOException("reset")),
                attempt: 1,
                anythingEmitted: false
            )
        );
    }

    [Fact]
    public void CanRetryLlmTurn_JsonParsingError_NotRetried()
    {
        Assert.False(
            ConversationLoop.CanRetryLlmTurn(
                new System.Text.Json.JsonException("bad json"),
                attempt: 1,
                anythingEmitted: false
            )
        );
    }

    [Fact]
    public void LlmRetryDelay_ExponentialWithJitter_CappedAt8Seconds()
    {
        var first = ConversationLoop.LlmRetryDelay(1);
        var tenth = ConversationLoop.LlmRetryDelay(10);

        // attempt=1 基准 1000ms + ≤20% jitter；attempt=10 已到 8s 上限 + ≤20% jitter。
        Assert.InRange(first.TotalMilliseconds, 1000, 1200);
        Assert.InRange(tenth.TotalMilliseconds, 8000, 9600);
    }

    [Fact]
    public void LlmRetryDelay_RateLimit_CappedAt60Seconds()
    {
        var first = ConversationLoop.LlmRetryDelay(1, rateLimited: true);
        var tenth = ConversationLoop.LlmRetryDelay(10, rateLimited: true);

        // 429/TPM：1s 起、封顶 60s，jitter ≤15%。
        Assert.InRange(first.TotalMilliseconds, 1000, 1150);
        Assert.InRange(tenth.TotalMilliseconds, 60_000, 69_000);
    }
}
