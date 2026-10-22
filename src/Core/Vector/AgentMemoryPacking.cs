namespace Core.Vector;

/// <summary>
/// Token 预算打包工具：0/1 背包 + 近重复抑制。
/// 移植自 remembrane 的 knapsack_pack + deduplicate 逻辑。
///
/// 在给定 token 预算内选择总分数最高的记忆子集。
/// 预算约束是硬保证（绝不超 budget）。
/// 使用粗粒度 DP + 贪心回填，保证不超预算且接近最优。
/// 近重复抑制：余弦相似度 ≥ threshold 的重复对，只保留 score 较高的。
/// </summary>
public static class AgentMemoryPacking
{
    /// <summary>
    /// 0/1 背包：在预算内选择 score 总和最大的索引子集。
    /// 使用粗粒度 DP + 贪心回填，保证不超预算。
    /// </summary>
    /// <param name="scores">每个候选的分数。</param>
    /// <param name="tokens">每个候选的 token 数。</param>
    /// <param name="budget">预算上限（硬约束）。</param>
    /// <returns>(选中的索引列表, 总 token 数)。</returns>
    public static (List<int> Selected, int TotalTokens) KnapsackPack(
        double[] scores,
        int[] tokens,
        int budget
    )
    {
        var n = scores.Length;
        if (n == 0 || budget <= 0)
            return ([], 0);

        // 粗粒度 buckets：将 budget 分为 1024 份，向上取整保证不超预算
        var gran = Math.Max(1, budget / 1024);
        var b = budget / gran;
        var w = new int[n];
        for (var i = 0; i < n; i++)
            w[i] = (tokens[i] + gran - 1) / gran; // ceil 除法

        // DP: dp[cap] = 最大分数
        var dp = new double[b + 1];
        // choice[i][cap] = 是否选择了第 i 个物品达到此容量
        var choice = new bool[n][];
        for (var i = 0; i < n; i++)
            choice[i] = new bool[b + 1];

        for (var i = 0; i < n; i++)
        {
            var wi = w[i];
            var si = scores[i];
            if (si <= 0 || wi > b)
                continue;

            for (var cap = b; cap >= wi; cap--)
            {
                var cand = dp[cap - wi] + si;
                if (cand > dp[cap])
                {
                    dp[cap] = cand;
                    choice[i][cap] = true;
                }
            }
        }

        // 回溯：找到最大 dp 值的容量
        var bestCap = 0;
        for (var cap = 0; cap <= b; cap++)
        {
            if (dp[cap] > dp[bestCap])
                bestCap = cap;
        }

        // 重建选择
        var selected = new List<int>();
        var remaining = bestCap;
        for (var i = n - 1; i >= 0; i--)
        {
            if (remaining >= w[i] && choice[i][remaining])
            {
                selected.Add(i);
                remaining -= w[i];
            }
        }
        selected.Reverse();

        // 硬保证：确保不超预算
        var total = selected.Sum(i => tokens[i]);
        while (selected.Count > 0 && total > budget)
        {
            // 移除贡献最低的
            var worst = selected.OrderBy(i => scores[i]).First();
            selected.Remove(worst);
            total -= tokens[worst];
        }

        // 贪心回填：粗粒度留下了空间，用剩余预算填充最高分的未选项目
        var chosenSet = new HashSet<int>(selected);
        var candidates = Enumerable
            .Range(0, n)
            .Where(i => !chosenSet.Contains(i) && scores[i] > 0)
            .OrderByDescending(i => scores[i]);

        foreach (var i in candidates)
        {
            if (total + tokens[i] <= budget)
            {
                chosenSet.Add(i);
                selected.Add(i);
                total += tokens[i];
            }
        }

        return (selected, total);
    }

    /// <summary>
    /// 近重复抑制：去除余弦相似度 ≥ threshold 的重复对。
    /// 保留每对中 score 较高的那个。
    /// </summary>
    /// <param name="results">召回结果列表。</param>
    /// <param name="vectors">每个结果对应的向量。</param>
    /// <param name="threshold">相似度阈值（默认 0.92）。</param>
    /// <returns>保留下来的索引列表（按原始顺序）。</returns>
    public static List<int> Deduplicate(
        List<RecallResult> results,
        List<float[]> vectors,
        double threshold = 0.92
    )
    {
        var kept = new List<int>();
        if (results.Count == 0 || vectors.Count == 0)
            return kept;

        for (var i = 0; i < results.Count; i++)
        {
            var keep = true;
            var vecI = vectors.Count > i ? vectors[i] : null;

            foreach (var j in kept)
            {
                var vecJ = vectors.Count > j ? vectors[j] : null;
                if (vecI is null || vecJ is null || vecI.Length != vecJ.Length)
                    continue;

                var sim = AgentMemoryScoring.CosineSimilarity(vecI, vecJ);
                if (sim >= threshold)
                {
                    // 保留 score 较高的
                    if (results[i].Score <= results[j].Score)
                    {
                        keep = false;
                        break;
                    }
                    // 当前结果 score 更高，替换旧结果
                    kept.Remove(j);
                    break;
                }
            }

            if (keep)
                kept.Add(i);
        }

        return kept;
    }

    /// <summary>
    /// 估算文本的 token 数（CJK 感知：中文 1 字 ≈ 1.5 token，ASCII ≈ 4 字符/token）。
    /// 原 len/4 英文启发式对中文高估 2-4 倍，导致 memory_pack 预算利用率不足。
    /// </summary>
    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var cjk = 0;
        var ascii = 0;
        foreach (var c in text)
        {
            // CJK 统一表意文字 + 扩展 A + 兼容表意（BMP 内；扩展 B+ 为 surrogate pair，忽略）
            if (
                c
                is >= '\u4E00'
                    and <= '\u9FFF'
                    or >= '\u3400'
                    and <= '\u4DBF'
                    or >= '\uF900'
                    and <= '\uFAFF'
            )
            {
                cjk++;
            }
            else if (char.IsAsciiLetterOrDigit(c))
            {
                ascii++;
            }
        }

        return cjk * 3 / 2 + ascii / 4 + 1;
    }
}
