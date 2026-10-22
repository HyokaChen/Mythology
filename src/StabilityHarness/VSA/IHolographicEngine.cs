namespace StabilityHarness.VSA;

/// <summary>
/// VSA 全息引擎接口。继承 Core.Stability.IHolographicEngine，
/// 额外暴露 VSA 底层操作（Bind/Bundle/随机向量/恢复等）。
/// 父接口已定义 Dimensionality/InvariantCount/EncodeText/RegisterInvariant/
/// CheckDrift/CheckAllDrift/ListInvariants，此处仅保留新增成员，避免 CS0108。
/// </summary>
public interface IHolographicEngine : Core.Stability.IHolographicEngine
{
    sbyte[] Bind(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b);
    sbyte[] Bundle(IReadOnlyList<sbyte[]> vectors);
    double CosineSimilarity(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b);
    double[] BatchCosineSimilarity(ReadOnlySpan<sbyte> target, IReadOnlyList<sbyte[]> candidates);

    sbyte[] GenerateRandomVector();
    sbyte[][] BatchEncodeTexts(string[] texts);

    bool TryRecover(string name, Span<sbyte> output);
    sbyte[] Recover(string name);
    bool RemoveInvariant(string name);
}
