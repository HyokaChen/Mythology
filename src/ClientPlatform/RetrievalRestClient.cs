using Microsoft.Extensions.Logging;

namespace ClientPlatform;

/// <summary>
/// 云端检索 REST 客户端 — 调用 /v1/retrieval/scrape|search|map|discover。
///
/// 鉴权：数据面 X-API-Key（与 /v1/agent、/v1/sync、/v1/capabilities 探活一致）。
/// Bearer 通道保留给控制面 JWT 会话——API Key 走 Bearer 会被服务端拒 401 invalid_credentials。
///
/// 计费：服务端 [SyncBilling] 三段式（SyncBillingFilter 预留→结算/释放），每次调用扣
/// RetrievalUnitCost 积分（CloudOptions），积分不足抛 402 usage_limit_exceeded；
/// 执行失败释放预留 = 失败请求不扣费。
///
/// 传输（baseUrl 归一 / X-API-Key / 重试 / 按请求 60s 安全网 / 401→403 吊销总线 /
/// 错误映射）一律由 <see cref="CloudRestTransport"/> 收口；本类只声明端点与请求体形状。
/// 响应是服务端 JSON 原文透传给工具层，故请求体走 camelCase
/// （<see cref="ClientJsonDefaults.CamelCaseRequest"/>——这些端点在服务端以 JsonDocument
/// 手工读取字面量键，不走 MVC 模型绑定）。
///
/// 端点收口 CloudEndpoints.ResolveCloudBaseUrl() + /v1/retrieval。
/// </summary>
public sealed class RetrievalRestClient
{
    private readonly CloudRestTransport _transport;

    public RetrievalRestClient(
        string baseUrl,
        Func<string?> apiKeyProvider,
        HttpClient? http = null,
        CloudAuthInvalidationBus? invalidationBus = null,
        ILogger? logger = null
    )
    {
        _transport = new CloudRestTransport(baseUrl, apiKeyProvider, http, invalidationBus, logger);
    }

    /// <summary>抓取网页为 Markdown/HTML/JSON。</summary>
    public Task<string> ScrapeAsync(object request, CancellationToken ct = default) =>
        PostAsync("/v1/retrieval/scrape", request, ct);

    /// <summary>多引擎元搜索（RRF 共识融合）。</summary>
    public Task<string> SearchAsync(object request, CancellationToken ct = default) =>
        PostAsync("/v1/retrieval/search", request, ct);

    /// <summary>站点 URL 发现（sitemap + 页面内链接）。</summary>
    public Task<string> MapAsync(object request, CancellationToken ct = default) =>
        PostAsync("/v1/retrieval/map", request, ct);

    /// <summary>探测页面 JS 调用的 API 端点。</summary>
    public Task<string> DiscoverAsync(object request, CancellationToken ct = default) =>
        PostAsync("/v1/retrieval/discover", request, ct);

    private Task<string> PostAsync(string path, object request, CancellationToken ct) =>
        _transport.SendForStringAsync(
            HttpMethod.Post,
            path,
            request,
            ClientJsonDefaults.CamelCaseRequest,
            ct,
            completionOption: HttpCompletionOption.ResponseHeadersRead
        );
}
