using Core.Shared;
using Xunit;

namespace Core.Tests;

/// <summary>
/// SecretRedactor 凭据脱敏测试：LlmTornado 异常消息里的请求头、Bearer 令牌与裸密钥字面量。
/// </summary>
public sealed class SecretRedactorTests
{
    /// <summary>LlmTornado HttpRequestException 的真实消息形态（键名保留，值被脱敏）。</summary>
    [Fact]
    public void Redact_LlmTornadoRequestHeaders_RemovesApiKey()
    {
        const string message = """
            Error at Embeddings (https://api.example.cn/v1/embeddings) with HTTP status code: ServiceUnavailable. Request: {"User-Agent":["Mozilla/5.0"],"Authorization":["Bearer sk-i2FCsLpgN7q8Ybpcq9REIFq2F49rPp5W2eJf"],"api-key":["sk-i2FCsLpgN7q8Ybpcq9REIFq2F49rPp5W2eJf"]}
            """;

        var redacted = SecretRedactor.Redact(message);

        Assert.DoesNotContain("sk-i2FCsLpgN7q8", redacted, StringComparison.Ordinal);
        Assert.Contains("\"Authorization\":", redacted, StringComparison.Ordinal);
        Assert.Contains("\"api-key\":", redacted, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Mask, redacted, StringComparison.Ordinal);
        // 诊断信息必须保留
        Assert.Contains("ServiceUnavailable", redacted, StringComparison.Ordinal);
        Assert.Contains("https://api.example.cn/v1/embeddings", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_BearerTokenInHeaderLine_Masked()
    {
        var redacted = SecretRedactor.Redact("Authorization: Bearer abcdef1234567890XYZ");

        Assert.Equal($"Authorization: Bearer {SecretRedactor.Mask}", redacted);
    }

    [Fact]
    public void Redact_BareKeyLiteral_Masked()
    {
        var redacted = SecretRedactor.Redact("api_key: sk-abcdefghijklmnopqrstuvwxyz012345");

        Assert.DoesNotContain("abcdefghijklmnop", redacted, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Mask, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_TextWithoutSecrets_ReturnsSameInstance()
    {
        const string text = "工具搜索索引 embedding 失败，将仅使用 BM25 检索";

        Assert.Same(text, SecretRedactor.Redact(text));
    }

    [Fact]
    public void Redact_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal("", SecretRedactor.Redact(null));
        Assert.Equal("", SecretRedactor.Redact(""));
    }

    /// <summary>短 token（如 "Bearer x"）不匹配，避免把普通英文句子误脱敏。</summary>
    [Fact]
    public void Redact_ShortToken_NotMasked()
    {
        const string text = "use a Bearer token";

        Assert.Equal(text, SecretRedactor.Redact(text));
    }
}
