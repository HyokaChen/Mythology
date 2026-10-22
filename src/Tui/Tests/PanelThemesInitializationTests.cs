using Tui.Controls;
using Xunit;

namespace Tui.Tests;

/// <summary>
/// PanelThemes.Current 静态初始化与 ThemeStyles 派生函数回归测试。
/// 防止：静态初始化顺序问题（属性初始化器先于 Dark 字段执行 → Current 为 null →
/// SetUpPanel 构造时 ThemeStyles.Build* 抛 NullReferenceException，TUI 启动崩溃）。
/// </summary>
public sealed class PanelThemesInitializationTests
{
    [Fact]
    public void PanelThemes_Current_Initialized_NotNull()
    {
        // 静态构造正确填充（不为 null）；其他测试可能已切换主题，故只断言非 null 与颜色有效
        Assert.NotNull(PanelThemes.Current);
        Assert.NotEqual(default, PanelThemes.Current.ConversationBg);
        Assert.NotEqual(default, PanelThemes.Current.DialogContentBg);
    }

    [Fact]
    public void ThemeStyles_Build_OnDark_NoNullReference()
    {
        var c = PanelThemes.Current;
        // 覆盖 SetUpPanel 构造路径（分区栏 / 工具带 / 分区内容）使用的全部派生函数
        _ = ThemeStyles.BuildDialog(c);
        _ = ThemeStyles.BuildTabControl(c);
        _ = ThemeStyles.BuildCollapsible(c);
        _ = ThemeStyles.BuildButton(c);
        _ = ThemeStyles.BuildButtonAccent(c); // 用户崩溃点
        _ = ThemeStyles.BuildButtonDanger(c);
        _ = ThemeStyles.BuildCloseButton(c);
        _ = ThemeStyles.BuildSwitch(c);
    }

    /// <summary>
    /// 弹层家族（Popup / OptionList / ListBox / MenuList / Tooltip）必须都带主题浮层底色。
    /// 框架默认让它们各用自带主题（中性冷灰 #1E2D37 / 基础底 #1F1F1F）——在暖色主题里
    /// 就是「一块黑」（用户实机报障：下拉 / ⋯ 菜单展开一块黑，对话框左右两侧都出现）。
    /// 注入入口：<see cref="PanelThemes.InjectOverlayStyles"/>（ThemeService 与测试都调它）。
    /// </summary>
    [Fact]
    public void OverlayStyles_CarryThemeSurfaceBackground()
    {
        var c = PanelThemes.Dark;
        var expected = c.DialogContentBg;

        static void AssertBg(
            XenoAtom.Terminal.UI.Style? style,
            XenoAtom.Terminal.UI.Color want,
            string what
        )
        {
            Assert.True(
                style is { } s && s.TryGetBackground(out var bg) && bg == want,
                $"{what} 的背景不是主题浮层色（期望 {want}）"
            );
        }

        AssertBg(PanelThemes.BuildPopup(c).SurfaceStyle, expected, "Popup.SurfaceStyle");
        AssertBg(PanelThemes.BuildOptionList(c).Item, expected, "OptionList.Item");
        AssertBg(PanelThemes.BuildListBox(c).Item, expected, "ListBox.Item");
        AssertBg(PanelThemes.BuildMenuList(c).ItemStyle, expected, "MenuList.ItemStyle");
        AssertBg(PanelThemes.BuildTooltip(c).SurfaceStyle, expected, "Tooltip.SurfaceStyle");
    }

    /// <summary>
    /// TabControl 的 strip / 未选中 tab 底色必须等于对话框内容面（Pill Tab）。
    /// 框架重画 strip 时会算错宽度（用户实机：切 tab 后一条色块在对话框右缘内外移动）——
    /// 底色与面同色后那类错画在视觉上消失；独立「tab 条色」不得回潮。
    /// </summary>
    [Fact]
    public void TabStyles_StripBlendsWithDialogSurface()
    {
        var c = PanelThemes.Dark;
        var style = ThemeStyles.BuildTabControl(c);

        static void AssertBg(
            XenoAtom.Terminal.UI.Style? s,
            XenoAtom.Terminal.UI.Color want,
            string what
        )
        {
            Assert.True(
                s is { } v && v.TryGetBackground(out var bg) && bg == want,
                $"{what} 的底色不是对话框内容面（期望 {want}）"
            );
        }

        AssertBg(style.StripStyle, c.DialogContentBg, "StripStyle");
        AssertBg(style.TabStyle, c.DialogContentBg, "TabStyle（未选中）");
        AssertBg(style.OverflowButtonStyle, c.DialogContentBg, "OverflowButtonStyle");
    }

    [Fact]
    public void PanelThemes_Current_Settable_ForThemeService()
    {
        // ThemeService 切换主题时会写 Current；验证 setter 可用且生效
        PanelThemes.Current = PanelThemes.Light;
        try
        {
            Assert.Same(PanelThemes.Light, PanelThemes.Current);
            Assert.Equal(PanelThemes.Light.DialogContentBg, PanelThemes.DesertContentBg);
        }
        finally
        {
            PanelThemes.Current = PanelThemes.Dark; // 还原，避免影响其他测试
        }
    }
}
