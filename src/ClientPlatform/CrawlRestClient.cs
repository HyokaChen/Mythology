using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ClientPlatform;

/// <summary>爬取任务启动结果（POST /v1/retrieval/crawl 的 202 载荷，服务端 kebab-case）。</summary>
public sealed record CrawlJobStart(string JobId, string Status, int ReservedUnits);

/// <summary>
/// 云端爬取任务客户端（异步任务三段式生命周期）— 调用 /v1/retrieval/crawl 三端点：
/// 提交（202，Idempotency-Key 必填）→ 快照轮询（GET）→ 取消（DELETE，按已成功页结算）。
///
/// 鉴权：数据面 X-API-Key。计费：服务端 [SyncBilling] Manual 模式——提交时预留
/// limit×RetrievalUnitCost，后台按成功页结算（robots-blocked/失败/重复页不计费），
/// 任务结束释放余量；积分不足在提交时抛 402 usage_limit_exceeded。
/// 传输（重试 / 按请求 60s 安全网 / 401→403 吊销总线 / 错误映射）由
/// <see cref="CloudRestTransport"/> 收口（提交幂等性由 Idempotency-Key 保证，
/// 重试不会产生重复任务/重复预留）。
///
/// 请求体走 camelCase（服务端 JsonDocument 手工读取 maxDepth 等多词键），
/// 202 响应按 kebab-case 反序列化。
/// </summary>
public sealed class CrawlRestClient
{
    private readonly CloudRestTransport _transport;

    public CrawlRestClient(
        string baseUrl,
        Func<string?> apiKeyProvider,
        HttpClient? http = null,
        CloudAuthInvalidationBus? invalidationBus = null,
        ILogger? logger = null
    )
    {
        _transport = new CloudRestTransport(baseUrl, apiKeyProvider, http, invalidationBus, logger);
    }

    /// <summary>
    /// 启动异步爬取任务（202）。Idempotency-Key 必填（1-200 字符）：同一键重复提交不会
    /// 重复预留额度（服务端按幂等键去重），客户端网络重试应复用同一键。
    /// </summary>
    public async Task<CrawlJobStart> StartAsync(
        object request,
        string idempotencyKey,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
        {
            throw new ArgumentException(
                "Idempotency-Key 必须为 1-200 字符（服务端强制）。",
                nameof(idempotencyKey)
            );
        }

        var json = await _transport.SendForStringAsync(
            HttpMethod.Post,
            "/v1/retrieval/crawl",
            request,
            ClientJsonDefaults.CamelCaseRequest,
            ct,
            idempotencyKey: idempotencyKey,
            completionOption: HttpCompletionOption.ResponseHeadersRead
        );
        return JsonSerializer.Deserialize<CrawlJobStart>(json, ClientJsonDefaults.Cloud)
            ?? throw new CloudApiException("crawl_failed", "爬取任务启动响应无法解析。");
    }

    /// <summary>
    /// 爬取任务快照：status（queued/running/completed/failed/cancelled）、
    /// completed/total、robotsBlocked、chargedUnits、errors 与分页 pages；
    /// includeMarkdown = true 时 pages 携带 Markdown 正文。
    /// </summary>
    public Task<string> GetSnapshotAsync(
        string jobId,
        bool includeMarkdown = false,
        CancellationToken ct = default
    ) =>
        _transport.SendForStringAsync(
            HttpMethod.Get,
            $"/v1/retrieval/crawl/{Uri.EscapeDataString(jobId)}{(includeMarkdown ? "?include=markdown" : "")}",
            body: null,
            ClientJsonDefaults.CamelCaseRequest,
            ct,
            completionOption: HttpCompletionOption.ResponseHeadersRead
        );

    /// <summary>取消任务（202；后台按已成功页数结算后进入 cancelled 终态）。</summary>
    public async Task CancelAsync(string jobId, CancellationToken ct = default) =>
        _ = await _transport.SendForStringAsync(
            HttpMethod.Delete,
            $"/v1/retrieval/crawl/{Uri.EscapeDataString(jobId)}",
            body: null,
            ClientJsonDefaults.CamelCaseRequest,
            ct,
            completionOption: HttpCompletionOption.ResponseHeadersRead
        );
}
