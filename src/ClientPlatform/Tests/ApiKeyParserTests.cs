using ClientPlatform;
using Xunit;

namespace ClientPlatform.Tests;

/// <summary>
/// ApiKeyParser 测试：手动 API Key 的 publicId 解析（镜像后端 ApiKeyService.TryParse）。
/// 回归：原先手动解析错误地追加 dev_ 前缀（dev_{parts[2]}），导致 oauth… 变 dev_oauth…、dev… 变 dev_dev…。
/// </summary>
public sealed class ApiKeyParserTests
{
    [Fact]
    public void OAuthKey_ReturnsOauthPublicId_WithoutDevPrefix()
    {
        // oauth 前缀 key：publicId = oauth1234…（不追加 dev_）
        Assert.Equal(
            "oauthabcd1234efgh5678",
            ApiKeyParser.TryGetPublicId("mk_live_oauthabcd1234efgh5678_secret")
        );
    }

    [Fact]
    public void DeviceKey_ReturnsDevPublicId_WithoutDoublePrefix()
    {
        // dev 前缀 key：publicId = devabcd1234…（不变成 dev_devabcd…）
        Assert.Equal(
            "devabcd1234efgh5678",
            ApiKeyParser.TryGetPublicId("mk_live_devabcd1234efgh5678_secret")
        );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrWhitespace_ReturnsNull(string? key) =>
        Assert.Null(ApiKeyParser.TryGetPublicId(key));

    [Fact]
    public void WrongPrefix_ReturnsNull()
    {
        Assert.Null(ApiKeyParser.TryGetPublicId("sk_live_abc_secret"));
        Assert.Null(ApiKeyParser.TryGetPublicId("mk_test_abc_secret")); // 数据面 Key 只签发 mk_live_
    }

    [Fact]
    public void MissingSecret_ReturnsNull()
    {
        // 只有 3 段（缺 secret）
        Assert.Null(ApiKeyParser.TryGetPublicId("mk_live_abc"));
    }

    [Fact]
    public void EmptyPublicIdOrSecret_ReturnsNull()
    {
        Assert.Null(ApiKeyParser.TryGetPublicId("mk_live__secret"));
        Assert.Null(ApiKeyParser.TryGetPublicId("mk_live_abc_"));
    }

    [Fact]
    public void SecretContainingUnderscore_ParsesPublicIdCorrectly()
    {
        // secret 含下划线：Split('_', 4) 第 4 段整体为 secret，publicId 仍为第 3 段
        Assert.Equal("abc", ApiKeyParser.TryGetPublicId("mk_live_abc_def_ghi_jkl"));
    }

    [Fact]
    public void LeadingTrailingWhitespace_IsTrimmed()
    {
        Assert.Equal("abc123", ApiKeyParser.TryGetPublicId("  mk_live_abc123_secret  "));
    }
}
