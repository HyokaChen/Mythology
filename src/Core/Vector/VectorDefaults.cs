namespace Core.Vector;

/// <summary>
/// 向量默认值常量类。
/// 集中管理向量相关的默认配置，避免硬编码分散在各地。
/// </summary>
public static class VectorDefaults
{
    /// <summary>默认向量维度（与主流 Embedding 模型对齐，如 OpenAI text-embedding-3）。</summary>
    public const int Dimension = 1024;

    /// <summary>默认返回的搜索结果数量。</summary>
    public const int DefaultTopK = 10;
}
