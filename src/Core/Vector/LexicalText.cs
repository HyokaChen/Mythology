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
/// <item>索引侧用 <see cref="JiebaNet.Segmenter.JiebaSegmenter.CutForSearch"/>：实体名作为整词命中，
/// 同时补发它在词典里的子词（潮音堂书店 → 潮音/书店/潮音堂书店），部分查询不会因为“长词优先”而消失。</item>
/// <item>查询侧 AND（一致性车道）用最佳路径（<c>Cut</c>）保持精确；OR（召回车道）用搜索模式放宽到子词。</item>
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

    /// <summary>查询分词（默认词典，召回车道）。</summary>
    public static string[] GetQueryTerms(string query) => GetQueryTerms(query, novelId: null);

    /// <summary>查询分词（召回车道：含子词）。</summary>
    public static string[] GetQueryTerms(string query, string? novelId) =>
        GetQueryTerms(query, novelId, searchMode: true);

    /// <summary>
    /// 查询分词（长度 ≥2、去重、最多 40 项）。
    /// <paramref name="searchMode"/>=true 含子词（OR 车道）；false 只取最佳路径（AND 车道）。
    /// </summary>
    public static string[] GetQueryTerms(string query, string? novelId, bool searchMode)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        return Tokenize(query, searchMode)
            .Where(term => term.Length >= 2)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxQueryTerms)
            .ToArray();
    }

    /// <summary>FTS5 词项加引号（内部双引号转义）。</summary>
    public static string Quote(string term) => "\"" + term.Replace("\"", "\"\"") + "\"";

    private static List<string> Tokenize(string text, bool searchMode)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var tokens = new List<string>(text.Length / 2);
        foreach (var token in NovelLexicon.Cut(text, searchMode))
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
