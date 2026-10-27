using System.Diagnostics;
using Core.Vector;
using Microsoft.Data.Sqlite;

namespace StabilityHarness.Retrieval;

/// <summary>评测口径。</summary>
public enum GoldMode
{
    /// <summary>严格：gold 必须是该实体自己的档案文档（角色档案/地点档案/情节线档案/场景定义）。</summary>
    Strict,

    /// <summary>宽松：gold 是任何提到该实体名的文档（含关系陈述与正文块）。</summary>
    Loose,
}

/// <summary>真实 JEV（SystemOne `noul` 判定）重排选项；为 null 时该臂不跑。</summary>
public sealed record JevOptions(
    string BaseUrl,
    string ApiKey,
    string Model = "d1-omni",
    string Path = "/v1/systemone",
    int TimeoutMs = 15_000,
    int MaxStateChars = 1200,
    int MaxCandidates = 12,
    int Overfetch = 1,
    double Weight = 0.5,
    string Criteria = "relevant"
);

/// <summary>查询类别。</summary>
public enum QueryKind
{
    /// <summary>实体档案直查（「{名}的性格和设定是什么？」）。</summary>
    Fact,

    /// <summary>部分名（只给名字的前 2 字）。</summary>
    Partial,

    /// <summary>关系陈述（「和{名}相关的关系是什么？」）。</summary>
    Relation,
}

/// <summary>一条查询及其 gold。</summary>
public sealed record EvalQuery(string Id, QueryKind Kind, string Text, IReadOnlyList<int> Gold);

/// <summary>
/// 单条查询在某个方案下的结果。
/// <paramref name="FoundAt5"/> / <paramref name="FoundAt10"/> 是**前 k 条内**命中的 gold 数，
/// AE@k（找齐全部证据）必须按它们判断——早期实现误用「全程命中数」，把 AE 抬虚了。
/// </summary>
public sealed record QueryResult(int FirstGoldRank, int FoundAt5, int FoundAt10, int GoldTotal)
{
    public bool HitAt(int k) => FirstGoldRank is > 0 && FirstGoldRank <= k;

    public bool AllAt5 => GoldTotal > 0 && FoundAt5 == GoldTotal;

    public double ReciprocalRank => FirstGoldRank > 0 ? 1.0 / FirstGoldRank : 0;
}

/// <summary>一个方案的汇总指标。</summary>
public sealed record ArmMetrics(
    string Arm,
    double Recall5,
    double Recall10,
    double AllAt5,
    double Mrr,
    int Queries
);

/// <summary>
/// 词法检索评测（SAG2 口径）：BM25 候选 → 可选词项扩展二轮 → 交错合并，指标用 R@k 与 AE@k（是否找齐全部证据）。
///
/// gold 从库本身推导，不依赖外部标注：
/// <list type="bullet">
/// <item>实体档案文档的正文以「角色: 名字」「地点: 名字」「情节线: 名字」「场景: 名字」「小说: 名字」开头，
/// 由此拿到实体名与其档案文档；</item>
/// <item>Fact：查询「{名}的性格和设定是什么？」，Strict 的 gold = 该档案文档；</item>
/// <item>Partial：查询名字前 2 字，Loose 的 gold = 提到该名的文档；</item>
/// <item>Relation：查询「和{名}相关的关系是什么？」，gold = 提到该名的关系陈述。</item>
/// </list>
/// 只读打开数据库（mode=ro），不会修改仓库或用户库。
/// </summary>
public static class RetrievalEval
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    /// <summary>档案类文档类型 → 展示名。</summary>
    private static readonly (string Prefix, int Kind)[] ProfilePrefixes =
    [
        ("角色: ", (int)DocumentKind.CharacterProfile),
        ("地点: ", (int)DocumentKind.LocationProfile),
        ("情节线: ", (int)DocumentKind.PlotlineProfile),
        ("场景: ", (int)DocumentKind.SceneDefinition),
        ("组织: ", (int)DocumentKind.FactionProfile),
        ("道具: ", (int)DocumentKind.ItemProfile),
    ];

    /// <summary>执行一次评测，返回各方案指标与明细行（供命令行打印）。</summary>
    public static async Task<(List<ArmMetrics> Metrics, List<string> Details)> RunAsync(
        string dbPath,
        string novelId,
        GoldMode mode = GoldMode.Strict,
        int candidateK = 100,
        JevOptions? jev = null,
        EmbeddingOptions? embedding = null,
        CancellationToken ct = default
    )
    {
        var docs = await LoadDocsAsync(dbPath, novelId, ct);
        if (docs.Count == 0)
            throw new InvalidOperationException($"数据库 {dbPath} 里没有 novel '{novelId}' 的块");

        var entities = ExtractEntities(docs);
        if (entities.Count == 0)
            throw new InvalidOperationException(
                "库里找不到档案类文档（角色/地点/情节线/场景），无法推导 gold"
            );

        // 注册实体名：模拟生产链路（启动对账会把这些名字注册进全局词典）
        NovelLexicon.Clear();
        NovelLexicon.Update(novelId, entities.Keys);

        var queries = BuildQueries(docs, entities, mode);
        var baseline = docs.Select(d => PlainTokens(d.Text)).ToList();
        var current = docs.Select(d =>
                LexicalText
                    .ToIndexText(d.Text, novelId)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .ToList()
            )
            .ToList();
        var expansion = new TwoRoundExpansion(current);
        var mmr = new MmrSelector(docs);

        var armRankings = new List<List<List<int>>>
        {
            queries.Select(q => Bm25(baseline, QueryTerms(q.Text, novelId))).ToList(),
            queries.Select(q => Bm25(current, QueryTerms(q.Text, novelId))).ToList(),
            queries
                .Select(q =>
                    Interleave(
                        Bm25(current, QueryTerms(q.Text, novelId)),
                        expansion.ExpandAndSearch(q.Text, novelId)
                    )
                )
                .ToList(),
            queries
                .Select(q => mmr.Select(Bm25(current, QueryTerms(q.Text, novelId)), 10))
                .ToList(),
            queries
                .Select(q =>
                {
                    var ranking = Bm25(current, QueryTerms(q.Text, novelId));
                    var gold = q.Gold.ToHashSet();
                    var window = ranking.Take(20).ToList();
                    return window
                        .Where(gold.Contains)
                        .Concat(window.Where(i => !gold.Contains(i)))
                        .Concat(ranking.Skip(window.Count))
                        .ToList();
                })
                .ToList(),
        };
        string[] armNames =
        [
            "A 基线（默认词典·最佳路径）",
            "B 现行（实体注册 + 2/3 字片段）",
            "C 现行 + 词项扩展二轮（SAG2 式）",
            "D 现行 + 文档级 MMR（多样性，DPP 近似）",
            "E 现行 + 完美判定重排（JEV 上限，oracle）",
        ];

        using var jevClient = jev is null
            ? null
            : new Core.Inference.SystemOneClient(
                new Core.Data.SystemOneConfig
                {
                    Enabled = true,
                    BaseUrl = jev.BaseUrl,
                    Path = jev.Path,
                    ApiKey = jev.ApiKey,
                    Model = jev.Model,
                    TimeoutMs = jev.TimeoutMs,
                    MaxStateChars = jev.MaxStateChars,
                    Rerank = new Core.Data.SystemOneRerankConfig
                    {
                        Enabled = true,
                        Overfetch = jev.Overfetch,
                        Weight = jev.Weight,
                        PassageChars = jev.MaxStateChars / 4,
                        MaxCandidates = jev.MaxCandidates,
                        Criteria = jev.Criteria,
                    },
                }
            );

        if (jev is not null && jevClient is not null)
        {
            var realRerank = new List<List<int>>(queries.Count);
            foreach (var q in queries)
            {
                var ranking = Bm25(current, QueryTerms(q.Text, novelId));
                realRerank.Add(await RerankWithJevAsync(ranking, q.Text, docs, jevClient, ct));
            }
            armRankings.Add(realRerank);
            armNames = [.. armNames, "F 现行 + 真实 JEV 判定重排"];
        }

        if (embedding is not null)
        {
            using var embClient = new OpenAiEmbeddingClient(embedding);
            var docVectors = await embClient.EmbedAsync(docs.Select(d => d.Text).ToList(), ct);
            var queryVectors = await embClient.EmbedAsync(queries.Select(q => q.Text).ToList(), ct);
            var dense = queries
                .Select((_, i) => DenseRanking(docVectors, queryVectors[i]))
                .ToList();
            var hybrid = queries
                .Select(
                    (_, i) =>
                        Rrf(
                            armRankings[1][i], // B 现行词法
                            dense[i],
                            60
                        )
                )
                .ToList();
            armRankings.Add(dense);
            armNames = [.. armNames, $"G 纯 dense（{embClient.Dimension} 维）"];
            armRankings.Add(hybrid);
            armNames = [.. armNames, "H 混合 RRF（B 词法 + G dense）"];

            if (jev is not null && jevClient is not null)
            {
                var hybridJev = new List<List<int>>(queries.Count);
                foreach (var (q, i) in queries.Select((q, i) => (q, i)))
                    hybridJev.Add(await RerankWithJevAsync(hybrid[i], q.Text, docs, jevClient, ct));
                armRankings.Add(hybridJev);
                armNames = [.. armNames, "I 混合 + 真实 JEV（低候选）"];
            }
        }

        var metrics = new List<ArmMetrics>();
        for (var arm = 0; arm < armRankings.Count; arm++)
            metrics.Add(
                Summarize(
                    armNames[arm],
                    queries.Select((q, i) => Evaluate(armRankings[arm][i], q, candidateK)).ToList()
                )
            );

        var details = new List<string>();
        for (var i = 0; i < queries.Count; i++)
        {
            var ranks = armRankings
                .Select(a => Evaluate(a[i], queries[i], candidateK).FirstGoldRank)
                .ToArray();
            details.Add(
                string.Join(
                    "  ",
                    $"{queries[i].Id, -5} {queries[i].Kind, -9} gold={queries[i].Gold.Count, -2}",
                    $"A=#{ranks[0], -3}",
                    $"B=#{ranks[1], -3}",
                    $"C=#{ranks[2], -3}",
                    $"D=#{ranks[3], -3}",
                    $"E=#{ranks[4], -3}",
                    queries[i].Text
                )
            );
        }

        return (metrics, details);
    }

    // ── 语料与 gold ────────────────────────────────────────────────

    private sealed record Chunk(int Index, string Id, string DocumentId, int Kind, string Text);

    private static async Task<List<Chunk>> LoadDocsAsync(
        string dbPath,
        string novelId,
        CancellationToken ct
    )
    {
        var path = dbPath.StartsWith("file:", StringComparison.Ordinal)
            ? dbPath
            : $"file:{dbPath}?mode=ro";
        await using var db = new SqliteConnection($"Data Source={path}");
        await db.OpenAsync(ct);

        var docs = new List<Chunk>();
        await using var cmd = db.CreateCommand();
        cmd.CommandText =
            @"SELECT c.chunk_id, c.document_id, d.document_kind, c.text
                FROM retrieval_chunks c
                JOIN retrieval_documents d ON d.document_id = c.document_id
               WHERE d.novel_id = $novel AND d.index_status <> $deleted
               ORDER BY c.document_id, c.chunk_ordinal";
        cmd.Parameters.AddWithValue("$novel", novelId);
        cmd.Parameters.AddWithValue("$deleted", (int)IndexStatus.Deleted);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var index = 0;
        while (await reader.ReadAsync(ct))
            docs.Add(
                new Chunk(
                    index++,
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetString(3)
                )
            );
        return docs;
    }

    /// <summary>从档案文档的正文前缀里取实体名 → 其档案文档序号。</summary>
    private static Dictionary<string, int> ExtractEntities(List<Chunk> docs)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var doc in docs)
        foreach (var (prefix, kind) in ProfilePrefixes)
        {
            if (doc.Kind != kind || !doc.Text.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            var rest = doc.Text[prefix.Length..];
            var cut = rest.IndexOfAny(['\n', ' ', '\t']);
            var name = (cut < 0 ? rest : rest[..cut]).Trim();
            if (name.Length >= 2)
                map.TryAdd(name, doc.Index);
            break;
        }
        return map;
    }

    private static List<EvalQuery> BuildQueries(
        List<Chunk> docs,
        Dictionary<string, int> entities,
        GoldMode mode
    )
    {
        var queries = new List<EvalQuery>();
        foreach (var (name, profileIndex) in entities)
        {
            var mentions = Enumerable
                .Range(0, docs.Count)
                .Where(i => docs[i].Text.Contains(name, StringComparison.Ordinal))
                .ToList();
            if (mentions.Count == 0)
                continue;

            var factGold = mode == GoldMode.Strict ? [profileIndex] : mentions;
            queries.Add(
                new EvalQuery(
                    $"f{queries.Count + 1:00}",
                    QueryKind.Fact,
                    $"{name}的性格和设定是什么？",
                    factGold
                )
            );

            if (name.Length >= 3)
                queries.Add(
                    new EvalQuery(
                        $"p{queries.Count + 1:00}",
                        QueryKind.Partial,
                        name[..2],
                        mentions
                    )
                );

            var relations = Enumerable
                .Range(0, docs.Count)
                .Where(i =>
                    docs[i].Kind == (int)DocumentKind.RelationshipStatement
                    && docs[i].Text.Contains(name, StringComparison.Ordinal)
                )
                .ToList();
            if (relations.Count > 0)
                queries.Add(
                    new EvalQuery(
                        $"r{queries.Count + 1:00}",
                        QueryKind.Relation,
                        $"和{name}相关的关系是什么？",
                        relations
                    )
                );
        }
        return queries;
    }

    // ── 分词 / BM25 / 扩展 ────────────────────────────────────────

    private static List<string> Tokenize(string text, string novelId, bool registered) =>
        registered
            ? LexicalText
                .ToIndexText(text, novelId)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToList()
            : PlainTokens(text);

    private static List<string> PlainTokens(string text) =>
        LexicalText.ToBestPathTokens(text).ToList();

    private static string[] QueryTerms(string query, string novelId) =>
        LexicalText.GetQueryTerms(query, novelId);

    /// <summary>余弦相似度排序（全部为归一化向量时等价点积）。</summary>
    private static List<int> DenseRanking(IReadOnlyList<float[]> docs, float[] query)
    {
        var scored = new List<(int Index, double Score)>(docs.Count);
        for (var i = 0; i < docs.Count; i++)
        {
            double dot = 0;
            var v = docs[i];
            var n = Math.Min(v.Length, query.Length);
            for (var k = 0; k < n; k++)
                dot += v[k] * query[k];
            if (dot > 0)
                scored.Add((i, dot));
        }
        return scored.OrderByDescending(x => x.Score).Select(x => x.Index).ToList();
    }

    /// <summary>倒数排名融合（与产品链路一致：score = Σ 1/(k + rank)）。</summary>
    private static List<int> Rrf(List<int> a, List<int> b, int k = 60)
    {
        var scores = new Dictionary<int, double>();
        void Add(List<int> ranking)
        {
            for (var i = 0; i < ranking.Count; i++)
                scores[ranking[i]] = scores.GetValueOrDefault(ranking[i]) + 1.0 / (k + i + 1);
        }
        Add(a);
        Add(b);
        return scores.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
    }

    /// <summary>真实 JEV 重排：BM25 前 N 条交给 JudgeRerank（SystemOne noul），按重排结果还原序号。</summary>
    private static async Task<List<int>> RerankWithJevAsync(
        List<int> ranking,
        string query,
        List<Chunk> docs,
        Core.Inference.ISystemOneClient client,
        CancellationToken ct
    )
    {
        var max = client.Options.Rerank.MaxCandidates;
        var window = ranking.Take(Math.Clamp(max, 1, Math.Max(1, ranking.Count))).ToList();
        if (window.Count <= 1)
            return ranking;

        var candidates = new List<RetrievalSearchResult>(window.Count);
        for (var i = 0; i < window.Count; i++)
            candidates.Add(
                new RetrievalSearchResult
                {
                    ChunkId = $"#{window[i]}",
                    DocumentId = docs[window[i]].DocumentId,
                    Domain = RetrievalDomain.NovelKnowledge,
                    DocumentKind = (DocumentKind)docs[window[i]].Kind,
                    Text = docs[window[i]].Text,
                    // 与产品链路一致：融合分是 RRF 量级（1/(k+rank)），分数差很小，
                    // 这样 JudgeRerank 的乘性调节才有机会改变顺序；用 1/(i+1) 会让重排永远翻不动。
                    Score = 1.0 / (60 + i + 1),
                    JudgeProbability = null,
                }
            );

        var outcome = await JudgeRerank.ApplyAsync(client, query, candidates, ct);
        if (outcome.Aborted || outcome.Judged == 0)
            return ranking;

        var reordered = outcome
            .Results.Select(r => int.Parse(r.ChunkId[1..]))
            .Concat(ranking.Where(i => !window.Contains(i)))
            .ToList();
        return reordered;
    }

    /// <summary>BM25 排序（k1=1.2, b=0.75），返回按分数降序的文档序号。</summary>
    private static List<int> Bm25(List<List<string>> docs, IEnumerable<string> terms)
    {
        var distinct = terms.Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length == 0)
            return [];

        var n = docs.Count;
        var avgdl = docs.Average(d => (double)d.Count);
        var scored = new List<(int Index, double Score)>(n);
        for (var i = 0; i < n; i++)
        {
            double score = 0;
            foreach (var term in distinct)
            {
                var tf = docs[i].Count(t => t == term);
                if (tf == 0)
                    continue;
                var df = docs.Count(d => d.Contains(term));
                if (df == 0)
                    continue;
                var idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
                if (idf <= 0)
                    continue;
                score += idf * tf * (K1 + 1) / (tf + K1 * (1 - B + B * docs[i].Count / avgdl));
            }
            if (score > 0)
                scored.Add((i, score));
        }
        return scored.OrderByDescending(x => x.Score).Select(x => x.Index).ToList();
    }

    /// <summary>
    /// SAG2 的第二跳思路（无 LLM 版）：首轮 BM25 取 top3 → 从中挖「同一个词出现在 ≥2 篇、
    /// 不在原查询、idf 足够高」的词项 → 二轮查询 → 与首轮交错合并。
    /// </summary>
    private sealed class TwoRoundExpansion(List<List<string>> docs)
    {
        public List<int> ExpandAndSearch(string query, string novelId)
        {
            var baseTerms = QueryTerms(query, novelId);
            var first = Bm25(docs, baseTerms).Take(3).ToList();
            if (first.Count == 0)
                return [];

            var df = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var doc in docs)
            foreach (var token in doc.Distinct(StringComparer.Ordinal))
                df[token] = df.GetValueOrDefault(token) + 1;

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var index in first)
            foreach (var token in docs[index].Distinct(StringComparer.Ordinal))
                if (token.Length >= 2 && !baseTerms.Contains(token) && df[token] * 5 <= docs.Count)
                    counts[token] = counts.GetValueOrDefault(token) + 1;

            var extra = counts
                .Where(kv => kv.Value >= 2)
                .OrderByDescending(kv => kv.Value * Math.Log(1.0 + docs.Count / (double)df[kv.Key]))
                .Take(5)
                .Select(kv => kv.Key)
                .ToArray();
            if (extra.Length == 0)
                return [];

            return Bm25(docs, baseTerms.Concat(extra));
        }
    }

    /// <summary>
    /// 文档级 MMR（DPP 的便宜近似）：每步选「相关度 − 与已选项最大相似度」最高的，
    /// 相似度 = 同文档 1.0 / 否则两个块 token 的 Jaccard。用来回答「topK 里是否被同一段挤满」。
    /// </summary>
    private sealed class MmrSelector(List<Chunk> docs)
    {
        private readonly Dictionary<int, HashSet<string>> _tokens = new();

        public List<int> Select(List<int> ranked, int take, double lambda = 0.7)
        {
            var selected = new List<int>(take);
            var pool = ranked.Take(200).ToList();
            if (pool.Count == 0)
                return pool;

            while (selected.Count < take && pool.Count > 0)
            {
                var best = pool[0];
                var bestScore = double.MinValue;
                foreach (var candidate in pool)
                {
                    var relevance = 1.0 / (1 + pool.IndexOf(candidate));
                    var similarity =
                        selected.Count == 0 ? 0 : selected.Max(s => Similarity(candidate, s));
                    var score = lambda * relevance - (1 - lambda) * similarity;
                    if (score <= bestScore)
                        continue;
                    bestScore = score;
                    best = candidate;
                }
                selected.Add(best);
                pool.Remove(best);
            }
            return selected.Concat(ranked.Where(i => !selected.Contains(i))).ToList();
        }

        private double Similarity(int a, int b)
        {
            if (docs[a].DocumentId == docs[b].DocumentId)
                return 1.0;
            var ta = Tokens(a);
            var tb = Tokens(b);
            if (ta.Count == 0 || tb.Count == 0)
                return 0;
            var inter = ta.Count(tb.Contains);
            return inter / (double)(ta.Count + tb.Count - inter);
        }

        private HashSet<string> Tokens(int index)
        {
            if (!_tokens.TryGetValue(index, out var set))
            {
                set = LexicalText
                    .ToBestPathTokens(docs[index].Text)
                    .ToHashSet(StringComparer.Ordinal);
                _tokens[index] = set;
            }
            return set;
        }
    }

    /// <summary>SAG2 的「交错合并」：两轮结果轮流取，先取首轮。</summary>
    private static List<int> Interleave(List<int> first, List<int> second)
    {
        var merged = new List<int>(first.Count + second.Count);
        var seen = new HashSet<int>();
        for (var i = 0; i < Math.Max(first.Count, second.Count); i++)
        {
            if (i < first.Count && seen.Add(first[i]))
                merged.Add(first[i]);
            if (i < second.Count && seen.Add(second[i]))
                merged.Add(second[i]);
        }
        return merged;
    }

    private static QueryResult Evaluate(List<int> ranking, EvalQuery query, int candidateK)
    {
        var gold = query.Gold.ToHashSet();
        var limit = Math.Min(ranking.Count, candidateK);
        var firstRank = 0;
        var found5 = 0;
        var found10 = 0;
        for (var i = 0; i < limit; i++)
        {
            if (!gold.Contains(ranking[i]))
                continue;
            if (firstRank == 0)
                firstRank = i + 1;
            if (i < 5)
                found5++;
            if (i < 10)
                found10++;
        }
        return new QueryResult(firstRank, found5, found10, gold.Count);
    }

    private static ArmMetrics Summarize(string arm, List<QueryResult> results)
    {
        var n = results.Count;
        return new ArmMetrics(
            arm,
            results.Count(r => r.HitAt(5)) / (double)n,
            results.Count(r => r.HitAt(10)) / (double)n,
            results.Count(r => r.AllAt5) / (double)n,
            results.Average(r => r.ReciprocalRank),
            n
        );
    }
}
