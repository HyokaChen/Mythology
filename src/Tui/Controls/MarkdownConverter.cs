using System.Text;
using Tui.Services;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Extensions.Markdown;
using XenoAtom.Terminal.UI.Extensions.Markdown.Styling;
using XenoAtom.Terminal.UI.Styling;

namespace Tui.Controls;

/// <summary>
/// Markdown 到 XenoAtom Markup 的转换器。
/// 提供预处理、表格渲染、任务列表、图片和链接处理等功能。
/// </summary>
public static class MarkdownConverter
{
    /// <summary>H1 赭石色（篝火）。</summary>
    private static readonly Color HeadingAccentColor = Color.Rgb(0xA6, 0x5E, 0x2E);

    /// <summary>H2 同色（篝火）。</summary>
    private static readonly Color Heading2Color = Color.Rgb(0xA6, 0x5E, 0x2E);

    /// <summary>H3 铜色。</summary>
    private static readonly Color Heading3Color = Color.Rgb(0xD6, 0x8A, 0x48);

    /// <summary>H4 月光。</summary>
    private static readonly Color Heading4Color = Color.Rgb(0xC7, 0xB2, 0x99);

    /// <summary>H5 暗月光。</summary>
    private static readonly Color Heading5Color = Color.Rgb(0x8F, 0x82, 0x70);

    /// <summary>H6 篝火岩。</summary>
    private static readonly Color Heading6Color = Color.Rgb(0x6E, 0x4A, 0x36);

    /// <summary>加粗强调色（亮月光）。</summary>
    private static readonly Color StrongAccentColor = Color.Rgb(0xFB, 0xF1, 0xC7);

    /// <summary>行内代码前景色（琥珀橙）。</summary>
    private static readonly Color InlineCodeFgColor = Color.Rgb(0xFB, 0x94, 0x37);

    /// <summary>行内代码背景色（深灰棕）。</summary>
    private static readonly Color InlineCodeBgColor = Color.Rgb(0x2D, 0x24, 0x24);

    /// <summary>引用前缀色（篝火岩）。</summary>
    private static readonly Color QuotePrefixColor = Color.Rgb(0x6E, 0x4A, 0x36);

    /// <summary>Alert Note 色调（铜色）。</summary>
    private static readonly Color AlertNoteColor = Color.Rgb(0xD6, 0x8A, 0x48);

    /// <summary>Alert Tip 色调（鼠尾草绿）。</summary>
    private static readonly Color AlertTipColor = Color.Rgb(0x8E, 0xC0, 0x7C);

    /// <summary>Alert Important 色调（沙漠玫瑰）。</summary>
    private static readonly Color AlertImportantColor = Color.Rgb(0xD3, 0x86, 0x9B);

    /// <summary>Alert Warning 色调（金琥珀）。</summary>
    private static readonly Color AlertWarningColor = Color.Rgb(0xFA, 0xBD, 0x2F);

    /// <summary>Alert Caution 色调（赤陶红）。</summary>
    private static readonly Color AlertCautionColor = Color.Rgb(0xFB, 0x49, 0x34);

    /// <summary>MarkdownConverter 实例锁（线程安全）。</summary>
    private static readonly Lock MdConverterLock = new();

    /// <summary>XenoAtom Markdown 转换器实例（初始 Desert 样式，主题切换时经 ApplyTheme 重建）。</summary>
    private static readonly MarkdownMarkupConverter MdConverter = CreateConverter(colors: null);

    /// <summary>创建转换器实例：colors 为 null 时用 Desert 默认色（现有静态常量），否则用面板配色推导。</summary>
    private static MarkdownMarkupConverter CreateConverter(PanelColors? colors) =>
        new() { Theme = ThemeRegistry.Desert.UiTheme, Style = BuildMarkdownStyle(colors) };

    /// <summary>
    /// 按主题重建 Markdown 样式：Desert（colors == null）保留原有 6 级标题色阶；
    /// 其他主题从 PanelColors 推导（标题/代码/引用/告警统一语义映射）。
    /// </summary>
    private static MarkdownStyle BuildMarkdownStyle(PanelColors? c)
    {
        var heading1 = c?.MarkdownHeadingFg ?? HeadingAccentColor;
        var heading2 = c?.MarkdownHeadingFg ?? Heading2Color;
        var heading3 = c?.MarkdownHeadingFg ?? Heading3Color;
        var heading4 = c?.SidePanelFg ?? Heading4Color;
        var heading5 = c?.Dim ?? Heading5Color;
        var heading6 = c?.Border ?? Heading6Color;
        var strong = c?.AssistantBubbleFg ?? StrongAccentColor;
        var codeFg = c?.MarkdownCodeFg ?? InlineCodeFgColor;
        var codeBg = c?.MarkdownCodeBg ?? InlineCodeBgColor;
        var quote = c?.Border ?? QuotePrefixColor;
        var note = c?.AccentBlue ?? AlertNoteColor;
        var tip = c?.AccentGreen ?? AlertTipColor;
        var important = c?.AccentBlue ?? AlertImportantColor;
        var warning = c?.AccentYellow ?? AlertWarningColor;
        var caution = c?.AccentRed ?? AlertCautionColor;

        return MarkdownStyle.Default with
        {
            Heading1Style =
                Style.None.WithForeground(heading1) | TextStyle.Bold | TextStyle.Underline,
            Heading2Style = Style.None.WithForeground(heading2) | TextStyle.Bold,
            Heading3Style = Style.None.WithForeground(heading3) | TextStyle.Bold,
            Heading4Style = Style.None.WithForeground(heading4) | TextStyle.Bold,
            Heading5Style = Style.None.WithForeground(heading5) | TextStyle.Bold,
            Heading6Style = Style.None.WithForeground(heading6) | TextStyle.Bold | TextStyle.Dim,
            StrongStyle = Style.None.WithForeground(strong) | TextStyle.Bold,
            EmphasisStyle = Style.None.WithForeground(strong) | TextStyle.Italic,
            InlineCodeStyle = Style.None.WithForeground(codeFg).WithBackground(codeBg),
            QuotePrefix = "▎ ",
            QuotePrefixStyle = Style.None.WithForeground(quote),
            UnorderedListBullet = "▸",
            NoteAlert = new MarkdownAlertStyle
            {
                BorderStyle = Style.None.WithForeground(note),
                TitleStyle = Style.None.WithForeground(note) | TextStyle.Bold,
                BackgroundStyle = Style.None,
            },
            TipAlert = new MarkdownAlertStyle
            {
                BorderStyle = Style.None.WithForeground(tip),
                TitleStyle = Style.None.WithForeground(tip) | TextStyle.Bold,
                BackgroundStyle = Style.None,
            },
            ImportantAlert = new MarkdownAlertStyle
            {
                BorderStyle = Style.None.WithForeground(important),
                TitleStyle = Style.None.WithForeground(important) | TextStyle.Bold,
                BackgroundStyle = Style.None,
            },
            WarningAlert = new MarkdownAlertStyle
            {
                BorderStyle = Style.None.WithForeground(warning),
                TitleStyle = Style.None.WithForeground(warning) | TextStyle.Bold,
                BackgroundStyle = Style.None,
            },
            CautionAlert = new MarkdownAlertStyle
            {
                BorderStyle = Style.None.WithForeground(caution),
                TitleStyle = Style.None.WithForeground(caution) | TextStyle.Bold,
                BackgroundStyle = Style.None,
            },
        };
    }

    /// <summary>
    /// 应用主题到 Markdown 渲染（主题切换时调用）。
    /// 注意：已转换的 Markup 文本不会自动变色，仅影响之后的转换。
    /// </summary>
    /// <param name="colors">面板配色。</param>
    /// <param name="uiTheme">XenoAtom 控件主题。</param>
    public static void ApplyTheme(PanelColors colors, Theme uiTheme)
    {
        lock (MdConverterLock)
        {
            MdConverter.Theme = uiTheme;
            MdConverter.Style = BuildMarkdownStyle(colors);
        }
    }

    /// <summary>
    /// 将 Markdown 文本转换为 XenoAtom Markup。
    /// 预处理流水线：Emoji keycaps → 表格 → 图片 → 链接 → 任务列表 → ZWSP → 转换 → 后处理。
    /// </summary>
    /// <param name="markdown">原始 Markdown 文本。</param>
    /// <param name="contentWidth">内容区宽度（用于表格和装饰线宽度计算）。</param>
    /// <returns>XenoAtom Markup 字符串。</returns>
    public static string Convert(string markdown, int contentWidth = 0)
    {
        var preprocessed = PreprocessEmojiKeycaps(markdown);
        preprocessed = PreprocessMarkdownTables(preprocessed, contentWidth);
        preprocessed = PreprocessImages(preprocessed);
        preprocessed = PreprocessLinks(preprocessed);
        preprocessed = PreprocessTaskLists(preprocessed);
        preprocessed = InsertZwspAroundEmphasis(preprocessed);
        string markup;
        lock (MdConverterLock)
        {
            markup = MdConverter.Convert(preprocessed);
        }
        markup = markup.Replace("\u200B", string.Empty);
        markup = StripHeadingHashes(markup);
        markup = InsertHeadingDecorations(markup, contentWidth);
        markup = ReplaceThematicBreakLines(markup, contentWidth);
        return markup;
    }

    /// <summary>
    /// Inserts decorative lines below H1 and H2 headings.
    /// H1: double line ═══, H2: single line ───
    /// </summary>
    private static string InsertHeadingDecorations(string markup, int contentWidth)
    {
        if (string.IsNullOrEmpty(markup))
            return markup;

        var sb = new StringBuilder(markup.Length + 128);
        var lines = markup.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            sb.Append(lines[i]);

            // Detect heading lines by looking for heading style markup patterns
            // XenoAtom wraps headings with style tags like [bold underline #fabd2f]...[/]
            // We check if the NEXT line is already a blank line (spacing) and replace it
            // or insert a new decoration line after the heading.
            if (IsHeadingLine(lines[i], out var level))
            {
                // 计算标题纯文本的可见显示宽度，装饰线长度等于标题宽度
                var stripped = StripMarkupTags(lines[i]);
                var headingWidth = VisibleLength(stripped);
                var lineLen = headingWidth > 0 ? headingWidth : Math.Max(stripped.Length, 3);
                // H1/H2 装饰线不超过面板宽度（避免太短的标题线视觉太弱）
                // 但如果面板宽度更小则用面板宽度
                if (contentWidth > 0 && lineLen < contentWidth)
                    lineLen = Math.Max(lineLen, 3);

                switch (level)
                {
                    case 1:
                        sb.Append('\n');
                        sb.Append("[dim]");
                        sb.Append(new string('═', lineLen));
                        sb.Append("[/]");
                        break;
                    case 2:
                        sb.Append('\n');
                        sb.Append("[dim]");
                        sb.Append(new string('·', lineLen));
                        sb.Append("[/]");
                        break;
                }
            }

            if (i < lines.Length - 1)
                sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Strips XenoAtom markup tags from a line, leaving only plain visible text.
    /// Removes [tag] and [/tag] style brackets, but preserves text inside them.
    /// </summary>
    private static string StripMarkupTags(string line)
    {
        if (string.IsNullOrEmpty(line))
            return line;

        var sb = new StringBuilder(line.Length);
        var i = 0;
        while (i < line.Length)
        {
            if (line[i] == '[' && i + 1 < line.Length)
            {
                var closeBracket = line.IndexOf(']', i + 1);
                if (closeBracket >= 0)
                {
                    // Skip the entire [...] tag (including the ']')
                    i = closeBracket + 1;
                    continue;
                }
            }
            sb.Append(line[i]);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Detects if a markup line is a heading and returns its level (1-6).
    /// Looks for the heading style pattern that XenoAtom generates.
    /// </summary>
    private static bool IsHeadingLine(string line, out int level)
    {
        level = 0;
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0)
            return false;

        // XenoAtom heading markup: starts with style tags containing heading-specific colors
        // H1: [bold underline #a65e2e]...  (篝火)
        // H2: [bold #a65e2e]...
        // H3: [bold #d68a48]...  (铜色)
        // H4: [bold #c7b299]...  (月光)
        // H5: [bold #8f8270]...  (暗月光)
        // H6: [bold dim #6e4a36]...  (篝火岩)
        if (trimmed.StartsWith("[bold underline #a65e2e]"))
        {
            level = 1;
            return true;
        }
        if (trimmed.StartsWith("[bold #a65e2e]"))
        {
            level = 2;
            return true;
        }
        if (trimmed.StartsWith("[bold #d68a48]"))
        {
            level = 3;
            return true;
        }
        if (trimmed.StartsWith("[bold #c7b299]"))
        {
            level = 4;
            return true;
        }
        if (trimmed.StartsWith("[bold #8f8270]"))
        {
            level = 5;
            return true;
        }
        if (trimmed.StartsWith("[bold dim #6e4a36]"))
        {
            level = 6;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Replaces XenoAtom's hardcoded 40-char thematic break line (───...)
    /// with a dynamic-width version that fills the content area.
    /// XenoAtom renders `---` as a line of 40 ─ characters wrapped in style tags.
    /// </summary>
    private static string ReplaceThematicBreakLines(string markup, int contentWidth)
    {
        if (string.IsNullOrEmpty(markup) || contentWidth <= 0)
            return markup;

        var sb = new StringBuilder(markup.Length + 64);
        var lines = markup.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Detect XenoAtom's thematic break output: a line consisting only of ─ characters
            // possibly wrapped in style tags like [dim]────...────[/]
            if (IsThematicBreakLine(line, out var stylePrefix, out var styleSuffix))
            {
                sb.Append(stylePrefix);
                sb.Append(new string('─', contentWidth));
                sb.Append(styleSuffix);
            }
            else
            {
                sb.Append(line);
            }

            if (i < lines.Length - 1)
                sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Detects if a markup line is XenoAtom's rendered thematic break.
    /// Returns the surrounding style tags so they can be preserved when replacing.
    /// </summary>
    private static bool IsThematicBreakLine(
        string line,
        out string stylePrefix,
        out string styleSuffix
    )
    {
        stylePrefix = "";
        styleSuffix = "";

        var trimmed = line.Trim();
        if (trimmed.Length == 0)
            return false;

        // Extract optional style prefix like [dim] or [#504945]
        var contentStart = 0;
        var contentEnd = trimmed.Length;

        if (trimmed.StartsWith('['))
        {
            var closeBracket = trimmed.IndexOf(']');
            if (closeBracket >= 0 && closeBracket < trimmed.Length - 1)
            {
                stylePrefix = trimmed[..(closeBracket + 1)];
                contentStart = closeBracket + 1;
            }
        }

        // Check for closing style tag [/]
        if (trimmed.EndsWith("[/]"))
        {
            styleSuffix = "[/]";
            contentEnd = trimmed.Length - 3;
        }

        // The content between style tags should be all ─ characters, at least 5
        if (contentEnd - contentStart < 5)
            return false;

        for (var i = contentStart; i < contentEnd; i++)
        {
            if (trimmed[i] != '─')
                return false;
        }

        return true;
    }

    /// <summary>
    /// Preprocesses task list checkboxes: [x] → ☑, [ ] → ☐.
    /// </summary>
    private static string PreprocessTaskLists(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '[' && i + 1 < text.Length)
            {
                var next = text[i + 1];
                switch (next)
                {
                    case 'x' or 'X' when i + 2 < text.Length && text[i + 2] == ']':
                        sb.Append('☑');
                        i += 2; // skip 'x' and ']'
                        continue;
                    case ' ' when i + 2 < text.Length && text[i + 2] == ']':
                        sb.Append('☐');
                        i += 2; // skip space and ']'
                        continue;
                }
            }
            sb.Append(text[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Preprocesses image syntax ![alt](url) into a card-style box display.
    /// Converts ![alt](url) to a box-drawing frame showing alt text and filename.
    /// </summary>
    private static string PreprocessImages(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var sb = new StringBuilder(text.Length + 128);
        var i = 0;
        while (i < text.Length)
        {
            // Detect ![alt](url)
            if (text[i] == '!' && i + 1 < text.Length && text[i + 1] == '[')
            {
                var altStart = i + 2;
                var altEnd = text.IndexOf(']', altStart);
                if (altEnd < 0)
                {
                    sb.Append(text[i]);
                    i++;
                    continue;
                }

                var urlStart = altEnd + 1;
                if (urlStart >= text.Length || text[urlStart] != '(')
                {
                    sb.Append(text[i]);
                    i++;
                    continue;
                }

                var urlEnd = text.IndexOf(')', urlStart + 1);
                if (urlEnd < 0)
                {
                    sb.Append(text[i]);
                    i++;
                    continue;
                }

                var alt = text.Substring(altStart, altEnd - altStart);
                var url = text.Substring(urlStart + 1, urlEnd - urlStart - 1);

                // Extract filename from URL
                var filename = url;
                var lastSlash = url.LastIndexOf('/');
                if (lastSlash >= 0 && lastSlash + 1 < url.Length)
                    filename = url[(lastSlash + 1)..];
                // Truncate long filenames
                if (filename.Length > 40)
                    filename = filename[..37] + "...";

                // Build box display
                var contentWidth = Math.Max(Math.Max(filename.Length, alt.Length) + 4, 12);
                contentWidth = Math.Min(contentWidth, 60);

                sb.Append("[dim]┌─ 🖼 ─");
                sb.Append(new string('─', contentWidth - 6));
                sb.Append("┐[/]\n");

                sb.Append("[dim]│[/] [bold]");
                sb.Append(EscapeMarkup(filename));
                sb.Append("[/]");
                // Pad to content width
                var namePad = contentWidth - 2 - filename.Length;
                if (namePad > 0)
                    sb.Append(' ', namePad);
                sb.Append("[dim]│[/]\n");

                if (!string.IsNullOrEmpty(alt))
                {
                    sb.Append("[dim]│[/]  ");
                    sb.Append(
                        EscapeMarkup(
                            alt.Length > contentWidth - 4 ? alt[..(contentWidth - 7)] + "..." : alt
                        )
                    );
                    var altPad = contentWidth - 4 - Math.Min(alt.Length, contentWidth - 4);
                    if (altPad > 0)
                        sb.Append(' ', altPad);
                    sb.Append(" [dim]│[/]\n");
                }

                sb.Append("[dim]└");
                sb.Append(new string('─', contentWidth));
                sb.Append("┘[/]");

                i = urlEnd + 1;
                continue;
            }

            sb.Append(text[i]);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Preprocesses link syntax [text](url) to append the URL in dim style after the link text.
    /// Preserves the original link syntax for XenoAtom to apply LinkStyle, but adds the URL as visible text.
    /// </summary>
    private static string PreprocessLinks(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var sb = new StringBuilder(text.Length + 128);
        var i = 0;
        while (i < text.Length)
        {
            // Detect [text](url) — but skip ![image](url) which is handled by PreprocessImages
            if (text[i] == '[' && (i == 0 || text[i - 1] != '!'))
            {
                var textStart = i + 1;
                var depth = 1;
                var j = textStart;
                while (j < text.Length && depth > 0)
                {
                    switch (text[j])
                    {
                        case '[':
                            depth++;
                            break;
                        case ']':
                            depth--;
                            break;
                    }

                    if (depth > 0)
                        j++;
                }
                var textEnd = j;

                if (textEnd < text.Length && textEnd >= 0)
                {
                    var afterBracket = textEnd + 1;
                    if (afterBracket < text.Length && text[afterBracket] == '(')
                    {
                        var urlEnd = text.IndexOf(')', afterBracket + 1);
                        if (urlEnd >= 0)
                        {
                            var linkText = text.Substring(textStart, textEnd - textStart);
                            var url = text.Substring(afterBracket + 1, urlEnd - afterBracket - 1);

                            // Only show URL if it's not the same as the link text (autolinks)
                            // and it's short enough to be useful
                            if (!string.IsNullOrEmpty(url) && url.Length <= 80 && url != linkText)
                            {
                                // Output: [text](url) followed by dim URL
                                // Keep the original markdown so XenoAtom can style the link text
                                sb.Append(text[i..(urlEnd + 1)]);
                                // Add a space and dim URL
                                sb.Append(" [dim](");
                                sb.Append(EscapeMarkup(url.Length > 60 ? url[..57] + "..." : url));
                                sb.Append(")[/]");
                            }
                            else
                            {
                                sb.Append(text[i..(urlEnd + 1)]);
                            }

                            i = urlEnd + 1;
                            continue;
                        }
                    }
                }
            }

            sb.Append(text[i]);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Escapes literal square brackets for XenoAtom markup: <c>[</c> → <c>[[</c>、<c>]</c> → <c>]]</c>。
    /// 转换结果交给 Markup(string) 渲染，故必须用它的转义写法（<c>\[</c> 会原样显示反斜杠）。
    /// </summary>
    private static string EscapeMarkup(string text) => text.Replace("[", "[[").Replace("]", "]]");

    /// <summary>列对齐方式。</summary>
    private enum ColumnAlign
    {
        Left,
        Center,
        Right,
    }

    /// <summary>
    /// 单元格文本分词后的显示单元（用于 CJK 感知的自动换行）。
    /// </summary>
    private readonly record struct CellToken(
        int Start,
        int End,
        int VisibleWidth,
        bool CanBreakAfter
    );

    /// <summary>
    /// Tokenizes cell text into display units for CJK-aware word wrapping.
    /// Each token represents either: a CJK character, an ASCII word, a space,
    /// a formatting span (**bold**, *italic*), or an emoji character.
    /// </summary>
    private static List<CellToken> TokenizeCell(string text)
    {
        var tokens = new List<CellToken>();
        var i = 0;
        while (i < text.Length)
        {
            // Bold **...**
            if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var close = IndexOfUnescaped(text, "**", i + 2);
                var end = close >= 0 ? close + 2 : i + 2;
                var vw = close >= 0 ? VisibleLength(text[i..end]) : 0;
                tokens.Add(new CellToken(i, end, vw, true));
                i = end;
                continue;
            }
            // Italic *...* — only if not followed by another *
            if (text[i] == '*' && (i + 1 >= text.Length || text[i + 1] != '*'))
            {
                var close = IndexOfUnescaped(text, "*", i + 1);
                var end = close >= 0 ? close + 1 : i + 1;
                var vw = close >= 0 ? VisibleLength(text[i..end]) : 0;
                tokens.Add(new CellToken(i, end, vw, true));
                i = end;
                continue;
            }
            // CJK character — break point before AND after
            if (IsCjk(text[i]))
            {
                var w = CharDisplayWidth(text[i]);
                tokens.Add(new CellToken(i, i + 1, w, true));
                i++;
                continue;
            }
            // Supplementary plane emoji (surrogate pair)
            // Also handles ZWJ sequences: emoji (VS16? U+200D emoji+VS16?)*
            if (
                char.IsHighSurrogate(text[i])
                && i + 1 < text.Length
                && char.IsLowSurrogate(text[i + 1])
            )
            {
                var seqEnd = i + 2;
                // Skip optional variation selector after base emoji
                if (seqEnd < text.Length && text[seqEnd] == '\uFE0F')
                    seqEnd++;
                // Scan for ZWJ sequence continuation
                while (seqEnd < text.Length && text[seqEnd] == '\u200D')
                {
                    seqEnd++; // skip ZWJ
                    if (
                        seqEnd < text.Length
                        && char.IsHighSurrogate(text[seqEnd])
                        && seqEnd + 1 < text.Length
                        && char.IsLowSurrogate(text[seqEnd + 1])
                    )
                        seqEnd += 2; // surrogate pair
                    else if (seqEnd < text.Length && CharDisplayWidth(text[seqEnd]) >= 1)
                        seqEnd++; // BMP symbol (width 1 or 2)
                    else
                        break;
                    // Skip optional variation selector U+FE0F
                    if (seqEnd < text.Length && text[seqEnd] == '\uFE0F')
                        seqEnd++;
                }
                // Compute width per-character (matches wcwidth/terminal)
                var seqWidth = 0;
                for (var si = i; si < seqEnd; si++)
                {
                    if (
                        char.IsHighSurrogate(text[si])
                        && si + 1 < seqEnd
                        && char.IsLowSurrogate(text[si + 1])
                    )
                    {
                        seqWidth += 2;
                        si++; // skip low surrogate
                    }
                    else
                    {
                        seqWidth += CharDisplayWidth(text[si]);
                    }
                }
                tokens.Add(new CellToken(i, seqEnd, seqWidth, true));
                i = seqEnd;
                continue;
            }
            // BMP emoji (covered by CharDisplayWidth ranges)
            if (CharDisplayWidth(text[i]) == 2)
            {
                tokens.Add(new CellToken(i, i + 1, 2, true));
                i++;
                continue;
            }
            // Zero-width characters (variation selectors, joiners, combining marks)
            // Attach to previous token so they're preserved in the output
            if (CharDisplayWidth(text[i]) == 0)
            {
                if (tokens.Count > 0)
                {
                    var prev = tokens[^1];
                    tokens[^1] = prev with { End = i + 1 };
                }
                i++;
                continue;
            }
            // Space — break point
            if (text[i] == ' ')
            {
                tokens.Add(new CellToken(i, i + 1, 1, true));
                i++;
                continue;
            }
            // ASCII word — greedily consume letters/digits/punctuation
            var wordStart = i;
            while (
                i < text.Length
                && !IsCjk(text[i])
                && CharDisplayWidth(text[i]) == 1
                && text[i] != ' '
                && text[i] != '*'
                && !(
                    char.IsHighSurrogate(text[i])
                    && i + 1 < text.Length
                    && char.IsLowSurrogate(text[i + 1])
                )
            )
            {
                i++;
            }
            if (i > wordStart)
            {
                var wordLen = i - wordStart;
                tokens.Add(new CellToken(wordStart, i, wordLen, true));
            }
        }
        return tokens;
    }

    /// <summary>
    /// Finds the next unescaped occurrence of <paramref name="needle"/> in <paramref name="text"/>
    /// starting at <paramref name="start"/>. Returns the index or -1 if not found.
    /// </summary>
    private static int IndexOfUnescaped(string text, string needle, int start)
    {
        var i = start;
        while (i <= text.Length - needle.Length)
        {
            // Skip escaped characters
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i += 2;
                continue;
            }
            if (text.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
            i++;
        }
        return -1;
    }

    /// <summary>
    /// Wraps cell text to fit within <paramref name="targetWidth"/> display columns,
    /// producing a list of text lines. CJK characters break anywhere; ASCII breaks at
    /// token boundaries. Formatting spans (**bold**, *italic*) are preserved intact.
    /// </summary>
    private static List<string> WrapCell(string text, int targetWidth)
    {
        if (targetWidth <= 0)
            return [text];

        var tokens = TokenizeCell(text);
        if (tokens.Count == 0)
            return [""];

        var lines = new List<string>();
        var lineTokens = new List<CellToken>();
        var lineWidth = 0;

        foreach (
            var tok in tokens.Where(tok =>
                lineTokens.Count != 0 || !text.AsSpan(tok.Start, tok.End - tok.Start).IsWhiteSpace()
            )
        )
        {
            if (lineWidth + tok.VisibleWidth <= targetWidth)
            {
                lineTokens.Add(tok);
                lineWidth += tok.VisibleWidth;
            }
            else
            {
                // Flush current line if not empty
                if (lineTokens.Count > 0)
                {
                    lines.Add(JoinTokens(text, lineTokens).TrimEnd());
                    lineTokens.Clear();
                    lineWidth = 0;
                }

                // Skip leading spaces on new line
                if (text.AsSpan(tok.Start, tok.End - tok.Start).IsWhiteSpace())
                    continue;

                var raw = text[tok.Start..tok.End];
                var isPlainText = tok.VisibleWidth == raw.Length;
                if (isPlainText && tok.VisibleWidth > targetWidth)
                {
                    for (var ci = 0; ci < raw.Length; )
                    {
                        var cw = CharDisplayWidth(raw[ci]);
                        if (lineWidth + cw > targetWidth && lineWidth > 0)
                        {
                            lines.Add(JoinTokens(text, lineTokens).TrimEnd());
                            lineTokens.Clear();
                            lineWidth = 0;
                        }
                        lineTokens.Add(new CellToken(tok.Start + ci, tok.Start + ci + 1, cw, true));
                        lineWidth += cw;
                        ci++;
                    }
                }
                else
                {
                    lineTokens.Add(tok);
                    lineWidth = tok.VisibleWidth;
                }
            }
        }
        if (lineTokens.Count > 0)
            lines.Add(JoinTokens(text, lineTokens).TrimEnd());

        return FixCjkPunctuation(lines, targetWidth);
    }

    // CJK punctuation that should NOT start a line (attaches to preceding char)
    private static bool IsCjkClosingPunct(char c) =>
        c
            is '\u3001'
                or '\u3002'
                or '\uFF0C'
                or '\uFF1A'
                or '\uFF1B'
                or '\uFF01'
                or '\uFF1F'
                or '\uFF09'
                or '\u3011'
                or '\u3009'
                or '\u300B'
                or '\u300D'
                or '\u300F'
                or '\u3015'
                or '\u3017'
                or '\uFF0E';

    // CJK punctuation that should NOT end a line (attaches to following char)
    private static bool IsCjkOpeningPunct(char c) =>
        c is '\uFF08' or '\u3010' or '\u300A' or '\u300C' or '\u300E' or '\u3014' or '\u3016';

    /// <summary>
    /// Fixes line-break positions for CJK punctuation.
    /// Closing punctuation (，。、）》 etc.) should not start a line.
    /// Opening punctuation (（【《 etc.) should not end a line.
    /// </summary>
    private static List<string> FixCjkPunctuation(List<string> lines, int targetWidth)
    {
        if (lines.Count <= 1)
            return lines;

        // Phase 1: pull closing punct from next line start to current line end
        // Only if it won't make the current line exceed targetWidth
        for (var i = 0; i < lines.Count - 1; i++)
        {
            while (lines[i + 1].Length > 0 && IsCjkClosingPunct(lines[i + 1][0]))
            {
                var punctChar = lines[i + 1][0];
                var punctWidth = CharDisplayWidth(punctChar);
                if (VisibleLength(lines[i]) + punctWidth > targetWidth)
                    break; // would overflow — leave punct on next line
                lines[i] += punctChar;
                lines[i + 1] = lines[i + 1].Length > 1 ? lines[i + 1][1..] : "";
            }
        }

        // Phase 2: pull opening punct from current line start to previous line end
        // Only if it won't make the previous line exceed targetWidth
        for (var i = 1; i < lines.Count; i++)
        {
            while (lines[i].Length > 0 && IsCjkOpeningPunct(lines[i][0]))
            {
                var prev = lines[i - 1];
                if (prev.Length > 0 && IsCjkOpeningPunct(prev[^1]))
                    break;
                var punctChar = lines[i][0];
                var punctWidth = CharDisplayWidth(punctChar);
                if (VisibleLength(prev) + punctWidth > targetWidth)
                    break; // would overflow — leave punct on current line
                lines[i - 1] += punctChar;
                lines[i] = lines[i].Length > 1 ? lines[i][1..] : "";
                if (lines[i].Length == 0)
                    break;
            }
        }

        // Phase 3: push opening punct from line end to next line start
        // Only if it won't make the next line exceed targetWidth
        for (var i = 0; i < lines.Count - 1; i++)
        {
            while (lines[i].Length > 0 && IsCjkOpeningPunct(lines[i][^1]))
            {
                var nextLine = lines[i + 1];
                var punctChar = lines[i][^1];
                var punctWidth = CharDisplayWidth(punctChar);
                if (VisibleLength(nextLine) + punctWidth > targetWidth)
                    break; // would overflow next line — leave punct at current line end
                lines[i + 1] = punctChar + nextLine;
                lines[i] = lines[i][..^1];
            }
        }

        return lines.Where(l => l.Length > 0).ToList();
    }

    /// <summary>
    /// Joins consecutive tokens back into a raw text substring.
    /// </summary>
    private static string JoinTokens(string text, List<CellToken> tokens)
    {
        if (tokens.Count == 0)
            return "";
        var start = tokens[0].Start;
        var end = tokens[^1].End;
        return text[start..end];
    }

    /// <summary>最小列宽（网格模式）。</summary>
    private const int MinColumnWidth = 4;

    /// <summary>网格模式最小列宽（低于此值退回列表模式）。</summary>
    private const int MinColumnWidthForGrid = 10;

    /// <summary>
    /// Computes column widths to fit within <paramref name="contentWidth"/>.
    /// Returns target widths and a flag indicating whether list fallback is needed.
    /// Table width formula: Σ(colWidths) + 3*N + 1 (N = column count).
    /// </summary>
    private static bool ComputeTargetWidths(
        int[] naturalWidths,
        int colCount,
        int contentWidth,
        out int[] targetWidths
    )
    {
        targetWidths = new int[colCount];
        var overhead = 3 * colCount + 1;
        var available = contentWidth - overhead;

        // Can everything fit naturally?
        var totalNatural = naturalWidths.Sum();
        if (totalNatural <= available)
        {
            Array.Copy(naturalWidths, targetWidths, colCount);
            return false; // no overflow
        }

        // Check minimum viability for grid mode
        var minTotal = colCount * MinColumnWidthForGrid;
        if (minTotal > available)
        {
            Array.Copy(naturalWidths, targetWidths, colCount);
            return true; // need list fallback
        }

        // Water-fill allocation
        // Per-column cap: no column exceeds 40% of available, and at least equal-split
        const double maxColShare = 0.40;
        var maxPerCol = Math.Max((int)(available * maxColShare), available / colCount);
        var allocWidths = new int[colCount];
        for (var c = 0; c < colCount; c++)
            allocWidths[c] = Math.Min(naturalWidths[c], maxPerCol);

        // Phase 1: give each column its minimum
        var remaining = available;
        for (var c = 0; c < colCount; c++)
        {
            targetWidths[c] = Math.Min(MinColumnWidth, allocWidths[c]);
            remaining -= targetWidths[c];
        }
        // Phase 2: distribute remaining proportionally, capped at allocWidths
        var needs = new int[colCount];
        var totalNeed = 0;
        for (var c = 0; c < colCount; c++)
        {
            needs[c] = Math.Max(0, allocWidths[c] - targetWidths[c]);
            totalNeed += needs[c];
        }

        if (totalNeed > 0 && remaining > 0)
        {
            // Sort column indices by allocWidths descending (wider columns get priority)
            var indices = Enumerable
                .Range(0, colCount)
                .OrderByDescending(c => allocWidths[c])
                .ToArray();

            foreach (var c in indices)
            {
                if (remaining <= 0)
                    break;
                var share =
                    totalNeed > 0 ? (int)Math.Round((double)needs[c] / totalNeed * remaining) : 0;
                share = Math.Min(share, needs[c]);
                share = Math.Min(share, remaining);
                targetWidths[c] += share;
                remaining -= share;
                totalNeed -= needs[c];
            }

            // Distribute any leftover evenly
            if (remaining > 0)
            {
                for (var c = 0; c < colCount && remaining > 0; c++)
                {
                    var room = allocWidths[c] - targetWidths[c];
                    if (room > 0)
                    {
                        var add = Math.Min(1, remaining);
                        targetWidths[c] += add;
                        remaining -= add;
                    }
                }
            }
        }

        // Ensure no column exceeds its alloc width
        for (var c = 0; c < colCount; c++)
            targetWidths[c] = Math.Min(targetWidths[c], allocWidths[c]);
        return false; // grid mode ok
    }

    /// <summary>
    /// Renders a multi-line table where cells are word-wrapped to fit within target column widths.
    /// </summary>
    private static void RenderMultiLineTable(
        StringBuilder sb,
        List<string> headerCells,
        List<List<string>> dataRows,
        int[] colWidths,
        List<ColumnAlign> alignments
    )
    {
        var colCount = colWidths.Length;

        // Wrap header cells
        var headerLines = new List<string>[colCount];
        for (var c = 0; c < colCount; c++)
            headerLines[c] = WrapCell(headerCells[c], colWidths[c]);

        // Wrap data cells
        var dataCellLines = new List<string>[dataRows.Count][];
        for (var r = 0; r < dataRows.Count; r++)
        {
            dataCellLines[r] = new List<string>[colCount];
            for (var c = 0; c < colCount; c++)
                dataCellLines[r][c] = WrapCell(
                    c < dataRows[r].Count ? dataRows[r][c] : "",
                    colWidths[c]
                );
        }

        // Top border
        sb.Append(BuildBorder('┌', '┬', '┐', '─', colWidths));
        sb.Append("  \n");

        // Header row
        var headerHeight = headerLines.Max(l => l.Count);
        RenderMultiLineRow(sb, '│', headerLines, colWidths, alignments, headerHeight);

        // Header separator
        sb.Append(BuildBorder('├', '┼', '┤', '─', colWidths));
        sb.Append("  \n");

        // Data rows
        for (var r = 0; r < dataRows.Count; r++)
        {
            var rowHeight = dataCellLines[r].Max(l => l.Count);
            RenderMultiLineRow(sb, '│', dataCellLines[r], colWidths, alignments, rowHeight);

            if (r < dataRows.Count - 1)
            {
                sb.Append(BuildBorder('├', '┼', '┤', '─', colWidths));
                sb.Append("  \n");
            }
        }

        // Bottom border
        sb.Append(BuildBorder('└', '┴', '┘', '─', colWidths));
    }

    /// <summary>
    /// Renders one layer (all columns at a given line index) of a multi-line row.
    /// </summary>
    private static void RenderMultiLineRow(
        StringBuilder sb,
        char vertical,
        List<string>[] cellLines,
        int[] colWidths,
        List<ColumnAlign> alignments,
        int rowHeight
    )
    {
        for (var line = 0; line < rowHeight; line++)
        {
            sb.Append(vertical);
            for (var c = 0; c < colWidths.Length; c++)
            {
                var text = line < cellLines[c].Count ? cellLines[c][line] : "";
                var align = c < alignments.Count ? alignments[c] : ColumnAlign.Left;
                sb.Append(' ');
                sb.Append(PadCell(text, colWidths[c], align));
                sb.Append(' ');
                sb.Append(vertical);
            }
            sb.Append("  \n");
        }
    }

    /// <summary>
    /// Renders a table as a list of key-value records when the grid won't fit.
    /// First column becomes a bold heading; remaining columns rendered as "**header**: value".
    /// Long values are word-wrapped to fit within contentWidth.
    /// </summary>
    private static void RenderTableAsList(
        StringBuilder sb,
        List<string> headerCells,
        List<List<string>> dataRows,
        int contentWidth
    )
    {
        for (var r = 0; r < dataRows.Count; r++)
        {
            if (r > 0)
                sb.Append("\n\n");

            // First column as heading
            var key = dataRows[r].Count > 0 ? dataRows[r][0] : "";
            sb.Append("**");
            sb.Append(key);
            sb.Append("**  \n");

            // Remaining columns as labeled fields
            for (var c = 1; c < headerCells.Count; c++)
            {
                var label = c < headerCells.Count ? headerCells[c] : "";
                var value = c < dataRows[r].Count ? dataRows[r][c] : "";
                var prefix = $"**{label}**: ";
                var prefixVlen = VisibleLength(prefix);
                var valueTarget = contentWidth > 0 ? Math.Max(contentWidth - prefixVlen, 4) : 0;

                if (valueTarget > 0 && VisibleLength(value) > valueTarget)
                {
                    // Wrap long values — use markdown hard line break (two trailing spaces + newline)
                    // so MdConverter.Convert() preserves the line breaks
                    var wrappedLines = WrapCell(value, valueTarget);
                    for (var li = 0; li < wrappedLines.Count; li++)
                    {
                        sb.Append(li == 0 ? prefix : new string(' ', prefixVlen));
                        sb.Append(wrappedLines[li]);
                        // Hard line break: two trailing spaces + newline
                        sb.Append("  \n");
                    }
                }
                else
                {
                    sb.Append(prefix);
                    sb.Append(value);
                    // Hard line break to prevent MdConverter from merging lines
                    sb.Append("  \n");
                }
            }
        }
    }

    private static string PreprocessMarkdownTables(string text, int contentWidth = 0)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var lines = text.Split('\n');
        var sb = new StringBuilder(text.Length + 256);
        var i = 0;

        while (i < lines.Length)
        {
            var currentLine = lines[i].TrimEnd('\r');
            if (!IsTableRow(currentLine))
            {
                sb.Append(lines[i]);
                if (i < lines.Length - 1)
                    sb.Append('\n');
                i++;
                continue;
            }

            var tableStart = i;
            while (i < lines.Length && IsTableRow(lines[i].TrimEnd('\r')))
                i++;

            var tableLines = lines[tableStart..i];

            if (
                tableLines.Length < 2
                || !TryParseSeparatorRow(tableLines[1].TrimEnd('\r'), out var alignments)
            )
            {
                for (var j = 0; j < tableLines.Length; j++)
                {
                    sb.Append(tableLines[j]);
                    if (tableStart + j < lines.Length - 1)
                        sb.Append('\n');
                }
                continue;
            }

            var headerCells = ParseRow(tableLines[0].TrimEnd('\r'));
            var colCount = headerCells.Count;

            // Extend alignments if header has more columns
            while (alignments.Count < colCount)
                alignments.Add(ColumnAlign.Left);

            var dataRows = new List<List<string>>();
            for (var j = 2; j < tableLines.Length; j++)
            {
                var cells = ParseRow(tableLines[j].TrimEnd('\r'));
                while (cells.Count < colCount)
                    cells.Add("");
                dataRows.Add(cells);
            }

            var colWidths = new int[colCount];
            for (var c = 0; c < colCount; c++)
            {
                colWidths[c] = VisibleLength(headerCells[c]);
                foreach (var row in dataRows)
                    colWidths[c] = Math.Max(colWidths[c], VisibleLength(row[c]));
                if (colWidths[c] < 1)
                    colWidths[c] = 1;
            }

            // Check if table fits within content width
            var tableWidth = colWidths.Sum() + 3 * colCount + 1;
            if (contentWidth > 0 && tableWidth > contentWidth)
            {
                var needListFallback = ComputeTargetWidths(
                    colWidths,
                    colCount,
                    contentWidth,
                    out var targetWidths
                );

                if (needListFallback)
                {
                    RenderTableAsList(sb, headerCells, dataRows, contentWidth);
                }
                else
                {
                    RenderMultiLineTable(sb, headerCells, dataRows, targetWidths, alignments);
                }
            }
            else
            {
                sb.Append(BuildBorder('┌', '┬', '┐', '─', colWidths));
                sb.Append("  \n");
                sb.Append(BuildRow('│', headerCells, colWidths, alignments));
                sb.Append("  \n");
                sb.Append(BuildBorder('├', '┼', '┤', '─', colWidths));
                sb.Append("  \n");

                for (var r = 0; r < dataRows.Count; r++)
                {
                    sb.Append(BuildRow('│', dataRows[r], colWidths, alignments));
                    sb.Append("  \n");
                    if (r < dataRows.Count - 1)
                    {
                        sb.Append(BuildBorder('├', '┼', '┤', '─', colWidths));
                        sb.Append("  \n");
                    }
                }

                sb.Append(BuildBorder('└', '┴', '┘', '─', colWidths));
            }

            if (i < lines.Length)
                sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>判断一行是否为 Markdown 表格行（| 开头和结尾）。</summary>
    private static bool IsTableRow(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length > 1 && trimmed[0] == '|' && trimmed[^1] == '|';
    }

    /// <summary>
    /// Tries to parse a separator row and extract column alignment info.
    /// Returns true if the line is a valid separator row.
    /// :--- = left, ---: = right, :---: = center, --- = left (default)
    /// </summary>
    private static bool TryParseSeparatorRow(string line, out List<ColumnAlign> alignments)
    {
        alignments = [];
        var trimmed = line.Trim();
        if (!IsTableRow(trimmed))
            return false;
        var inner = trimmed[1..^1];
        var hasDash = false;
        foreach (var segment in inner.Split('|'))
        {
            var s = segment.Trim();
            if (s.Length == 0)
                continue;
            if (s.Any(c => c != '-' && c != ':'))
            {
                return false;
            }
            if (s.Contains('-'))
                hasDash = true;

            // Determine alignment
            var leftColon = s.StartsWith(':');
            var rightColon = s.EndsWith(':');
            if (leftColon && rightColon)
                alignments.Add(ColumnAlign.Center);
            else if (rightColon)
                alignments.Add(ColumnAlign.Right);
            else
                alignments.Add(ColumnAlign.Left);
        }
        return hasDash;
    }

    /// <summary>解析表格数据行，返回单元格内容列表。</summary>
    private static List<string> ParseRow(string line)
    {
        var trimmed = line.Trim();
        var inner = trimmed[1..^1];
        return inner.Split('|').Select(cell => cell.Trim()).ToList();
    }

    /// <summary>
    /// 计算文本的可见显示宽度（排除 Markdown 格式标记）。
    /// 处理 **bold**、*italic*、__bold__、_italic_、~~strikethrough~~、
    /// ![image](url)、[link](url)、`code`、转义字符、代理对和 ZWJ 序列。
    /// </summary>
    private static int VisibleLength(string text)
    {
        var len = 0;
        var i = 0;
        while (i < text.Length)
        {
            // ** bold (asterisk)
            if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                continue;
            }

            switch (text[i])
            {
                // * italic (asterisk)
                case '*':
                    i += 1;
                    continue;
                // __ bold (underscore)
                case '_' when i + 1 < text.Length && text[i + 1] == '_':
                    i += 2;
                    continue;
            }

            // _ italic (underscore) — only if surrounded by non-word chars
            if (text[i] == '_' && IsUnderscoreDelimiter(text, i))
            {
                i += 1;
                continue;
            }
            // ~~ strikethrough
            if (text[i] == '~' && i + 1 < text.Length && text[i + 1] == '~')
            {
                i += 2;
                continue;
            }
            // ![alt](url) — image: only alt text is visible
            if (text[i] == '!' && i + 1 < text.Length && text[i + 1] == '[')
            {
                i += 2; // skip ![
                var altStart = i;
                while (i < text.Length && text[i] != ']')
                    i++;
                // count alt text characters
                for (var a = altStart; a < i; a++)
                    len += CharDisplayWidth(text[a]);
                if (i < text.Length)
                    i++; // skip ]
                if (i < text.Length && text[i] == '(')
                {
                    i++; // skip (
                    while (i < text.Length && text[i] != ')')
                        i++; // skip url
                    if (i < text.Length)
                        i++; // skip )
                }
                continue;
            }

            switch (text[i])
            {
                // [text](url) — link: only text is visible
                case '[':
                {
                    i++; // skip [
                    var linkTextStart = i;
                    // find matching ] — handle nested brackets
                    var depth = 1;
                    while (i < text.Length && depth > 0)
                    {
                        switch (text[i])
                        {
                            case '[':
                                depth++;
                                break;
                            case ']':
                                depth--;
                                break;
                        }

                        if (depth > 0)
                            i++;
                    }
                    // count link text characters
                    for (var a = linkTextStart; a < i; a++)
                        len += CharDisplayWidth(text[a]);
                    if (i < text.Length)
                        i++; // skip ]
                    // skip (url) or [ref]
                    if (i < text.Length && text[i] == '(')
                    {
                        i++; // skip (
                        while (i < text.Length && text[i] != ')')
                            i++;
                        if (i < text.Length)
                            i++; // skip )
                    }
                    else if (i < text.Length && text[i] == '[')
                    {
                        i++; // skip [
                        while (i < text.Length && text[i] != ']')
                            i++;
                        if (i < text.Length)
                            i++; // skip ]
                    }
                    continue;
                }
                // `inline code`
                case '`':
                {
                    i += 1;
                    while (i < text.Length && text[i] != '`')
                    {
                        len += CharDisplayWidth(text[i]);
                        i++;
                    }
                    if (i < text.Length)
                        i++;
                    continue;
                }
                // \ escaped character — skip backslash, count the escaped char
                case '\\' when i + 1 < text.Length:
                    i += 1; // skip backslash
                    len += CharDisplayWidth(text[i]);
                    i++;
                    continue;
            }

            // Surrogate pair (supplementary plane emoji)
            if (
                char.IsHighSurrogate(text[i])
                && i + 1 < text.Length
                && char.IsLowSurrogate(text[i + 1])
            )
            {
                // Scan through any ZWJ sequence to skip it as a unit.
                // Count width per-character (matching wcwidth/terminal behavior)
                // rather than collapsing the whole sequence to width 2 —
                // because XenoAtom's rendering uses per-rune wcwidth which
                // counts ♀ as width 1 inside a ZWJ sequence.
                var seqEnd = i + 2;
                // Skip optional variation selector after base emoji
                if (seqEnd < text.Length && text[seqEnd] == '\uFE0F')
                    seqEnd++;
                while (seqEnd < text.Length && text[seqEnd] == '\u200D')
                {
                    // Skip ZWJ
                    seqEnd++;
                    // Skip the next emoji unit (surrogate pair or BMP symbol)
                    if (
                        seqEnd < text.Length
                        && char.IsHighSurrogate(text[seqEnd])
                        && seqEnd + 1 < text.Length
                        && char.IsLowSurrogate(text[seqEnd + 1])
                    )
                    {
                        seqEnd += 2; // surrogate pair
                    }
                    else if (seqEnd < text.Length && CharDisplayWidth(text[seqEnd]) >= 1)
                    {
                        seqEnd++; // BMP symbol (width 1 or 2)
                    }
                    else
                    {
                        break;
                    }
                    // Skip optional variation selector U+FE0F
                    if (seqEnd < text.Length && text[seqEnd] == '\uFE0F')
                        seqEnd++;
                }
                // Count width per-character through the sequence (matches wcwidth)
                for (var si = i; si < seqEnd; si++)
                {
                    if (
                        char.IsHighSurrogate(text[si])
                        && si + 1 < seqEnd
                        && char.IsLowSurrogate(text[si + 1])
                    )
                    {
                        len += 2; // supplementary plane emoji
                        si++; // skip low surrogate
                    }
                    else
                    {
                        len += CharDisplayWidth(text[si]);
                    }
                }
                i = seqEnd;
                continue;
            }
            len += CharDisplayWidth(text[i]);
            i++;
        }
        return len;
    }

    /// <summary>
    /// Determines if an underscore at position i acts as an emphasis delimiter.
    /// Underscores surrounded by alphanumeric chars are not delimiters (e.g. snake_case).
    /// </summary>
    private static bool IsUnderscoreDelimiter(string text, int i)
    {
        var prevChar = i > 0 ? text[i - 1] : '\0';
        var nextChar = i + 1 < text.Length ? text[i + 1] : '\0';
        // If both adjacent chars are word chars, this underscore is not a delimiter
        return !char.IsLetterOrDigit(prevChar) || !char.IsLetterOrDigit(nextChar);
    }

    /// <summary>
    /// 计算单个字符的终端显示宽度。
    /// CJK 字符宽度 2，零宽字符宽度 0，其余 ASCII 宽度 1。
    /// 包含完整的 BMP emoji 宽度映射。
    /// </summary>
    private static int CharDisplayWidth(char c)
    {
        return c switch
        {
            // Zero-width characters
            // Variation selector
            // ZWJ
            // ZWNJ
            // Word joiner
            // Mongolian vowel separator
            // Combining marks (Unicode Mn category — common ones)
            // Combining diacritical marks
            // Combining diacritical marks extended
            // Combining diacritical marks supplement
            // Combining diacritical marks for symbols
            // Combining half marks
            '\u200B'
            or '\uFE0F'
            or '\u200D'
            or '\u200C'
            or '\u2060'
            or '\u180E'
            or >= '\u0300'
            and <= '\u036F'
            or >= '\u1AB0'
            and <= '\u1AFF'
            or >= '\u1DC0'
            and <= '\u1DFF'
            or >= '\u20D0'
            and <= '\u20FF'
            or >= '\uFE20'
            and <= '\uFE2F' =>
                0 // ZWSP
            ,
            // CJK ranges (width 2)
            // ☀☁☂☃☄
            // ☎
            // ☔☕
            // ☘
            // ☝
            // ☠
            // ☢☣
            // ☦
            // ☪
            // ☮☯
            // ☸☹☺
            >= '\u4E00'
            and <= '\u9FFF'
            or >= '\u3400'
            and <= '\u4DBF'
            or >= '\uF900'
            and <= '\uFAFF'
            or >= '\u3000'
            and <= '\u303F'
            or >= '\uFF01'
            and <= '\uFF60'
            or >= '\u3040'
            and <= '\u309F'
            or >= '\u30A0'
            and <= '\u30FF'
            or >= '\uAC00'
            and <= '\uD7AF'
            or >= '\u2E80'
            and <= '\u2FDF'
            or >= '\u2600'
            and <= '\u2604'
            or '\u260E'
            or >= '\u2614'
            and <= '\u2615'
            or '\u2618'
            or '\u261D'
            or '\u2620'
            or >= '\u2622'
            and <= '\u2623'
            or '\u2626'
            or '\u262A'
            or >= '\u262E'
            and <= '\u262F'
            or >= '\u2638'
            and <= '\u263A' => 2,
            // ♀ U+2640 and ♂ U+2642 are width 1 in wcwidth (text presentation default).
            // They only appear as width 2 when part of a ZWJ emoji sequence
            // (e.g. 🕵️‍♀️), where the terminal applies glyph substitution.
            // Our VisibleLength handles ZWJ sequences separately.
            // ♂ (text default, wcwidth=1)
            '\u2640' or '\u2642' =>
                1 // ♀ (text default, wcwidth=1)
            ,
            // ♠
            // ♣
            // ♥♦
            // ♨
            // ♻
            // ♾♿
            // ⚒-⚗
            // ⚙
            // ⚛⚜
            // ⚠⚡
            // ⚧
            // ⚪⚫
            // ⚰⚱
            // ⚽⚾
            // ⛄⛅
            // ⛈
            // ⛎
            // ⛏
            // ⛑
            // ⛓⛔
            // ⛩⛪
            // ⛰-⛵
            // ⛷-⛺
            // ⛽
            // Dingbats emoji
            // ✂
            // ✅
            // ✈-✍
            // ✏
            // ✒
            // ✔
            // ✖
            // ✝
            // ✡
            // ✨
            // ✳✴
            // ❄
            // ❇
            // ❌
            // ❎
            // ❓❔❕
            // ❗
            // ❣❤
            // ➕➖➗
            // ➡
            // ➰
            // ➿
            // Additional BMP emoji (watches ⌚⌛, media controls ⏩-⏳, squares ◻◼◽◾, arrows ⤴⤵)
            // Miscellaneous Symbols and Arrows — emoji only: arrows, squares, star, circle
            // ⬅⬆⬇
            // ⬛⬜
            // ⭐
            // ⭕
            '\u265F'
            or '\u2660'
            or '\u2663'
            or >= '\u2665'
            and <= '\u2666'
            or '\u2668'
            or '\u267B'
            or >= '\u267E'
            and <= '\u267F'
            or >= '\u2692'
            and <= '\u2697'
            or '\u2699'
            or >= '\u269B'
            and <= '\u269C'
            or >= '\u26A0'
            and <= '\u26A1'
            or '\u26A7'
            or >= '\u26AA'
            and <= '\u26AB'
            or >= '\u26B0'
            and <= '\u26B1'
            or >= '\u26BD'
            and <= '\u26BE'
            or >= '\u26C4'
            and <= '\u26C5'
            or '\u26C8'
            or '\u26CE'
            or '\u26CF'
            or '\u26D1'
            or >= '\u26D3'
            and <= '\u26D4'
            or >= '\u26E9'
            and <= '\u26EA'
            or >= '\u26F0'
            and <= '\u26F5'
            or >= '\u26F7'
            and <= '\u26FA'
            or '\u26FD'
            or '\u2702'
            or '\u2705'
            or >= '\u2708'
            and <= '\u270D'
            or '\u270F'
            or '\u2712'
            or '\u2714'
            or '\u2716'
            or '\u271D'
            or '\u2721'
            or '\u2728'
            or >= '\u2733'
            and <= '\u2734'
            or '\u2744'
            or '\u2747'
            or '\u274C'
            or '\u274E'
            or >= '\u2753'
            and <= '\u2755'
            or '\u2757'
            or >= '\u2763'
            and <= '\u2764'
            or >= '\u2795'
            and <= '\u2797'
            or '\u27A1'
            or '\u27B0'
            or '\u27BF'
            or >= '\u231A'
            and <= '\u231B'
            or >= '\u23E9'
            and <= '\u23F3'
            or >= '\u25FB'
            and <= '\u25FE'
            or >= '\u2934'
            and <= '\u2935'
            or >= '\u2B05'
            and <= '\u2B07'
            or >= '\u2B1B'
            and <= '\u2B1C'
            or '\u2B50'
            or '\u2B55' =>
                2 // ♟
            ,
            _ => 1,
        };
    }

    /// <summary>构建表格边框行（┌─┬─┐ 等）。</summary>
    private static string BuildBorder(char left, char mid, char right, char fill, int[] colWidths)
    {
        var sb = new StringBuilder();
        sb.Append(left);
        for (var c = 0; c < colWidths.Length; c++)
        {
            sb.Append(new string(fill, colWidths[c] + 2));
            if (c < colWidths.Length - 1)
                sb.Append(mid);
        }
        sb.Append(right);
        return sb.ToString();
    }

    /// <summary>按指定对齐方式填充单元格文本到目标显示宽度。</summary>
    private static string PadCell(string text, int targetDisplayWidth, ColumnAlign align)
    {
        var displayWidth = VisibleLength(text);
        if (displayWidth > targetDisplayWidth)
        {
            // Truncate to fit — safety net for any edge case where WrapCell still overflows
            var truncated = TruncateToDisplayWidth(text, targetDisplayWidth);
            displayWidth = VisibleLength(truncated);
            if (displayWidth >= targetDisplayWidth)
                return truncated;
            text = truncated;
        }
        var padding = targetDisplayWidth - displayWidth;
        return align switch
        {
            ColumnAlign.Right => new string(' ', padding) + text,
            ColumnAlign.Center => new string(' ', padding / 2)
                + text
                + new string(' ', padding - padding / 2),
            _ => text + new string(' ', padding), // Left (default)
        };
    }

    /// <summary>
    /// Truncates text so that its visible display width does not exceed <paramref name="maxWidth"/>.
    /// Preserves formatting spans (**, *) by counting them as zero-width.
    /// </summary>
    private static string TruncateToDisplayWidth(string text, int maxWidth)
    {
        var len = 0;
        var i = 0;
        while (i < text.Length)
        {
            // ** bold
            if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                continue;
            }
            // * italic
            if (text[i] == '*')
            {
                i += 1;
                continue;
            }
            // Surrogate pair + ZWJ sequence
            if (
                char.IsHighSurrogate(text[i])
                && i + 1 < text.Length
                && char.IsLowSurrogate(text[i + 1])
            )
            {
                // Scan the whole ZWJ sequence first to compute its width
                var seqEnd = i + 2;
                if (seqEnd < text.Length && text[seqEnd] == '\uFE0F')
                    seqEnd++;
                while (seqEnd < text.Length && text[seqEnd] == '\u200D')
                {
                    seqEnd++;
                    if (
                        seqEnd < text.Length
                        && char.IsHighSurrogate(text[seqEnd])
                        && seqEnd + 1 < text.Length
                        && char.IsLowSurrogate(text[seqEnd + 1])
                    )
                        seqEnd += 2;
                    else if (seqEnd < text.Length && CharDisplayWidth(text[seqEnd]) >= 1)
                        seqEnd++;
                    else
                        break;
                    if (seqEnd < text.Length && text[seqEnd] == '\uFE0F')
                        seqEnd++;
                }
                // Compute width per-character (matches wcwidth/terminal)
                var seqWidth = 0;
                for (var si = i; si < seqEnd; si++)
                {
                    if (
                        char.IsHighSurrogate(text[si])
                        && si + 1 < seqEnd
                        && char.IsLowSurrogate(text[si + 1])
                    )
                    {
                        seqWidth += 2;
                        si++;
                    }
                    else
                    {
                        seqWidth += CharDisplayWidth(text[si]);
                    }
                }
                if (len + seqWidth > maxWidth)
                    return text[..i];
                len += seqWidth;
                i = seqEnd;
                continue;
            }
            var cw = CharDisplayWidth(text[i]);
            if (len + cw > maxWidth)
                return text[..i];
            len += cw;
            i++;
        }
        return text;
    }

    /// <summary>构建表格数据行（│ cell │ cell │）。</summary>
    private static string BuildRow(
        char vertical,
        List<string> cells,
        int[] colWidths,
        List<ColumnAlign> alignments
    )
    {
        var sb = new StringBuilder();
        sb.Append(vertical);
        for (var c = 0; c < colWidths.Length; c++)
        {
            var cellText = c < cells.Count ? cells[c] : "";
            var align = c < alignments.Count ? alignments[c] : ColumnAlign.Left;
            sb.Append(' ');
            sb.Append(PadCell(cellText, colWidths[c], align));
            sb.Append(' ');
            sb.Append(vertical);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 在强调标记（* 或 _）周围插入零宽空格（ZWSP），
    /// 防止 CJK 字符与强调标记粘连导致渲染异常。
    /// </summary>
    private static string InsertZwspAroundEmphasis(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var runs = new List<(int start, int len, char delimiter)>();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '*' || text[i] == '_')
            {
                var runStart = i;
                var delim = text[i];
                while (i < text.Length && text[i] == delim)
                    i++;
                runs.Add((runStart, i - runStart, delim));
            }
            else
            {
                i++;
            }
        }

        if (runs.Count == 0)
            return text;

        var sb = new StringBuilder(text.Length + 64);
        var runIdx = 0;
        for (i = 0; i < text.Length; i++)
        {
            if (runIdx < runs.Count && i == runs[runIdx].start)
            {
                var (start, len, delim) = runs[runIdx];
                var prevChar = start > 0 ? text[start - 1] : '\0';
                var nextChar = start + len < text.Length ? text[start + len] : '\0';

                if (prevChar != '\0' && NeedsZwspBoundary(prevChar))
                    sb.Append('\u200B');
                sb.Append(delim, len);
                if (nextChar != '\0' && NeedsZwspBoundary(nextChar))
                    sb.Append('\u200B');

                i = start + len - 1;
                runIdx++;
            }
            else
            {
                sb.Append(text[i]);
            }
        }
        return sb.ToString();
    }

    /// <summary>判断字符是否需要 ZWSP 边界（CJK 字符或标点）。</summary>
    private static bool NeedsZwspBoundary(char c) => IsCjk(c) || char.IsPunctuation(c);

    /// <summary>判断字符是否为 CJK 字符（中日韩统一表意文字及假名等）。</summary>
    private static bool IsCjk(char c) =>
        c
            is >= '\u4E00'
                and <= '\u9FFF'
                or >= '\u3400'
                and <= '\u4DBF'
                or >= '\u3000'
                and <= '\u303F'
                or >= '\uFF00'
                and <= '\uFFEF'
                or >= '\uF900'
                and <= '\uFAFF'
                or >= '\u2E80'
                and <= '\u2EFF'
                or >= '\u2F00'
                and <= '\u2FDF'
                or >= '\u3040'
                and <= '\u309F'
                or >= '\u30A0'
                and <= '\u30FF'
                or >= '\uAC00'
                and <= '\uD7AF';

    /// <summary>
    /// 预处理 Emoji keycap 序列：数字+FE0F+20E3 → 数字+点号。
    /// 终端渲染 keycap emoji 时宽度不稳定，替换为安全的 ASCII 显示。
    /// </summary>
    private static string PreprocessEmojiKeycaps(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsAsciiDigit(c) && i + 1 < text.Length)
            {
                var next = text[i + 1];
                switch (next)
                {
                    case '\uFE0F' when i + 2 < text.Length && text[i + 2] == '\u20E3':
                        sb.Append(c).Append('.');
                        i += 2;
                        continue;
                    case '\u20E3':
                        sb.Append(c).Append('.');
                        i += 1;
                        continue;
                }
            }
            if (c is '#' or '*' && i + 1 < text.Length)
            {
                var next = text[i + 1];
                switch (next)
                {
                    case '\uFE0F' when i + 2 < text.Length && text[i + 2] == '\u20E3':
                        sb.Append(c);
                        i += 2;
                        continue;
                    case '\u20E3':
                        sb.Append(c);
                        i += 1;
                        continue;
                }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ══════════════════════════════════════════════════════
    //  Internal test-accessible wrappers (via MarkdownConverterTesting)
    // ══════════════════════════════════════════════════════

    internal static int CharDisplayWidthPublic(char c) => CharDisplayWidth(c);

    internal static int VisibleLengthPublic(string text) => VisibleLength(text);

    internal static bool IsCjkPublic(char c) => IsCjk(c);

    internal static string PreprocessTaskListsPublic(string text) => PreprocessTaskLists(text);

    internal static string PreprocessEmojiKeycapsPublic(string text) =>
        PreprocessEmojiKeycaps(text);

    internal static string PreprocessImagesPublic(string text) => PreprocessImages(text);

    internal static string PreprocessLinksPublic(string text) => PreprocessLinks(text);

    internal static bool IsHeadingLinePublic(string line, out int level) =>
        IsHeadingLine(line, out level);

    internal static string StripHeadingHashesPublic(string markup) => StripHeadingHashes(markup);

    internal static string StripMarkupTagsPublic(string line) => StripMarkupTags(line);

    internal static string BuildBorderPublic(
        char left,
        char mid,
        char right,
        char fill,
        int[] colWidths
    ) => BuildBorder(left, mid, right, fill, colWidths);

    internal static string PadCellPublic(string text, int targetWidth, int align) =>
        PadCell(text, targetWidth, (ColumnAlign)align);

    internal static string TruncateToDisplayWidthPublic(string text, int maxWidth) =>
        TruncateToDisplayWidth(text, maxWidth);

    internal static string EscapeMarkupPublic(string text) => EscapeMarkup(text);

    internal static string InsertZwspAroundEmphasisPublic(string text) =>
        InsertZwspAroundEmphasis(text);

    internal static List<string> WrapCellPublic(string text, int targetWidth) =>
        WrapCell(text, targetWidth);

    internal static (int[] TargetWidths, bool NeedFallback) ComputeTargetWidthsPublic(
        int[] naturalWidths,
        int colCount,
        int contentWidth
    )
    {
        var needFallback = ComputeTargetWidths(naturalWidths, colCount, contentWidth, out var tw);
        return (tw, needFallback);
    }

    // ══════════════════════════════════════════════════════

    /// <summary>
    /// 剥离 XenoAtom 转换后残留的 # 标题前缀（如 "## Title" → "Title"）。
    /// 在代码块内部不处理。
    /// </summary>
    private static string StripHeadingHashes(string markup)
    {
        if (string.IsNullOrEmpty(markup))
            return markup;

        var sb = new StringBuilder(markup.Length);
        var i = 0;
        var len = markup.Length;
        var inCodeBlock = false;

        while (i < len)
        {
            // Track code block boundaries: [code]...[/code]
            if (i + 5 < len && markup.AsSpan(i, 5) is "[code]")
            {
                inCodeBlock = true;
                sb.Append(markup[i]);
                i++;
                continue;
            }
            if (i + 6 < len && markup.AsSpan(i, 6) is "[/code]")
            {
                inCodeBlock = false;
                sb.Append(markup[i]);
                i++;
                continue;
            }

            // Only strip # prefixes outside code blocks
            if (!inCodeBlock && (i == 0 || markup[i - 1] == '\n'))
            {
                var hashCount = 0;
                var j = i;
                while (j < len && markup[j] == '#' && hashCount < 6)
                {
                    hashCount++;
                    j++;
                }
                if (hashCount is >= 1 and <= 6 && j < len && markup[j] == ' ')
                {
                    i = j + 1;
                    continue;
                }
            }

            sb.Append(markup[i]);
            i++;
        }

        return sb.ToString();
    }
}
