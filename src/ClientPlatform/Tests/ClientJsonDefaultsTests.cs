using System.Text.Json;
using ClientPlatform;
using Xunit;

namespace ClientPlatform.Tests;

/// <summary>
/// P3-22 双 wire 约定的不变量（防「统一序列化选项」回归）：
/// - <see cref="ClientJsonDefaults.Cloud"/> = kebab-case，供 MVC 模型绑定端点（harness/sync/oauth/device）；
/// - <see cref="ClientJsonDefaults.CamelCaseRequest"/> = camelCase，供服务端 JsonDocument 手读字面量键的端点
///   （retrieval/knowledge/embedding）——这些端点读不到 kebab 键会静默使用默认值且不报错。
/// 两者不可互换；本测试钉死差异，端点级 camelCase 多词键另有 Crawl/Retrieval 客户端测试覆盖。
/// </summary>
public sealed class ClientJsonDefaultsTests
{
    private sealed record WireSample(string MultiWordKey, int Single);

    [Fact]
    public void CloudAndCamelCaseRequest_KeepDistinctWireStyles()
    {
        var sample = new WireSample("v", 1);

        var kebab = JsonSerializer.Serialize(sample, ClientJsonDefaults.Cloud);
        var camel = JsonSerializer.Serialize(sample, ClientJsonDefaults.CamelCaseRequest);

        Assert.Contains("\"multi-word-key\"", kebab, StringComparison.Ordinal);
        Assert.DoesNotContain("multiWordKey", kebab, StringComparison.Ordinal);

        Assert.Contains("\"multiWordKey\"", camel, StringComparison.Ordinal);
        Assert.DoesNotContain("multi-word-key", camel, StringComparison.Ordinal);
    }

    [Fact]
    public void NamingPolicies_AreKebabAndCamel()
    {
        Assert.Equal(
            JsonNamingPolicy.KebabCaseLower,
            ClientJsonDefaults.Cloud.PropertyNamingPolicy
        );
        Assert.Equal(
            JsonNamingPolicy.CamelCase,
            ClientJsonDefaults.CamelCaseRequest.PropertyNamingPolicy
        );
    }
}
