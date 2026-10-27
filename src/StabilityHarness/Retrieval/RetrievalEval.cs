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

/// <summary>单条查询在某个方案下的结果。</summary>
public sealed record QueryResult(int FirstGoldRank, int GoldFound, int GoldTotal)
{
    public bool HitAt(int k) => FirstGoldRank is > 0 && FirstGoldRank <= k;

    public bool AllAt(int k) => GoldTotal > 0 && GoldFound == GoldTotal;

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
        };
        string[] armNames =
        [
            "A 基线（默认词典·最佳路径）",
            "B 现行（实体注册 + 2/3 字片段）",
            "C 现行 + 词项扩展二轮（SAG2 式）",
        ];

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
                    queries[i].Text
                )
            );
        }

        return (metrics, details);
    }

    // ── 语料与 gold ────────────────────────────────────────────────

    private sealed record Chunk(int Index, string Id, int Kind, string Text);

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
            @"SELECT c.chunk_id, d.document_kind, c.text
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
                new Chunk(index++, reader.GetString(0), reader.GetInt32(1), reader.GetString(2))
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
        var found = 0;
        for (var i = 0; i < limit; i++)
        {
            if (!gold.Contains(ranking[i]))
                continue;
            found++;
            if (firstRank == 0)
                firstRank = i + 1;
        }
        return new QueryResult(firstRank, found, gold.Count);
    }

    private static ArmMetrics Summarize(string arm, List<QueryResult> results)
    {
        var n = results.Count;
        return new ArmMetrics(
            arm,
            results.Count(r => r.HitAt(5)) / (double)n,
            results.Count(r => r.HitAt(10)) / (double)n,
            results.Count(r => r.AllAt(5)) / (double)n,
            results.Average(r => r.ReciprocalRank),
            n
        );
    }
}
