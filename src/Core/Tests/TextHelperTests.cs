using Core.Shared;
using Xunit;

namespace Core.Tests;

public sealed class TextHelperTests
{
    // ── SplitParagraphs ──

    [Fact]
    public void SplitParagraphs_SingleParagraph_SingleEntry()
    {
        var result = TextHelper.SplitParagraphs("这是一段文字。");
        Assert.Single(result);
        Assert.Equal("这是一段文字。", result[0]);
    }

    [Fact]
    public void SplitParagraphs_DoubleNewline_Splits()
    {
        var result = TextHelper.SplitParagraphs("第一段。\n\n第二段。");
        Assert.Equal(2, result.Count);
        Assert.Equal("第一段。", result[0]);
        Assert.Equal("第二段。", result[1]);
    }

    [Fact]
    public void SplitParagraphs_SingleNewline_Splits()
    {
        var result = TextHelper.SplitParagraphs("第一段。\n第二段。");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void SplitParagraphs_CRLF_Splits()
    {
        var result = TextHelper.SplitParagraphs("第一段。\r\n\r\n第二段。");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void SplitParagraphs_TrailingNewline_NoEmptyEntry()
    {
        var result = TextHelper.SplitParagraphs("内容。\n\n");
        Assert.Single(result);
        Assert.Equal("内容。", result[0]);
    }

    [Fact]
    public void SplitParagraphs_LeadingNewline_NoEmptyEntry()
    {
        var result = TextHelper.SplitParagraphs("\n\n内容。");
        Assert.Single(result);
        Assert.Equal("内容。", result[0]);
    }

    [Fact]
    public void SplitParagraphs_MultipleBlankLines_NoEmptyEntries()
    {
        var result = TextHelper.SplitParagraphs("A\n\n\n\nB");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void SplitParagraphs_EmptyString_EmptyList()
    {
        var result = TextHelper.SplitParagraphs("");
        Assert.Empty(result);
    }

    [Fact]
    public void SplitParagraphs_WhitespaceOnly_EmptyList()
    {
        var result = TextHelper.SplitParagraphs("   \n\n  \n  ");
        Assert.Empty(result);
    }

    [Fact]
    public void SplitParagraphs_StringOverload_SameResult()
    {
        const string text = "第一段。\n\n第二段。";
        var spanResult = TextHelper.SplitParagraphs(text.AsSpan());
        var stringResult = TextHelper.SplitParagraphs(text);
        Assert.Equal(spanResult, stringResult);
    }

    // ── IsInDialogue ──

    [Fact]
    public void IsInDialogue_InsideAngleQuotes_True()
    {
        var text = "他说：「你好。」";
        // Position 4 is inside 「」
        Assert.True(TextHelper.IsInDialogue(text, 4));
    }

    [Fact]
    public void IsInDialogue_OutsideQuotes_False()
    {
        var text = "他说：「你好。」";
        // Position 0 is outside quotes
        Assert.False(TextHelper.IsInDialogue(text, 0));
    }

    [Fact]
    public void IsInDialogue_InsideDoubleQuotes_True()
    {
        var text = "他说：\"你好。\"";
        Assert.True(TextHelper.IsInDialogue(text, 5));
    }

    [Fact]
    public void IsInDialogue_NestedQuotes_CorrectTracking()
    {
        var text = "「他说『好』」";
        // Position inside 『』 should still be in dialogue
        Assert.True(TextHelper.IsInDialogue(text, 4));
    }

    [Fact]
    public void IsInDialogue_AfterClosingQuote_False()
    {
        var text = "「你好」结束";
        // Position after 」 should be false
        Assert.False(TextHelper.IsInDialogue(text, 5));
    }

    [Fact]
    public void IsInDialogue_PositionBeyondLength_False()
    {
        var text = "短文本";
        Assert.False(TextHelper.IsInDialogue(text, 100));
    }

    [Fact]
    public void IsInDialogue_StringOverload_SameResult()
    {
        const string text = "「测试内容」";
        var spanResult = TextHelper.IsInDialogue(text.AsSpan(), 3);
        var stringResult = TextHelper.IsInDialogue(text, 3);
        Assert.Equal(spanResult, stringResult);
    }
}
