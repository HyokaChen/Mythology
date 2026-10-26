using Core.Vector;
using Xunit;

namespace Core.Tests;

public sealed class LexicalTextTests
{
    [Fact]
    public void ToIndexText_SplitsChineseIntoWords()
    {
        var tokens = LexicalText
            .ToIndexText("对过往有着近乎固执的眷恋，灵魂仍在共鸣。")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains("眷恋", tokens);
        Assert.Contains("灵魂", tokens);
    }

    [Fact]
    public void BuildMatchQuery_UsesQuotedOr()
    {
        var query = LexicalText.BuildMatchQuery("眷恋 灵魂");

        Assert.Equal("\"眷恋\" OR \"灵魂\"", query);
    }

    [Fact]
    public void BuildMatchQuery_CanUseAndForPrecision()
    {
        var query = LexicalText.BuildMatchQuery("眷恋 灵魂", useOr: false);

        Assert.Equal("\"眷恋\" AND \"灵魂\"", query);
    }

    [Fact]
    public void BuildMatchQuery_EmptyWhenOnlyStopwords()
    {
        Assert.Equal("", LexicalText.BuildMatchQuery("的 了 是"));
    }
}
