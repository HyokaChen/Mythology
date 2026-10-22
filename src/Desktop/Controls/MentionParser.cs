using System.Text.RegularExpressions;

namespace Desktop.Controls;

/// <summary>
/// @ 提及补全解析器：定位光标前的 "@query"（支持中文/字母/数字/下划线），
/// 返回查询词与起始索引，供 View 层驱动补全弹窗。
/// </summary>
public static partial class MentionParser
{
    /// <summary>@ 提及解析结果（Found=false 表示光标前不是提及）。</summary>
    public readonly record struct Mention(bool Found, string Query, int Start);

    [GeneratedRegex(@"@([\p{L}\p{N}_\-]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex MentionRegex();

    /// <summary>尝试解析光标前的 @ 提及（裸 @ = Found 且 Query 为空）。</summary>
    public static Mention TryGetMention(string text, int caretIndex)
    {
        if (caretIndex <= 0 || text.Length == 0)
            return new Mention(false, string.Empty, 0);

        var before = caretIndex >= text.Length ? text : text[..caretIndex];
        var match = MentionRegex().Match(before);
        // 必须紧贴光标：`$` 在 .NET 中也会匹配「字符串末尾的 \n 之前」，
        // 不加这条守卫，Enter 换行后弹窗会残留。
        if (!match.Success || match.Index + match.Length != before.Length)
            return new Mention(false, string.Empty, 0);
        return new Mention(true, match.Groups[1].Value, match.Index);
    }

    /// <summary>将选中建议插入文本（替换 "@query" 为 "@名称 "）。</summary>
    public static string InsertSuggestion(
        string text,
        int caretIndex,
        int mentionStart,
        string suggestion
    )
    {
        var replaced = text[..mentionStart] + "@" + suggestion + " " + text[caretIndex..];
        return replaced;
    }
}
