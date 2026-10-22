// 功能：Markdown → 微信可读纯文本 + 按长度分段
//
// 微信不渲染 Markdown（**加粗**、# 标题、[链接](url) 都会原样显示），所以发送前要降级。
// 与 LKK 的 WeChatMarkdownFilter（16KB）不同，这里只覆盖实际会出现的语法：
// 代码围栏、标题、引用、列表、强调、行内代码、链接与图片、分隔线。不追求完整 CommonMark。

using System.Text;
using System.Text.RegularExpressions;

namespace Channels.WeChat;

/// <summary>微信文本降级与分段。</summary>
internal static partial class WeChatMarkdown
{
    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+")]
    private static partial Regex HeadingPrefix();

    [GeneratedRegex(@"^\s{0,3}>\s?")]
    private static partial Regex QuotePrefix();

    [GeneratedRegex(@"^\s{0,3}[-*+]\s+")]
    private static partial Regex BulletPrefix();

    /// <summary>分隔线：--- / *** / ___（允许空格）。整行只有这些字符时才算。</summary>
    [GeneratedRegex(@"^\s{0,3}(?:-{3,}|\*{3,}|_{3,})\s*$")]
    private static partial Regex HorizontalRule();

    /// <summary>图片：先于链接处理，否则 ![alt](url) 会被链接规则吃掉前半段。</summary>
    [GeneratedRegex(@"!\[([^\]]*)\]\(([^)]+)\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)]+)\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"`([^`\n]+)`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"\*\*(.+?)\*\*|__(.+?)__")]
    private static partial Regex Bold();

    /// <summary>
    /// 强调只处理带 ASCII 边界的 *x* / _x_。
    /// 不能用 \w 做边界：.NET 的 \w 包含汉字，会把「的*斜体*」里的星号当成词内字符而漏过。
    /// </summary>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9_*])\*([^*\n]+)\*(?![A-Za-z0-9_*])|(?<![A-Za-z0-9_])_([^_\n]+)_(?![A-Za-z0-9_])"
    )]
    private static partial Regex Italic();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankRun();

    /// <summary>把一个 chunk 的 Markdown 降级成微信可读文本。</summary>
    public static string ToPlainText(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return string.Empty;

        var sb = new StringBuilder(markdown.Length);
        var inFence = false;

        foreach (var rawLine in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            // 围栏行丢弃，内容保留（微信里代码块本就无法高亮，留着围栏只会显示三个反引号）
            if (rawLine.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            var line = rawLine;
            if (!inFence)
            {
                if (HorizontalRule().IsMatch(line))
                    continue; // 分隔线在微信里是噪音

                line = HeadingPrefix().Replace(line, string.Empty);
                line = QuotePrefix().Replace(line, string.Empty);
                line = BulletPrefix().Replace(line, "· ");

                // 行内降级也必须在围栏外做：围栏内是代码，改它就改错了内容
                line = Image().Replace(line, "$1");
                line = Link().Replace(line, "$1 ($2)");
                line = InlineCode().Replace(line, "$1");
                line = Bold()
                    .Replace(
                        line,
                        m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value
                    );
                line = Italic()
                    .Replace(
                        line,
                        m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value
                    );
            }

            sb.Append(line).Append('\n');
        }

        return BlankRun().Replace(sb.ToString(), "\n\n").TrimEnd('\n');
    }

    /// <summary>
    /// 按微信单条上限分段。
    /// **不截断**：静默丢掉回复后半段是最不能接受的失败模式，所以宁可连发多条。
    /// 优先在行边界切；单行本身就超限时才硬切。
    /// </summary>
    /// <param name="text">已降级的纯文本。</param>
    /// <param name="maxChars">单条上限（字符）。</param>
    public static IReadOnlyList<string> SplitForWeChat(
        string text,
        int maxChars = WeChatConstants.MaxTextChars
    )
    {
        if (string.IsNullOrEmpty(text))
            return [];
        if (maxChars <= 0)
            return [text];

        var chunks = new List<string>();
        var current = new StringBuilder();

        void FlushCurrent()
        {
            if (current.Length == 0)
                return;
            chunks.Add(current.ToString().TrimEnd('\n'));
            current.Clear();
        }

        foreach (var line in text.Split('\n'))
        {
            if (line.Length > maxChars)
            {
                // 超长单行（例如一整段没换行的正文）：先交掉已累积的，再硬切
                FlushCurrent();
                for (var offset = 0; offset < line.Length; offset += maxChars)
                    chunks.Add(line.Substring(offset, Math.Min(maxChars, line.Length - offset)));
                continue;
            }

            // +1 = 换行符本身
            if (current.Length + line.Length + 1 > maxChars)
                FlushCurrent();

            if (current.Length > 0)
                current.Append('\n');
            current.Append(line);
        }

        FlushCurrent();
        return chunks;
    }
}
