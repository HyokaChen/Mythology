using System.Text.RegularExpressions;
using Tui.Controls;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Rendering;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// TUI 密钥输入契约：默认显示固定遮蔽 + 末 4 位，完整值只在聚焦时出现。
/// </summary>
public sealed class SecretTextBoxTests
{
    [Theory]
    [InlineData("sk-1234567890abcd", "••••••••abcd")]
    [InlineData("abcd", "••••")]
    [InlineData("abc", "•••")]
    public void Mask_KeepsOnlyLastFour(string secret, string expected)
    {
        Assert.Equal(expected, SecretTextBox.Mask(secret));
    }

    [Fact]
    public void Unfocused_RendersMaskSummary_WithLastFourCharacters()
    {
        var input = new SecretTextBox().Text("sk-1234567890abcd");
        var root = new VStack(input);

        var buffer = VisualSnapshotRenderer.Render(root, 24, 3);
        var rendered = StripMarkup(string.Join('\n', buffer.ToMarkupLines()));

        Assert.Contains("••••••••abcd", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-1234567890abcd", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Unfocused_NarrowField_StillShowsVisibleTail()
    {
        var input = new SecretTextBox().Text("sk-1234567890abcd");
        var root = new VStack(input);

        var buffer = VisualSnapshotRenderer.Render(root, 10, 3);
        var rendered = StripMarkup(string.Join('\n', buffer.ToMarkupLines()));

        Assert.Contains("abcd", rendered, StringComparison.Ordinal);
    }

    private static string StripMarkup(string text) =>
        Regex.Replace(text, @"\[[^\[\]]*\]", "");
}
