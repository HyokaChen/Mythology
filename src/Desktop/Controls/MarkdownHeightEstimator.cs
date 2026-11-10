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

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseCodeBlockSpanFixer()
        .Build();

    private const double TableCellVerticalPadding = 6; // 0,3,10,3
    private const double TablePaddingVertical = 12; // 8,6
    private const double TableMarginVertical = 16; // 0,8

    // ConversationView overrides Border.QuoteBlock: Padding=0, BorderThickness=0.
    private const double QuotePaddingVertical = 0;
    private const double QuoteMarginVertical = 16; // base margin 0,8
    private const double QuoteHorizontalPadding = 0;
    private const double CodeHeaderHeight = 24;
    private const double CodeTextMarginVertical = 8;
    private const double CodeBorderHeight = 2;
    private const double ThematicHeight = 1;
    private const double ThematicMarginVertical = 4;

    public static double EstimateDocument(string markdown, double width, Style? style = null)
    {
        var resolved = style ?? Conversation;
        var document = Markdown.Parse(markdown ?? "", Pipeline);
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
        private readonly double _width;

        public Estimator(double width, Style style)
        {
            _width = Math.Max(1, width);
            _style = style;
            _probe.FontFamily = style.FontFamily;
            _probe.FontSize = style.FontSize;
            _probe.LineHeight = style.LineHeight;
        }

        public double EstimateContainer(ContainerBlock container, double width)
        {
            var total = 0.0;
            var first = true;
            foreach (var child in container)
            {
                if (child is null)
                    continue;
                if (!first)
                    total += _style.ContainerSpacing;
                total += EstimateBlock(child, width);
                first = false;
            }
            return total;
        }

        internal double EstimateBlock(Block block, double width)
        {
            width = Math.Max(1, width);
            return block switch
            {
                HeadingBlock heading => MeasureHeading(heading, width),
                ParagraphBlock paragraph => MeasureParagraph(paragraph, width),
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
            // The Heading Border + nested TextBlock measure 6px above the bare
            // line box (font line box/border), in addition to the style margin.
            return MeasureText(ExtractInlines(heading.Inline), fontSize, _style.LineHeight, width)
                + margin
                + 6;
        }

        private double MeasureParagraph(ParagraphBlock paragraph, double width)
        {
            return MeasureText(
                    ExtractInlines(paragraph.Inline),
                    _style.FontSize,
                    _style.LineHeight,
                    width
                ) + _style.ParagraphMargin;
        }

        private double MeasureCode(Markdig.Syntax.CodeBlock code, double width)
        {
            var text = ExtractCode(code);
            var codeHeight = MeasureText(
                text,
                _style.FontSize,
                _style.LineHeight,
                Math.Max(1, width - CodeTextMarginVertical)
            );
            return CodeHeaderHeight + CodeTextMarginVertical + codeHeight + CodeBorderHeight;
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
                var content = EstimateContainer(listItem, Math.Max(1, width - bulletWidth));
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
            var rowIndex = 0;
            foreach (var row in table.OfType<TableRow>())
            {
                var rowHeight = 0.0;
                foreach (var cell in row.OfType<TableCell>())
                {
                    var content = EstimateContainer(cell, double.PositiveInfinity);
                    var border = row.IsHeader ? 2.0 : 1.0;
                    rowHeight = Math.Max(rowHeight, content + TableCellVerticalPadding + border);
                }
                rows.Add(rowHeight);
                rowIndex++;
            }

            _ = width;
            return TableMarginVertical + TablePaddingVertical + rows.Sum();
        }

        private double MeasureQuote(QuoteBlock quote, double width)
        {
            var content = EstimateContainer(quote, Math.Max(1, width - QuoteHorizontalPadding));
            return content + QuotePaddingVertical + QuoteMarginVertical;
        }

        private double MeasureFallback(Block block, double width)
        {
            if (block is LeafBlock leaf && leaf.Inline is { } inline)
                return MeasureText(
                    ExtractInlines(inline),
                    _style.FontSize,
                    _style.LineHeight,
                    width
                );
            return Math.Max(1, width);
        }

        private double MeasureText(string text, double fontSize, double lineHeight, double width)
        {
            if (string.IsNullOrEmpty(text))
                return lineHeight;

            _probe.FontSize = fontSize;
            _probe.LineHeight = lineHeight;
            _probe.Text = text;
            _probe.Measure(new Size(Math.Max(1, width), double.PositiveInfinity));
            return Math.Max(lineHeight, _probe.DesiredSize.Height);
        }
    }

    private static string ExtractInlines(ContainerInline? inline)
    {
        if (inline is null)
            return "";
        var builder = new StringBuilder();
        AppendInline(inline, builder);
        return builder.ToString();
    }

    private static void AppendInline(Inline inline, StringBuilder builder)
    {
        switch (inline)
        {
            case LiteralInline literal:
                builder.Append(literal.Content.AsSpan());
                return;
            case Markdig.Syntax.Inlines.CodeInline code:
                builder.Append(code.Content.AsSpan());
                return;
            case LineBreakInline:
                builder.Append('\n');
                return;
            case LinkInline link:
                foreach (var child in link)
                    AppendInline(child, builder);
                return;
            case ContainerInline container:
                foreach (var child in container)
                    AppendInline(child, builder);
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
