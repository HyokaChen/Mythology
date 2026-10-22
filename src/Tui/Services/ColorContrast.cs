using XenoAtom.Terminal.UI;

namespace Tui.Services;

/// <summary>
/// 颜色对比度工具 — WCAG 2.x 相对亮度与对比度计算 + 颜色混合。
/// 供主题构建时验证可读性与单元测试门禁共用（见 ThemeContrastTests）。
/// </summary>
internal static class ColorContrast
{
    /// <summary>WCAG 相对亮度（0..1，越接近 1 越亮）。</summary>
    public static double Luminance(Color c)
    {
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

        static double Channel(byte v)
        {
            var x = v / 255.0;
            return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
    }

    /// <summary>
    /// WCAG 对比度（1..21，越大越好；正文 AA ≥ 4.5，大文本/图标 ≥ 3.0）。
    /// </summary>
    public static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>线性混合：结果 = bg 与 fg 按 t（0..1）混合，t=1 完全变为 fg。</summary>
    public static Color Mix(Color bg, Color fg, double t) =>
        Color.Rgb(
            (byte)Math.Round(bg.R + (fg.R - bg.R) * t),
            (byte)Math.Round(bg.G + (fg.G - bg.G) * t),
            (byte)Math.Round(bg.B + (fg.B - bg.B) * t)
        );

    /// <summary>向目标色过渡 t（0..1），用于提亮（target=白）或压暗（target=黑）。</summary>
    public static Color Toward(Color c, Color target, double t) => Mix(c, target, t);
}
