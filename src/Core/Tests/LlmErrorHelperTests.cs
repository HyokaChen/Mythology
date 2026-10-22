using Core.Infrastructure;
using Xunit;

namespace Core.Tests;

public class LlmErrorHelperTests
{
    [Fact]
    public void Extract_NonHttpException_ReturnsNullStatusCode()
    {
        var ex = new InvalidOperationException("something went wrong");
        var (code, msg, isOverflow) = LlmErrorHelper.Extract(ex);

        Assert.Null(code);
        Assert.Equal("something went wrong", msg);
        Assert.False(isOverflow);
    }

    [Fact]
    public void Extract_HttpRequestException_ExtractsStatusCode()
    {
        var inner = new InvalidOperationException("bad request");
        var ex = new HttpRequestException("error", inner, System.Net.HttpStatusCode.BadRequest);

        var (code, msg, _) = LlmErrorHelper.Extract(ex);

        Assert.Equal(400, code);
        Assert.Equal("bad request", msg);
    }

    [Fact]
    public void Extract_ContextOverflowDetected_ReturnsTrue()
    {
        var ex = new HttpRequestException("error", null, System.Net.HttpStatusCode.BadRequest);
        // Set InnerException via a different approach — use the exception itself
        var contextEx = new HttpRequestException(
            "context_length_exceeded",
            null,
            System.Net.HttpStatusCode.BadRequest
        );
        var (code, msg, isOverflow) = LlmErrorHelper.Extract(contextEx);

        Assert.Equal(400, code);
        Assert.True(isOverflow);
    }

    [Fact]
    public void Extract_ContextOverflowWithPromptTooLong_ReturnsTrue()
    {
        var ex = new HttpRequestException(
            "prompt is too long for this model",
            null,
            System.Net.HttpStatusCode.BadRequest
        );
        var (code, msg, isOverflow) = LlmErrorHelper.Extract(ex);

        Assert.Equal(400, code);
        Assert.True(isOverflow);
    }

    [Fact]
    public void Extract_ContextOverflowWithTooManyTokens_ReturnsTrue()
    {
        var ex = new HttpRequestException(
            "too many tokens in the prompt",
            null,
            System.Net.HttpStatusCode.BadRequest
        );
        var (code, msg, isOverflow) = LlmErrorHelper.Extract(ex);

        Assert.Equal(400, code);
        Assert.True(isOverflow);
    }

    [Fact]
    public void Extract_ContextOverflowWithMaximumContext_ReturnsTrue()
    {
        var ex = new HttpRequestException(
            "maximum context length exceeded",
            null,
            System.Net.HttpStatusCode.BadRequest
        );
        var (code, msg, isOverflow) = LlmErrorHelper.Extract(ex);

        Assert.Equal(400, code);
        Assert.True(isOverflow);
    }

    [Fact]
    public void Extract_ContextAndLengthCombined_ReturnsTrue()
    {
        var ex = new HttpRequestException(
            "This model's context length is 128k",
            null,
            System.Net.HttpStatusCode.BadRequest
        );
        var (code, msg, isOverflow) = LlmErrorHelper.Extract(ex);

        Assert.Equal(400, code);
        Assert.True(isOverflow);
    }

    [Fact]
    public void Extract_PromptAndTooLongCombined_ReturnsTrue()
    {
        var ex = new HttpRequestException(
            "prompt exceeds maximum allowed length",
            null,
            System.Net.HttpStatusCode.BadRequest
        );
        var (code, msg, isOverflow) = LlmErrorHelper.Extract(ex);

        Assert.Equal(400, code);
        Assert.True(isOverflow);
    }

    [Fact]
    public void Extract_InvalidTokenError_ReturnsFalse()
    {
        // Should NOT trigger context overflow — "token" alone in "invalid token" is not enough
        var ex = new HttpRequestException(
            "invalid token in request",
            null,
            System.Net.HttpStatusCode.BadRequest
        );
        var (code, msg, isOverflow) = LlmErrorHelper.Extract(ex);

        Assert.Equal(400, code);
        Assert.False(isOverflow);
    }

    [Fact]
    public void Extract_Non400StatusCode_ReturnsFalse()
    {
        var ex = new HttpRequestException(
            "context length exceeded",
            null,
            System.Net.HttpStatusCode.InternalServerError
        );
        var (code, msg, isOverflow) = LlmErrorHelper.Extract(ex);

        Assert.Equal(500, code);
        Assert.False(isOverflow);
    }

    [Fact]
    public void Extract_InnerExceptionMessagePreferred_OverOuter()
    {
        var inner = new InvalidOperationException("inner error message");
        var ex = new HttpRequestException("outer error", inner);

        var (_, msg, _) = LlmErrorHelper.Extract(ex);

        Assert.Equal("inner error message", msg);
    }

    [Fact]
    public void Extract_NoInnerException_UsesOuterMessage()
    {
        var ex = new HttpRequestException("direct error message", null);

        var (_, msg, _) = LlmErrorHelper.Extract(ex);

        Assert.Equal("direct error message", msg);
    }

    [Fact]
    public void Extract_DeepSeekExceedContextSize_ReturnsTrue()
    {
        // DeepSeek 返回: "exceed_context_size_error" + "exceeds the available context size"
        var inner = new InvalidOperationException(
            "{\"error\":{\"code\":400,\"message\":\"request (22209 tokens) exceeds the available context size (6384 tokens)\","
                + "\"type\":\"exceed_context_size_error\"}}"
        );
        var ex = new HttpRequestException("error", inner, System.Net.HttpStatusCode.BadRequest);

        var (code, msg, isOverflow) = LlmErrorHelper.Extract(ex);

        Assert.Equal(400, code);
        Assert.True(isOverflow);
    }

    [Fact]
    public void Extract_ContextWindowExceeded_ReturnsTrue()
    {
        var ex = new HttpRequestException(
            "context window exceeded",
            null,
            System.Net.HttpStatusCode.BadRequest
        );
        var (code, _, isOverflow) = LlmErrorHelper.Extract(ex);

        Assert.Equal(400, code);
        Assert.True(isOverflow);
    }

    [Fact]
    public void Extract_ContextSizeExceeded_ReturnsTrue()
    {
        var ex = new HttpRequestException(
            "context_size_exceeded",
            null,
            System.Net.HttpStatusCode.BadRequest
        );
        var (code, _, isOverflow) = LlmErrorHelper.Extract(ex);

        Assert.Equal(400, code);
        Assert.True(isOverflow);
    }

    [Fact]
    public void Extract_PromptExceedsLength_ReturnsTrue()
    {
        // "exceeds" (第三人称) 而非 "exceeded" (过去式)
        var ex = new HttpRequestException(
            "prompt exceeds the maximum context length",
            null,
            System.Net.HttpStatusCode.BadRequest
        );
        var (code, _, isOverflow) = LlmErrorHelper.Extract(ex);

        Assert.Equal(400, code);
        Assert.True(isOverflow);
    }
}
