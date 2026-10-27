using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StabilityHarness.Retrieval;

/// <summary>OpenAI 兼容 embedding 端点配置（`POST {BaseUrl}/v1/embeddings`）。</summary>
public sealed record EmbeddingOptions(
    string BaseUrl,
    string ApiKey,
    string Model = "wemm-embedding",
    int TimeoutMs = 15_000,
    int BatchSize = 32
);

/// <summary>
/// 极简 OpenAI 兼容 embedding 客户端：`/v1/embeddings`，`encoding_format=float`。
/// 仅用于评测（不做重试/降级策略——失败直接抛，避免把评测结果算成空向量）。
/// </summary>
public sealed class OpenAiEmbeddingClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly EmbeddingOptions _options;
    private readonly Dictionary<string, float[]> _cache = new(StringComparer.Ordinal);

    public OpenAiEmbeddingClient(EmbeddingOptions options)
    {
        _options = options;
        _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(options.TimeoutMs) };
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                options.ApiKey
            );
    }

    public int Dimension { get; private set; }

    /// <summary>批量取向量（带进程内缓存；同文本只请求一次）。</summary>
    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct = default
    )
    {
        var missing = texts.Where(t => !_cache.ContainsKey(t)).Distinct(StringComparer.Ordinal).ToList();
        for (var i = 0; i < missing.Count; i += _options.BatchSize)
        {
            var batch = missing.Skip(i).Take(_options.BatchSize).ToList();
            var url = _options.BaseUrl.TrimEnd('/') + "/v1/embeddings";
            using var response = await _http
                .PostAsJsonAsync(
                    url,
                    new RequestBody(_options.Model, batch, "float"),
                    CancellationTokenSource
                        .CreateLinkedTokenSource(ct)
                        .Token
                )
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"embedding 失败：HTTP {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}"
                );
            var body =
                await response.Content.ReadFromJsonAsync<ResponseBody>(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("embedding 返回空响应");
            if (body.Data.Count != batch.Count)
                throw new InvalidOperationException(
                    $"embedding 数量不匹配：期望 {batch.Count} 实际 {body.Data.Count}"
                );
            for (var k = 0; k < batch.Count; k++)
            {
                _cache[batch[k]] = body.Data[k].Embedding;
                if (Dimension == 0)
                    Dimension = body.Data[k].Embedding.Length;
            }
        }

        return texts.Select(t => _cache[t]).ToList();
    }

    public void Dispose() => _http.Dispose();

    private sealed record RequestBody(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input,
        [property: JsonPropertyName("encoding_format")] string EncodingFormat
    );

    private sealed record ResponseBody(
        [property: JsonPropertyName("data")] List<ResponseItem> Data
    );

    private sealed record ResponseItem(
        [property: JsonPropertyName("embedding")] float[] Embedding
    );
}
