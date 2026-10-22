using System.Text;
using Tui.Services;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;

namespace Tui.Controls;

/// <summary>
/// 控件样式派生器 — 从 <see cref="PanelColors"/> 参数化构建控件样式。
/// 语义映射与 Desert 原有样式一一对应（沙丘=SidePanelBg、月光=SidePanelFg、
/// 铜=AccentBlue、篝火=Border、亮月光=DialogContentFg 等），因此 Desert 主题的
/// 派生结果与原 PanelThemes.Desert* 视觉一致；其余主题获得语义等价的样式。
/// 变体样式（Accent/Danger/Close）在控件构建时派生（弹窗临时构建 → 跟随当前主题）；
/// 通用样式（Dialog/TabControl/Collapsible/Button）由 ThemeService 注入根视觉继承。
/// </summary>
internal static class ThemeStyles
{
    /// <summary>构建对话框样式 — 无边框纯色块（Desert 风格），面/壳同色。</summary>
    public static DialogStyle BuildDialog(PanelColors c) =>
        DialogStyle.Default with
        {
            Glyphs = PanelThemes.NoLineGlyphs,
            SurfaceStyle = Style
                .None.WithForeground(c.DialogContentFg)
                .WithBackground(c.DialogContentBg),
            BorderCellStyle = Style
                .None.WithForeground(c.DialogContentBg)
                .WithBackground(c.DialogContentBg),
            FocusedBorderCellStyle = Style
                .None.WithForeground(c.DialogContentBg)
                .WithBackground(c.DialogContentBg),
            LabelBackgroundStyle = Style
                .None.WithForeground(c.DialogContentFg)
                .WithBackground(c.DialogContentBg),
            ResizeHandleHoverStyle = Style
                .None.WithForeground(c.DialogContentFg)
                .WithBackground(c.DialogContentBg),
        };

    /// <summary>
    /// 构建滚动条样式 —— 轨道/滑块都取调色板的显式色值。
    ///
    /// 框架的 <c>ScrollBarStyle.Default</c> / <c>ScrollViewerStyle.Default</c> 里
    /// <c>TrackStyle</c> 与 <c>ThumbStyle</c> **都是 null**，渲染时落到终端默认底（黑）：
    /// 对话框里多行输入框内容一超长、hover 显出滚动条，就是一条黑竖条（用户实机报障：
    /// 「鼠标从对话框移到背板就出现黑条、切 tab 换位置」）。任何滚动条都必须有显式底色。
    /// </summary>
    public static ScrollBarStyle BuildScrollBar(PanelColors c) =>
        new()
        {
            Thickness = 1,
            MinThumbLength = 2,
            TrackStyle = Style
                .None.WithForeground(c.ScrollbarTrack)
                .WithBackground(c.ScrollbarTrack),
            ThumbStyle = Style
                .None.WithForeground(c.ScrollbarThumb)
                .WithBackground(c.ScrollbarThumb),
        };

    /// <summary>
    /// 构建 ScrollViewer 样式 —— 与 <see cref="BuildScrollBar"/> 同色。
    /// 内层 ScrollBar 的轨道/滑块由 viewer 端传入，只注入 ScrollBarStyle 不生效，两处都要给。
    /// </summary>
    public static ScrollViewerStyle BuildScrollViewer(PanelColors c) =>
        new()
        {
            ScrollBarThickness = 1,
            TrackStyle = Style
                .None.WithForeground(c.ScrollbarTrack)
                .WithBackground(c.ScrollbarTrack),
            ThumbStyle = Style
                .None.WithForeground(c.ScrollbarThumb)
                .WithBackground(c.ScrollbarThumb),
        };

    /// <summary>
    /// 构建 TabControl 样式 — Pill tab（DESIGN.md：透明底 + 选中态轻洗色）。
    ///
    /// strip 与未选中 tab 的底色取**对话框内容面**而不是独立的「tab 条色」：
    /// 框架重画 strip 时宽度会算错（用户实机：切 tab 后一条底色带出现在对话框右缘内外不同位置），
    /// 底色与面同色后，这类错画在视觉上消失。
    /// </summary>
    public static TabControlStyle BuildTabControl(PanelColors c) =>
        TabControlStyle.NoBorder with
        {
            TabSelectedStyle =
                Style.None.WithForeground(c.TabSelectedFg).WithBackground(c.TabSelectedBg)
                | TextStyle.Bold,
            TabStyle = Style.None.WithForeground(c.TabFg).WithBackground(c.DialogContentBg),
            TabHoveredStyle = Style
                .None.WithForeground(c.TabHoveredFg)
                .WithBackground(c.TabHoveredBg),
            StripStyle = Style.None.WithForeground(c.TabFg).WithBackground(c.DialogContentBg),
            OverflowPreviousRune = new Rune('◀'),
            OverflowNextRune = new Rune('▶'),
            OverflowButtonStyle = Style
                .None.WithForeground(c.TabFg)
                .WithBackground(c.DialogContentBg),
            OverflowButtonHoveredStyle = Style
                .None.WithForeground(c.TabHoveredFg)
                .WithBackground(c.TabStripBg),
            OverflowButtonPressedStyle = Style
                .None.WithForeground(c.TabSelectedFg)
                .WithBackground(c.DialogContentBg),
            OverflowButtonDisabledStyle = Style
                .None.WithForeground(c.DialogContentBg)
                .WithBackground(c.DialogContentBg),
        };

    /// <summary>构建折叠项样式 — Header 与侧栏同色，hover/焦点用强调色。</summary>
    public static CollapsibleStyle BuildCollapsible(PanelColors c) =>
        CollapsibleStyle.Default with
        {
            ExpandedGlyph = new Rune('▾'),
            CollapsedGlyph = new Rune('▸'),
            SpaceBetweenGlyphAndHeader = 1,
            ContentSpacing = 0,
            Header = Style.None.WithForeground(c.SidePanelFg).WithBackground(c.SidePanelBg),
            HeaderHovered = Style.None.WithForeground(c.AccentBlue).WithBackground(c.SidePanelBg),
            HeaderPressed = Style
                .None.WithForeground(c.DialogContentFg)
                .WithBackground(c.SidePanelBg),
            HeaderFocused = Style.None.WithForeground(c.AccentBlue).WithBackground(c.SidePanelBg),
        };

    /// <summary>构建默认按钮样式 — 边框色底 + 次文本，hover 强调色。</summary>
    public static ButtonStyle BuildButton(PanelColors c) =>
        ButtonStyle.Default with
        {
            Padding = new Thickness(1, 0, 1, 0),
            Normal = Style.None.WithForeground(c.SidePanelFg).WithBackground(c.Border),
            Hovered =
                Style.None.WithForeground(c.AccentBlue).WithBackground(c.SidePanelBg)
                | TextStyle.Bold,
            Pressed =
                Style.None.WithForeground(c.DialogContentFg).WithBackground(c.SidePanelBg)
                | TextStyle.Bold,
            Focused =
                Style.None.WithForeground(c.AccentBlue).WithBackground(c.Border)
                | TextStyle.Underline,
        };

    /// <summary>
    /// 构建分区分栏按钮样式 — 选中态对齐原 tab 选中态（篝火底 + 亮月光字 + 粗体），
    /// 未选中 = 面板底色 + 月光字（常态与 panel 融为一体，hover 点铜色）。
    /// </summary>
    public static ButtonStyle BuildSectionButton(PanelColors c, bool selected) =>
        ButtonStyle.Default with
        {
            Padding = new Thickness(1, 0, 1, 0),
            Normal =
                Style
                    .None.WithForeground(selected ? c.TabSelectedFg : c.TabFg)
                    .WithBackground(selected ? c.TabSelectedBg : c.TabStripBg)
                | (selected ? TextStyle.Bold : TextStyle.None),
            Hovered =
                Style
                    .None.WithForeground(selected ? c.TabSelectedFg : c.TabHoveredFg)
                    .WithBackground(selected ? c.TabSelectedBg : c.TabStripBg) | TextStyle.Bold,
            Pressed =
                Style.None.WithForeground(c.TabSelectedFg).WithBackground(c.TabSelectedBg)
                | TextStyle.Bold,
            Focused =
                Style
                    .None.WithForeground(selected ? c.TabSelectedFg : c.TabHoveredFg)
                    .WithBackground(selected ? c.TabSelectedBg : c.TabStripBg)
                | TextStyle.Underline,
        };

    /// <summary>构建强调按钮样式 — 主强调色底 + 深色文字（添加/创建等主要操作）。</summary>
    public static ButtonStyle BuildButtonAccent(PanelColors c) =>
        ButtonStyle.Default with
        {
            Padding = new Thickness(1, 0, 1, 0),
            Normal =
                Style.None.WithForeground(c.ConversationBg).WithBackground(c.AccentBlue)
                | TextStyle.Bold,
            Hovered =
                Style.None.WithForeground(c.ConversationBg).WithBackground(c.AccentYellow)
                | TextStyle.Bold,
            Pressed =
                Style
                    .None.WithForeground(c.ConversationBg)
                    .WithBackground(ColorContrast.Toward(c.AccentBlue, Color.Rgb(0, 0, 0), 0.25))
                | TextStyle.Bold,
            Focused =
                Style.None.WithForeground(c.ConversationBg).WithBackground(c.AccentBlue)
                | TextStyle.Bold
                | TextStyle.Underline,
        };

    /// <summary>构建危险按钮样式 — 红色系底 + 白字（删除/移除等破坏性操作）。</summary>
    public static ButtonStyle BuildButtonDanger(PanelColors c) =>
        ButtonStyle.Default with
        {
            Padding = new Thickness(1, 0, 1, 0),
            Normal =
                Style
                    .None.WithForeground(c.DialogContentFg)
                    .WithBackground(ColorContrast.Toward(c.AccentRed, Color.Rgb(0, 0, 0), 0.25))
                | TextStyle.Bold,
            Hovered =
                Style.None.WithForeground(c.DialogContentFg).WithBackground(c.AccentRed)
                | TextStyle.Bold,
            Pressed =
                Style
                    .None.WithForeground(c.DialogContentFg)
                    .WithBackground(ColorContrast.Toward(c.AccentRed, Color.Rgb(0, 0, 0), 0.45))
                | TextStyle.Bold,
            Focused =
                Style
                    .None.WithForeground(c.DialogContentFg)
                    .WithBackground(ColorContrast.Toward(c.AccentRed, Color.Rgb(0, 0, 0), 0.25))
                | TextStyle.Underline,
        };

    /// <summary>构建关闭按钮样式 — 融入标题条（面同色），强调色文字。</summary>
    public static ButtonStyle BuildCloseButton(PanelColors c) =>
        ButtonStyle.Default with
        {
            Padding = new Thickness(1, 0, 0, 0),
            ShowBorder = false,
            Normal = Style.None.WithForeground(c.AccentRed).WithBackground(c.DialogContentBg),
            Hovered =
                Style
                    .None.WithForeground(
                        ColorContrast.Toward(c.AccentRed, Color.Rgb(255, 255, 255), 0.15)
                    )
                    .WithBackground(c.DialogContentBg) | TextStyle.Bold,
            Pressed =
                Style.None.WithForeground(c.DialogContentFg).WithBackground(c.DialogContentBg)
                | TextStyle.Bold,
            Focused =
                Style.None.WithForeground(c.AccentRed).WithBackground(c.DialogContentBg)
                | TextStyle.Bold,
        };

    /// <summary>构建开关样式 — ON 强调色轨道，OFF 边框色轨道（圆形滑块）。</summary>
    public static SwitchStyle BuildSwitch(PanelColors c) =>
        SwitchStyle.Round with
        {
            TrackOnActive = Style
                .None.WithForeground(c.DialogContentFg)
                .WithBackground(c.AccentBlue),
            TrackOnInactive = Style.None.WithForeground(c.Border).WithBackground(c.SidePanelBg),
            TrackOffActive = Style.None.WithForeground(c.SidePanelFg).WithBackground(c.Border),
            TrackOffInactive = Style.None.WithForeground(c.Border).WithBackground(c.Border),
            ThumbOn = Style.None.WithForeground(c.ConversationBg).WithBackground(c.AccentBlue),
            ThumbOff = Style.None.WithForeground(c.SidePanelFg).WithBackground(c.SidePanelBg),
            TrackHovered = Style.None.WithForeground(c.SidePanelFg).WithBackground(c.Border),
            TrackFocused = Style.None.WithForeground(c.AccentBlue).WithBackground(c.SidePanelBg),
        };
}
