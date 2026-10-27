namespace Core.Vector;

/// <summary>
/// 词法检索文本处理：写入 FTS 前用 jieba 预分词，查询时把词项拼成 OR/AND。
///
/// 背景：SQLite FTS5 的 unicode61 tokenizer 会把连续中文当成一个 token，
/// 导致「对过往有着近乎固执的眷恋」整句成为一个词，查询「眷恋」命中 0。
/// 这里不依赖 simple 原生扩展，而是把分词结果以空格连接后写入普通 FTS5 列，
/// 从而在任何平台都能得到词级 BM25。
///
/// 发射模式（实测依据见 docs/lexical-retokenize-plan.md §A0）：
/// <list type="bullet">
/// <item>索引侧：长度 ≥4 的词与已注册实体名，除自身外再补发它在 jieba 词典里的 2/3 字片段
/// （中国人民大学 → +中国/人民/大学；潮音堂书店 → +潮音/书店），避免「长词优先」把组成词吞掉。</item>
/// <item>查询侧 AND（一致性车道）只取最佳路径保持精确；OR（召回车道）用同一套补子词规则放宽召回。</item>
/// </list>
/// 传入 novelId 时使用全局实体词典（<see cref="NovelLexicon"/>）。
/// </summary>
public static class LexicalText
{
    /// <summary>单次查询最多使用的词项数（防止 OR 过长）。</summary>
    public const int MaxQueryTerms = 40;

    /// <summary>写入 FTS 的分词文本（空格连接；停用词/标点由分词器过滤）。索引侧固定用搜索模式。</summary>
    public static string ToIndexText(string text) => ToIndexText(text, novelId: null);

    /// <summary>写入 FTS 的分词文本（长词 + 已知子词）。</summary>
    public static string ToIndexText(string text, string? novelId)
    {
        var tokens = Tokenize(text, searchMode: true);
        return tokens.Count == 0 ? "" : string.Join(' ', tokens);
    }

    /// <summary>
    /// 构造 FTS5 MATCH 表达式（默认 OR）。
    /// <paramref name="useOr"/>=true：<c>"词" OR "词"</c>（联想车道，高召回，含子词）；
    /// false：<c>"词" AND "词"</c>（一致性车道，高精确，只取最佳路径）。
    /// </summary>
    public static string BuildMatchQuery(string query, bool useOr = true) =>
        BuildMatchQuery(query, novelId: null, useOr);

    /// <summary>构造 FTS5 MATCH 表达式（使用全局实体词典）。</summary>
    public static string BuildMatchQuery(string query, string? novelId, bool useOr = true)
    {
        var terms = GetQueryTerms(query, novelId, useOr);
        if (terms.Length == 0)
            return "";

        return string.Join(useOr ? " OR " : " AND ", terms.Select(Quote));
    }

    /// <summary>查询分词（默认词典）。</summary>
    public static string[] GetQueryTerms(string query) => GetQueryTerms(query, novelId: null);

    /// <summary>查询分词（使用全局实体词典）。</summary>
    public static string[] GetQueryTerms(string query, string? novelId) =>
        GetQueryTerms(query, novelId, searchMode: false);

    /// <summary>
    /// 查询分词（长度 ≥2、去重、最多 40 项）。
    /// **查询侧永远不展开子词**：索引侧已经为实体名补了 2/3 字片段，查询再展开只会把
    /// 「陈折花」稀释成「陈折/折花」，让只提到片段的正文块挤掉档案（实测档案排名 #2 → #11）。
    /// <paramref name="searchMode"/> 仅为兼容保留。
    /// </summary>
    public static string[] GetQueryTerms(string query, string? novelId, bool searchMode)
    {
        _ = searchMode;
        if (string.IsNullOrWhiteSpace(query))
            return [];

        return Tokenize(query, searchMode: false)
            .Where(term => term.Length >= 2)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxQueryTerms)
            .ToArray();
    }

    /// <summary>FTS5 词项加引号（内部双引号转义）。</summary>
    public static string Quote(string term) => "\"" + term.Replace("\"", "\"\"") + "\"";

    /// <summary>
    /// 切词。搜索模式下为两类词补发子词（jieba 认识的 2/3 字片段）：
    /// <list type="bullet">
    /// <item>**长度 ≥4 的词**：它们会吞掉有意义的组成词——「中国人民大学」整词进索引后，
    /// 搜「大学」「中国」「人民」「中国人民」全都会落空（实测 A 方案 0~1 块，补发后 4 块）。</item>
    /// <item>**已注册的实体名**（任意长度）：名字注册成整词后，「体育馆」这类组成词查询同样会失效。</item>
    /// </list>
    /// 2–3 字的普通词不补：它们吞掉的信息量极小，补了只增索引（全局补发的实测代价是 9.7% vs 2.2%）。
    /// </summary>
    private static List<string> Tokenize(string text, bool searchMode)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var tokens = Clean(NovelLexicon.Cut(text, searchMode: false));
        if (!searchMode || tokens.Count == 0)
            return tokens;

        var existing = tokens.ToHashSet(StringComparer.Ordinal);

        // ① 已注册的实体名：补**全部** 2/3 字片段（不查 jieba 词表）。
        //    实体名的片段几乎都不在通用词表里（实测 陈折/海野/木戸/星罗 全是未登录词），
        //    只补“词典认识的”等于没补——而「海野和天音什么关系」正是检索/记忆里最常见的问法。
        foreach (var token in tokens.Where(NovelLexicon.IsRegistered).ToArray())
        foreach (var gram in Ngrams(token))
            if (existing.Add(gram))
                tokens.Add(gram);

        // ② 长度 ≥4 的普通长词：补它在 jieba 词表里存在的 2/3 字片段。
        //    这类词数量多，无差别补 2-gram 会让索引膨胀，故只补“看起来像词”的片段。
        var expandable = tokens
            .Where(t => t.Length >= 4 && !NovelLexicon.IsRegistered(t))
            .ToArray();
        if (expandable.Length > 0)
            foreach (var extra in Clean(NovelLexicon.Cut(text, searchMode: true)))
            {
                if (existing.Contains(extra))
                    continue;
                if (expandable.Any(word => word.Contains(extra, StringComparison.Ordinal)))
                {
                    existing.Add(extra);
                    tokens.Add(extra);
                }
            }

        return tokens;
    }

    /// <summary>
    /// 切出该词的 2/3 字片段（用于实体名的部分命中）。
    /// 不过滤跨虚词的片段（之谜/守护 这类是有意义的后缀，印的/的守 是无害噪声）——
    /// 过滤会把「之谜」这类真实查询需求一起干掉，实测代价 0.4% token 换 17/17 的部分名召回。
    /// </summary>
    private static IEnumerable<string> Ngrams(string word)
    {
        for (var len = 2; len <= 3; len++)
        for (var i = 0; i + len <= word.Length; i++)
            yield return word.Substring(i, len);
    }

    /// <summary>最佳路径分词（不补子词）：基线对照与「查询侧」共用同一套过滤规则。</summary>
    public static string[] ToBestPathTokens(string text) =>
        Clean(NovelLexicon.Cut(text, searchMode: false)).Where(t => t.Length >= 2).ToArray();

    /// <summary>去标点/空白/停用词，统一小写。</summary>
    private static List<string> Clean(IEnumerable<string> raw)
    {
        var tokens = new List<string>();
        foreach (var token in raw)
        {
            var trimmed = token.Trim();
            if (trimmed.Length == 0)
                continue;
            if (trimmed.All(ch => char.IsPunctuation(ch) || ch == '_' || ch == ' '))
                continue;
            trimmed = trimmed.ToLowerInvariant();
            if (AgentMemoryConflict.Stopwords.Contains(trimmed))
                continue;
            tokens.Add(trimmed);
        }
        return tokens;
    }
}
