using System.Text.Json;
using Core.Vector;
using JiebaNet.Segmenter;
using Microsoft.Extensions.Logging;

namespace Core.Tools;

/// <summary>
/// 工具搜索引擎：为 84 个工具构建检索索引，支持词典分词 + BM25 + dense embedding + RRF 融合。
///
/// 设计决策：
/// - 工具文档量极小（~84 条），不需要 SqliteRetrievalStore 的重型管线（内存倒排索引）。
/// - 中文分词：共享 Vector.AgentMemoryBm25.Segmenter 的 jieba.NET 单例（避免两份词典加载）。
/// - BM25 用内存倒排索引实现（分词后统计 TF/IDF）。
/// - Dense 检索用 IEmbeddingService（云端统一提供的外部 OpenAI 兼容接口）；文档范数预计算。
/// - RRF 融合复用与 SqliteRetrievalStore 相同的算法（k/权重可配置）。
/// - 索引 swap-on-build：构建到局部快照后原子发布，读取方始终看到完整一致的索引。
/// </summary>
public sealed class ToolSearchEngine
{
    private readonly IEmbeddingService _embeddingService;
    private readonly ILogger? _logger;

    // ── 并发安全：swap-on-build 快照 ──
    // 构建全程在局部结构上进行，完成后一次性原子赋值 _snapshot。
    // 读取方（SearchAsync/GetDocument）总是拿到完整一致的快照，
    // 不会读到半建索引；失效 = 置空快照。字段为 volatile 引用保证可见性。
    private volatile IndexSnapshot? _snapshot;

    // dense 失败冷却：失败后 30s 内跳过 dense 通道（省网络往返 + 降日志噪音）
    private long _denseCooldownUntilTicks;

    // BM25 参数
    private const double Bm25K1 = 1.5;
    private const double Bm25B = 0.75;

    // RRF 参数（可配置，默认值与 SqliteRetrievalStore 一致）
    private readonly int _rrfK;
    private readonly double _denseWeight;
    private readonly double _lexicalWeight;

    /// <summary>创建工具搜索引擎。</summary>
    /// <param name="embeddingService">embedding 服务（本地或云端）。</param>
    /// <param name="rrfK">RRF 融合常数 k（默认 60）。</param>
    /// <param name="denseWeight">dense 通道权重（默认 0.6）。</param>
    /// <param name="lexicalWeight">词法通道权重（默认 0.4）。</param>
    public ToolSearchEngine(
        IEmbeddingService embeddingService,
        ILogger? logger = null,
        int rrfK = 60,
        double denseWeight = 0.6,
        double lexicalWeight = 0.4
    )
    {
        _embeddingService = embeddingService;
        _logger = logger;
        _rrfK = rrfK;
        _denseWeight = denseWeight;
        _lexicalWeight = lexicalWeight;
    }

    /// <summary>索引是否已就绪。</summary>
    public bool IsIndexed => _snapshot is not null;

    /// <summary>
    /// 从 ToolRegistry 构建索引。应在插件加载完成后调用一次。
    /// 幂等：重复调用会重建索引。
    /// 并发安全：构建到局部快照，完成后原子发布（swap-on-build）。
    /// </summary>
    public async Task BuildIndexAsync(ToolRegistry registry, CancellationToken ct = default)
    {
        var documents = new Dictionary<string, ToolDocument>(StringComparer.Ordinal);
        var embeddings = new Dictionary<string, (float[] Vec, double Norm)>(StringComparer.Ordinal);
        var docLengths = new Dictionary<string, int>(StringComparer.Ordinal);
        var invertedIndex = new Dictionary<string, List<(string ToolName, int Tf)>>(
            StringComparer.Ordinal
        );

        var tools = registry.GetAll();
        var docTexts = new List<(string ToolName, string DocText, string[] Tokens)>();

        foreach (var tool in tools)
        {
            var doc = BuildDocument(tool);
            documents[tool.Name] = doc;

            // 分词
            var tokens = Tokenize(doc.SearchableText);
            docLengths[tool.Name] = tokens.Length;

            docTexts.Add((tool.Name, doc.SearchableText, tokens));
        }

        var docCount = docTexts.Count;
        var avgDocLength = docCount > 0 ? docTexts.Sum(d => d.Tokens.Length) / (double)docCount : 1;

        // 构建倒排索引（普通 Dictionary，build 期间不暴露给读者）
        foreach (var (toolName, _, tokens) in docTexts)
        {
            var tfMap = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var token in tokens)
            {
                tfMap[token] = tfMap.TryGetValue(token, out var count) ? count + 1 : 1;
            }

            foreach (var (token, tf) in tfMap)
            {
                if (!invertedIndex.TryGetValue(token, out var postings))
                {
                    invertedIndex[token] = [(toolName, tf)];
                }
                else
                {
                    postings.Add((toolName, tf));
                }
            }
        }

        // 批量生成 embedding（预计算范数，查询时省一半乘加）
        if (_embeddingService is not DummyEmbeddingService)
        {
            try
            {
                var texts = docTexts.Select(d => d.DocText).ToList();
                var names = docTexts.Select(d => d.ToolName).ToList();

                if (_embeddingService is IBatchEmbeddingService batchService)
                {
                    var vectors = await batchService.EmbedBatchAsync(texts, ct);
                    for (var i = 0; i < names.Count; i++)
                    {
                        if (vectors[i].Any(v => v != 0f))
                            embeddings[names[i]] = (vectors[i], VectorNorm(vectors[i]));
                    }
                }
                else
                {
                    // 逐条生成
                    for (var i = 0; i < names.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var vec = await _embeddingService.EmbedAsync(texts[i], ct);
                        if (vec.Any(v => v != 0f))
                            embeddings[names[i]] = (vec, VectorNorm(vec));
                    }
                }

                _logger?.LogInformation(
                    "工具搜索索引 embedding 完成: {Count}/{Total} 条",
                    embeddings.Count,
                    names.Count
                );
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "工具搜索索引 embedding 失败，将仅使用 BM25 检索");
            }
        }

        // 原子发布快照
        _snapshot = new IndexSnapshot
        {
            Documents = documents,
            InvertedIndex = invertedIndex,
            DocLengths = docLengths,
            Embeddings = embeddings,
            AvgDocLength = avgDocLength,
            DocCount = docCount,
        };
        _logger?.LogInformation(
            "工具搜索索引构建完成: {Count} 个工具, {TokenCount} 个倒排词项",
            docCount,
            invertedIndex.Count
        );
    }

    /// <summary>使索引失效（插件卸载/工具注册变更后调用）。</summary>
    public void InvalidateIndex()
    {
        _snapshot = null;
    }

    /// <summary>
    /// 搜索工具：BM25 + dense + RRF 融合。
    /// </summary>
    /// <param name="query">自然语言查询（如"检查角色完整性"）。</param>
    /// <param name="topK">返回结果数（默认 5）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>命中的工具名列表，按相关性降序。</returns>
    public async Task<List<ToolSearchHit>> SearchAsync(
        string query,
        int topK = 5,
        CancellationToken ct = default
    )
    {
        var snapshot = _snapshot;
        if (snapshot is null || string.IsNullOrWhiteSpace(query))
            return [];

        // 1. BM25 词法检索
        var bm25Results = Bm25Search(snapshot, query, topK * 4);

        // 2. Dense 向量检索
        var denseResults = await DenseSearchAsync(snapshot, query, topK * 4, ct);

        // 3. RRF 融合
        var fused = RrfFusion(bm25Results, denseResults);

        return fused.Take(topK).ToList();
    }

    /// <summary>获取工具文档（用于 ToolSearchTool 返回摘要）。</summary>
    public ToolDocument? GetDocument(string toolName)
    {
        var snapshot = _snapshot;
        return snapshot is not null && snapshot.Documents.TryGetValue(toolName, out var doc)
            ? doc
            : null;
    }

    // ── 内部实现 ──

    /// <summary>
    /// 构建工具检索文档：拼接 name + label + description + aliases + parameters + examples。
    /// </summary>
    private static ToolDocument BuildDocument(AgentTool tool)
    {
        var parts = new List<string> { tool.Name, tool.Label, tool.Description };

        if (tool.Aliases.Length > 0)
            parts.AddRange(tool.Aliases);

        if (tool.ExampleQueries.Length > 0)
            parts.AddRange(tool.ExampleQueries);

        // 提取参数名（预计算到 ParamNames，避免 ToolSearchTool 每次命中重复解析）
        var paramNames = Array.Empty<string>();
        if (tool.Parameters.HasValue)
        {
            try
            {
                using var doc = JsonDocument.Parse(tool.Parameters!.Value.GetRawText());
                if (doc.RootElement.TryGetProperty("properties", out var props))
                {
                    var names = new List<string>();
                    foreach (var prop in props.EnumerateObject())
                    {
                        names.Add(prop.Name);
                        parts.Add(prop.Name);
                        if (prop.Value.TryGetProperty("description", out var desc))
                            parts.Add(desc.GetString() ?? "");
                    }
                    paramNames = names.ToArray();
                }
            }
            catch
            {
                // schema 解析失败不影响索引
            }
        }

        var searchableText = string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return new ToolDocument(
            tool.Name,
            tool.Label,
            tool.Description,
            tool.Aliases,
            searchableText,
            paramNames
        );
    }

    /// <summary>
    /// 中文分词：jieba 正向最大匹配（FMM，cutAll:false）。
    /// 共享 AgentMemoryBm25 的单例，避免两份词典加载。
    /// </summary>
    private static JiebaSegmenter Segmenter => AgentMemoryBm25.Segmenter.Value;

    private static string[] Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var tokens = new List<string>(text.Length / 2);
        foreach (var token in Segmenter.Cut(text, cutAll: false))
        {
            var trimmed = token.Trim();
            if (trimmed.Length == 0)
                continue;
            // 过滤纯标点/下划线 token（jieba 会把 check_character_completeness 拆出 "_"）
            if (trimmed.All(ch => char.IsPunctuation(ch) || ch == '_'))
                continue;
            // 过滤中文停用词（与 AgentMemoryBm25.Tokenize 保持一致，提升 IDF 区分度）
            if (trimmed.Length <= 2 && AgentMemoryConflict.Stopwords.Contains(trimmed))
                continue;
            tokens.Add(trimmed.ToLowerInvariant());
        }

        return tokens.ToArray();
    }

    /// <summary>
    /// BM25 搜索：对 query 分词后在倒排索引中检索。
    /// </summary>
    private static List<(string ToolName, double Score)> Bm25Search(
        IndexSnapshot snapshot,
        string query,
        int topK
    )
    {
        var queryTokens = Tokenize(query);
        if (queryTokens.Length == 0)
            return [];

        var scores = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var token in queryTokens.Distinct(StringComparer.Ordinal))
        {
            if (!snapshot.InvertedIndex.TryGetValue(token, out var postings))
                continue;

            // IDF = log((N - df + 0.5) / (df + 0.5) + 1)
            var df = postings.Count;
            var idf = Math.Log((snapshot.DocCount - df + 0.5) / (df + 0.5) + 1);

            foreach (var (toolName, tf) in postings)
            {
                var docLen = snapshot.DocLengths.GetValueOrDefault(toolName);
                // BM25 score = IDF * (tf * (k1 + 1)) / (tf + k1 * (1 - b + b * dl/avgdl))
                var numerator = tf * (Bm25K1 + 1);
                var denominator =
                    tf + Bm25K1 * (1 - Bm25B + Bm25B * docLen / snapshot.AvgDocLength);
                var score = idf * numerator / denominator;

                scores[toolName] = scores.TryGetValue(toolName, out var existing)
                    ? existing + score
                    : score;
            }
        }

        return scores
            .OrderByDescending(kv => kv.Value)
            .Take(topK)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    /// <summary>
    /// Dense 向量搜索：query embedding vs 工具文档 embedding 余弦相似度。
    /// 文档范数在 build 时预计算，查询只算 dot。
    /// dense 失败后进入 30s 冷却，冷却期内直接跳过（省网络往返 + 降噪）。
    /// </summary>
    private async Task<List<(string ToolName, double Score)>> DenseSearchAsync(
        IndexSnapshot snapshot,
        string query,
        int topK,
        CancellationToken ct
    )
    {
        if (snapshot.Embeddings.Count == 0)
            return [];

        // 冷却期内跳过 dense 通道
        if (Environment.TickCount64 < Volatile.Read(ref _denseCooldownUntilTicks))
            return [];

        try
        {
            var queryVec = await _embeddingService.EmbedAsync(query, ct);
            if (queryVec.All(v => v == 0f))
                return [];

            var queryNorm = VectorNorm(queryVec);
            if (queryNorm <= 0)
                return [];

            var results = new List<(string ToolName, double Score)>(snapshot.Embeddings.Count);
            foreach (var (toolName, (docVec, docNorm)) in snapshot.Embeddings)
            {
                var dot = DotProduct(queryVec, docVec);
                var denom = queryNorm * docNorm;
                var similarity = denom > 0 ? dot / denom : 0;
                results.Add((toolName, similarity));
            }

            return results.OrderByDescending(r => r.Score).Take(topK).ToList();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "工具搜索 dense 检索失败，将仅使用 BM25 检索（冷却 30s）");
            Volatile.Write(ref _denseCooldownUntilTicks, Environment.TickCount64 + 30_000);
            return [];
        }
    }

    /// <summary>向量点积。</summary>
    private static double DotProduct(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
            return 0;

        double dot = 0;
        for (var i = 0; i < a.Length; i++)
            dot += a[i] * b[i];
        return dot;
    }

    /// <summary>向量范数（L2）。</summary>
    private static double VectorNorm(float[] v)
    {
        var sumSq = v.Aggregate<float, double>(0, (current, t) => current + t * t);
        return Math.Sqrt(sumSq);
    }

    /// <summary>
    /// RRF 融合：BM25 + Dense 结果按排名融合。
    /// 与 SqliteRetrievalStore.RrfFusion 相同算法。
    /// </summary>
    private List<ToolSearchHit> RrfFusion(
        List<(string ToolName, double Score)> bm25Results,
        List<(string ToolName, double Score)> denseResults
    )
    {
        var scores = new Dictionary<string, (double Score, int Bm25Rank, int DenseRank)>(
            StringComparer.Ordinal
        );

        // BM25 通道
        for (var i = 0; i < bm25Results.Count; i++)
        {
            var (toolName, _) = bm25Results[i];
            var rank = i + 1;
            var rrfScore = _lexicalWeight / (_rrfK + rank);
            scores[toolName] = (rrfScore, rank, 0);
        }

        // Dense 通道
        for (var i = 0; i < denseResults.Count; i++)
        {
            var (toolName, _) = denseResults[i];
            var rank = i + 1;
            var rrfScore = _denseWeight / (_rrfK + rank);
            scores[toolName] = scores.TryGetValue(toolName, out var existing)
                ? (existing.Score + rrfScore, existing.Bm25Rank, rank)
                : (rrfScore, 0, rank);
        }

        return scores
            .OrderByDescending(kv => kv.Value.Score)
            .Select(kv => new ToolSearchHit(
                kv.Key,
                kv.Value.Score,
                kv.Value.Bm25Rank,
                kv.Value.DenseRank
            ))
            .ToList();
    }

    /// <summary>索引快照：构建完成后整体发布，读取方只读。</summary>
    private sealed class IndexSnapshot
    {
        public required IReadOnlyDictionary<string, ToolDocument> Documents { get; init; }
        public required IReadOnlyDictionary<
            string,
            List<(string ToolName, int Tf)>
        > InvertedIndex { get; init; }
        public required IReadOnlyDictionary<string, int> DocLengths { get; init; }
        public required IReadOnlyDictionary<
            string,
            (float[] Vec, double Norm)
        > Embeddings { get; init; }
        public required double AvgDocLength { get; init; }
        public required int DocCount { get; init; }
    }
}

/// <summary>工具检索文档。</summary>
public sealed record ToolDocument
{
    public string Name { get; init; }
    public string Label { get; init; }
    public string Description { get; init; }
    public string[] Aliases { get; init; }
    public string SearchableText { get; init; }
    public string[] ParamNames { get; init; }

    public ToolDocument(
        string name,
        string label,
        string description,
        string[] aliases,
        string searchableText,
        string[]? paramNames = null
    )
    {
        Name = name;
        Label = label;
        Description = description;
        Aliases = aliases;
        SearchableText = searchableText;
        ParamNames = paramNames ?? [];
    }
}

/// <summary>工具搜索命中结果。</summary>
public sealed record ToolSearchHit(string ToolName, double Score, int Bm25Rank, int DenseRank);
