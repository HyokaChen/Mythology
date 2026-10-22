using System.Text.Json;
using Xunit;

namespace ClientPlatform.Tests;

/// <summary>
/// 同步能力客户端 → 真实后端跨仓库契约测试（与 Auth / Retrieval 契约测试共享同一后端子进程）：
/// knowledge / crawl / embedding 三条链路的鉴权、路由与降级语义——
/// 「两端各自测自己想象的对方」盲区的直接防线。
/// </summary>
[Collection("live-backend")]
public sealed class SyncCapabilitiesContractTests(CloudBackendFixture fixture)
{
    private const string SkipReason =
        "未找到私有后端（设置 MYTHOLOGY_CLOUD_BACKEND 或保持兄弟目录布局），跳过跨仓库契约测试";

    private const string ContractKey = "mk_test_dev-local_contract";

    [Fact]
    public async Task KnowledgeSearch_AgainstLiveBackend_ReachesEndpoint_503WithoutStore()
    {
        if (!fixture.Available)
            Assert.Skip(SkipReason);
        var ct = TestContext.Current.CancellationToken;

        var client = new KnowledgeRestClient(fixture.BaseUrl!, () => ContractKey);

        // 契约测试后端无 PG：知识库不可用 → 503 knowledge_unavailable。
        // 能走到这一步 = X-API-Key 鉴权与 [SyncBilling] filter 链路已打通（否则 401）。
        var ex = await Assert.ThrowsAsync<CloudApiException>(() =>
            client.SearchAsync(new { query = "角色设计" }, ct)
        );

        Assert.Equal("knowledge_unavailable", ex.Code);
    }

    [Fact]
    public async Task Crawl_AgainstLiveBackend_StartsAndReachesTerminalState()
    {
        if (!fixture.Available)
            Assert.Skip(SkipReason);
        var ct = TestContext.Current.CancellationToken;

        var client = new CrawlRestClient(fixture.BaseUrl!, () => ContractKey);

        // 爬后端自身的健康端点：离线可跑的最小爬取任务（limit=1）。
        var start = await client.StartAsync(
            new
            {
                url = $"{fixture.BaseUrl}/health/ready",
                limit = 1,
                maxDepth = 0,
                import = false,
            },
            $"contract-crawl-{Guid.NewGuid():N}",
            ct
        );

        Assert.False(string.IsNullOrWhiteSpace(start.JobId));
        Assert.Equal("queued", start.Status);

        var status = await WaitTerminalAsync(client, start.JobId, ct);
        Assert.Contains(status, new[] { "completed", "failed", "cancelled" });
    }

    [Fact]
    public async Task Embedding_AgainstLiveBackend_AuthenticatesAndFailsExplicitlyWhenUnavailable()
    {
        if (!fixture.Available)
            Assert.Skip(SkipReason);
        var ct = TestContext.Current.CancellationToken;

        var client = new CloudEmbeddingClient(fixture.BaseUrl!, () => ContractKey);

        // 契约测试后端未配置 VectorApiKey → available=false（服务端零向量降级语义）。
        // 客户端显式失败而非静默零向量；能走到这一步 = 鉴权 + 响应解析链路已打通。
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.EmbedAsync("契约测试文本", ct)
        );

        Assert.Contains("不可用", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Usage_AgainstLiveBackend_ReturnsCurrentPeriodQuota()
    {
        if (!fixture.Available)
            Assert.Skip(SkipReason);
        var ct = TestContext.Current.CancellationToken;

        var client = new UsageRestClient(fixture.BaseUrl!, () => ContractKey);

        // 零价查询：不需要 PG（usage 走账本/配额计数，与知识库/爬取不同）。
        // 能取到 200 = X-API-Key 数据面鉴权 + 工作区解析 + kebab 载荷解析全链路已打通。
        var usage = await client.GetUsageAsync(ct: ct);

        Assert.Equal(DateTimeOffset.UtcNow.ToString("yyyy-MM"), usage.Period); // period 缺省当月
        Assert.True(usage.IncludedUnits > 0, "套餐内含额度应为正数（dev 默认 5000）");
        Assert.True(usage.RemainingUnits >= 0, "剩余额度不应为负");
        Assert.True(
            usage.UsedUnits + usage.ReservedUnits + usage.RemainingUnits <= usage.IncludedUnits,
            "已用 + 在途 + 剩余 不得超过内含额度"
        );

        // 账本端点同链路：空账本也应回 200 + 合法 period（不翻页）。
        var ledger = await client.GetLedgerAsync(limit: 1, ct: ct);
        Assert.Equal(usage.Period, ledger.Period);
    }

    [Fact]
    public async Task Capabilities_AgainstLiveBackend_AdvertisesClientContractSurface()
    {
        if (!fixture.Available)
            Assert.Skip(SkipReason);
        var ct = TestContext.Current.CancellationToken;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await http.GetAsync(
            $"{fixture.BaseUrl!.TrimEnd('/')}/v1/capabilities",
            ct
        );
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = document.RootElement;

        // P3-21：契约版本与客户端基线一致（跨仓常量的第一道防线）。
        Assert.Equal(
            CloudContract.SupportedContractVersion,
            root.GetProperty("contract-version").GetString()
        );

        // 同步能力发现面：客户端实际调用的端点必须都在清单中（缺一即为跨仓漂移）。
        var syncIds = root.GetProperty("sync-capabilities")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetString())
            .ToHashSet(StringComparer.Ordinal);
        foreach (
            var id in new[]
            {
                "knowledge.categories",
                "knowledge.browse",
                "knowledge.document",
                "knowledge.search",
                "retrieval.scrape",
                "retrieval.search",
                "retrieval.map",
                "retrieval.discover",
                "retrieval.crawl",
                "embedding",
            }
        )
        {
            Assert.Contains(id, syncIds);
        }

        // 匿名发现同样携带契约版本（客户端探活比对不依赖鉴权成功路径）。
        Assert.True(root.TryGetProperty("contract-version", out _));
    }

    private static async Task<string> WaitTerminalAsync(
        CrawlRestClient client,
        string jobId,
        CancellationToken ct
    )
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = await client.GetSnapshotAsync(jobId, ct: ct);
            using var document = JsonDocument.Parse(snapshot);
            var status = document.RootElement.GetProperty("status").GetString() ?? "";
            if (status is "completed" or "failed" or "cancelled")
            {
                return status;
            }
            await Task.Delay(500, ct);
        }
        throw new TimeoutException("爬取任务 60s 未到终态。");
    }
}
