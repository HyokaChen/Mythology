namespace Core.Vector;

/// <summary>
/// Agent 记忆评分公式：移植自 remembrane 的加权和评分。
///
/// score = w_sim · similarity + w_rec · recency + w_imp · importance + w_use · usefulness
///
/// recency    = exp(-ln2 · age / half_life)
///              age = max(0, now - last_accessed_at)  ← 从最后访问算起，不是创建时间
/// usefulness = 1 / (1 + exp(-accumulator))            ← sigmoid，0 → 0.5 中性
///
/// 硬规则：similarity 必须 &gt; 0，recency/importance 不能替代相关性。
/// </summary>
public static class AgentMemoryScoring
{
    private const double Ln2 = 0.6931471805599453;

    /// <summary>
    /// 计算时间衰退因子。
    /// age 从 last_accessed_at 算起（类比 spaced repetition），
    /// 若从未访问过则从 created_at 算起。
    /// </summary>
    /// <param name="createdAt">创建时间戳。</param>
    /// <param name="lastAccessedAt">最后访问时间戳（可为 null）。</param>
    /// <param name="now">当前时间戳。</param>
    /// <param name="halfLife">半衰期（秒）。</param>
    /// <returns>[0, 1] 范围的衰退因子。</returns>
    public static double RecencyFactor(
        double createdAt,
        double? lastAccessedAt,
        double now,
        double halfLife
    )
    {
        var anchor = lastAccessedAt ?? createdAt;
        var age = Math.Max(0.0, now - anchor);
        return Math.Exp(-Ln2 * age / halfLife);
    }

    /// <summary>
    /// 将有用性累加器通过 sigmoid 压缩到 [0, 1]。
    /// 0 累加器 → 0.5 中性值；正反馈 → 趋近 1；负反馈 → 趋近 0。
    /// </summary>
    public static double UsefulnessFactor(double usefulness) => 1.0 / (1.0 + Math.Exp(-usefulness));

    /// <summary>
    /// 计算综合评分。
    /// </summary>
    /// <param name="similarity">文本相似度（余弦或混合）。</param>
    /// <param name="memory">记忆记录。</param>
    /// <param name="config">评分配置。</param>
    /// <param name="now">当前时间戳（冻结时间用）。</param>
    /// <returns>(综合评分, 时间衰退因子)。</returns>
    public static (double Score, double Recency) CompositeScore(
        double similarity,
        MemoryRecord memory,
        AgentMemoryScoringConfig config,
        double now
    )
    {
        var (wSim, wRec, wImp, wUse) = config.NormalizedWeights;

        var recency = RecencyFactor(
            memory.CreatedAt,
            memory.LastAccessedAt,
            now,
            config.HalfLifeSeconds
        );

        var usefulness = UsefulnessFactor(memory.Usefulness);

        var score =
            wSim * similarity + wRec * recency + wImp * memory.Importance + wUse * usefulness;

        return (score, recency);
    }

    /// <summary>
    /// 计算混合模式 similarity：0.65 · cosine + 0.35 · bm25_normalized。
    /// </summary>
    public static double HybridSimilarity(
        double cosineSimilarity,
        double normalizedBm25,
        double keywordWeight
    ) => (1.0 - keywordWeight) * cosineSimilarity + keywordWeight * normalizedBm25;

    /// <summary>
    /// 计算两个 float 数组的余弦相似度。
    /// </summary>
    public static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
            return 0.0;

        double dot = 0,
            normA = 0,
            normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom < 1e-10 ? 0.0 : dot / denom;
    }
}
