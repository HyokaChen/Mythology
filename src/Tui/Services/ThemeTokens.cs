using Tui.Controls;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Styling;

namespace Tui.Services;

/// <summary>
/// 主题基础色板 token — 每个主题只定义这些值（身份 + 层级背景 + 文本 + 语义色），
/// PanelColors 的 57 个字段由 <see cref="ThemeMapper"/> 按语义规则自动推导，
/// 个别字段可用 record with 表达式覆盖（见 ThemeRegistry 中各主题定义）。
/// </summary>
public sealed record ThemeTokens
{
    /// <summary>稳定标识（/theme 命令与持久化用，小写短横线）。</summary>
    public required string Id { get; init; }

    /// <summary>显示名。</summary>
    public required string Name { get; init; }

    /// <summary>选择弹窗中的一句话描述。</summary>
    public required string Description { get; init; }

    /// <summary>是否为深色主题（决定 XenoAtom Theme 亮度与 Toast 语义色方向）。</summary>
    public required bool IsDark { get; init; }

    // ── 层级背景（由深到浅：阅读 → 控件 → 功能）──
    /// <summary>阅读层 — 对话背景（正文对比基准）。</summary>
    public required Color Bg0 { get; init; }

    /// <summary>控件层 — 输入框 / 用户气泡 / 工具气泡。</summary>
    public required Color Bg1 { get; init; }

    /// <summary>功能层 — 侧栏 / tab 条。</summary>
    public required Color Bg2 { get; init; }

    /// <summary>浮层 — 对话框 / 代码块。</summary>
    public required Color Surface { get; init; }

    /// <summary>浮层高一级 — 边框 / 分隔线 / 滚动条滑块。</summary>
    public required Color SurfaceAlt { get; init; }

    // ── 文本（三级，对比度逐级降低但 ≥ 3:1）──
    /// <summary>正文（对 Bg0 需 ≥ 4.5:1）。</summary>
    public required Color Fg0 { get; init; }

    /// <summary>次文本（对 Bg0 需 ≥ 3:1）。</summary>
    public required Color Fg1 { get; init; }

    /// <summary>dim / placeholder / 弱化（对 Bg0 需 ≥ 3:1；勿直接取主题 comment 色，多数过暗）。</summary>
    public required Color Fg2 { get; init; }

    // ── 语义色（对 Bg0 需 ≥ 3:1；Gruvbox 类暗语义色用 bright 变体）──
    /// <summary>主强调 — 选中 / 链接 / 标题 / 信息 Toast。</summary>
    public required Color Accent { get; init; }

    /// <summary>错误 / 删除。</summary>
    public required Color Red { get; init; }

    /// <summary>成功 / 新增。</summary>
    public required Color Green { get; init; }

    /// <summary>警告。</summary>
    public required Color Yellow { get; init; }

    /// <summary>蓝色强调。</summary>
    public required Color Blue { get; init; }

    /// <summary>紫色强调。</summary>
    public required Color Purple { get; init; }

    /// <summary>青色强调（Markdown 链接）。</summary>
    public required Color Cyan { get; init; }

    /// <summary>橙色强调。</summary>
    public required Color Orange { get; init; }

    /// <summary>选区背景（默认取 SurfaceAlt）。</summary>
    public Color SelectionBg { get; init; }
}

/// <summary>
/// 主题控件样式包 — 由 ThemeStyles 从 PanelColors 派生；ThemeService 注入根视觉供控件继承。
/// </summary>
public sealed record ThemeControlStyles(
    DialogStyle Dialog,
    TabControlStyle TabControl,
    CollapsibleStyle Collapsible,
    ButtonStyle Button,
    ButtonStyle ButtonAccent,
    ButtonStyle ButtonDanger,
    ButtonStyle CloseButton,
    SwitchStyle Switch
);

/// <summary>
/// 已注册主题：token 基板 + 推导的 <see cref="PanelColors"/> + XenoAtom UI Theme + 控件样式包。
/// </summary>
public sealed class ThemeDefinition
{
    /// <summary>基础 token。</summary>
    public required ThemeTokens Tokens { get; init; }

    /// <summary>57 字段面板配色（由 Tokens 推导，可覆盖）。</summary>
    public required PanelColors Colors { get; init; }

    /// <summary>XenoAtom 控件主题（16 色 ColorScheme 推导 + InputFill 覆盖）。</summary>
    public required Theme UiTheme { get; init; }

    /// <summary>控件样式包（Dialog/TabControl/Collapsible/Button 等，注入根视觉继承）。</summary>
    public required ThemeControlStyles ControlStyles { get; init; }

    /// <summary>稳定标识。</summary>
    public string Id => Tokens.Id;

    /// <summary>显示名。</summary>
    public string Name => Tokens.Name;

    /// <summary>是否深色。</summary>
    public bool IsDark => Tokens.IsDark;
}

/// <summary>
/// 语义映射器：ThemeTokens → PanelColors（57 字段）。
/// 规则对全部主题一致；Desert 作为"金标准"反推验证过，新主题用 Map + with 覆盖微调。
/// </summary>
internal static class ThemeMapper
{
    /// <summary>从 token 推导面板配色。</summary>
    public static PanelColors Map(ThemeTokens t)
    {
        // Toast：深色主题 = 语义色暗 tint 底 + 语义色字（tint 0.15 保持底色接近控件层，避免同族色混底）；
        // 浅色主题 = 语义色浅 tint 底 + 语义色字
        var toastTint = t.IsDark ? 0.15 : 0.10;

        // 错误文本向白提亮（暗红语义色主题如 Nord 也需可读；错误有图标+加粗兜底）
        var errorFg = t.IsDark
            ? ColorContrast.Toward(t.Red, Color.Rgb(255, 255, 255), 0.25)
            : t.Red;

        return new PanelColors(
            // ── 气泡 ──
            UserBubbleBg: t.Bg1,
            UserBubbleFg: t.Fg0,
            AssistantBubbleBg: t.Bg0,
            AssistantBubbleFg: t.Fg0,
            ToolBubbleBg: t.Bg1,
            ToolBubbleFg: t.Accent,
            ThinkingBubbleBg: t.Bg0,
            ThinkingBubbleFg: t.Fg2,
            InterruptBubbleBg: ColorContrast.Mix(t.Bg1, t.Red, t.IsDark ? 0.25 : 0.12),
            InterruptBubbleFg: errorFg,
            ErrorBubbleBg: ColorContrast.Mix(t.Bg1, t.Red, t.IsDark ? 0.30 : 0.12),
            ErrorBubbleFg: errorFg,
            // ── 面板背景 ──
            ConversationBg: t.Bg0,
            SidePanelBg: t.Bg2,
            SidePanelFg: t.Fg1,
            TabStripBg: t.Bg2,
            TabFg: t.Fg1,
            TabSelectedBg: t.Accent,
            TabSelectedFg: t.Bg0,
            TabHoveredBg: t.Bg2,
            TabHoveredFg: t.Accent,
            HeaderBg: t.Bg0,
            HeaderFg: t.Accent,
            PromptBg: t.Bg1,
            PromptFg: t.Fg0,
            StatusBg: t.Bg0,
            StatusFg: t.Fg1,
            // ── 对话框 ──
            DialogContentBg: t.Surface,
            DialogContentFg: t.Fg0,
            // ── 强调色 ──
            AccentBlue: t.Blue,
            AccentGreen: t.Green,
            AccentRed: t.Red,
            AccentYellow: t.Yellow,
            AccentOrange: t.Orange,
            Dim: t.Fg2,
            Border: t.SurfaceAlt,
            DividerBg: t.SurfaceAlt,
            // 输入框填充：比侧栏面板底色深一档，让搜索框从面板里「凹」出来
            InputFill: t.IsDark
                ? ColorContrast.Mix(t.Bg0, t.Bg2, 0.35)
                : ColorContrast.Mix(t.Bg0, t.Fg2, 0.12),
            // ── Markdown ──
            MarkdownCodeBg: t.Surface,
            MarkdownCodeFg: t.Fg1,
            MarkdownInlineBg: t.Surface,
            MarkdownInlineFg: t.Fg1,
            MarkdownHeadingFg: t.Accent,
            MarkdownLinkFg: t.Cyan,
            // ── Toast ──
            ToastSuccessBg: ToastBg(t.Green),
            ToastSuccessFg: t.Green,
            ToastWarningBg: ToastBg(t.Yellow),
            ToastWarningFg: t.Yellow,
            ToastErrorBg: ToastBg(t.Red),
            ToastErrorFg: errorFg,
            ToastInfoBg: ToastBg(t.Accent),
            ToastInfoFg: t.Accent,
            // ── 工具 ──
            ScrollbarTrack: t.Bg0,
            ScrollbarThumb: t.SurfaceAlt,
            SelectionBg: t.SelectionBg.IsRgbLike ? t.SelectionBg : t.SurfaceAlt,
            SelectionFg: t.Fg0
        );

        Color ToastBg(Color semantic) => ColorContrast.Mix(t.Bg1, semantic, toastTint);
    }
}
