using Tui.Controls;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Text;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// Markup 转义口径（2026-09-27 统一）：
/// 1) <c>ToolCallRenderer.Escape</c> = 把字面量方括号转成 XenoAtom 的 <c>[[</c>/<c>]]</c>，
///    **只用于 <c>Markup(string)</c> 路径**（字符串拼接/变量）；
/// 2) <c>new Markup($"…")</c> 走插值字符串处理器，取值会被自动转义——插值里**不要**再手工 Escape；
/// 3) 纯文本消费方（`string` → `TextBlock`，如 Toast.Content）**不转义**。
/// 本文件把三条口径钉成可执行断言（失败即口径被破坏）。
/// </summary>
public sealed class MarkupEscapeTests
{
    private static string Parse(string markup)
    {
        var parser = new MarkupTextParser();
        return parser.Parse(markup, out _);
    }

    /// <summary>转义 → 解析 应还原原文（对任意文案成立：方括号、连续方括号、CJK、emoji）。</summary>
    [Theory]
    [InlineData("[系统] 提示")]
    [InlineData("a]b[c")]
    [InlineData("[[already]]")]
    [InlineData("角色[主角]·[反派]")]
    [InlineData("纯文本没有方括号")]
    [InlineData("emoji 🎭 [bold] 混排")]
    public void Escape_RoundTripsThroughMarkupParser(string text)
    {
        var escaped = ToolCallRenderer.Escape(text);
        Assert.Equal(text, Parse(escaped));
    }

    /// <summary>插值路径：取值里的方括号由处理器自动转义，解析后应原样保留（且标签被消化）。</summary>
    [Theory]
    [InlineData("[系统] 提示")]
    [InlineData("a]b[c")]
    [InlineData("[[already]]")]
    public void MarkupInterpolation_KeepsLiteralBracketsAndParsesTags(string text)
    {
        var markup = new Markup($"[dim]{text}[/]");
        var plain = Parse(markup.Text ?? "");
        Assert.Equal(text, plain); // 取值原样保留
        Assert.DoesNotContain("[dim]", plain); // 标签被解析而非当字面量
    }

    /// <summary>Markdown → Markup 转换同样必须用合法转义（否则对话里出现 backslash/双括号）。</summary>
    [Fact]
    public void MarkdownConverter_EscapesBracketsForMarkup()
    {
        var converted = MarkdownConverter.Convert(
            "这是 [系统] 提示与 [链接](https://example.com/a[b])",
            80
        );
        var plain = Parse(converted);
        Assert.Contains("[系统]", plain, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\[", plain, StringComparison.Ordinal);
    }
}
