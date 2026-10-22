namespace StabilityHarness.RGDecimator;

/// <summary>
/// 向量相似度计算接口。
/// 将 RGDecimator 与 VSA 的具体实现解耦。
/// </summary>
public interface ISimilarityFunction
{
    double Compute(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b);
}

/// <summary>
/// 默认相似度实现，委托给 VSA 核心计算。
/// </summary>
internal sealed class VsaSimilarityFunction : ISimilarityFunction
{
    public double Compute(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b) =>
        VSA.VsaCore.CosineSimilarity(a, b);
}
