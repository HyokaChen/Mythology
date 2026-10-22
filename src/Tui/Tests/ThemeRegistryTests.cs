using Tui.Controls;
using Tui.Services;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Styling;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// ThemeRegistry 测试：注册表完整性（Id 唯一、查找、深浅标记与背景亮度一致、
/// Desert 保留原配色、推导配色无默认色残留）。
/// </summary>
public sealed class ThemeRegistryTests
{
    [Fact]
    public void All_HasExpectedThemes()
    {
        Assert.Equal(8, ThemeRegistry.All.Count);
        Assert.Equal(
            new[]
            {
                "desert",
                "desert-light",
                "catppuccin",
                "nord",
                "tokyo-night",
                "gruvbox",
                "dracula",
                "solarized-light",
            },
            ThemeRegistry.All.Select(t => t.Id)
        );
    }

    [Fact]
    public void All_IdsUnique()
    {
        Assert.Equal(
            ThemeRegistry.All.Count,
            ThemeRegistry.All.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()
        );
    }

    [Fact]
    public void All_NamesAndDescriptionsNonEmpty()
    {
        foreach (var t in ThemeRegistry.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Name), $"{t.Id}: Name 为空");
            Assert.False(
                string.IsNullOrWhiteSpace(t.Tokens.Description),
                $"{t.Id}: Description 为空"
            );
        }
    }

    [Fact]
    public void Get_ByCaseInsensitiveId()
    {
        Assert.Same(ThemeRegistry.Desert, ThemeRegistry.Get("DESERT"));
        Assert.Same(ThemeRegistry.Catppuccin, ThemeRegistry.Get("catppuccin"));
        Assert.Null(ThemeRegistry.Get("nope"));
    }

    [Fact]
    public void IsDark_MatchesBackgroundLuminance()
    {
        foreach (var t in ThemeRegistry.All)
        {
            var lum = ColorContrast.Luminance(t.Tokens.Bg0);
            Assert.Equal(t.IsDark, lum < 0.5);
        }
    }

    [Fact]
    public void Desert_ColorsAreOriginalPalette()
    {
        Assert.Equal(PanelThemes.Dark, ThemeRegistry.Desert.Colors);
        Assert.Equal(PanelThemes.Light, ThemeRegistry.DesertLight.Colors);
    }

    [Fact]
    public void DesertLight_TextBoxFillMatchesDialogSurfaceUntilFocused()
    {
        var theme = ThemeRegistry.DesertLight.UiTheme;

        var normal = TextBoxStyle.Default.BackgroundStyle(theme, false);
        Assert.True(normal.TryGetBackground(out var normalBg));
        Assert.Equal(ThemeRegistry.DesertLight.Colors.DialogContentBg, normalBg);

        var focused = TextBoxStyle.Default.BackgroundStyle(theme, true);
        Assert.True(focused.TryGetBackground(out var focusedBg));
        Assert.Equal(Color.Rgb(0xFF, 0xFF, 0xFF), focusedBg);
    }

    [Fact]
    public void DerivedColors_KeyFieldsMapped()
    {
        // Catppuccin：ConversationBg = base，HeaderFg = accent（blue），SidePanelBg = surface2（最亮功能层）
        var cc = ThemeRegistry.Catppuccin.Colors;
        Assert.Equal(Color.Rgb(0x1E, 0x1E, 0x2E), cc.ConversationBg);
        Assert.Equal(Color.Rgb(0x89, 0xB4, 0xFA), cc.HeaderFg);
        Assert.Equal(Color.Rgb(0x58, 0x5B, 0x70), cc.SidePanelBg); // surface2
        Assert.Equal(Color.Rgb(0x45, 0x47, 0x5A), cc.DialogContentBg); // surface1 — 对话框亮于输入框

        // Solarized Light：正文 = base01（用户拍板偏离官方 base00）
        var sl = ThemeRegistry.SolarizedLight.Tokens;
        Assert.Equal(Color.Rgb(0x58, 0x6E, 0x75), sl.Fg0);
        Assert.False(sl.IsDark);

        // Gruvbox：语义色用 bright 变体（红色）
        Assert.Equal(Color.Rgb(0xFB, 0x49, 0x34), ThemeRegistry.Gruvbox.Tokens.Red);
    }
}
