using System.Security.Cryptography;
using System.Text;

namespace Core.Vector;

/// <summary>
/// 本地确定性嵌入服务（移植 remembrane 的 HashEmbedder，中文适配版）。
///
/// 特征：jieba 词（权重 2.0）+ 字符 n-gram (3,4)（权重 1.0），
/// 每个特征经 MD5 哈希映射到固定维度向量（带符号累加），最后归一化。
///
/// 性质：
/// - 确定性：同一文本 → 同一向量（可单元测试）
/// - 本地零依赖：无网络、无模型、无 API Key，完全离线
/// - 词法相似（非语义）：n-gram 容忍轻微字序/拼写差异，与 BM25 互补
///
/// 语义级召回不属于本类职责——那是 云端私有引擎（云端付费）
/// 或本地神经模型的范畴（见 本地向量检索保持免费）。
/// </summary>
public sealed class HashEmbedder : IEmbeddingService
{
    private readonly (int N, double Weight)[] _charNgrams;
    private readonly double _wordWeight;

    /// <summary>创建哈希嵌入器。</summary>
    /// <param name="dimension">向量维度（必须为正）。</param>
    public HashEmbedder(int dimension = 512)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimension);
        Dimension = dimension;
        _charNgrams = [(3, 1.0), (4, 1.0)];
        _wordWeight = 2.0;
    }

    /// <summary>向量维度。</summary>
    public int Dimension { get; }

    /// <summary>模型标识。</summary>
    public string ModelId => $"hash-{Dimension}";

    /// <summary>
    /// 生成文本的确定性向量。
    /// 空文本返回零向量。
    /// </summary>
    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var vec = new float[Dimension];
        if (string.IsNullOrWhiteSpace(text))
            return Task.FromResult(vec);

        // 词特征（jieba 中文词 + 英文词，与 AgentMemoryBm25 同一分词器/词典）
        foreach (var token in AgentMemoryBm25.Tokenize(text))
            AddFeature(vec, $"w:{token}", _wordWeight);

        // 字符 n-gram 特征（去标点/空白后的连续字符，捕获字序局部结构）
        var cleaned = CleanChars(text);
        foreach (var (n, weight) in _charNgrams)
        {
            for (var i = 0; i + n <= cleaned.Length; i++)
                AddFeature(vec, $"c{n}:{cleaned.Substring(i, n)}", weight);
        }

        Normalize(vec);
        return Task.FromResult(vec);
    }

    private void AddFeature(float[] vec, string feature, double weight)
    {
        var digest = MD5.HashData(Encoding.UTF8.GetBytes(feature));
        // Python 语义的无符号取模：避免 C# 负数取模为负
        var idx = (int)((uint)BitConverter.ToInt32(digest, 0) % (uint)vec.Length);
        var sign = digest[4] % 2 == 0 ? 1.0 : -1.0;
        vec[idx] += (float)(sign * weight);
    }

    private static string CleanChars(string text)
    {
        // 保留汉字/字母/数字/下划线，去掉标点与空白
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Where(c => !char.IsPunctuation(c) && !char.IsWhiteSpace(c)))
        {
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static void Normalize(float[] vec)
    {
        var sumSq = vec.Sum(v => (double)v * v);
        if (sumSq <= 0)
            return;
        var norm = Math.Sqrt(sumSq);
        for (var i = 0; i < vec.Length; i++)
            vec[i] = (float)(vec[i] / norm);
    }
}
