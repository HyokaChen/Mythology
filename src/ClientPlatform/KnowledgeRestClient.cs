using Microsoft.Extensions.Logging;

namespace ClientPlatform;

/// <summary>
/// 云端素材知识库客户端 — 调用 /v1/knowledge/categories|documents|documents/{id}|search。
///
/// 鉴权：数据面 X-API-Key（Bearer 通道保留给控制面 JWT，API Key 走 Bearer 会被拒 401）。
/// 计费：服务端 [SyncBilling] 三段式——search 按次扣 KnowledgeUnitCost（不足抛
/// 402 usage_limit_exceeded，失败请求释放不扣费）；浏览/取档/分类零价。
/// 库不可用（服务端未配置 ParadeDB）→ 503 knowledge_unavailable。
/// 传输（重试 / 按请求 60s 安全网 / 401→403 吊销总线 / 错误映射）由
/// <see cref="CloudRestTransport"/> 收口。
///
/// 响应为服务端 kebab-case JSON 原文（由调用方/工具直接消费）；请求体走 camelCase——
/// 这些端点在服务端以 JsonDocument 手工读取字面量键（limit/category/mode 等单见；
/// 多词键如未来扩展必须 camelCase，ClientJsonDefaults.Cloud 的 kebab 会静默丢弃）。
/// </summary>
public sealed class KnowledgeRestClient
{
    private readonly CloudRestTransport _transport;

    public KnowledgeRestClient(
        string baseUrl,
        Func<string?> apiKeyProvider,
        HttpClient? http = null,
        CloudAuthInvalidationBus? invalidationBus = null,
        ILogger? logger = null
    )
    {
        _transport = new CloudRestTransport(baseUrl, apiKeyProvider, http, invalidationBus, logger);
    }

    /// <summary>素材分类清单（全部分类与子分类及文档数，零价）。</summary>
    public Task<string> CategoriesAsync(CancellationToken ct = default) =>
        GetAsync("/v1/knowledge/categories", ct);

    /// <summary>按分类/子分类分页浏览素材文档元信息（游标翻页，零价）。</summary>
    public Task<string> BrowseAsync(
        string? category = null,
        string? subcategory = null,
        int limit = 20,
        string? cursor = null,
        CancellationToken ct = default
    )
    {
        var query = new List<string> { $"limit={Math.Clamp(limit, 1, 100)}" };
        if (!string.IsNullOrWhiteSpace(category))
            query.Add($"category={Uri.EscapeDataString(category)}");
        if (!string.IsNullOrWhiteSpace(subcategory))
            query.Add($"subcategory={Uri.EscapeDataString(subcategory)}");
        if (!string.IsNullOrWhiteSpace(cursor))
            query.Add($"cursor={Uri.EscapeDataString(cursor)}");
        return GetAsync($"/v1/knowledge/documents?{string.Join("&", query)}", ct);
    }

    /// <summary>取整篇素材文档（含 Markdown 正文，零价）。</summary>
    public Task<string> GetDocumentAsync(Guid documentId, CancellationToken ct = default) =>
        GetAsync($"/v1/knowledge/documents/{documentId}", ct);

    /// <summary>混合检索（BM25 jieba + 向量余弦 RRF；mode=lexical 强制词法单分支）。</summary>
    public Task<string> SearchAsync(object request, CancellationToken ct = default) =>
        _transport.SendForStringAsync(
            HttpMethod.Post,
            "/v1/knowledge/search",
            request,
            ClientJsonDefaults.CamelCaseRequest,
            ct,
            completionOption: HttpCompletionOption.ResponseHeadersRead
        );

    /// <summary>按当前场景获取素材改写建议（卡片级；按次计费，返回服务端 kebab-case JSON 原文）。</summary>
    public Task<string> HintAsync(object request, CancellationToken ct = default) =>
        _transport.SendForStringAsync(
            HttpMethod.Post,
            "/v1/knowledge/hint",
            request,
            ClientJsonDefaults.CamelCaseRequest,
            ct,
            completionOption: HttpCompletionOption.ResponseHeadersRead
        );

    private Task<string> GetAsync(string path, CancellationToken ct) =>
        _transport.SendForStringAsync(
            HttpMethod.Get,
            path,
            body: null,
            ClientJsonDefaults.CamelCaseRequest,
            ct,
            completionOption: HttpCompletionOption.ResponseHeadersRead
        );
}
