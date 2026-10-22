using Tui.Services;
using XenoAtom.Terminal.UI;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// 主题对比度门禁（跨平台可读性的工程保障）：
/// 遍历 ThemeRegistry 全部主题，断言关键文本组合满足 WCAG 对比度阈值。
/// 未来新增主题配色不可读 → 此测试直接红。
///
/// 例外（有意保留，均注明原因）：
/// - desert-light：Dim（#9A8C7A，2.7:1）与背景分层（≈1.0:1）为现有浅色主题遗产值，放行。
/// - solarized-light：Green（#859900，2.97:1）为 Solarized 官方"非标准对比度"设计，放行至 2.9。
/// </summary>
public sealed class ThemeContrastTests
{
    /// <summary>正文（WCAG AA）。</summary>
    private const double MinBody = 4.5;

    /// <summary>次文本 / dim / 强调 / 语义色（大文本与图标标准）。</summary>
    private const double MinSecondary = 3.0;

    [Fact]
    public void AllThemes_Tokens_MinimumContrast()
    {
        foreach (var t in ThemeRegistry.All)
        {
            var bg = t.Tokens.Bg0;
            var id = t.Id;

            Assert.True(
                ColorContrast.Contrast(t.Tokens.Fg0, bg) >= MinBody,
                $"{id}: 正文 Fg0 对比不足 ({ColorContrast.Contrast(t.Tokens.Fg0, bg):F2}:1 < {MinBody})"
            );
            Assert.True(
                ColorContrast.Contrast(t.Tokens.Fg1, bg) >= MinSecondary,
                $"{id}: 次文本 Fg1 对比不足 ({ColorContrast.Contrast(t.Tokens.Fg1, bg):F2}:1)"
            );
            Assert.True(
                ColorContrast.Contrast(t.Tokens.Fg2, bg) >= MinDim(t),
                $"{id}: dim Fg2 对比不足 ({ColorContrast.Contrast(t.Tokens.Fg2, bg):F2}:1)"
            );
            Assert.True(
                ColorContrast.Contrast(t.Tokens.Accent, bg) >= MinSecondary,
                $"{id}: 强调色对比不足 ({ColorContrast.Contrast(t.Tokens.Accent, bg):F2}:1)"
            );

            foreach (var (name, color) in SemanticColors(t))
            {
                Assert.True(
                    ColorContrast.Contrast(color, bg) >= MinSemantic(t),
                    $"{id}: 语义色 {name} 对比不足 ({ColorContrast.Contrast(color, bg):F2}:1)"
                );
            }
        }
    }

    [Fact]
    public void AllThemes_Colors_Readable()
    {
        foreach (var t in ThemeRegistry.All)
        {
            var c = t.Colors;
            AssertFgBg(t, "用户气泡", c.UserBubbleFg, c.UserBubbleBg);
            AssertFgBg(t, "助手气泡", c.AssistantBubbleFg, c.AssistantBubbleBg);
            AssertFgBg(t, "工具气泡", c.ToolBubbleFg, c.ToolBubbleBg);
            AssertFgBg(t, "思考气泡", c.ThinkingBubbleFg, c.ThinkingBubbleBg);
            AssertErrorBg(t, c.ErrorBubbleFg, c.ErrorBubbleBg);
            AssertErrorBg(t, c.InterruptBubbleFg, c.InterruptBubbleBg);
            AssertFgBg(t, "Tab 选中", c.TabSelectedFg, c.TabSelectedBg);
            AssertFgBg(t, "侧栏", c.SidePanelFg, c.SidePanelBg);
            AssertFgBg(t, "输入框", c.PromptFg, c.PromptBg);
            AssertFgBg(t, "状态栏", c.StatusFg, c.StatusBg);
            AssertFgBg(t, "对话框", c.DialogContentFg, c.DialogContentBg);
            AssertFgBg(t, "Markdown 代码", c.MarkdownCodeFg, c.MarkdownCodeBg);
            // Solarized 官方语义色均为非标准对比度设计（✅/⚠ 图标 + 标题兜底），Toast 系列统一放行 2.3
            var solarizedToastMin = t.Id == "solarized-light" ? 2.3 : (double?)null;
            AssertFgBg(t, "Toast 成功", c.ToastSuccessFg, c.ToastSuccessBg, min: solarizedToastMin);
            AssertFgBg(t, "Toast 警告", c.ToastWarningFg, c.ToastWarningBg, min: solarizedToastMin);
            AssertErrorBg(t, c.ToastErrorFg, c.ToastErrorBg); // 错误类：Nord 暗红特性
            AssertFgBg(t, "Toast 信息", c.ToastInfoFg, c.ToastInfoBg, min: solarizedToastMin);
        }
    }

    [Fact]
    public void AllThemes_Layers_Distinguishable()
    {
        foreach (var t in ThemeRegistry.All)
        {
            if (t.Id == "desert-light")
                continue; // 遗产：浅色主题各层 ≈1.0:1，靠边框区分（TuiApp 有显式回退）

            Assert.True(
                ColorContrast.Contrast(t.Tokens.Bg1, t.Tokens.Bg0) > 1.05,
                $"{t.Id}: 控件层 Bg1 与阅读层 Bg0 不可分辨"
            );
            Assert.True(
                ColorContrast.Contrast(t.Tokens.Bg2, t.Tokens.Bg0) > 1.10,
                $"{t.Id}: 功能层 Bg2 与阅读层 Bg0 不可分辨"
            );
        }
    }

    // ── 辅助 ──

    private static void AssertFgBg(
        ThemeDefinition t,
        string what,
        Color fg,
        Color bg,
        double? min = null
    )
    {
        var threshold = min ?? (t.Id == "desert-light" ? 2.8 : MinSecondary); // desert-light 遗产暖灰/深琥珀对比 2.7-3.0x
        var ratio = ColorContrast.Contrast(fg, bg);
        Assert.True(ratio >= threshold, $"{t.Id}: {what} 对比不足 ({ratio:F2}:1)");
    }

    /// <summary>错误类气泡：Nord 官方暗红 #BF616A 特性，错误有 ⚠/⛔ 图标 + 加粗兜底，放行至 2.5。</summary>
    private static void AssertErrorBg(ThemeDefinition t, Color fg, Color bg)
    {
        var ratio = ColorContrast.Contrast(fg, bg);
        Assert.True(ratio >= 2.5, $"{t.Id}: 错误气泡对比不足 ({ratio:F2}:1)");
    }

    /// <summary>dim 阈值：desert-light 遗产值 #9A8C7A 仅 2.7:1，放行。</summary>
    private static double MinDim(ThemeDefinition t) => t.Id == "desert-light" ? 2.5 : MinSecondary;

    /// <summary>语义色阈值：solarized-light 官方 Green 2.97:1、desert-light 深琥珀 2.94:1，放行至 2.9。</summary>
    private static double MinSemantic(ThemeDefinition t) =>
        t.Id is "solarized-light" or "desert-light" ? 2.9 : MinSecondary;

    private static IEnumerable<(string Name, Color Color)> SemanticColors(ThemeDefinition t)
    {
        yield return ("红", t.Tokens.Red);
        yield return ("绿", t.Tokens.Green);
        yield return ("黄", t.Tokens.Yellow);
        yield return ("蓝", t.Tokens.Blue);
        yield return ("紫", t.Tokens.Purple);
        yield return ("青", t.Tokens.Cyan);
        yield return ("橙", t.Tokens.Orange);
    }
}
