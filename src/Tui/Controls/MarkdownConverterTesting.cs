namespace Tui.Controls;

/// <summary>
/// 测试桥接器：将 MarkdownConverter 的 private 方法暴露为 internal，
/// 供 Tui.Tests 项目通过 InternalsVisibleTo 访问。
/// 生产代码不应使用此类。
/// </summary>
internal static class MarkdownConverterTesting
{
    /// <summary>计算单个字符的终端显示宽度。</summary>
    public static int CharDisplayWidthPublic(char c) => MarkdownConverter.CharDisplayWidthPublic(c);

    /// <summary>计算文本的可见显示宽度（排除格式标记）。</summary>
    public static int VisibleLengthPublic(string text) =>
        MarkdownConverter.VisibleLengthPublic(text);

    /// <summary>判断字符是否为 CJK 字符。</summary>
    public static bool IsCjkPublic(char c) => MarkdownConverter.IsCjkPublic(c);

    /// <summary>预处理任务列表复选框（[x]→☑, [ ]→☐）。</summary>
    public static string PreprocessTaskListsPublic(string text) =>
        MarkdownConverter.PreprocessTaskListsPublic(text);

    /// <summary>预处理 Emoji keycap 序列。</summary>
    public static string PreprocessEmojiKeycapsPublic(string text) =>
        MarkdownConverter.PreprocessEmojiKeycapsPublic(text);

    /// <summary>预处理图片语法为卡片式框显示。</summary>
    public static string PreprocessImagesPublic(string text) =>
        MarkdownConverter.PreprocessImagesPublic(text);

    /// <summary>预处理链接语法，追加 URL 显示。</summary>
    public static string PreprocessLinksPublic(string text) =>
        MarkdownConverter.PreprocessLinksPublic(text);

    /// <summary>判断一行是否为标题行并返回级别。</summary>
    public static bool IsHeadingLinePublic(string line, out int level) =>
        MarkdownConverter.IsHeadingLinePublic(line, out level);

    /// <summary>剥离转换后残留的 # 标题前缀。</summary>
    public static string StripHeadingHashesPublic(string markup) =>
        MarkdownConverter.StripHeadingHashesPublic(markup);

    /// <summary>剥离 Markup 标签，返回纯文本。</summary>
    public static string StripMarkupTagsPublic(string line) =>
        MarkdownConverter.StripMarkupTagsPublic(line);

    /// <summary>构建表格边框行。</summary>
    public static string BuildBorderPublic(
        char left,
        char mid,
        char right,
        char fill,
        int[] colWidths
    ) => MarkdownConverter.BuildBorderPublic(left, mid, right, fill, colWidths);

    /// <summary>按对齐方式填充单元格文本。</summary>
    public static string PadCellPublic(string text, int targetWidth, int align) =>
        MarkdownConverter.PadCellPublic(text, targetWidth, align);

    /// <summary>截断文本到指定显示宽度。</summary>
    public static string TruncateToDisplayWidthPublic(string text, int maxWidth) =>
        MarkdownConverter.TruncateToDisplayWidthPublic(text, maxWidth);

    /// <summary>转义 Markup 特殊字符。</summary>
    public static string EscapeMarkupPublic(string text) =>
        MarkdownConverter.EscapeMarkupPublic(text);

    /// <summary>在强调标记周围插入零宽空格。</summary>
    public static string InsertZwspAroundEmphasisPublic(string text) =>
        MarkdownConverter.InsertZwspAroundEmphasisPublic(text);

    /// <summary>CJK 感知的单元格文本自动换行。</summary>
    public static List<string> WrapCellPublic(string text, int targetWidth) =>
        MarkdownConverter.WrapCellPublic(text, targetWidth);

    /// <summary>计算表格列目标宽度，返回是否需要回退到列表模式。</summary>
    public static (int[] TargetWidths, bool NeedFallback) ComputeTargetWidthsPublic(
        int[] naturalWidths,
        int colCount,
        int contentWidth
    ) => MarkdownConverter.ComputeTargetWidthsPublic(naturalWidths, colCount, contentWidth);
}
