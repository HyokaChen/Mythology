using Core.Shared;
using Xunit;

namespace Core.Tests;

public sealed class AgentErrorFormatterTests
{
    [Fact]
    public void SessionJsonError_IsReducedToReadableLine()
    {
        const string raw =
            """❌ 429 {"error":{"code":"","message":"You've reached the API rate limit for free users. (request id: abc)","type":"AgnesAI_error"}}""";

        var text = AgentErrorFormatter.Format(raw, 429);

        Assert.Equal(
            "HTTP 429 · You've reached the API rate limit for free users. (request id: abc)",
            text
        );
        Assert.DoesNotContain("❌", text);
        Assert.DoesNotContain("{\"error\"", text);
    }

    [Fact]
    public void StatusInsideJson_IsUsedWhenCallerHasNoStatus()
    {
        const string raw =
            """{"error":{"message":"Too Many Requests","type":"throttling_error"},"status":429}""";

        Assert.Equal("HTTP 429 · Too Many Requests", AgentErrorFormatter.Format(raw));
    }

    [Fact]
    public void PlainStatusPrefix_IsNormalized()
    {
        Assert.Equal("HTTP 429 · rate limit", AgentErrorFormatter.Format("429 rate limit"));
        Assert.Equal("HTTP 429 · rate limit", AgentErrorFormatter.Format("HTTP 429: rate limit"));
    }

    [Fact]
    public void OpenAiPrefixAndLongText_AreCleaned()
    {
        var text = AgentErrorFormatter.Format("OpenAIException - " + new string('x', 500));

        Assert.DoesNotContain("OpenAIException", text);
        Assert.EndsWith("…", text);
    }

    [Fact]
    public void EmptyError_FallsBackToUnknown()
    {
        Assert.Equal("未知错误", AgentErrorFormatter.Format(null));
    }
}
