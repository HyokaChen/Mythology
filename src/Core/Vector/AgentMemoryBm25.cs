using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using JiebaNet.Segmenter;

namespace Core.Vector;

/// <summary>
/// 纯 C# BM25 Okapi 实现，移植自 remembrane 的 bm25.py。
/// 中文分词使用 jieba.NET，支持从小说实体自动构建自定义词典。
///
/// 增量词典策略：
/// - 对每部小说维护实体名称的哈希缓存
/// - 实体未变更时跳过词典重建（零成本）
/// - 实体变更时自动重建并重新加载
/// - 词典文件持久化到 ~/.mythology/dicts/{novelId}.dict
/// </summary>
public static class AgentMemoryBm25
{
    // jieba 分词器单例（线程安全；internal 供 ToolSearchEngine 共享，避免两份词典加载）
    internal static readonly Lazy<JiebaSegmenter> Segmenter = new(
        () => new JiebaSegmenter(),
        LazyThreadSafetyMode.ExecutionAndPublication
    );

    // 每部小说实体名称的哈希缓存：{novelId: hash}
    private static readonly ConcurrentDictionary<string, int> DictHashes = new();

    // 词典文件目录
    private static readonly string DictsDir = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "dicts"
    );

    /// <summary>
    /// 为指定小说构建自定义词典（增量：仅实体名称变更时重建）。
    /// 从实体名称列表中提取角色名、地名、组织名等专有名词，
    /// 写入 jieba 自定义词典文件并加载到分词器。
    /// </summary>
    /// <param name="novelId">小说 ID。</param>
    /// <param name="entityNames">所有实体名称列表（角色名、别名、地名、组织名、物品名等）。</param>
    /// <returns>true 表示词典已更新（新建或变更），false 表示跳过（无变更）。</returns>
    public static bool BuildUserDict(string novelId, HashSet<string> entityNames)
    {
        // 计算当前实体名称的哈希
        var currentHash = ComputeHash(entityNames);

        // 增量检查：哈希相同则跳过
        if (DictHashes.TryGetValue(novelId, out var cachedHash) && cachedHash == currentHash)
            return false;

        // 写入词典文件
        var dictPath = Path.Combine(DictsDir, $"{novelId}.dict");
        Directory.CreateDirectory(DictsDir);
        File.WriteAllLines(
            dictPath,
            entityNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => $"{n.Trim()} 10")
        );

        // 加载到 jieba
        if (File.Exists(dictPath))
            Segmenter.Value.LoadUserDict(dictPath);

        // 更新缓存
        DictHashes[novelId] = currentHash;

        return true;
    }

    /// <summary>
    /// 删除小说的词典缓存（实体变更时调用）。
    /// 下次 BuildUserDict 时会自动重建。
    /// </summary>
    public static void InvalidateDict(string novelId)
    {
        DictHashes.TryRemove(novelId, out _);
        var dictPath = Path.Combine(DictsDir, $"{novelId}.dict");
        if (File.Exists(dictPath))
            File.Delete(dictPath);
    }

    /// <summary>
    /// 分词：使用 jieba 处理中文/英文/数字混合文本。
    /// 过滤纯标点 token 与高频停用词（的/了/是/在/有 等），统一小写化。
    /// 停用词表与 AgentMemoryConflict 共享，避免两份漂移。
    /// </summary>
    public static List<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var tokens = new List<string>(text.Length / 2);
        foreach (var token in Segmenter.Value.Cut(text, cutAll: false))
        {
            var trimmed = token.Trim();
            if (trimmed.Length == 0)
                continue;
            // 过滤纯标点/下划线/空格 token
            if (trimmed.All(ch => char.IsPunctuation(ch) || ch == '_' || ch == ' '))
                continue;
            trimmed = trimmed.ToLowerInvariant();
            // 过滤高频停用词（提升 BM25 IDF 区分度；中文单字虚词占比远高于英文）
            if (AgentMemoryConflict.Stopwords.Contains(trimmed))
                continue;
            tokens.Add(trimmed);
        }
        return tokens;
    }

    /// <summary>
    /// 计算 term frequency 字典。
    /// </summary>
    public static Dictionary<string, int> TermFrequencies(List<string> tokens)
    {
        var tf = new Dictionary<string, int>(tokens.Count);
        foreach (var t in tokens)
        {
            tf.TryGetValue(t, out var count);
            tf[t] = count + 1;
        }
        return tf;
    }

    /// <summary>
    /// 计算 BM25 原始分数（未归一化）。
    /// </summary>
    public static double[] Bm25Scores(
        List<string> queryTerms,
        List<List<string>>? documents = null,
        double k1 = 1.5,
        double b = 0.75,
        List<Dictionary<string, int>>? tfs = null,
        List<int>? docLens = null
    )
    {
        var n = documents?.Count ?? tfs?.Count ?? 0;
        if (n == 0 || queryTerms.Count == 0)
            return [];

        // 计算或获取 TF 和文档长度
        List<Dictionary<string, int>> actualTfs;
        List<int> actualDocLens;

        if (tfs is not null && docLens is not null)
        {
            actualTfs = tfs;
            actualDocLens = docLens;
        }
        else if (documents is not null)
        {
            actualTfs = documents.ConvertAll(TermFrequencies);
            actualDocLens = documents.ConvertAll(d => d.Count);
        }
        else
        {
            return [];
        }

        if (actualDocLens.Count == 0)
            return [];

        // 平均文档长度
        var avgdl = actualDocLens.Average();

        // 去重查询词，计算每个词的文档频率
        var uniqueTerms = new HashSet<string>(queryTerms);
        var df = new Dictionary<string, int>(uniqueTerms.Count);
        foreach (var term in uniqueTerms)
        {
            var count = actualTfs.Count(tf => tf.ContainsKey(term));
            df[term] = count;
        }

        // 计算 IDF
        var idf = new Dictionary<string, double>(uniqueTerms.Count);
        foreach (var term in uniqueTerms)
        {
            var docFreq = df[term];
            idf[term] = Math.Log(1.0 + (n - docFreq + 0.5) / (docFreq + 0.5));
        }

        // 计算每个文档的 BM25 分数
        var scores = new double[n];
        var k1p1 = k1 + 1.0;

        for (var i = 0; i < n; i++)
        {
            var tf = actualTfs[i];
            var dl = actualDocLens[i];
            var denomNorm = k1 * (1.0 - b + b * dl / avgdl);

            double score = 0;
            foreach (var term in queryTerms)
            {
                if (tf.TryGetValue(term, out var freq))
                {
                    score += idf[term] * freq * k1p1 / (freq + denomNorm);
                }
            }
            scores[i] = score;
        }

        return scores;
    }

    /// <summary>
    /// 对 BM25 原始分数做归一化处理（除以最大值），使结果落在 [0, 1]。
    /// </summary>
    public static double[] NormalizeScores(double[] raw)
    {
        if (raw.Length == 0)
            return raw;

        var peak = raw.Max();
        if (peak <= 0)
            return raw;

        var normalized = new double[raw.Length];
        for (var i = 0; i < raw.Length; i++)
            normalized[i] = raw[i] / peak;

        return normalized;
    }

    /// <summary>
    /// 便捷方法：对查询和文档列表直接计算归一化 BM25 分数。
    /// </summary>
    public static double[] ComputeNormalizedBm25(
        string query,
        List<string> documents,
        double k1 = 1.5,
        double b = 0.75
    )
    {
        var queryTerms = Tokenize(query);
        var docTokens = documents.ConvertAll(Tokenize);
        var raw = Bm25Scores(queryTerms, docTokens, k1, b);
        return NormalizeScores(raw);
    }

    /// <summary>
    /// 计算一组字符串的确定性哈希。
    /// 用于增量判断：实体名称未变时跳过词典重建。
    /// </summary>
    private static int ComputeHash(HashSet<string> names)
    {
        // 排序后合并为单字符串，取稳定哈希
        var sorted = names.OrderBy(n => n, StringComparer.Ordinal);
        var joined = string.Join("\n", sorted);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return BitConverter.ToInt32(bytes, 0);
    }
}
