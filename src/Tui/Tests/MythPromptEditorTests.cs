using Tui.Controls;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// MythPromptEditor 测试：Up/Down 在行内光标移动与历史导航之间的分界逻辑。
/// 多行粘贴内容时，Up 应先移动到首行，到达首行后才触发历史导航。
/// </summary>
public class MythPromptEditorTests
{
    // ── CountLineBreaksBefore：行号/总行数计算 ──

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("abc", 0)]
    public void CountLineBreaksBefore_SingleLineOrEmpty_ReturnsZero(string? text, int expected)
    {
        Assert.Equal(expected, MythPromptEditor.CountLineBreaksBefore(text ?? "", 100));
    }

    [Theory]
    [InlineData("a\nb", 0, 0)] // 光标在首行
    [InlineData("a\nb", 1, 0)] // 首行末尾
    [InlineData("a\nb", 2, 1)] // 光标在第二行起点
    [InlineData("a\nb", 3, 1)] // 第二行末尾
    [InlineData("x\ny\nz", 2, 1)] // 中间行（光标在 'y'）
    [InlineData("x\ny\nz", 4, 2)] // 末行（光标在 'z'）
    public void CountLineBreaksBefore_MultiLine_ReturnsCaretLine(
        string text,
        int caretIndex,
        int expected
    )
    {
        Assert.Equal(expected, MythPromptEditor.CountLineBreaksBefore(text, caretIndex));
    }

    [Theory]
    [InlineData("a\r\nb", 3, 1)] // \r\n 只算一个换行
    [InlineData("a\rb", 3, 1)] // 孤立 \r 也算换行
    [InlineData("a\r\nb", 1, 0)] // 光标落在 \r 与 \n 之间 → 仍属首行
    [InlineData("a\r\nb", 2, 1)] // 光标落在 \n 上 → 已跨入第二行
    public void CountLineBreaksBefore_CrLfAndLoneCr_CountedAsSingleBreak(
        string text,
        int caretIndex,
        int expected
    )
    {
        Assert.Equal(expected, MythPromptEditor.CountLineBreaksBefore(text, caretIndex));
    }

    [Fact]
    public void CountLineBreaksBefore_IndexBeyondText_ClampsToTextLength()
    {
        // 总行数语义：末行行号 = 换行数；行数 = 换行数 + 1
        Assert.Equal(2, MythPromptEditor.CountLineBreaksBefore("a\nb\nc", 999));
    }

    [Fact]
    public void CountLineBreaksBefore_TrailingNewLine_CountsEmptyLastLine()
    {
        // "a\n" = 两行（第二行为空），换行数为 1
        Assert.Equal(1, MythPromptEditor.CountLineBreaksBefore("a\n", 2));
        Assert.Equal(0, MythPromptEditor.CountLineBreaksBefore("a\n", 1));
    }

    // ── 导航分界语义（由 OnKeyDown 使用） ──

    [Fact]
    public void UpNavigationBoundary_CaretOnFirstLine_TriggersHistory()
    {
        // 粘贴了三行内容、光标在第一行（或单行文本）：Up 应导航历史而非移动光标
        var atFirstLine = MythPromptEditor.CountLineBreaksBefore("第一行\n第二行\n第三行", 1);
        Assert.Equal(0, atFirstLine); // 0 → 命中历史导航分支
    }

    [Fact]
    public void UpNavigationBoundary_CaretBelowFirstLine_StaysInText()
    {
        // 光标在第二/三行：Up 应先做行内上移（>0 → 交给基类）
        var belowFirstLine = MythPromptEditor.CountLineBreaksBefore("第一行\n第二行\n第三行", 4);
        Assert.True(belowFirstLine > 0);
    }

    [Fact]
    public void DownNavigationBoundary_CaretOnLastLine_TriggersHistory()
    {
        // 光标在末行：Down 应导航历史（行号 == 末行 → 命中历史分支）
        var text = "a\nb\nc";
        var caretLine = MythPromptEditor.CountLineBreaksBefore(text, 5);
        var lastLine = MythPromptEditor.CountLineBreaksBefore(text, text.Length);
        Assert.Equal(lastLine, caretLine);
    }

    [Fact]
    public void DownNavigationBoundary_CaretAboveLastLine_StaysInText()
    {
        // 光标在中间行：Down 应先做行内下移（行号 < 末行 → 交给基类）
        var text = "a\nb\nc";
        var caretLine = MythPromptEditor.CountLineBreaksBefore(text, 2);
        var lastLine = MythPromptEditor.CountLineBreaksBefore(text, text.Length);
        Assert.True(caretLine < lastLine);
    }
}
