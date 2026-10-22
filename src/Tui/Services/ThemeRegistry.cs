using Tui.Controls;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Styling;

namespace Tui.Services;

/// <summary>
/// 主题注册表 — 全部内置主题的单一事实源。
/// 默认主题为 Desert（现有配色原样保留）；其余 6 个主题为社区知名配色
/// （Catppuccin Mocha / Nord / Tokyo Night / Gruvbox / Dracula / Solarized Light），
/// 色值取自各主题官方 palette，经语义映射 + 对比度门禁校验。
/// </summary>
public static class ThemeRegistry
{
    // ── Desert Dark 配色方案（声明在 Desert 字段之前：静态字段按文本顺序初始化）──
    /// <summary>Desert Dark 配色方案：基于暖棕色调的 16 色终端配色。</summary>
    private static readonly ColorScheme DesertScheme = new()
    {
        Name = "Desert Dark A",
        Background = Color.Rgb(0x1F, 0x1F, 0x1F), // 夜空
        Foreground = Color.Rgb(0xC7, 0xB2, 0x99), // 月光
        Black = Color.Rgb(0x1F, 0x1F, 0x1F), // 夜空
        Red = Color.Rgb(0xFB, 0x49, 0x34), // terracotta
        Green = Color.Rgb(0x8E, 0xC0, 0x7C), // sage green
        Yellow = Color.Rgb(0xFA, 0xBD, 0x2F), // golden amber
        Blue = Color.Rgb(0xD6, 0x8A, 0x48), // 铜色
        Purple = Color.Rgb(0xD3, 0x86, 0x9B), // desert rose
        Cyan = Color.Rgb(0x8E, 0xC0, 0x7C), // sage green
        White = Color.Rgb(0xC7, 0xB2, 0x99), // 月光
        BrightBlack = Color.Rgb(0x6E, 0x4A, 0x36), // 篝火岩
        BrightRed = Color.Rgb(0xFB, 0x94, 0x37), // amber orange
        BrightGreen = Color.Rgb(0xA9, 0xB6, 0x54), // soft sage
        BrightYellow = Color.Rgb(0xFA, 0xBD, 0x2F), // golden amber
        BrightBlue = Color.Rgb(0xD6, 0x8A, 0x48), // 铜色
        BrightPurple = Color.Rgb(0xD3, 0x86, 0x9B), // desert rose
        BrightCyan = Color.Rgb(0x8E, 0xC0, 0x7C), // sage green
        BrightWhite = Color.Rgb(0xFB, 0xF1, 0xC7), // 亮月光
    };

    // ── 默认主题：Desert（现有配色全量保留，不走推导）──

    /// <summary>Desert 深色（默认）— token 反推仅供门禁测试，Colors 用现有 PanelThemes.Dark。</summary>
    public static readonly ThemeDefinition Desert = New(
        new ThemeTokens
        {
            Id = "desert",
            Name = "Desert",
            Description = "沙漠暖色 · 默认",
            IsDark = true,
            Bg0 = Color.Rgb(0x1F, 0x1F, 0x1F),
            Bg1 = Color.Rgb(0x2D, 0x24, 0x24),
            Bg2 = Color.Rgb(0x5B, 0x3A, 0x29),
            Surface = Color.Rgb(0x3A, 0x3A, 0x3A),
            SurfaceAlt = Color.Rgb(0x6E, 0x4A, 0x36),
            Fg0 = Color.Rgb(0xC7, 0xB2, 0x99),
            Fg1 = Color.Rgb(0xC7, 0xB2, 0x99),
            Fg2 = Color.Rgb(0x8F, 0x82, 0x70),
            Accent = Color.Rgb(0xD6, 0x8A, 0x48),
            Red = Color.Rgb(0xFB, 0x49, 0x34),
            Green = Color.Rgb(0x8E, 0xC0, 0x7C),
            Yellow = Color.Rgb(0xFA, 0xBD, 0x2F),
            Blue = Color.Rgb(0xD6, 0x8A, 0x48),
            Purple = Color.Rgb(0xD3, 0x86, 0x9B),
            Cyan = Color.Rgb(0x8E, 0xC0, 0x7C),
            Orange = Color.Rgb(0xFB, 0x94, 0x37),
            SelectionBg = Color.Rgb(0x6E, 0x4A, 0x36),
        },
        colors: PanelThemes.Dark,
        uiTheme: BuildDesertTheme(),
        styles: DesertStyles()
    );

    /// <summary>Desert 浅色 — 现有 PanelThemes.Light 原样保留（防功能回退）。</summary>
    public static readonly ThemeDefinition DesertLight = New(
        new ThemeTokens
        {
            Id = "desert-light",
            Name = "Desert Light",
            Description = "沙漠浅色 · 暖奶油",
            IsDark = false,
            Bg0 = Color.Rgb(0xFB, 0xF1, 0xC7),
            Bg1 = Color.Rgb(0xF5, 0xF0, 0xE1),
            Bg2 = Color.Rgb(0xF5, 0xF0, 0xE1),
            Surface = Color.Rgb(0xEB, 0xDB, 0xB2),
            SurfaceAlt = Color.Rgb(0xD5, 0xC4, 0xA1),
            Fg0 = Color.Rgb(0x3C, 0x38, 0x36),
            Fg1 = Color.Rgb(0x4A, 0x3F, 0x35),
            Fg2 = Color.Rgb(0x9A, 0x8C, 0x7A),
            Accent = Color.Rgb(0xAF, 0x60, 0x14),
            Red = Color.Rgb(0xCC, 0x24, 0x1D),
            Green = Color.Rgb(0x4E, 0x7E, 0x3E),
            Yellow = Color.Rgb(0xAF, 0x87, 0x14),
            Blue = Color.Rgb(0xAF, 0x60, 0x14),
            Purple = Color.Rgb(0x6C, 0x71, 0xC4),
            Cyan = Color.Rgb(0x1F, 0x7A, 0x70), // 深青（对比达标；仅门禁/推导用，渲染链接仍走 PanelThemes.Light 深铜）
            Orange = Color.Rgb(0xD6, 0x5D, 0x00),
            SelectionBg = Color.Rgb(0xD5, 0xC4, 0xA1),
        },
        colors: PanelThemes.Light,
        styles: DesertLightStyles(),
        // Blend unfocused inputs into the dialog surface while retaining the framework's white focus fill.
        inputFill: PanelThemes.Light.DialogContentBg,
        inputFillFocused: Theme.DefaultLight.InputFillFocused
    );

    // ── Catppuccin Mocha ──

    public static readonly ThemeDefinition Catppuccin = New(
        new ThemeTokens
        {
            Id = "catppuccin",
            Name = "Catppuccin",
            Description = "Mocha · 柔和粉紫",
            IsDark = true,
            Bg0 = Color.Rgb(0x1E, 0x1E, 0x2E), // base
            Bg1 = Color.Rgb(0x31, 0x32, 0x44), // surface0 — 输入框/用户气泡
            Bg2 = Color.Rgb(0x58, 0x5B, 0x70), // surface2 — 侧栏（最亮功能层，拉开明暗）
            Surface = Color.Rgb(0x45, 0x47, 0x5A), // surface1 — 对话框浮层（亮于输入框）
            SurfaceAlt = Color.Rgb(0x6C, 0x70, 0x86), // overlay0 — 边框/分隔线
            Fg0 = Color.Rgb(0xCD, 0xD6, 0xF4), // text
            Fg1 = Color.Rgb(0xBA, 0xC2, 0xDE), // subtext1
            Fg2 = Color.Rgb(0xA6, 0xAD, 0xC8), // subtext0
            Accent = Color.Rgb(0x89, 0xB4, 0xFA), // blue
            Red = Color.Rgb(0xF3, 0x8B, 0xA8),
            Green = Color.Rgb(0xA6, 0xE3, 0xA1),
            Yellow = Color.Rgb(0xF9, 0xE2, 0xAF),
            Blue = Color.Rgb(0x89, 0xB4, 0xFA),
            Purple = Color.Rgb(0xCB, 0xA6, 0xF7), // mauve
            Cyan = Color.Rgb(0x94, 0xE2, 0xD5), // teal
            Orange = Color.Rgb(0xFA, 0xB3, 0x87), // peach
            SelectionBg = Color.Rgb(0x58, 0x5B, 0x70), // surface2
        }
    );

    // ── Nord ──

    private static readonly ThemeDefinition Nord = New(
        new ThemeTokens
        {
            Id = "nord",
            Name = "Nord",
            Description = "北极蓝 · 低饱和",
            IsDark = true,
            Bg0 = Color.Rgb(0x2E, 0x34, 0x40), // nord0
            Bg1 = Color.Rgb(0x3B, 0x42, 0x52), // nord1 — 输入框
            Bg2 = Color.Rgb(0x4C, 0x56, 0x6A), // nord3 — 侧栏（最亮）
            Surface = Color.Rgb(0x4C, 0x56, 0x6A), // nord3 — 对话框浮层（nord 色阶少，与侧栏同档，靠边框勾边）
            SurfaceAlt = Color.Rgb(0x5A, 0x66, 0x7E), // nord3 提亮 — 边框/分隔线
            Fg0 = Color.Rgb(0xEC, 0xEF, 0xF4), // nord6
            Fg1 = Color.Rgb(0xE5, 0xE9, 0xF0), // nord5
            Fg2 = Color.Rgb(0xD8, 0xDE, 0xE9), // nord4
            Accent = Color.Rgb(0x88, 0xC0, 0xD0), // nord8 frost
            Red = Color.Rgb(0xBF, 0x61, 0x6A), // nord11
            Green = Color.Rgb(0xA3, 0xBE, 0x8C), // nord14
            Yellow = Color.Rgb(0xEB, 0xCB, 0x8B), // nord13
            Blue = Color.Rgb(0x81, 0xA1, 0xC1), // nord9
            Purple = Color.Rgb(0xB4, 0x8E, 0xAD), // nord15
            Cyan = Color.Rgb(0x8F, 0xBC, 0xBB), // nord7
            Orange = Color.Rgb(0xD0, 0x87, 0x70), // nord12
            SelectionBg = Color.Rgb(0x4C, 0x56, 0x6A), // nord3
        }
    );

    // ── Tokyo Night ──

    private static readonly ThemeDefinition TokyoNight = New(
        new ThemeTokens
        {
            Id = "tokyo-night",
            Name = "Tokyo Night",
            Description = "东京之夜 · 高对比蓝紫",
            IsDark = true,
            Bg0 = Color.Rgb(0x1A, 0x1B, 0x26), // bg
            Bg1 = Color.Rgb(0x29, 0x2E, 0x42), // bg_highlight — 输入框
            Bg2 = Color.Rgb(0x41, 0x48, 0x68), // ansi black — 侧栏（灰蓝，明暗拉开）
            Surface = Color.Rgb(0x33, 0x46, 0x7C), // selection — 对话框浮层（蓝紫色相突出）
            SurfaceAlt = Color.Rgb(0x56, 0x5F, 0x89), // comment — 边框/分隔线
            Fg0 = Color.Rgb(0xC0, 0xCA, 0xF5), // fg
            Fg1 = Color.Rgb(0xA9, 0xB1, 0xD6), // terminal bright7
            Fg2 = Color.Rgb(0x9A, 0xA5, 0xCE), // comment 提亮（原 comment 对比仅 2.8:1，不可作 dim）
            Accent = Color.Rgb(0x7A, 0xA2, 0xF7), // blue
            Red = Color.Rgb(0xF7, 0x76, 0x8E),
            Green = Color.Rgb(0x9E, 0xCE, 0x6A),
            Yellow = Color.Rgb(0xE0, 0xAF, 0x68),
            Blue = Color.Rgb(0x7A, 0xA2, 0xF7),
            Purple = Color.Rgb(0xBB, 0x9A, 0xF7),
            Cyan = Color.Rgb(0x7D, 0xCF, 0xFF),
            Orange = Color.Rgb(0xFF, 0x9E, 0x64),
            SelectionBg = Color.Rgb(0x33, 0x46, 0x7C), // selection
        }
    );

    // ── Gruvbox Dark（medium contrast，语义色用 bright 变体）──

    public static readonly ThemeDefinition Gruvbox = New(
        new ThemeTokens
        {
            Id = "gruvbox",
            Name = "Gruvbox",
            Description = "复古暖棕 · retro",
            IsDark = true,
            Bg0 = Color.Rgb(0x28, 0x28, 0x28), // dark0
            Bg1 = Color.Rgb(0x3C, 0x38, 0x36), // dark1 — 输入框
            Bg2 = Color.Rgb(0x66, 0x5C, 0x54), // dark3 — 侧栏（最亮）
            Surface = Color.Rgb(0x50, 0x49, 0x45), // dark2 — 对话框浮层
            SurfaceAlt = Color.Rgb(0x7C, 0x6F, 0x64), // dark4 — 边框/分隔线
            Fg0 = Color.Rgb(0xEB, 0xDB, 0xB2), // light1
            Fg1 = Color.Rgb(0xD5, 0xC4, 0xA1), // light2
            Fg2 = Color.Rgb(0xA8, 0x99, 0x84), // light4
            Accent = Color.Rgb(0xFE, 0x80, 0x19), // bright_orange
            Red = Color.Rgb(0xFB, 0x49, 0x34), // bright_red
            Green = Color.Rgb(0xB8, 0xBB, 0x26), // bright_green
            Yellow = Color.Rgb(0xFA, 0xBD, 0x2F), // bright_yellow
            Blue = Color.Rgb(0x83, 0xA5, 0x98), // bright_blue
            Purple = Color.Rgb(0xD3, 0x86, 0x9B), // bright_purple
            Cyan = Color.Rgb(0x8E, 0xC0, 0x7C), // bright_aqua
            Orange = Color.Rgb(0xFE, 0x80, 0x19), // bright_orange
            SelectionBg = Color.Rgb(0x66, 0x5C, 0x54), // dark3
        }
    );

    // ── Dracula ──

    private static readonly ThemeDefinition Dracula = New(
        new ThemeTokens
        {
            Id = "dracula",
            Name = "Dracula",
            Description = "紫粉高饱和 · drac",
            IsDark = true,
            Bg0 = Color.Rgb(0x28, 0x2A, 0x36), // bg
            Bg1 = Color.Rgb(0x44, 0x47, 0x5A), // current line — 输入框
            Bg2 = Color.Rgb(0x44, 0x47, 0x5A), // 侧栏（Dracula 层少，与输入框同色，靠边框区分）
            Surface = Color.Rgb(0x62, 0x72, 0xA4), // comment — 对话框浮层（亮蓝紫灰，突出）
            SurfaceAlt = Color.Rgb(0x62, 0x72, 0xA4), // comment — 边框/分隔线
            Fg0 = Color.Rgb(0xF8, 0xF8, 0xF2), // fg
            Fg1 = Color.Rgb(0xE6, 0xE6, 0xE1), // fg 微暗
            Fg2 = Color.Rgb(0x85, 0x8A, 0xA0), // comment 提亮（原 comment 仅 3.0:1）
            Accent = Color.Rgb(0xBD, 0x93, 0xF9), // purple
            Red = Color.Rgb(0xFF, 0x55, 0x55),
            Green = Color.Rgb(0x50, 0xFA, 0x7B),
            Yellow = Color.Rgb(0xF1, 0xFA, 0x8C),
            Blue = Color.Rgb(0x8B, 0xE9, 0xFD), // cyan 兼作 blue
            Purple = Color.Rgb(0xBD, 0x93, 0xF9),
            Cyan = Color.Rgb(0x8B, 0xE9, 0xFD),
            Orange = Color.Rgb(0xFF, 0xB8, 0x6C),
            SelectionBg = Color.Rgb(0x44, 0x47, 0x5A),
        }
    );

    // ── Solarized Light（正文用 base01，偏离官方 base00 语义以达 4.5:1）──

    public static readonly ThemeDefinition SolarizedLight = New(
        new ThemeTokens
        {
            Id = "solarized-light",
            Name = "Solarized",
            Description = "Solarized · 浅色",
            IsDark = false,
            Bg0 = Color.Rgb(0xFD, 0xF6, 0xE3), // base3
            Bg1 = Color.Rgb(0xEE, 0xE8, 0xD5), // base2
            Bg2 = Color.Rgb(0xEE, 0xE8, 0xD5), // base2
            Surface = Color.Rgb(0xEE, 0xE8, 0xD5), // base2
            SurfaceAlt = Color.Rgb(0x93, 0xA1, 0xA1), // base1（边框）
            Fg0 = Color.Rgb(0x58, 0x6E, 0x75), // base01（正文，~5.0:1；官方正文 base00 仅 4.1:1）
            Fg1 = Color.Rgb(0x65, 0x7B, 0x83), // base00
            Fg2 = Color.Rgb(0x70, 0x80, 0x89), // base0/base00 之间（自定义，保持 dim ≥ 3:1）
            Accent = Color.Rgb(0x26, 0x8B, 0xD2), // blue
            Red = Color.Rgb(0xDC, 0x32, 0x2F),
            Green = Color.Rgb(0x85, 0x99, 0x00),
            Yellow = Color.Rgb(0xB5, 0x89, 0x00),
            Blue = Color.Rgb(0x26, 0x8B, 0xD2),
            Purple = Color.Rgb(0x6C, 0x71, 0xC4), // violet
            Cyan = Color.Rgb(0x2A, 0xA1, 0x98),
            Orange = Color.Rgb(0xCB, 0x4B, 0x16),
            SelectionBg = Color.Rgb(0x93, 0xA1, 0xA1), // base1
        }
    );

    /// <summary>全部主题（顺序即 /theme toggle 循环顺序）。</summary>
    public static readonly IReadOnlyList<ThemeDefinition> All =
    [
        Desert,
        DesertLight,
        Catppuccin,
        Nord,
        TokyoNight,
        Gruvbox,
        Dracula,
        SolarizedLight,
    ];

    /// <summary>按 Id 查找主题。</summary>
    public static ThemeDefinition? Get(string id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>构造注册项：Colors 默认走语义推导，UiTheme 默认走通用构建。</summary>
    /// <summary>构造注册项：Colors 默认走语义推导，UiTheme 默认走通用构建，样式默认从 Colors 派生。</summary>
    private static ThemeDefinition New(
        ThemeTokens tokens,
        PanelColors? colors = null,
        Theme? uiTheme = null,
        ThemeControlStyles? styles = null,
        Color? inputFill = null,
        Color? inputFillFocused = null
    )
    {
        var c = colors ?? ThemeMapper.Map(tokens);
        var s =
            styles
            ?? new ThemeControlStyles(
                ThemeStyles.BuildDialog(c),
                ThemeStyles.BuildTabControl(c),
                ThemeStyles.BuildCollapsible(c),
                ThemeStyles.BuildButton(c),
                ThemeStyles.BuildButtonAccent(c),
                ThemeStyles.BuildButtonDanger(c),
                ThemeStyles.BuildCloseButton(c),
                ThemeStyles.BuildSwitch(c)
            );
        return new ThemeDefinition
        {
            Tokens = tokens,
            Colors = c,
            UiTheme = uiTheme ?? BuildUiTheme(tokens, inputFill, inputFillFocused),
            ControlStyles = s,
        };
    }

    /// <summary>Desert 深色控件样式包（现有 PanelThemes 原值，视觉不变）。</summary>
    private static ThemeControlStyles DesertStyles() =>
        new(
            PanelThemes.DesertDialog,
            PanelThemes.DesertTabControl,
            PanelThemes.DesertCollapsible,
            PanelThemes.DesertButton,
            PanelThemes.DesertButtonAccent,
            PanelThemes.DesertButtonDanger,
            PanelThemes.DesertCloseButton,
            PanelThemes.DesertSwitch
        );

    /// <summary>Desert 浅色控件样式包（从浅色调色板派生）。</summary>
    private static ThemeControlStyles DesertLightStyles() =>
        new(
            ThemeStyles.BuildDialog(PanelThemes.Light),
            ThemeStyles.BuildTabControl(PanelThemes.Light),
            ThemeStyles.BuildCollapsible(PanelThemes.Light),
            ThemeStyles.BuildButton(PanelThemes.Light),
            ThemeStyles.BuildButtonAccent(PanelThemes.Light),
            ThemeStyles.BuildButtonDanger(PanelThemes.Light),
            ThemeStyles.BuildCloseButton(PanelThemes.Light),
            ThemeStyles.BuildSwitch(PanelThemes.Light)
        );

    /// <summary>
    /// 通用 XenoAtom Theme 构建：16 色 ColorScheme → Theme.FromScheme → palette-derived UI colors。
    /// 普通态和聚焦态输入底色可按主题覆盖；非 truecolor 终端由 ColorScheme 的 16 色映射自动降级。
    /// </summary>
    private static Theme BuildUiTheme(
        ThemeTokens t,
        Color? inputFill = null,
        Color? inputFillFocused = null
    )
    {
        var scheme = new ColorScheme
        {
            Name = t.Name,
            Background = t.Bg0,
            Foreground = t.Fg0,
            Black = t.Bg0,
            Red = t.Red,
            Green = t.Green,
            Yellow = t.Yellow,
            Blue = t.Accent,
            Purple = t.Purple,
            Cyan = t.Cyan,
            White = t.Fg0,
            BrightBlack = t.SurfaceAlt,
            BrightRed = t.Red,
            BrightGreen = t.Green,
            BrightYellow = t.Yellow,
            BrightBlue = t.Accent,
            BrightPurple = t.Purple,
            BrightCyan = t.Cyan,
            BrightWhite = t.Fg0,
        };
        var baseTheme = Theme.FromScheme(
            scheme,
            t.IsDark ? ThemeSchemeBrightness.Dark : ThemeSchemeBrightness.Light
        );
        return new Theme
        {
            Scheme = baseTheme.Scheme,
            Foreground = baseTheme.Foreground,
            Background = baseTheme.Background,
            Surface = baseTheme.Surface,
            SurfaceAlt = baseTheme.SurfaceAlt,
            // 弹层表面（下拉 / ⋯ 菜单 / 弹窗）：框架从 scheme 推导出的是中性冷灰
            // （Desert 下是 #1E2D37），在暖色主题里读起来就是「一块黑」——
            // 统一取 token 的 Surface（浮层色），与对话框内容面同色。
            PopupSurface = t.Surface,
            ControlFill = baseTheme.ControlFill,
            ControlFillHover = baseTheme.ControlFillHover,
            ControlFillPressed = baseTheme.ControlFillPressed,
            InputFill = inputFill ?? t.Bg1,
            InputFillFocused = inputFillFocused ?? ColorContrast.Mix(t.Bg1, t.Accent, 0.35),
            Border = baseTheme.Border,
            FocusBorder = t.Accent,
            Accent = baseTheme.Accent,
            Selection = baseTheme.Selection,
            Disabled = baseTheme.Disabled,
            Primary = baseTheme.Primary,
            Success = baseTheme.Success,
            Warning = baseTheme.Warning,
            Error = baseTheme.Error,
            Muted = baseTheme.Muted,
            Lines = baseTheme.Lines,
            ScrollBars = baseTheme.ScrollBars,
        };
    }

    // ── Desert Theme（从 ThemeService 迁移，原样保留）──

    /// <summary>构建 Desert 主题。</summary>
    private static Theme BuildDesertTheme()
    {
        var t = Theme.FromScheme(DesertScheme, ThemeSchemeBrightness.Dark);
        return new Theme
        {
            Scheme = t.Scheme,
            Foreground = t.Foreground,
            Background = t.Background,
            Surface = t.Surface,
            SurfaceAlt = t.SurfaceAlt,
            // 弹层表面：框架从 scheme 推导出的是中性冷灰（#1E2D37），暖色主题里像黑块——
            // 取「提亮夜空」浮层色，与对话框内容面一致
            PopupSurface = Color.Rgb(0x3A, 0x3A, 0x3A),
            ControlFill = t.ControlFill,
            ControlFillHover = t.ControlFillHover,
            ControlFillPressed = t.ControlFillPressed,
            InputFill = Color.Rgb(0x5B, 0x3A, 0x29), // 沙丘 — 输入框
            InputFillFocused = Color.Rgb(0x6E, 0x4A, 0x36), // 篝火岩 — 焦点输入
            Border = t.Border,
            FocusBorder = t.FocusBorder,
            Accent = t.Accent,
            Selection = t.Selection,
            Disabled = t.Disabled,
            Primary = t.Primary,
            Success = t.Success,
            Warning = t.Warning,
            Error = t.Error,
            Muted = t.Muted,
            Lines = t.Lines,
            ScrollBars = t.ScrollBars,
        };
    }
}
