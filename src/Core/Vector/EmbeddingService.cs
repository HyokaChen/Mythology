using LlmTornado;
using LlmTornado.Embedding;
using LlmTornado.Embedding.Models;

namespace Core.Vector;

/// <summary>
/// 基于 LlmTornado 的 Embedding 服务 —— 复用用户已配置的聊天 Provider（TornadoApi）来生成向量。
///
/// 通过 LlmTornado 统一接入 embedding，支持 OpenAI 兼容端点（含 Ollama/vLLM/云端）及 Gemini/Cohere/Voyage。
/// 维度由首次成功调用探测并缓存。
/// </summary>
public sealed class EmbeddingService : IEmbeddingService, IBatchEmbeddingService
{
    private readonly TornadoApi _api;
    private readonly EmbeddingModel _model;

    /// <summary>向量维度（由首次成功调用缓存；0 = 未知）。</summary>
    public int Dimension { get; private set; }

    /// <summary>使用的模型名称。</summary>
    public string ModelName { get; }

    /// <summary>
    /// 创建 LlmTornado embedding 服务。
    /// </summary>
    /// <param name="api">TornadoApi 实例（复用聊天配置）。</param>
    /// <param name="model">模型名称，如 "text-embedding-3-small"。默认 "text-embedding-3-small"。</param>
    public EmbeddingService(TornadoApi api, string model = "text-embedding-3-small")
    {
        ArgumentNullException.ThrowIfNull(api);
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("模型名称不能为空", nameof(model));
        _api = api;
        ModelName = model;
        _model = model; // EmbeddingModel 支持隐式从 string 转换
    }

    /// <summary>单条 embedding。</summary>
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var result = await Task.Run(() => _api.Embeddings.CreateEmbedding(_model, text), ct);
        var vec = result?.Data?.FirstOrDefault()?.Embedding;
        if (vec is null || vec.Length == 0)
            throw new InvalidOperationException("LlmTornado 返回了空的 embedding 结果");
        if (Dimension == 0)
            Dimension = vec.Length;
        return vec;
    }

    /// <summary>批量 embedding（一次 API 往返）。</summary>
    public async Task<float[][]> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct = default
    )
    {
        ct.ThrowIfCancellationRequested();
        var result = await Task.Run(() => _api.Embeddings.CreateEmbedding(_model, texts), ct);
        var data = result?.Data;
        if (data is null || data.Count == 0)
            throw new InvalidOperationException("LlmTornado 返回了空的 embedding 结果列表");

        var vectors = new float[data.Count][];
        for (var i = 0; i < data.Count; i++)
        {
            var vec = data[i]?.Embedding;
            if (vec is null || vec.Length == 0)
                throw new InvalidOperationException($"LlmTornado 结果中第 {i} 条 embedding 为空");
            vectors[i] = vec;
        }
        if (Dimension == 0 && vectors.Length > 0)
            Dimension = vectors[0].Length;
        return vectors;
    }
}
