using Tui.Controls;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// MarkdownConverter 核心测试：CharDisplayWidth、VisibleLength、CJK字符宽度、
/// Emoji宽度、ZWJ序列、表格渲染、TaskList、图片预处理。
/// 这些是双宽字符展示和表格对齐的关键逻辑。
/// </summary>
public class MarkdownConverterCharWidthTests
{
    // ══════════════════════════════════════════════════════
    //  CharDisplayWidth — CJK 双宽字符
    // ══════════════════════════════════════════════════════

    [Theory]
    [InlineData('中', 2)] // CJK Unified Ideograph
    [InlineData('文', 2)] // CJK Unified Ideograph
    [InlineData('日', 2)] // CJK Unified Ideograph
    [InlineData('本', 2)] // CJK Unified Ideograph
    [InlineData('語', 2)] // CJK Unified Ideograph
    [InlineData('한', 2)] // Hangul (Korean)
    [InlineData('あ', 2)] // Hiragana (Japanese)
    [InlineData('ア', 2)] // Katakana (Japanese)
    [InlineData('《', 2)] // CJK punctuation
    [InlineData('》', 2)] // CJK punctuation
    [InlineData('，', 2)] // Fullwidth comma
    [InlineData('。', 2)] // Fullwidth period
    public void CharDisplayWidth_Cjk_Returns2(char c, int expected)
    {
        Assert.Equal(expected, MarkdownConverterTesting.CharDisplayWidthPublic(c));
    }

    // ══════════════════════════════════════════════════════
    //  CharDisplayWidth — ASCII 单宽字符
    // ══════════════════════════════════════════════════════

    [Theory]
    [InlineData('a', 1)]
    [InlineData('A', 1)]
    [InlineData('1', 1)]
    [InlineData(' ', 1)]
    [InlineData('.', 1)]
    [InlineData('-', 1)]
    public void CharDisplayWidth_Ascii_Returns1(char c, int expected)
    {
        Assert.Equal(expected, MarkdownConverterTesting.CharDisplayWidthPublic(c));
    }

    // ══════════════════════════════════════════════════════
    //  CharDisplayWidth — Zero-Width Characters
    // ══════════════════════════════════════════════════════

    [Theory]
    [InlineData('\u200B', 0)] // ZWSP
    [InlineData('\uFE0F', 0)] // Variation selector
    [InlineData('\u200D', 0)] // ZWJ
    [InlineData('\u200C', 0)] // ZWNJ
    public void CharDisplayWidth_ZeroWidth_Returns0(char c, int expected)
    {
        Assert.Equal(expected, MarkdownConverterTesting.CharDisplayWidthPublic(c));
    }

    // ══════════════════════════════════════════════════════
    //  CharDisplayWidth — Combining Marks
    // ══════════════════════════════════════════════════════

    [Theory]
    [InlineData('\u0300', 0)] // Combining grave accent (◌̀)
    [InlineData('\u0301', 0)] // Combining acute accent (◌́)
    [InlineData('\u0308', 0)] // Combining diaeresis (◌̈)
    public void CharDisplayWidth_CombiningMark_Returns0(char c, int expected)
    {
        Assert.Equal(expected, MarkdownConverterTesting.CharDisplayWidthPublic(c));
    }

    // ══════════════════════════════════════════════════════
    //  CharDisplayWidth — BMP Emoji (width 2 on most terminals)
    // ══════════════════════════════════════════════════════

    [Theory]
    [InlineData('⭐', 2)] // U+2B50
    [InlineData('❤', 2)] // U+2764
    [InlineData('☕', 2)] // U+2615
    [InlineData('⚠', 2)] // U+26A0
    [InlineData('✨', 2)] // U+2728
    [InlineData('✅', 2)] // U+2705
    [InlineData('❌', 2)] // U+274C
    [InlineData('⬛', 2)] // U+2B1B
    public void CharDisplayWidth_EmojiBmp_Returns2(char c, int expected)
    {
        Assert.Equal(expected, MarkdownConverterTesting.CharDisplayWidthPublic(c));
    }

    // ══════════════════════════════════════════════════════
    //  CharDisplayWidth — Special width-1 symbols
    // ══════════════════════════════════════════════════════

    [Theory]
    [InlineData('♀', 1)] // U+2640 — wcwidth=1 (text presentation default)
    [InlineData('♂', 1)] // U+2642 — wcwidth=1 (text presentation default)
    public void CharDisplayWidth_Width1Symbols_Returns1(char c, int expected)
    {
        Assert.Equal(expected, MarkdownConverterTesting.CharDisplayWidthPublic(c));
    }

    // ══════════════════════════════════════════════════════
    //  VisibleLength — Mixed CJK + ASCII
    // ══════════════════════════════════════════════════════

    [Fact]
    public void VisibleLength_MixedCjkAscii_ReturnsCorrect()
    {
        // "你好AB" = 2+2+1+1 = 6
        Assert.Equal(6, MarkdownConverterTesting.VisibleLengthPublic("你好AB"));
    }

    [Fact]
    public void VisibleLength_PureAscii_ReturnsStringLength()
    {
        Assert.Equal(5, MarkdownConverterTesting.VisibleLengthPublic("hello"));
    }

    [Fact]
    public void VisibleLength_PureCjk_ReturnsDoubleLength()
    {
        Assert.Equal(8, MarkdownConverterTesting.VisibleLengthPublic("测试文本"));
    }

    [Fact]
    public void VisibleLength_EmptyString_Returns0()
    {
        Assert.Equal(0, MarkdownConverterTesting.VisibleLengthPublic(""));
    }

    [Fact]
    public void VisibleLength_BoldFormatting_ExcludesMarkers()
    {
        // **bold** → visible = "bold" = 4
        Assert.Equal(4, MarkdownConverterTesting.VisibleLengthPublic("**bold**"));
    }

    [Fact]
    public void VisibleLength_ItalicFormatting_ExcludesMarkers()
    {
        Assert.Equal(6, MarkdownConverterTesting.VisibleLengthPublic("*italic*"));
    }

    [Fact]
    public void VisibleLength_Strikethrough_ExcludesMarkers()
    {
        Assert.Equal(3, MarkdownConverterTesting.VisibleLengthPublic("~~del~~"));
    }

    [Fact]
    public void VisibleLength_Link_ExcludesUrl()
    {
        // [text](http://url) → visible = "text" = 4
        Assert.Equal(4, MarkdownConverterTesting.VisibleLengthPublic("[text](http://url)"));
    }

    [Fact]
    public void VisibleLength_InlineCode_ExcludesBackticks()
    {
        Assert.Equal(3, MarkdownConverterTesting.VisibleLengthPublic("`var`"));
    }

    [Fact]
    public void VisibleLength_EscapedChar_CountsOnlyVisible()
    {
        Assert.Equal(1, MarkdownConverterTesting.VisibleLengthPublic("\\["));
    }

    [Fact]
    public void VisibleLength_SimpleEmoji_Width2()
    {
        Assert.Equal(2, MarkdownConverterTesting.VisibleLengthPublic("❤"));
    }

    [Fact]
    public void VisibleLength_EmojiAndText_Mixed()
    {
        // "A❤中" = 1 + 2 + 2 = 5
        Assert.Equal(5, MarkdownConverterTesting.VisibleLengthPublic("A❤中"));
    }

    [Fact]
    public void VisibleLength_SurrogatePairEmoji_Width2()
    {
        // 🎉 = U+1F389 = high surrogate + low surrogate = width 2
        Assert.Equal(2, MarkdownConverterTesting.VisibleLengthPublic("🎉"));
    }

    [Fact]
    public void VisibleLength_UnderscoreInWord_CountsAsVisible()
    {
        // snake_case — underscores within words should count
        var len = MarkdownConverterTesting.VisibleLengthPublic("snake_case_var");
        Assert.True(len >= 12);
    }

    // ══════════════════════════════════════════════════════
    //  PreprocessTaskLists
    // ══════════════════════════════════════════════════════

    [Fact]
    public void TaskList_Checked_ConvertsToCheckbox()
    {
        var result = MarkdownConverterTesting.PreprocessTaskListsPublic("- [x] Done");
        Assert.Contains("☑", result);
        Assert.DoesNotContain("[x]", result);
    }

    [Fact]
    public void TaskList_Unchecked_ConvertsToEmptyBox()
    {
        var result = MarkdownConverterTesting.PreprocessTaskListsPublic("- [ ] Todo");
        Assert.Contains("☐", result);
        Assert.DoesNotContain("[ ]", result);
    }

    [Fact]
    public void TaskList_Mixed_CorrectlyConverted()
    {
        var input = "- [x] Done\n- [ ] Todo\n- [x] Also done";
        var result = MarkdownConverterTesting.PreprocessTaskListsPublic(input);
        Assert.Equal(3, result.Split("☑").Length - 1 + result.Split("☐").Length - 1);
    }

    [Fact]
    public void TaskList_EmptyInput_ReturnsEmpty()
    {
        var result = MarkdownConverterTesting.PreprocessTaskListsPublic("");
        Assert.Equal("", result);
    }

    // ══════════════════════════════════════════════════════
    //  IsCjk
    // ══════════════════════════════════════════════════════

    [Theory]
    [InlineData('中', true)]
    [InlineData('A', false)]
    [InlineData('1', false)]
    [InlineData('あ', true)]
    [InlineData('한', true)]
    [InlineData('ｱ', true)] // Halfwidth Katakana in FF00-FFEF range
    public void IsCjk_VariousChars_ReturnsCorrect(char c, bool expected)
    {
        Assert.Equal(expected, MarkdownConverterTesting.IsCjkPublic(c));
    }
}
