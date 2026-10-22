// 功能：终端二维码渲染测试 —— 字形集合、行列比例、异常输入兜底

using Tui.Controls;
using Xunit;

namespace Tui.Tests;

public sealed class TerminalQrTests
{
    private const string RealQrContent =
        "https://liteapp.weixin.qq.com/q/7GiQu1?qrcode=a6c329f83d2f068527a771dfcff74cc4&bot_type=3";

    [Fact]
    public void RendersOnlyHalfBlockGlyphs()
    {
        var rendered = TerminalQr.Render(RealQrContent);

        Assert.NotNull(rendered);
        // 字形集合固定：多出别的字符就意味着行宽计算错了（终端会错位）
        Assert.All(
            rendered,
            ch => Assert.True(ch is '█' or '▀' or '▄' or ' ' or '\n', $"意外字符：{(int)ch}")
        );
    }

    [Fact]
    public void RowsAreHalfOfColumnsSoTheCodeLooksSquare()
    {
        var rendered = TerminalQr.Render(RealQrContent)!;
        var lines = rendered.Split('\n');

        var cols = lines[0].Length;
        // 终端格子高宽比约 2:1：两行模块合成一行字符
        Assert.InRange(lines.Length, cols / 2 - 1, cols / 2 + 1);
    }

    [Fact]
    public void AllRowsHaveEqualWidth()
    {
        var rendered = TerminalQr.Render(RealQrContent)!;
        var lines = rendered.Split('\n');

        Assert.All(lines, line => Assert.Equal(lines[0].Length, line.Length));
    }

    [Fact]
    public void HasQuietZoneBorder()
    {
        // 四周留白：没有它，微信扫描识别率会明显下降
        var rendered = TerminalQr.Render(RealQrContent)!;
        var lines = rendered.Split('\n');

        Assert.Equal(new string(' ', lines[0].Length), lines[0]);
        Assert.Equal(new string(' ', lines[0].Length), lines[^1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankInputReturnsNull(string text)
    {
        // null 而不是抛异常：兜底文案由调用方决定（设备流是"请手动输入用户码"，微信是"请重试"）
        Assert.Null(TerminalQr.Render(text));
    }

    [Fact]
    public void ShorterTextYieldsSmallerCode()
    {
        var small = TerminalQr.Render("hi")!;
        var large = TerminalQr.Render(RealQrContent)!;

        Assert.True(
            small.Split('\n').Length <= large.Split('\n').Length,
            "短文本的版本应当不高于长文本"
        );
    }
}
