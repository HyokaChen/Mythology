namespace Core.Vector;

/// <summary>
/// 嵌入生成服务接口（IEmbeddingService）。
/// 定义了将文本转换为高维向量（Embedding）的标准契约。
/// 所有具体的嵌入服务（如 OpenAI、本地模型等）均需实现此接口。
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// 生成文本的向量嵌入。
    /// 将自然语言文本映射到高维向量空间，用于语义相似度计算。
    /// </summary>
    /// <param name="text">输入文本。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>浮点数数组表示的向量嵌入。</returns>
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);

    /// <summary>
    /// 向量维度（如 1536、768 等）。
    /// 表示每个文本被转换成的向量长度。
    /// </summary>
    int Dimension { get; }

    /// <summary>
    /// 模型标识（写入检索文档，用于换模型后触发重建）。
    /// 默认实现保留旧测试替身兼容；真实服务应覆盖为具体模型名。
    /// </summary>
    string ModelId => "unknown";
}
