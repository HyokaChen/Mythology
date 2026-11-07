using System.Text.RegularExpressions;

namespace Desktop.Controls;

/// <summary>
/// @ 文件路径补全解析器：定位光标前的 "@path" 查询词（可含空格、中文、路径分隔符），
/// 返回查询词与起始索引，供 View 层驱动文件补全弹窗。
/// 语义与 TUI 对齐：@ = 引用本地文件，实体提及走 # 实体引用。
/// </summary>
public static partial class MentionParser
{
    /// <summary>@ 路径解析结果（Found=false 表示光标前不是路径查询）。</summary>
    public readonly record struct Mention(bool Found, string Query, int Start);

    // 路径 token：贪婪到光标（不含换行）。空格合法（macOS 常见 "My Files/"），
    // 由 FilePathCompletion 按最后一个分隔符切分目录/前缀；多词句子的最后一个词
    // 若含路径特征才被视作路径，纯自然语言词不误触发。
    [GeneratedRegex(@"@([^\n\r]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex MentionRegex();

    /// <summary>尝试解析光标前的 @ 路径查询（裸 @ = Found 且 Query 为空）。</summary>
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

    /// <summary>
    /// 将选中建议插入文本：只替换查询词的最后一段（最后一个分隔符之后），
    /// 保留已输入的绝对/相对路径前缀（`@/Users/x/序` 选「序章.txt」→ `@/Users/x/序章.txt`）。
    /// 目录候选尾带 / 可继续导航；文件候选尾补空格。
    /// </summary>
    public static (string Text, int Caret) InsertSuggestion(
        string text,
        int caretIndex,
        int mentionStart,
        string suggestion
    )
    {
        var queryStart = mentionStart + 1;
        var limit = Math.Min(caretIndex, text.Length);
        var segmentStart = queryStart;
        for (var i = limit - 1; i >= queryStart; i--)
        {
            if (text[i] is '/' or '\\')
            {
                segmentStart = i + 1;
                break;
            }
        }

        var trailing = suggestion.EndsWith('/') ? "" : " ";
        var replaced = text[..segmentStart] + suggestion + trailing + text[limit..];
        var caret = segmentStart + suggestion.Length + trailing.Length;
        return (replaced, caret);
    }
}
