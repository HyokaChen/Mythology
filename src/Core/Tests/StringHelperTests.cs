using Core.Shared;
using Xunit;

namespace Core.Tests;

public class StringHelperTests
{
    [Fact]
    public void Truncate_ShortString_ReturnsOriginal()
    {
        var result = "hello".Truncate(10);
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Truncate_ExactLength_ReturnsOriginal()
    {
        var result = "hello".Truncate(5);
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Truncate_LongString_TruncatesWithEllipsis()
    {
        var result = "hello world".Truncate(8);
        Assert.Equal("hello w…", result);
    }

    [Fact]
    public void Truncate_EmptyString_ReturnsEmpty()
    {
        var result = "".Truncate(10);
        Assert.Equal("", result);
    }

    [Fact]
    public void Truncate_NullString_ThrowsNullReference()
    {
        string? s = null;
        Assert.Throws<NullReferenceException>(() => s!.Truncate(10));
    }

    [Fact]
    public void Truncate_CustomSuffix_UsesCustomSuffix()
    {
        var result = "hello world".Truncate(8, "..");
        Assert.Equal("hello ..", result);
    }

    [Fact]
    public void Truncate_SuffixLongerThanMaxLength_ReturnsSuffixOnly()
    {
        var result = "hello".Truncate(2, "***");
        Assert.Equal("***", result);
    }

    [Fact]
    public void Truncate_MaxLengthZero_ReturnsSuffixOnly()
    {
        var result = "hello".Truncate(0, "…");
        Assert.Equal("…", result);
    }

    [Fact]
    public void Truncate_SuffixLengthEqualsMaxLength_ReturnsSuffixOnly()
    {
        var result = "hello world".Truncate(3, "...");
        Assert.Equal("...", result);
    }

    [Fact]
    public void Truncate_UnicodeCharacters_TruncatesCorrectly()
    {
        var result = "你好世界啊".Truncate(4);
        Assert.Equal("你好世…", result);
    }
}
