using Tui.Controls;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// MarkdownConverter 深度测试：ZWJ emoji、复杂 CJK 折行、混合表格。
/// </summary>
public class MarkdownConverterDeepTests
{
    // ══════════════════════════════════════════════════════
    //  ZWJ Emoji 序列
    // ══════════════════════════════════════════════════════

    [Fact]
    public void VisibleLength_ZwjFamilyEmoji_WidthCorrect()
    {
        var family = "\U0001F468\u200D\U0001F469\u200D\U0001F467";
        var width = MarkdownConverterTesting.VisibleLengthPublic(family);
        Assert.Equal(6, width);
    }

    [Fact]
    public void VisibleLength_ZwjProfessionEmoji_WidthCorrect()
    {
        var techie = "\U0001F9D1\u200D\U0001F4BB";
        var width = MarkdownConverterTesting.VisibleLengthPublic(techie);
        Assert.Equal(4, width);
    }

    [Fact]
    public void VisibleLength_EmojiWithVariationSelector_Width2()
    {
        var heart = "\u2764\uFE0F";
        var width = MarkdownConverterTesting.VisibleLengthPublic(heart);
        Assert.Equal(2, width);
    }

    [Fact]
    public void VisibleLength_MultipleZwjSequences_MixedAscii()
    {
        var input = "A\U0001F468\u200D\U0001F469\u200D\U0001F467B\U0001F9D1\u200D\U0001F4BBC";
        var width = MarkdownConverterTesting.VisibleLengthPublic(input);
        Assert.Equal(13, width);
    }

    // ══════════════════════════════════════════════════════
    //  TokenizeCell / WrapCell
    // ══════════════════════════════════════════════════════

    [Fact]
    public void WrapCell_PureCjk_EachCharBreakable()
    {
        var lines = MarkdownConverterTesting.WrapCellPublic("测试文本", 4);
        Assert.Equal(2, lines.Count);
        Assert.Equal("测试", lines[0]);
        Assert.Equal("文本", lines[1]);
    }

    [Fact]
    public void WrapCell_MixedCjkAscii_BreaksCorrectly()
    {
        var lines = MarkdownConverterTesting.WrapCellPublic("AA测试BB", 4);
        Assert.True(lines.Count >= 2);
    }

    [Fact]
    public void WrapCell_BoldSpan_KeepsFormatting()
    {
        var lines = MarkdownConverterTesting.WrapCellPublic("A **bold** B", 6);
        var text = string.Join(" ", lines);
        Assert.Contains("**bold**", text);
    }

    [Fact]
    public void WrapCell_ItalicSpan_KeepsFormatting()
    {
        var lines = MarkdownConverterTesting.WrapCellPublic("text *italic* end", 10);
        var text = string.Join(" ", lines);
        Assert.Contains("*italic*", text);
    }

    [Fact]
    public void WrapCell_ContentPreserved()
    {
        var lines = MarkdownConverterTesting.WrapCellPublic("~~deleted~~ text", 12);
        var text = string.Join(" ", lines);
        Assert.Contains("deleted", text.Replace("~", ""));
        Assert.Contains("text", text);
    }

    [Fact]
    public void WrapCell_WideEnough_SingleLine()
    {
        var lines = MarkdownConverterTesting.WrapCellPublic("hello world", 20);
        Assert.Single(lines);
    }

    [Fact]
    public void WrapCell_CjkPunctuation_NoEmptyLines()
    {
        var lines = MarkdownConverterTesting.WrapCellPublic("测试。文本", 6);
        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.NotEmpty(l));
    }

    // ══════════════════════════════════════════════════════
    //  ComputeTargetWidths
    // ══════════════════════════════════════════════════════

    [Fact]
    public void ComputeTargetWidths_FitsNaturally_NoCompression()
    {
        var (targetWidths, needFallback) = MarkdownConverterTesting.ComputeTargetWidthsPublic(
            [5, 3, 4],
            3,
            30
        );
        Assert.False(needFallback);
        Assert.Equal([5, 3, 4], targetWidths);
    }

    [Fact]
    public void ComputeTargetWidths_TooWide_TriggersFallback()
    {
        var (_, needFallback) = MarkdownConverterTesting.ComputeTargetWidthsPublic(
            [30, 30, 30],
            3,
            30
        );
        Assert.True(needFallback);
    }

    [Fact]
    public void ComputeTargetWidths_PartiallyFits_WaterFill()
    {
        var (targetWidths, needFallback) = MarkdownConverterTesting.ComputeTargetWidthsPublic(
            [20, 10, 5],
            3,
            50
        );
        Assert.False(needFallback);
        Assert.All(targetWidths, w => Assert.True(w > 0));
    }

    // ══════════════════════════════════════════════════════
    //  表格 + Emoji + CJK 混合
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Convert_TableWithEmoji_RendersCorrectly()
    {
        var md = """
            | 角色 | 状态 |
            |------|------|
            | 林默 | ✅活跃 |
            | 青云 | ⚠阵亡 |
            """;

        var result = MarkdownConverter.Convert(md, 80);
        Assert.Contains("林默", result);
        Assert.Contains("┌", result);
    }

    [Fact]
    public void Convert_TableWithZwjEmoji_DoesNotBreak()
    {
        var md = """
            | Name | Family |
            |------|--------|
            | Alice | 👨‍👩‍👧 |
            """;

        var result = MarkdownConverter.Convert(md, 80);
        Assert.Contains("┌", result);
    }

    [Fact]
    public void Convert_TableMixedLanguage_AllAligns()
    {
        var md = """
            | 姓名 Name | 武器 Weapon |
            |-----------|-------------|
            | 林默 LinMo | 青云剑 QingYun Sword |
            """;

        var result = MarkdownConverter.Convert(md, 100);
        Assert.Contains("林默 LinMo", result);
        Assert.Contains("┌", result);
    }

    [Fact]
    public void Convert_TableNarrowWithLongCjk_DoesNotCrash()
    {
        var md = """
            | 角色名 | 冗长描述 |
            |--------|----------|
            | 林默 | 这是一段非常长的中文描述文本需要折行 |
            """;

        var result = MarkdownConverter.Convert(md, 40);
        // Just verify it doesn't crash and contains some content
        Assert.True(result.Length > 0);
        Assert.Contains("林默", result);
    }

    // ══════════════════════════════════════════════════════
    //  CharDisplayWidth 补充
    // ══════════════════════════════════════════════════════

    [Theory]
    [InlineData('⌚', 2)]
    [InlineData('⏩', 2)]
    [InlineData('⏳', 2)]
    [InlineData('⬅', 2)]
    [InlineData('⬆', 2)]
    [InlineData('⭕', 2)]
    [InlineData('➕', 2)]
    [InlineData('➖', 2)]
    public void CharDisplayWidth_MoreBmpEmoji_Returns2(char c, int expected)
    {
        Assert.Equal(expected, MarkdownConverterTesting.CharDisplayWidthPublic(c));
    }

    // ══════════════════════════════════════════════════════
    //  边界情况
    // ══════════════════════════════════════════════════════

    [Fact]
    public void Convert_HorizontalRule_ContainsText()
    {
        var md = "---\n\nText";
        var result = MarkdownConverter.Convert(md, 50);
        Assert.Contains("Text", result);
    }

    [Fact]
    public void Convert_CodeBlock_Fenced()
    {
        var md = """
            ```
            var x = 1;
            ```
            """;
        var result = MarkdownConverter.Convert(md, 80);
        Assert.Contains("var x = 1;", result);
    }

    [Fact]
    public void Convert_NestedBoldInItalic_DoesNotThrow()
    {
        var md = "*italic **bold** more*";
        var result = MarkdownConverter.Convert(md, 80);
        Assert.NotEmpty(result);
    }
}
