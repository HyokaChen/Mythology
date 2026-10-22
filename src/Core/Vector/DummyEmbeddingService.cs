using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Core.Vector;

/// <summary>
/// 虚拟嵌入服务（DummyEmbeddingService）—— 占位实现，用于生成随机向量。
/// 生产环境应替换为真实的 Embedding API（如 OpenAI、本地模型等）。
/// 优化点：使用 SIMD（AVX-512/AVX2）加速向量归一化。
/// </summary>
public sealed class DummyEmbeddingService : IEmbeddingService, IBatchEmbeddingService
{
    // 随机数生成器，用于生成伪随机向量值
    private readonly Random _rng = new();

    /// <summary>向量维度（与 VectorDefaults 对齐）。</summary>
    public int Dimension => VectorDefaults.Dimension;

    /// <summary>
    /// 生成指定文本的向量嵌入。
    /// 注意：此处为占位实现，实际返回的是随机向量，仅用于测试或演示。
    /// </summary>
    /// <param name="text">输入文本（此处未实际使用）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>归一化后的随机向量。</returns>
    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var vector = new float[Dimension];
        for (var i = 0; i < Dimension; i++)
            vector[i] = (float)(_rng.NextDouble() * 2 - 1);

        NormalizeSIMD(vector);
        return Task.FromResult(vector);
    }

    /// <summary>
    /// 批量嵌入 — 占位实现逐条生成随机向量（仅用于测试/演示）。
    /// </summary>
    public async Task<float[][]> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct = default
    )
    {
        var vectors = new float[texts.Count][];
        for (var i = 0; i < texts.Count; i++)
            vectors[i] = await EmbedAsync(texts[i], ct);
        return vectors;
    }

    /// <summary>
    /// 使用 SIMD（AVX-512/AVX2/通用 SIMD）加速向量归一化。
    /// 将向量除以其模长，使向量长度为 1（单位向量）。
    /// </summary>
    /// <param name="v">待归一化的向量。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void NormalizeSIMD(float[] v)
    {
        // 计算向量模长的平方
        var magSq = v.Aggregate(0.0, (current, value) => current + value * value);
        if (magSq <= 0)
            return;
        var invMag = (float)(1.0 / Math.Sqrt(magSq));
        var i = 0;
        var len = v.Length;

        // AVX-512：一次处理 16 个 float（512 位 / 32 位 = 16）
        if (Avx512F.IsSupported && len >= 16)
        {
            var invV = Vector512.Create(invMag);
            var end = len - 15;
            for (; i < end; i += 16)
            {
                var va = Vector512.LoadUnsafe(ref v[i]);
                (va * invV).StoreUnsafe(ref v[i]);
            }
        }
        // AVX2：一次处理 8 个 float（256 位 / 32 位 = 8）
        else if (Avx.IsSupported && len >= 8)
        {
            var invV = Vector256.Create(invMag);
            var end = len - 7;
            for (; i < end; i += 8)
            {
                var va = Vector256.LoadUnsafe(ref v[i]);
                Avx.Multiply(va, invV).StoreUnsafe(ref v[i]);
            }
        }
        // 通用 SIMD（128 位平台无关）
        else if (
            System.Numerics.Vector.IsHardwareAccelerated
            && len >= System.Numerics.Vector<float>.Count
        )
        {
            var invV = new System.Numerics.Vector<float>(invMag);
            var end = len - System.Numerics.Vector<float>.Count + 1;
            for (; i < end; i += System.Numerics.Vector<float>.Count)
            {
                var va = new System.Numerics.Vector<float>(v, i);
                va *= invV;
                va.CopyTo(v, i);
            }
        }

        // 尾部标量处理：剩余不足一个 SIMD 宽度的元素
        for (; i < len; i++)
            v[i] *= invMag;
    }
}
