namespace Core.Shared;

/// <summary>
/// 轻量模糊匹配：供设定面板 / 大纲搜索框使用（TUI 与 Desktop 同一口径）。
///
/// <para>规则：忽略大小写；查询按空白拆成多词，每个词都必须作为「子序列」出现在文本里
/// （不要求连续、不要求词序），全部命中才算匹配。中英文通用：
/// 「林歌」命中「林长歌」，「yxc」命中「Yin Xue Chen」。</para>
/// </summary>
public static class FuzzySearch
{
    /// <summary>
    /// 文本是否命中查询。查询为空 / 全空白 → 恒真（不过滤）。
    /// </summary>
    public static bool Matches(string? text, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;
        if (string.IsNullOrEmpty(text))
            return false;

        foreach (var term in SplitTerms(query))
        {
            if (!IsSubsequence(text, term))
                return false;
        }
        return true;
    }

    /// <summary>查询分词（空白分隔；忽略空词）。</summary>
    public static IEnumerable<string> SplitTerms(string query) =>
        query.Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );

    /// <summary>term 是否为 text 的子序列（忽略大小写、不要求连续）。</summary>
    private static bool IsSubsequence(string text, string term)
    {
        var index = 0;
        foreach (var ch in text)
        {
            if (char.ToLowerInvariant(ch) != char.ToLowerInvariant(term[index]))
                continue;
            if (++index == term.Length)
                return true;
        }
        return false;
    }
}
