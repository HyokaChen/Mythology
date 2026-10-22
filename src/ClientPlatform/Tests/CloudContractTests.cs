using ClientPlatform;
using Xunit;

namespace ClientPlatform.Tests;

/// <summary>
/// CloudContract.TryParseExpiresAt 边界测试（§13 Tier 2 可选 expires-at 契约）：
/// 缺失/畸形/非对象/非字符串/解析失败全部回退 null（滑动续期），服务端真值优先。
/// </summary>
public sealed class CloudContractTests
{
    [Fact]
    public void ValidIso8601_ReturnsParsedOffset()
    {
        var parsed = CloudContract.TryParseExpiresAt(
            """{"expires-at":"2026-09-17T00:00:00Z","other":"x"}"""
        );
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero), parsed);
    }

    [Fact]
    public void ExpiresAtWithOffset_ReturnsParsedOffset()
    {
        var parsed = CloudContract.TryParseExpiresAt(
            """{"expires-at":"2026-09-17T08:00:00+08:00"}"""
        );
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 8, 0, 0, TimeSpan.FromHours(8)), parsed);
    }

    [Fact]
    public void MissingField_ReturnsNull()
    {
        Assert.Null(CloudContract.TryParseExpiresAt("""{"plan":"free"}"""));
    }

    [Fact]
    public void NonObjectRoot_ReturnsNull()
    {
        Assert.Null(CloudContract.TryParseExpiresAt("""[{"expires-at":"2026-09-17T00:00:00Z"}]"""));
        Assert.Null(CloudContract.TryParseExpiresAt("\"just a string\""));
        Assert.Null(CloudContract.TryParseExpiresAt("42"));
    }

    [Fact]
    public void NonStringValue_ReturnsNull()
    {
        Assert.Null(CloudContract.TryParseExpiresAt("""{"expires-at":1234567890}"""));
        Assert.Null(CloudContract.TryParseExpiresAt("""{"expires-at":null}"""));
        Assert.Null(CloudContract.TryParseExpiresAt("""{"expires-at":true}"""));
    }

    [Fact]
    public void MalformedDateString_ReturnsNull()
    {
        Assert.Null(CloudContract.TryParseExpiresAt("""{"expires-at":"not-a-date"}"""));
        Assert.Null(CloudContract.TryParseExpiresAt("""{"expires-at":""}"""));
    }

    [Fact]
    public void NonJsonBody_ReturnsNull()
    {
        Assert.Null(CloudContract.TryParseExpiresAt("<html>502 Bad Gateway</html>"));
        Assert.Null(CloudContract.TryParseExpiresAt(""));
        Assert.Null(CloudContract.TryParseExpiresAt("   "));
    }

    [Fact]
    public void MalformedJson_ReturnsNull()
    {
        Assert.Null(CloudContract.TryParseExpiresAt("""{"expires-at":"2026-09-17"""));
        Assert.Null(CloudContract.TryParseExpiresAt("not json {"));
    }

    // ── contract-version 解析（探活契约版本比对）──

    [Fact]
    public void ContractVersion_Present_ReturnsValue()
    {
        Assert.Equal(
            "1.0",
            CloudContract.TryParseContractVersion(
                """{"contract-version":"1.0","capabilities":[]}"""
            )
        );
        Assert.Equal(
            "2.1",
            CloudContract.TryParseContractVersion("""{"contract-version":"2.1"}""")
        );
    }

    [Fact]
    public void ContractVersion_MissingOrInvalid_ReturnsNull()
    {
        Assert.Null(CloudContract.TryParseContractVersion("""{"capabilities":[]}"""));
        Assert.Null(CloudContract.TryParseContractVersion("""{"contract-version":1}"""));
        Assert.Null(CloudContract.TryParseContractVersion("""{"contract-version":""}"""));
        Assert.Null(CloudContract.TryParseContractVersion("""{"contract-version":null}"""));
        Assert.Null(CloudContract.TryParseContractVersion("[1,2]"));
        Assert.Null(CloudContract.TryParseContractVersion("<html>502</html>"));
        Assert.Null(CloudContract.TryParseContractVersion(""));
    }

    [Fact]
    public void IsContractVersionSupported_UnknownTolerated_MismatchRejected()
    {
        // 未知（旧服务端未携带）容忍；与本客户端基线一致放行；其余视为不一致（仅告警用）。
        Assert.True(CloudContract.IsContractVersionSupported(null));
        Assert.True(
            CloudContract.IsContractVersionSupported(CloudContract.SupportedContractVersion)
        );
        Assert.False(CloudContract.IsContractVersionSupported("2.0"));
        Assert.False(CloudContract.IsContractVersionSupported("1.1"));
    }
}
