using System.Buffers;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LiveMarkdown.Avalonia;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Desktop.Controls;

/// <summary>
/// Source-derived height estimator for the base LiveMarkdown subset.
///
/// It deliberately reuses Avalonia's own <see cref="TextLayout"/> implementation
/// through a detached <see cref="TextBlock"/> probe for line breaking, rather than
/// approximating CJK/Latin widths with character counts. Block margins/paddings
/// mirror LiveMarkdown's base styles and the app's ConversationMarkdown overrides.
/// </summary>
public static class MarkdownHeightEstimator
{
    public readonly record struct Style(
        FontFamily FontFamily,
        double FontSize,
        double LineHeight,
        double ParagraphMargin,
        double ContainerSpacing
    );

    /// <summary>ConversationMarkdown: 16/24, paragraph bottom 8, root StackPanel 4.</summary>
    public static readonly Style Conversation = new(
        new FontFamily("Inter, PingFang SC, Microsoft YaHei, Noto Sans CJK SC"),
        16,
        24,
        8,
        4
    );

    /// <summary>StepContentMarkdown: 14/24, paragraph bottom 8, root StackPanel 4.</summary>
    public static readonly Style Step = new(
        new FontFamily("Inter, PingFang SC, Microsoft YaHei, Noto Sans CJK SC"),
        14,
        24,
        8,
        4
    );

    // Vectorized (SearchValues) fast path for plain novel prose; Markdown syntax
    // falls through to Markdig.
    private static readonly SearchValues<char> MarkdownMarkers = SearchValues.Create(
        "#*_`[]()!>|~\\-+<"
    );

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseCodeBlockSpanFixer()
        .Build();

    private const double TableCellVerticalPadding = 6; // 0,3,10,3
    private const double TableCellHorizontalPadding = 10; // 0,3,10,3
    private const double TablePaddingVertical = 12; // 8,6
    private const double TablePaddingHorizontal = 16; // 8,6
    private const double TableMarginVertical = 16; // 0,8

    // ScrollViewer.Table HorizontalScrollBarVisibility=Auto. When the table is
    // wider than the viewport this row (18px) is part of the measured height.
    private const double TableHorizontalScrollbarHeight = 18;

    // ConversationView overrides Border.QuoteBlock: Padding=0, BorderThickness=0.
    private const double QuotePaddingVertical = 0;
    private const double QuoteMarginVertical = 16; // base margin 0,8
    private const double QuoteHorizontalPadding = 0;

    // 对齐 Desktop/Styles/MarkdownStyles.axaml 的代码块模板：
    // 头部 = 22px 图标按钮 + 上下 5px padding + 1px 发丝分隔线；
    // 正文 = MarkdownTextBlock 的 12,10 外边距 + 代码块 1px 外框。
    private const double CodeHeaderHeight = 33;
    private const double CodeVerticalPadding = 22; // 10px × 2 + 1px × 2 外框
    private const double CodeFontSize = 11;
    private const double CodeLineHeight = 18;

    // MarkdownStyles.axaml sets CodeBlock Margin=0,2; the renderer's StackPanel
    // spacing is already counted separately by EstimateContainer.
    private const double CodeMarginVertical = 4;
    private static readonly FontFamily CodeFontFamily = new(
        "Fragment Mono, Menlo, Consolas, monospace"
    );
    private const double ThematicHeight = 1;
    private const double ThematicMarginVertical = 4;

    public static double EstimateDocument(string markdown, double width, Style? style = null)
    {
        var resolved = style ?? Conversation;
        var source = markdown ?? "";
        if (!HasMarkdownSyntax(source.AsSpan()))
            return EstimatePlainDocument(source, width, resolved);

        var document = Markdown.Parse(source, Pipeline);
        var estimator = new Estimator(width, resolved);
        return estimator.EstimateContainer(document, width);
    }

    public static double EstimatePlainText(
        string text,
        double width,
        double fontSize,
        double lineHeight
    )
    {
        var probe = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontFamily = Conversation.FontFamily,
            FontSize = fontSize,
            LineHeight = lineHeight,
            Text = text ?? "",
        };
        probe.Measure(new Size(Math.Max(1, width), double.PositiveInfinity));
        return Math.Max(lineHeight, probe.DesiredSize.Height);
    }

    private static bool HasMarkdownSyntax(ReadOnlySpan<char> source)
    {
        if (source.IndexOfAny(MarkdownMarkers) >= 0)
            return true;

        // Ordered list "1. text" / "1) text" has no marker from the set above.
        var lineStart = true;
        for (var i = 0; i < source.Length; i++)
        {
            var ch = source[i];
            if (lineStart && char.IsAsciiDigit(ch))
            {
                var j = i;
                while (j < source.Length && char.IsAsciiDigit(source[j]))
                    j++;
                if (
                    j > i
                    && j + 1 < source.Length
                    && (source[j] == '.' || source[j] == ')')
                    && char.IsWhiteSpace(source[j + 1])
                )
                    return true;
            }
            lineStart = ch == '\n';
        }
        return false;
    }

    private static double EstimatePlainDocument(string source, double width, Style style)
    {
        var span = source.AsSpan();
        var total = 0.0;
        var paragraphs = 0;
        while (!span.IsEmpty)
        {
            var separator = span.IndexOf("\n\n".AsSpan());
            var end = separator < 0 ? span.Length : separator;
            var paragraph = span[..end].Trim();
            if (!paragraph.IsEmpty)
            {
                var text = paragraph.ToString();
                var probe = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = style.FontFamily,
                    FontSize = style.FontSize,
                    LineHeight = style.LineHeight,
                    Text = text,
                };
                probe.Measure(new Size(Math.Max(1, width), double.PositiveInfinity));
                total +=
                    Math.Max(style.LineHeight, probe.DesiredSize.Height) + style.ParagraphMargin;
                paragraphs++;
            }
            if (separator < 0)
                break;
            span = span[(separator + 2)..];
        }
        if (paragraphs == 0)
            return 0;
        return total + Math.Max(0, paragraphs - 1) * style.ContainerSpacing;
    }

    /// <summary>Estimate one top-level block/segment (no document-level spacing).</summary>
    public static double EstimateBlock(string markdown, double width, Style? style = null)
    {
        var resolved = style ?? Conversation;
        var block = Markdown.Parse(markdown ?? "", Pipeline);
        var estimator = new Estimator(width, resolved);
        return estimator.EstimateContainer(block, width);
    }

    private sealed class Estimator
    {
        private readonly TextBlock _probe = new()
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = default,
            Padding = default,
        };

        private readonly Style _style;

        public Estimator(double width, Style style)
        {
            _style = style;
            _probe.FontFamily = style.FontFamily;
            _probe.FontSize = style.FontSize;
            _probe.LineHeight = style.LineHeight;
        }

        public double EstimateContainer(
            ContainerBlock container,
            double width,
            double? spacing = null,
            bool excludeInlineCode = false
        )
        {
            var total = 0.0;
            var first = true;
            var gap = spacing ?? _style.ContainerSpacing;
            foreach (var child in container)
            {
                if (child is null)
                    continue;
                if (!first)
                    total += gap;
                total += EstimateBlock(child, width, excludeInlineCode);
                first = false;
            }
            return total;
        }

        internal double EstimateBlock(Block block, double width, bool excludeInlineCode = false)
        {
            width = Math.Max(1, width);
            return block switch
            {
                HeadingBlock heading => MeasureHeading(heading, width),
                ParagraphBlock paragraph => MeasureParagraph(paragraph, width, excludeInlineCode),
                Markdig.Syntax.CodeBlock code => MeasureCode(code, width),
                ListBlock list => MeasureList(list, width),
                Table table => MeasureTable(table, width),
                QuoteBlock quote => MeasureQuote(quote, width),
                ThematicBreakBlock => ThematicHeight + ThematicMarginVertical,
                LinkReferenceDefinitionGroup => 0,
                _ => MeasureFallback(block, width),
            };
        }

        private double MeasureHeading(HeadingBlock heading, double width)
        {
            var (fontSize, margin) = heading.Level switch
            {
                1 => (24.0, 6.0),
                2 => (20.0, 4.0),
                3 => (18.0, 2.0),
                4 => (16.0, 2.0),
                _ => (16.0, 0.0),
            };
            // MarkdownStyles sets Heading TextBlock Margin=0,{6|4|2|0}; the
            // margin applies to both top and bottom, and the measured heading
            // line box is 28px for every heading level at this font.
            return MeasureInlineText(heading.Inline, fontSize, _style.LineHeight, width)
                + margin * 2;
        }

        private double MeasureParagraph(
            ParagraphBlock paragraph,
            double width,
            bool excludeInlineCode = false
        )
        {
            return MeasureInlineText(
                    paragraph.Inline,
                    _style.FontSize,
                    _style.LineHeight,
                    width,
                    includeCode: !excludeInlineCode
                ) + _style.ParagraphMargin;
        }

        private double MeasureCode(Markdig.Syntax.CodeBlock code, double width)
        {
            var text = ExtractCode(code);
            // CodeBlock has its own horizontal scrolling and never wraps source
            // lines. Using the body width here overestimates long code by 2-3×.
            var codeHeight = MeasureText(
                text,
                CodeFontSize,
                CodeLineHeight,
                double.PositiveInfinity,
                CodeFontFamily
            );
            return CodeHeaderHeight + CodeVerticalPadding + CodeMarginVertical + codeHeight;
        }

        private double MeasureList(ListBlock list, double width)
        {
            var total = 0.0;
            var bulletWidth = 0.0;
            var rows = new List<(double Bullet, double Content)>(list.Count);
            foreach (var item in list)
            {
                if (item is not ListItemBlock listItem)
                    continue;

                var bullet = list.IsOrdered ? $"{rows.Count + 1}." : "•";
                bulletWidth = Math.Max(
                    bulletWidth,
                    MeasureText(bullet, 14, 20, double.PositiveInfinity)
                );
                // MarkdownListGrid.ListBlock sets ListItemBlock Spacing=0; do not
                // add the document ContainerSpacing between a paragraph and its
                // nested list.
                var content = EstimateContainer(
                    listItem,
                    Math.Max(1, width - bulletWidth),
                    0,
                    excludeInlineCode: true
                );
                rows.Add((bulletWidth, content));
            }

            foreach (var row in rows)
            {
                total += Math.Max(row.Bullet, row.Content);
            }
            return total;
        }

        private double MeasureTable(Table table, double width)
        {
            var rows = new List<double>();
            var columnWidths = new List<double>();
            foreach (var row in table.OfType<TableRow>())
            {
                var rowHeight = 0.0;
                var cellIndex = 0;
                foreach (var cell in row.OfType<TableCell>())
                {
                    var content = EstimateContainer(cell, double.PositiveInfinity);
                    var border = row.IsHeader ? 2.0 : 1.0;
                    rowHeight = Math.Max(rowHeight, content + TableCellVerticalPadding + border);

                    var cellWidth = MeasureCellContentWidth(cell) + TableCellHorizontalPadding;
                    if (cellIndex < columnWidths.Count)
                        columnWidths[cellIndex] = Math.Max(columnWidths[cellIndex], cellWidth);
                    else
                        columnWidths.Add(cellWidth);
                    cellIndex++;
                }
                rows.Add(rowHeight);
            }

            var tableWidth = TablePaddingHorizontal + columnWidths.Sum();
            var horizontalScrollbar = tableWidth > width ? TableHorizontalScrollbarHeight : 0;
            return TableMarginVertical + TablePaddingVertical + horizontalScrollbar + rows.Sum();
        }

        private double MeasureCellContentWidth(TableCell cell)
        {
            var maxWidth = 0.0;
            foreach (var block in cell)
            {
                if (block is LeafBlock leaf && leaf.Inline is { } inline)
                    maxWidth = Math.Max(maxWidth, MeasureTextWidth(ExtractInlines(inline)));
            }
            return maxWidth;
        }

        private double MeasureTextWidth(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;
            _probe.FontFamily = _style.FontFamily;
            _probe.FontSize = _style.FontSize;
            _probe.LineHeight = _style.LineHeight;
            _probe.Text = text;
            _probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return _probe.DesiredSize.Width;
        }

        private double MeasureQuote(QuoteBlock quote, double width)
        {
            var content = EstimateContainer(quote, Math.Max(1, width - QuoteHorizontalPadding));
            return content + QuotePaddingVertical + QuoteMarginVertical;
        }

        private double MeasureFallback(Block block, double width)
        {
            if (block is LeafBlock leaf && leaf.Inline is { } inline)
                return MeasureInlineText(inline, _style.FontSize, _style.LineHeight, width);
            return Math.Max(1, width);
        }

        /// <summary>
        /// Inline code is a separately shaped chip in LiveMarkdown. List items
        /// exclude the chip's text from the parent line wrapping; ordinary
        /// paragraphs include it. The caller selects the observed behavior.
        /// </summary>
        private double MeasureInlineText(
            ContainerInline? inline,
            double fontSize,
            double lineHeight,
            double width,
            bool includeCode = true
        ) => MeasureText(ExtractInlines(inline, includeCode), fontSize, lineHeight, width);

        private double MeasureInlineWidth(ContainerInline? inline) =>
            MeasureTextWidth(ExtractInlines(inline, includeCode: true));

        private double MeasureText(
            string text,
            double fontSize,
            double lineHeight,
            double width,
            FontFamily? fontFamily = null
        )
        {
            if (string.IsNullOrEmpty(text))
                return lineHeight;

            _probe.FontFamily = fontFamily ?? _style.FontFamily;
            _probe.FontSize = fontSize;
            _probe.LineHeight = lineHeight;
            _probe.Text = text;
            _probe.Measure(new Size(Math.Max(1, width), double.PositiveInfinity));
            return Math.Max(lineHeight, _probe.DesiredSize.Height);
        }
    }

    private static string ExtractInlines(ContainerInline? inline, bool includeCode = true)
    {
        if (inline is null)
            return "";
        var builder = new StringBuilder();
        AppendInline(inline, builder, includeCode);
        return builder.ToString();
    }

    private static void AppendInline(
        Markdig.Syntax.Inlines.Inline inline,
        StringBuilder builder,
        bool includeCode
    )
    {
        switch (inline)
        {
            case LiteralInline literal:
                builder.Append(literal.Content.AsSpan());
                return;
            case Markdig.Syntax.Inlines.CodeInline code:
                if (includeCode)
                    builder.Append(code.Content.AsSpan());
                return;
            case LineBreakInline:
                builder.Append('\n');
                return;
            case LinkInline link:
                foreach (var child in link)
                    AppendInline(child, builder, includeCode);
                return;
            case ContainerInline container:
                foreach (var child in container)
                    AppendInline(child, builder, includeCode);
                return;
            default:
                if (inline is LeafInline)
                    builder.Append(inline.ToString());
                return;
        }
    }

    private static string ExtractCode(Markdig.Syntax.CodeBlock block)
    {
        if (block.Lines.Lines is null || block.Lines.Count == 0)
            return "";
        var builder = new StringBuilder();
        var lines = block.Lines.Lines.AsSpan(0, block.Lines.Count);
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                builder.Append('\n');
            builder.Append(lines[i].Slice.AsSpan());
        }
        return builder.ToString();
    }
}
