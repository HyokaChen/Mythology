using Tui.Controls;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// MarkdownConverter 其他功能测试：图片预处理、链接、Emoji Keycap、Heading装饰。
/// </summary>
public class MarkdownConverterFeaturesTests
{
    // ══════════════════════════════════════════════════════
    //  图片预处理
    // ══════════════════════════════════════════════════════

    [Fact]
    public void PreprocessImages_Basic_RendersCard()
    {
        var md = "![Alt text](https://example.com/image.png)";
        var result = MarkdownConverter.Convert(md, 80);

        Assert.Contains("🖼", result);
        Assert.Contains("image.png", result);
        Assert.Contains("Alt text", result);
    }

    [Fact]
    public void PreprocessImages_LongFilename_Truncates()
    {
        var longUrl = "https://example.com/" + new string('a', 50) + ".png";
        var md = $"![Alt]({longUrl})";
        var result = MarkdownConverter.Convert(md, 80);

        Assert.Contains("...", result); // 截断指示
    }

    // ══════════════════════════════════════════════════════
    //  链接预处理
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Convert_Link_ShowsLinkText()
    {
        var md = "[click here](https://example.com)";
        var result = MarkdownConverter.Convert(md, 80);

        Assert.Contains("click here", result);
    }

    [Fact]
    public void Convert_AutoLink_NoDuplicateUrl()
    {
        var md = "[https://example.com](https://example.com)";
        var result = MarkdownConverter.Convert(md, 80);

        // 链接文本和 URL 相同时不重复显示 URL
        var dimCount = result.Split("[dim]").Length - 1;
        Assert.True(dimCount <= 1);
    }

    // ══════════════════════════════════════════════════════
    //  TaskList
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Convert_TaskList_Checked()
    {
        var md = "- [x] Completed task";
        var result = MarkdownConverter.Convert(md, 80);
        Assert.Contains("☑", result);
    }

    [Fact]
    public void Convert_TaskList_Unchecked()
    {
        var md = "- [ ] Pending task";
        var result = MarkdownConverter.Convert(md, 80);
        Assert.Contains("☐", result);
    }

    // ══════════════════════════════════════════════════════
    //  Emoji Keycap 预处理
    // ══════════════════════════════════════════════════════

    [Fact]
    public void PreprocessEmojiKeycaps_DigitEmoji_AddsDot()
    {
        // 1⃣ (digit 1 + U+FE0F + U+20E3) → "1."
        var input = "1\uFE0F\u20E3";
        var result = MarkdownConverterTesting.PreprocessEmojiKeycapsPublic(input);
        Assert.Equal("1.", result);
    }

    [Fact]
    public void PreprocessEmojiKeycaps_PlainDigit_Unchanged()
    {
        var result = MarkdownConverterTesting.PreprocessEmojiKeycapsPublic("1");
        Assert.Equal("1", result);
    }

    // ══════════════════════════════════════════════════════
    //  Heading 检测
    // ══════════════════════════════════════════════════════

    [Fact]
    public void IsHeadingLine_H1_Markup()
    {
        var line = "[bold underline #a65e2e]My Title[/]";
        var isHeading = MarkdownConverterTesting.IsHeadingLinePublic(line, out var level);
        Assert.True(isHeading);
        Assert.Equal(1, level);
    }

    [Fact]
    public void IsHeadingLine_H2_Markup()
    {
        var line = "[bold #a65e2e]My Section[/]";
        var isHeading = MarkdownConverterTesting.IsHeadingLinePublic(line, out var level);
        Assert.True(isHeading);
        Assert.Equal(2, level);
    }

    [Fact]
    public void IsHeadingLine_PlainText_NotHeading()
    {
        var line = "Just a regular line";
        var isHeading = MarkdownConverterTesting.IsHeadingLinePublic(line, out var level);
        Assert.False(isHeading);
    }

    // ══════════════════════════════════════════════════════
    //  Heading hash 剥离
    // ══════════════════════════════════════════════════════

    [Fact]
    public void StripHeadingHashes_RemovesHashPrefix()
    {
        var input = "# Heading 1\nText";
        var result = MarkdownConverterTesting.StripHeadingHashesPublic(input);
        Assert.DoesNotContain("# Heading", result);
        Assert.Contains("Heading 1", result);
    }

    [Fact]
    public void StripHeadingHashes_CodeBlock_KeepsHashes()
    {
        var input = "[code]# not a heading[/code]";
        var result = MarkdownConverterTesting.StripHeadingHashesPublic(input);
        Assert.Contains("# not a heading", result);
    }

    // ══════════════════════════════════════════════════════
    //  Markup 标签剥离
    // ══════════════════════════════════════════════════════

    [Fact]
    public void StripMarkupTags_RemovesAllTags()
    {
        var input = "[bold #a65e2e]Bold Text[/] and [dim]Dim Text[/]";
        var result = MarkdownConverterTesting.StripMarkupTagsPublic(input);
        Assert.DoesNotContain("[", result);
        Assert.DoesNotContain("]", result);
        Assert.Contains("Bold Text", result);
        Assert.Contains("Dim Text", result);
    }

    // ══════════════════════════════════════════════════════
    //  EscapeMarkup
    // ══════════════════════════════════════════════════════

    [Fact]
    public void EscapeMarkup_EscapesBrackets()
    {
        // XenoAtom 的转义写法是 [[/]]（旧的 \[/\] 会原样显示反斜杠）→ 断言「转义后解析回原文」
        var input = "[not a tag]";
        var result = MarkdownConverterTesting.EscapeMarkupPublic(input);
        Assert.Equal("[[not a tag]]", result);

        var parser = new XenoAtom.Terminal.UI.Text.MarkupTextParser();
        Assert.Equal(input, parser.Parse(result, out _));
    }

    // ══════════════════════════════════════════════════════
    //  TruncateToDisplayWidth
    // ══════════════════════════════════════════════════════

    [Fact]
    public void TruncateToDisplayWidth_ShortText_ReturnsAll()
    {
        var result = MarkdownConverterTesting.TruncateToDisplayWidthPublic("hello", 10);
        Assert.Equal("hello", result);
    }

    [Fact]
    public void TruncateToDisplayWidth_LongText_Truncates()
    {
        var result = MarkdownConverterTesting.TruncateToDisplayWidthPublic("hello world", 5);
        Assert.True(result.Length <= 6); // 5 chars visible + potential formatting
    }

    [Fact]
    public void TruncateToDisplayWidth_Cjk_TruncatesAtBoundary()
    {
        // "你好世界" = 2+2+2+2 = 8 visible width, truncate to 4 → should keep 2 CJK chars
        var result = MarkdownConverterTesting.TruncateToDisplayWidthPublic("你好世界", 4);
        Assert.True(MarkdownConverterTesting.VisibleLengthPublic(result) <= 4);
    }

    // ══════════════════════════════════════════════════════
    //  ZWSP around emphasis
    // ══════════════════════════════════════════════════════

    [Fact]
    public void InsertZwspAroundEmphasis_CjkBeforeBold_InsertsZwsp()
    {
        var input = "中文**bold**";
        var result = MarkdownConverterTesting.InsertZwspAroundEmphasisPublic(input);
        Assert.Contains("\u200B**bold**", result);
    }

    [Fact]
    public void InsertZwspAroundEmphasis_PureAscii_InsertsZwsp()
    {
        var input = "text **bold** more";
        var result = MarkdownConverterTesting.InsertZwspAroundEmphasisPublic(input);
        Assert.Contains("\u200B", result);
        Assert.Contains("**bold**", result);
    }

    // ══════════════════════════════════════════════════════
    //  PadCell
    // ══════════════════════════════════════════════════════

    [Fact]
    public void PadCell_LeftAlign_AddsPaddingRight()
    {
        var result = MarkdownConverterTesting.PadCellPublic("ab", 5, 0); // 0 = Left
        Assert.Equal("ab   ", result);
    }

    [Fact]
    public void PadCell_RightAlign_AddsPaddingLeft()
    {
        var result = MarkdownConverterTesting.PadCellPublic("ab", 5, 2); // 2 = Right
        Assert.Equal("   ab", result);
    }

    [Fact]
    public void PadCell_CenterAlign_SplitsPadding()
    {
        var result = MarkdownConverterTesting.PadCellPublic("a", 5, 1); // 1 = Center
        Assert.Equal("  a  ", result);
    }

    [Fact]
    public void PadCell_CjkText_PadsCorrectly()
    {
        // "中文" = 4 visible width, target 8 → pad 4 spaces right
        var result = MarkdownConverterTesting.PadCellPublic("中文", 8, 0);
        Assert.Equal("中文    ", result);
    }

    // ══════════════════════════════════════════════════════
    //  BuildBorder
    // ══════════════════════════════════════════════════════

    [Fact]
    public void BuildBorder_TwoColumns_RendersCorrectly()
    {
        var result = MarkdownConverterTesting.BuildBorderPublic('┌', '┬', '┐', '─', [5, 3]);
        // ┌───────┬─────┐
        Assert.StartsWith("┌", result);
        Assert.EndsWith("┐", result);
        Assert.Contains("┬", result);
        Assert.Contains("───────", result);
        Assert.Contains("─────", result);
    }
}
