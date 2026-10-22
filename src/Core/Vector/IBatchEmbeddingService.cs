namespace Core.Vector;

/// <summary>
/// 批量嵌入服务（IBatchEmbeddingService）—— 可选接口。
/// 一次 HTTP 往返为多段文本生成向量，替代逐条 EmbedAsync 的 N 次往返。
/// 由 <see cref="RetrievalKernel"/> 在服务实现此接口时自动启用。
/// </summary>
public interface IBatchEmbeddingService
{
    /// <summary>
    /// 批量生成文本向量嵌入。
    /// </summary>
    /// <param name="texts">输入文本列表（顺序与返回数组一一对应）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>与 <paramref name="texts"/> 顺序对应的向量数组。</returns>
    Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}
