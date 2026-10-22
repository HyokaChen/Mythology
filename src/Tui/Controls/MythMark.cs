using System.Text;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace Tui.Controls;

/// <summary>
/// Mythology 品牌 logo 小点缀字符画（方案 A — 预生成半块字符 + ANSI 真彩）。
/// 数据由 assets/mythology-mark.svg 栅格化生成（10 列 × 7 字符行），作为标题旁的点缀；
/// 每格由上下两个像素行组成：'▀' 用前景/背景色同时表达两行，透明半格透出面板背景。
/// 仅依赖标准 ANSI 真彩（Host 已强制 COLORTERM=truecolor）+ 等宽块字符，
/// Apple Terminal 与各平台终端通用；主题无关，无需随主题重建。
/// </summary>
internal static class MythMark
{
    /// <summary>调色板（索引 0..3 对应数据字符 '0'..'3'，'.' = 透明）。</summary>
    private static readonly Color[] Palette =
    [
        Color.Rgb(0x0A, 0x1D, 0x08), // 0 墨 — 书页轮廓 / 中缝
        Color.Rgb(0xC9, 0xD5, 0xC5), // 1 鼠尾草绿 — 左页
        Color.Rgb(0xE3, 0xC9, 0xD0), // 2 玫瑰粉 — 右页
        Color.Rgb(0x2B, 0x6B, 0x5E), // 3 青 — 中心向前路径
    ];

    /// <summary>列宽（字符列）。</summary>
    internal const int Width = 10;

    /// <summary>字符行数（像素行 ÷ 2）。</summary>
    internal static int Height => MarkPixels.Length / 2;

    // ── 像素行数据：每 2 行组成 1 个字符行（▀ 的上半/下半）──
    // 字符为调色板索引（'0'-'3'），'.' 表示透明。
    // 注：数据由 SVG 生成，勿手改；改版时重新栅格化替换。
    // internal：测试缝（Tui.Tests 通过 InternalsVisibleTo 访问）。
    internal static readonly string[] MarkPixels =
    [
        "0........0",
        "010....022",
        "0110..0222",
        "0111..2222",
        "01110.2222",
        "01110.2222",
        "01110.2222",
        "01110.2222",
        "01110.2222",
        "01110.3220",
        "..010.20..",
        "...0030...",
    ];

    /// <summary>构建 logo 画布（尺寸固定；透明处透出面板背景，主题无关）。</summary>
    internal static Canvas Build()
    {
        var height = Height;
        return new Canvas(ctx => Draw(ctx, height))
        {
            MinWidth = Width,
            MaxWidth = Width,
            MinHeight = height,
            MaxHeight = height,
        };
    }

    /// <summary>逐格绘制：每格上下两个像素行 → '▀' 前景/背景双色；透明半格仅设对应前景色。</summary>
    private static void Draw(CanvasContext ctx, int rows)
    {
        for (var r = 0; r < rows; r++)
        {
            var top = MarkPixels[2 * r];
            var bot = MarkPixels[2 * r + 1];
            for (var c = 0; c < Width; c++)
            {
                var t = PaletteIndex(top[c]);
                var b = PaletteIndex(bot[c]);
                if (t is null && b is null)
                    continue; // 全透明 → 面板背景透出

                var rune = t is not null ? new Rune('▀') : new Rune('▄');
                var style = Style.None;
                if (t is { } ti)
                    style = style.WithForeground(Palette[ti]);
                if (b is { } bi)
                    style = style.WithBackground(Palette[bi]);
                ctx.SetPixel(c, r, rune, style);
            }
        }
    }

    /// <summary>数据字符 → 调色板索引；'.' 或未知字符 → 透明。</summary>
    private static int? PaletteIndex(char ch) =>
        ch switch
        {
            '.' => null,
            >= '0' and <= '3' => ch - '0',
            _ => null,
        };
}
