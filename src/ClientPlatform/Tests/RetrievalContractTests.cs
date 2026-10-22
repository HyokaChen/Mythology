using Xunit;

namespace ClientPlatform.Tests;

/// <summary>
/// 检索 REST 客户端 → 真实后端跨仓库契约测试（CloudBackendFixture 拉起 Mythology.Cloud.Api 子进程）。
///
/// 关键回归面（2026-09-13 修复）：鉴权必须走 X-API-Key——曾以 Authorization: Bearer 发送 API Key，
/// 被数据面拒 401 invalid_credentials（真实后端实测）；stub 测试与后端自身测试都覆盖不到这个
/// 通道错配，只有真实后端契约测试能钉死。
/// </summary>
[Collection("live-backend")]
public sealed class RetrievalContractTests(CloudBackendFixture fixture)
{
    private const string SkipReason =
        "未找到私有后端（设置 MYTHOLOGY_CLOUD_BACKEND 或保持兄弟目录布局），跳过跨仓库契约测试";

    [Fact]
    public async Task Scrape_AgainstLiveBackend_AuthenticatesWithApiKey()
    {
        if (!fixture.Available)
            Assert.Skip(SkipReason);
        var ct = TestContext.Current.CancellationToken;

        var client = new RetrievalRestClient(fixture.BaseUrl!, () => "mk_test_dev-local_contract");

        // 抓取后端自身的健康端点（无需外网）：静态层直接命中。
        // 鉴权失败 → 401 → CloudApiException 抛出即测试失败；成功返回的任何 ExtractionResult
        // 都含 "status" 字段，足以证明「Key → X-API-Key 头 → 数据面裁决」整条链路打通。
        var json = await client.ScrapeAsync(new { url = $"{fixture.BaseUrl}/health/ready" }, ct);

        Assert.Contains("\"status\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Map_AgainstLiveBackend_AuthenticatesWithApiKey()
    {
        if (!fixture.Available)
            Assert.Skip(SkipReason);
        var ct = TestContext.Current.CancellationToken;

        var client = new RetrievalRestClient(fixture.BaseUrl!, () => "mk_test_dev-local_contract");

        // 站点 URL 发现打后端自身根路径：返回 {"links":[...]} 即证明鉴权与路由链路打通。
        var json = await client.MapAsync(new { url = $"{fixture.BaseUrl}/", limit = 100 }, ct);

        Assert.Contains("\"links\"", json, StringComparison.Ordinal);
    }
}
