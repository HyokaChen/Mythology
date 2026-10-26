namespace Core.Vector;

/// <summary>
/// 词法检索文本处理：写入 FTS 前用 jieba 预分词，查询时把词项拼成 OR。
///
/// 背景：SQLite FTS5 的 unicode61 tokenizer 会把连续中文当成一个 token，
/// 导致「对过往有着近乎固执的眷恋」整句成为一个词，查询「眷恋」命中 0。
/// 这里不依赖 simple 原生扩展，而是把分词结果以空格连接后写入普通 FTS5 列，
/// 从而在任何平台都能得到词级 BM25。
/// </summary>
public static class LexicalText
{
    /// <summary>单次查询最多使用的词项数（防止 OR 过长）。</summary>
    public const int MaxQueryTerms = 40;

    /// <summary>写入 FTS 的分词文本（空格连接；停用词/标点由分词器过滤）。</summary>
    public static string ToIndexText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        return string.Join(' ', AgentMemoryBm25.Tokenize(text));
    }

    /// <summary>
    /// 构造 FTS5 MATCH 表达式。
    /// <paramref name="useOr"/>=true：<c>"词" OR "词"</c>（联想车道，高召回）；
    /// false：<c>"词" AND "词"</c>（一致性车道，高精确）。
    /// 只保留长度 ≥2 的词项；无可用词项时返回空串，由调用方回退到原始转义查询。
    /// </summary>
    public static string BuildMatchQuery(string query, bool useOr = true)
    {
        var terms = GetQueryTerms(query);
        if (terms.Length == 0)
            return "";

        return string.Join(useOr ? " OR " : " AND ", terms.Select(Quote));
    }

    /// <summary>查询分词（长度 ≥2、去重、最多 40 项）。</summary>
    public static string[] GetQueryTerms(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        return AgentMemoryBm25
            .Tokenize(query)
            .Where(term => term.Length >= 2)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxQueryTerms)
            .ToArray();
    }

    /// <summary>FTS5 词项加引号（内部双引号转义）。</summary>
    public static string Quote(string term) => "\"" + term.Replace("\"", "\"\"") + "\"";
}
