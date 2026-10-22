using System.Text.Json;
using Core.Vector;
using Microsoft.Extensions.Logging;

namespace ClientPlatform;

/// <summary>
/// 云端 embedding 客户端 — 实现 Core 向量契约（<see cref="IEmbeddingService"/> /
/// <see cref="IBatchEmbeddingService"/>），调用 POST /v1/embedding。
///
/// 鉴权：数据面 X-API-Key。计费：服务端 [SyncBilling] 三段式——EmbeddingUnitCost
/// 默认 0 = 不计量；&gt;0 时按次预留/结算（失败释放不扣费）。
///
/// 维度：本地向量库（SqliteRetrievalStore / AgentMemoryStore）固定为
/// <see cref="VectorDefaults.Dimension"/>（1024）——云端 VectorDimension 必须与之相同，
/// 不一致显式失败（本地索引无法容纳异维度向量）。
/// 服务端未配置云端 embedding（available=false、返回零向量）时显式抛错而非静默降级，
/// 避免零向量污染本地索引。
///
/// 本客户端在 DI 容器构建前构造（宿主 embedding 工厂路径），不能走 IHttpClientFactory
/// 命名客户端——故由 <see cref="CloudRestTransport"/> 自建带官方 Resilience 重试流水线的
/// 客户端（与命名客户端同款：429/502/503/504 + 网络异常；402/401 不重试）。
/// 传输与错误映射收口在 transport；本类只保留 **payload 语义**校验（available / 维度 /
/// 条数），它们与 HTTP 状态码无关，故仍用 <c>embedding_failed</c> 等本地码。
/// </summary>
public sealed class CloudEmbeddingClient : IEmbeddingService, IBatchEmbeddingService
{
    private readonly CloudRestTransport _transport;

    public CloudEmbeddingClient(
        string baseUrl,
        Func<string?> apiKeyProvider,
        int dimension = VectorDefaults.Dimension,
        HttpClient? http = null,
        CloudAuthInvalidationBus? invalidationBus = null,
        ILogger? logger = null
    )
    {
        if (dimension <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dimension), "向量维度必须为正数。");
        }

        Dimension = dimension;
        _transport = new CloudRestTransport(baseUrl, apiKeyProvider, http, invalidationBus, logger);
    }

    /// <summary>向量维度（须与服务端 VectorDimension 一致；默认 VectorDefaults.Dimension）。</summary>
    public int Dimension { get; }

    /// <summary>单条 embedding（内部走批量端点，一次 HTTP 往返）。</summary>
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var vectors = await EmbedBatchAsync([text], ct);
        return vectors.Length > 0
            ? vectors[0]
            : throw new CloudApiException("embedding_failed", "云端 embedding 返回空结果。");
    }

    /// <summary>单批文本上限（与服务端 EmbeddingController 的 texts ≤ 100 校验对齐）。</summary>
    public const int MaxBatchSize = 100;

    /// <summary>
    /// 批量 embedding（顺序与返回数组一一对应）。超过 <see cref="MaxBatchSize"/> 条自动切批，
    /// 每批一次 HTTP 往返；不切批会让超限请求收到 400 并退化为逐条请求。
    /// </summary>
    public async Task<float[][]> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct = default
    )
    {
        if (texts.Count == 0)
        {
            return [];
        }
        if (texts.Count <= MaxBatchSize)
        {
            return await EmbedChunkAsync(texts, ct);
        }

        var results = new float[texts.Count][];
        var offset = 0;
        foreach (var chunk in texts.Chunk(MaxBatchSize))
        {
            var vectors = await EmbedChunkAsync(chunk, ct);
            for (var i = 0; i < vectors.Length; i++)
            {
                results[offset + i] = vectors[i];
            }
            offset += vectors.Length;
        }
        return results;
    }

    /// <summary>单批（≤ <see cref="MaxBatchSize"/> 条）一次 HTTP 往返。</summary>
    private async Task<float[][]> EmbedChunkAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var json = await _transport.SendForStringAsync(
            HttpMethod.Post,
            "/v1/embedding",
            new { texts },
            ClientJsonDefaults.CamelCaseRequest,
            ct,
            completionOption: HttpCompletionOption.ResponseHeadersRead
        );
        var parsed =
            JsonSerializer.Deserialize<EmbeddingResponse>(json, ClientJsonDefaults.Cloud)
            ?? throw new CloudApiException("embedding_failed", "云端 embedding 响应无法解析。");

        if (!parsed.Available)
        {
            throw new InvalidOperationException(
                "云端 embedding 不可用（服务端未配置 embedding 接口，返回零向量）。"
                    + "请配置云端 Cloud:VectorApiKey，或改用本地 embedding（config.yaml Embedding.Provider）。"
            );
        }
        if (parsed.Dimension != Dimension)
        {
            throw new InvalidOperationException(
                $"云端 embedding 维度 {parsed.Dimension} 与本地向量库维度 {Dimension} 不一致；"
                    + "请将云端 Cloud:VectorDimension 调整为一致，或改用本地 embedding。"
            );
        }
        if (parsed.Vectors.Count != texts.Count)
        {
            throw new CloudApiException(
                "embedding_failed",
                $"云端 embedding 返回 {parsed.Vectors.Count} 条向量，期望 {texts.Count} 条。"
            );
        }

        return parsed.Vectors.Select(vector => vector.ToArray()).ToArray();
    }

    /// <summary>POST /v1/embedding 响应（服务端 kebab-case，单见词；大小写不敏感读取）。</summary>
    private sealed record EmbeddingResponse(
        List<List<float>> Vectors,
        int Dimension,
        string Model,
        bool Available
    );
}
