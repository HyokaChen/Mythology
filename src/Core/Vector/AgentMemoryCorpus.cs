using System.Numerics.Tensors;

namespace Core.Vector;

/// <summary>
/// 记忆向量语料库：一次性 SELECT 构建，内存归一化，SIMD 点积热路径。
/// 设计对标 remembrane store.py 精确暴力评分，零数据库迁移。
/// 构造只在 RecallAsync 首次取到行后执行（缓存未命中），失效与 _callCacheVersion / _dataVersion 同步。
/// </summary>
public sealed class EmbeddingCorpus
{
    public string Key { get; } // "{owner}|{ns}|{novelId}"
    public List<string> Ids { get; } // 平行数组，索引与 matrix 行对应
    public Dictionary<string, int> Idx { get; } // memory_id -> row index
    public float[] Matrix { get; } // flat [rows * dim], 行已归一化
    public float[] Norms { get; } // 归一化后 ~1.0（用于调试/验证）
    public int Rows { get; }
    public int Dim { get; }

    // tokenCacheRef 不重复存储，指向 AgentMemoryStore 外部 _tokenCache（外部管理）
    // 本结构仅持有向量部分，分词由 Store 维持独立缓存。

    public EmbeddingCorpus(string key, int dim, List<string> ids, float[] matrix, float[] norms)
    {
        Key = key;
        Dim = dim;
        Ids = ids;
        Rows = ids.Count;
        Matrix = matrix;
        Norms = norms;
        Idx = new Dictionary<string, int>(ids.Count);
        for (var i = 0; i < ids.Count; i++)
            Idx[ids[i]] = i;
    }

    /// <summary>计算单行与查询向量的点积（行已归一化，cosine == dot）。ANN 候选子集路径用。</summary>
    public float RowDot(int rowIndex, ReadOnlySpan<float> qvec) =>
        TensorPrimitives.Dot(Matrix.AsSpan(rowIndex * Dim, Dim), qvec);

    /// <summary>取指定 id 对应行（构建向量结果用）。</summary>
    public bool TryGetRow(string id, out int index) => Idx.TryGetValue(id, out index);
}
