using Tui.Controls;
using XenoAtom.Terminal;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// ChromeText（外壳文本宽度安全）单元测试：
/// Clean 清洗 VS15/ZWJ/ZWNJ；Clip 按显示宽度截断（CJK/emoji 双宽，ASCII ".." 标记）；ForTitle 组合入口。
/// </summary>
public class ChromeTextTests
{
    [Fact]
    public void Clean_StripsVs15ZwjZwnj_KeepsVs16()
    {
        Assert.Equal("ABC", ChromeText.Clean("A\uFE0EB\u200DC\u200C"));
        Assert.Equal("A\uFE0FB", ChromeText.Clean("A\uFE0FB"));
    }

    [Fact]
    public void Clean_PassesPlainTextThrough()
    {
        Assert.Equal("你好，world", ChromeText.Clean("你好，world"));
        Assert.Equal("", ChromeText.Clean(""));
    }

    [Fact]
    public void Clip_LeavesTextWithinBudgetUnchanged()
    {
        Assert.Equal("你好", ChromeText.Clip("你好", 4));
        Assert.Equal("abc", ChromeText.Clip("abc", 3));
    }

    [Fact]
    public void Clip_TruncatesByDisplayWidth_WithAsciiMarker()
    {
        Assert.Equal("ab..", ChromeText.Clip("abcdef", 4));
        Assert.Equal("你..", ChromeText.Clip("你好世界", 4));
        Assert.Equal("你..", ChromeText.Clip("你好世界", 5));
    }

    [Fact]
    public void Clip_NeverExceedsRequestedWidth()
    {
        var cases = new[]
        {
            ("你好世界", 4),
            ("abcdef", 4),
            ("第一章·风起云涌", 8),
            ("abcdefg", 2),
        };
        foreach (var (text, max) in cases)
            Assert.True(
                TerminalTextUtility.GetWidth(ChromeText.Clip(text, max).AsSpan()) <= max,
                $"Clip(\"{text}\", {max}) 超宽"
            );
    }

    [Fact]
    public void ForTitle_CleansThenClips()
    {
        Assert.Equal("会..", ChromeText.ForTitle("会话\uFE0E列 表", 5));
        Assert.Equal("第三章", ChromeText.ForTitle("第三章", 30));
    }
}
