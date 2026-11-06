using System.Text;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Rendering;
using XenoAtom.Terminal.UI.Styling;

namespace Tui.Controls;

/// <summary>
/// TUI 密钥输入：失焦时显示固定长度遮蔽 + 末 4 位，聚焦时显示完整值。
/// 复用 TextBox 的编辑/滚动/选择行为，只覆写展示态绘制。
/// </summary>
internal sealed class SecretTextBox : TextBox
{
    public const int VisibleTailLength = 4;
    private const int MaskLength = 8;

    public SecretTextBox()
    {
        IsPassword = true;
        PasswordRevealMode = PasswordRevealMode.WhileFocused;
    }

    /// <summary>展示态摘要：保留末 4 位；短于等于 4 位时全部遮蔽。</summary>
    public static string Mask(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
            return "";

        return secret.Length <= VisibleTailLength
            ? new string('•', secret.Length)
            : $"{new string('•', MaskLength)}{secret[^VisibleTailLength..]}";
    }

    /// <inheritdoc />
    protected override void RenderOverride(CellBuffer buffer)
    {
        base.RenderOverride(buffer);

        if (HasFocus || string.IsNullOrWhiteSpace(Text))
            return;

        var theme = GetTheme();
        var style = GetTextBoxStyle();
        var padding = style.Padding;
        var inner = new Rectangle(
            Bounds.X + padding.Left,
            Bounds.Y + padding.Top,
            Math.Max(0, Bounds.Width - padding.Horizontal),
            Math.Max(0, Bounds.Height - padding.Vertical)
        );
        if (inner.Width <= 0 || inner.Height <= 0)
            return;

        var textStyle = style.BackgroundStyle(theme, focused: false);
        if (style.BackgroundBrush is { } backgroundBrush)
        {
            buffer.FillRectWithBrush(
                inner,
                textStyle,
                foregroundBrush: null,
                backgroundBrush,
                theme.GradientMixSpace
            );
        }
        else
        {
            for (var y = inner.Y; y < inner.Y + inner.Height; y++)
            for (var x = inner.X; x < inner.X + inner.Width; x++)
                buffer.SetCell(x, y, new Rune(' '), textStyle);
        }

        var masked = Mask(Text);
        if (masked.Length > inner.Width)
            masked = masked[^inner.Width..];
        if (masked.Length > 0)
            buffer.WriteText(inner.X, inner.Y, masked, textStyle);
    }
}
