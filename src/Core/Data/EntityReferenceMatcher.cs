namespace Core.Data;

/// <summary>
/// `#类型:名称` 的名称匹配（补全插入与发送内联共用）：
/// 只接受「最长且结束于词边界」的实体名——名称可以含空格（章节标题 / 卷名），
/// 但不能把「李」匹配进「李四」这种更长的词里。
/// </summary>
public static class EntityReferenceMatcher
{
    /// <summary>
    /// 在 <paramref name="text"/> 的 <paramref name="nameStart"/> 处找最长的实体名。
    /// 词边界 = 名称之后是文本结尾、空白或非字母数字字符（标点 / `#` / `]` 等）。
    /// </summary>
    public static (string Name, string Id)? MatchLongest(
        string text,
        int nameStart,
        IReadOnlyList<(string Name, string Id)> references
    )
    {
        if (nameStart < 0 || nameStart >= text.Length)
            return null;

        (string Name, string Id)? best = null;
        foreach (var reference in references)
        {
            var name = reference.Name;
            if (string.IsNullOrEmpty(name) || nameStart + name.Length > text.Length)
                continue;
            if (!text.AsSpan(nameStart, name.Length).SequenceEqual(name.AsSpan()))
                continue;

            var end = nameStart + name.Length;
            if (end < text.Length && char.IsLetterOrDigit(text[end]))
                continue; // 「李」不能吃掉「李四」

            if (best is null || name.Length > best.Value.Name.Length)
                best = (name, reference.Id);
        }

        return best;
    }
}
