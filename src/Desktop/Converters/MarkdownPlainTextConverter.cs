using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Data.Converters;
using LiveMarkdown.Avalonia;

namespace Desktop.Converters;

/// <summary>
/// Markdown → 轻量纯文本（历史预取占位用）。只去掉最显眼的 Markdown 标记，
/// 不做完整解析；目的是避免在滚动路径上创建 MarkdownRenderer 的视觉树。
/// </summary>
public sealed partial class MarkdownPlainTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value is ObservableStringBuilder builder ? builder.ToString() : value as string;
        return string.IsNullOrEmpty(text) ? "" : Strip(text);
    }

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => throw new NotSupportedException();

    private static string Strip(string text)
    {
        text = CodeFenceRegex().Replace(text, "");
        text = TableSeparatorRegex().Replace(text, "");
        text = HeadingRegex().Replace(text, "");
        text = BlockquoteRegex().Replace(text, "");
        text = ImageRegex().Replace(text, "$1");
        text = LinkRegex().Replace(text, "$1");
        text = BoldStarRegex().Replace(text, "$1");
        text = BoldUnderRegex().Replace(text, "$1");
        text = ItalicStarRegex().Replace(text, "$1");
        text = ItalicUnderRegex().Replace(text, "$1");
        text = StrikeRegex().Replace(text, "$1");
        text = InlineCodeRegex().Replace(text, "$1");
        text = HtmlTagRegex().Replace(text, "");
        text = TablePipeRegex().Replace(text, "  ");
        text = MultiNewlineRegex().Replace(text, "\n\n");
        return text.Trim();
    }

    [GeneratedRegex(@"^```[^\n]*\n?", RegexOptions.Multiline)]
    private static partial Regex CodeFenceRegex();

    [GeneratedRegex(@"^\s*\|?[\s:|-]+\|[\s:|-]*$", RegexOptions.Multiline)]
    private static partial Regex TableSeparatorRegex();

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\s*>\s?", RegexOptions.Multiline)]
    private static partial Regex BlockquoteRegex();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]+\)")]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"\*\*([^*]+)\*\*")]
    private static partial Regex BoldStarRegex();

    [GeneratedRegex(@"__([^_]+)__")]
    private static partial Regex BoldUnderRegex();

    [GeneratedRegex(@"(?<!\*)\*([^*\n]+)\*(?!\*)")]
    private static partial Regex ItalicStarRegex();

    [GeneratedRegex(@"(?<!_)_([^_\n]+)_(?!_)")]
    private static partial Regex ItalicUnderRegex();

    [GeneratedRegex(@"~~([^~]+)~~")]
    private static partial Regex StrikeRegex();

    [GeneratedRegex(@"`([^`\n]+)`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s*\|\s*")]
    private static partial Regex TablePipeRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultiNewlineRegex();
}
