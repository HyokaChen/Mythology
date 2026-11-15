using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ClientPlatform;

/// <summary>题材目录响应（GET /v1/fiction/genres）——服务端题材父子树的只读投影。</summary>
public sealed record GenreCatalogResponse(
    string Version,
    IReadOnlyList<GenreCategoryResponse> Categories
);

/// <summary>一级题材分类。</summary>
public sealed record GenreCategoryResponse(
    string Name,
    IReadOnlyList<GenreSubcategoryResponse> Subcategories
);

/// <summary>二级题材分类。</summary>
public sealed record GenreSubcategoryResponse(string Name);

/// <summary>
/// 创作题材目录客户端 — 调用 /v1/fiction/genres。
///
/// 该端点与 knowledge.categories 语义不同：这里返回的是“玄幻 → 东方玄幻 / 轻小说 → 原生幻想”
/// 这类创作题材树，不是素材库目录。服务端由 FictionIntelligence.GenreDefinitions 单一事实源投影。
/// 鉴权 / 重试 / 401→403 吊销总线与其余数据面客户端一致（CloudRestTransport 收口）。
/// </summary>
public sealed class GenreCatalogRestClient
{
    private readonly CloudRestTransport _transport;

    public GenreCatalogRestClient(
        string baseUrl,
        Func<string?> apiKeyProvider,
        HttpClient? http = null,
        CloudAuthInvalidationBus? invalidationBus = null,
        ILogger? logger = null
    )
    {
        _transport = new CloudRestTransport(baseUrl, apiKeyProvider, http, invalidationBus, logger);
    }

    /// <summary>拉取全部一级/二级题材（零价，仅鉴权）。</summary>
    public async Task<GenreCatalogResponse> GetCatalogAsync(CancellationToken ct = default)
    {
        var response = await _transport.SendForJsonAsync<GenreCatalogResponse>(
            HttpMethod.Get,
            "/v1/fiction/genres",
            body: null,
            ClientJsonDefaults.CamelCaseRequest,
            ClientJsonDefaults.Cloud,
            ct
        );
        return response ?? throw new JsonException("fiction.genres 返回空响应。");
    }
}
