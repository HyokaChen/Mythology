using System.Text;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;

namespace Tui.Controls;

/// <summary>
/// 面板颜色配置记录 — 所有 TUI 面板共享的调色板。
/// Desert 主题：受沙漠景观启发的暖色调深色主题 —
/// 沙色、赤陶色、琥珀色、鼠尾草色。和谐对比，出色的可读性。
///
/// 色层体系（暗→亮）：
/// 夜空 #1F1F1F → charcoal #2D2424 → 沙丘 #5B3A29 → 篝火岩 #6E4A36 → 篝火 #A65E2E
/// 文字层：暗月光 #8F8270 → 月光 #C7B299 → 亮月光 #FBF1C7
/// 强调色：铜色 #D68A48
/// </summary>
public sealed record PanelColors(
    // ── 气泡颜色 ──
    /// <summary>用户气泡背景色。</summary>
    Color UserBubbleBg,
    /// <summary>用户气泡前景色。</summary>
    Color UserBubbleFg,
    /// <summary>助手气泡背景色。</summary>
    Color AssistantBubbleBg,
    /// <summary>助手气泡前景色。</summary>
    Color AssistantBubbleFg,
    /// <summary>工具调用气泡背景色。</summary>
    Color ToolBubbleBg,
    /// <summary>工具调用气泡前景色。</summary>
    Color ToolBubbleFg,
    /// <summary>思考气泡背景色。</summary>
    Color ThinkingBubbleBg,
    /// <summary>思考气泡前景色。</summary>
    Color ThinkingBubbleFg,
    /// <summary>中断气泡背景色。</summary>
    Color InterruptBubbleBg,
    /// <summary>中断气泡前景色。</summary>
    Color InterruptBubbleFg,
    /// <summary>错误气泡背景色。</summary>
    Color ErrorBubbleBg,
    /// <summary>错误气泡前景色。</summary>
    Color ErrorBubbleFg,
    // ── 面板背景 ──
    /// <summary>对话区背景色（夜空层）。</summary>
    Color ConversationBg,
    /// <summary>侧面板背景色（沙丘层）。</summary>
    Color SidePanelBg,
    /// <summary>侧面板前景色。</summary>
    Color SidePanelFg,
    /// <summary>Tab 条背景色。</summary>
    Color TabStripBg,
    /// <summary>Tab 未选中前景色。</summary>
    Color TabFg,
    /// <summary>Tab 选中背景色（篝火层）。</summary>
    Color TabSelectedBg,
    /// <summary>Tab 选中前景色。</summary>
    Color TabSelectedFg,
    /// <summary>Tab 悬停背景色。</summary>
    Color TabHoveredBg,
    /// <summary>Tab 悬停前景色。</summary>
    Color TabHoveredFg,
    /// <summary>标题栏背景色。</summary>
    Color HeaderBg,
    /// <summary>标题栏前景色。</summary>
    Color HeaderFg,
    /// <summary>输入区背景色（charcoal 层）。</summary>
    Color PromptBg,
    /// <summary>输入区前景色。</summary>
    Color PromptFg,
    /// <summary>状态栏背景色。</summary>
    Color StatusBg,
    /// <summary>状态栏前景色。</summary>
    Color StatusFg,
    // ── 对话框 ──
    /// <summary>对话框内容背景色。</summary>
    Color DialogContentBg,
    /// <summary>对话框内容前景色。</summary>
    Color DialogContentFg,
    // ── 强调色 ──
    /// <summary>蓝色强调色（Desert 中映射为铜色）。</summary>
    Color AccentBlue,
    /// <summary>绿色强调色（鼠尾草）。</summary>
    Color AccentGreen,
    /// <summary>红色强调色（赤陶）。</summary>
    Color AccentRed,
    /// <summary>黄色强调色（琥珀）。</summary>
    Color AccentYellow,
    /// <summary>橙色强调色。</summary>
    Color AccentOrange,
    /// <summary>暗淡文本色（暗月光）。</summary>
    Color Dim,
    /// <summary>边框色（篝火岩）。</summary>
    Color Border,
    /// <summary>分隔线背景色（沙丘影）。</summary>
    Color DividerBg,
    /// <summary>输入框填充色（搜索框等；比侧栏面板底色深一档，形成「凹陷」对比）。</summary>
    Color InputFill,
    // ── Markdown 渲染 ──
    /// <summary>Markdown 代码块背景色。</summary>
    Color MarkdownCodeBg,
    /// <summary>Markdown 代码块前景色。</summary>
    Color MarkdownCodeFg,
    /// <summary>Markdown 行内代码背景色。</summary>
    Color MarkdownInlineBg,
    /// <summary>Markdown 行内代码前景色。</summary>
    Color MarkdownInlineFg,
    /// <summary>Markdown 标题前景色。</summary>
    Color MarkdownHeadingFg,
    /// <summary>Markdown 链接前景色。</summary>
    Color MarkdownLinkFg,
    // ── Toast 通知 ──
    /// <summary>成功 Toast 背景色。</summary>
    Color ToastSuccessBg,
    /// <summary>成功 Toast 前景色。</summary>
    Color ToastSuccessFg,
    /// <summary>警告 Toast 背景色。</summary>
    Color ToastWarningBg,
    /// <summary>警告 Toast 前景色。</summary>
    Color ToastWarningFg,
    /// <summary>错误 Toast 背景色。</summary>
    Color ToastErrorBg,
    /// <summary>错误 Toast 前景色。</summary>
    Color ToastErrorFg,
    /// <summary>信息 Toast 背景色。</summary>
    Color ToastInfoBg,
    /// <summary>信息 Toast 前景色。</summary>
    Color ToastInfoFg,
    // ── 工具 ──
    /// <summary>滚动条轨道色。</summary>
    Color ScrollbarTrack,
    /// <summary>滚动条滑块色。</summary>
    Color ScrollbarThumb,
    /// <summary>选区背景色。</summary>
    Color SelectionBg,
    /// <summary>选区前景色。</summary>
    Color SelectionFg
);

/// <summary>
/// 面板主题样式定义。
/// 提供 Desert 主题的完整颜色配置和控件样式。
/// </summary>
public static class PanelThemes
{
    /// <summary>当前主题面板配色（ThemeService 切换主题时更新；构建时快照用，保证弹窗/动态控件跟随）。
    /// 注意：不能在此处用属性初始化器赋 Dark（静态字段按文本顺序初始化，Dark 声明在后，会赋成 null）。</summary>
    internal static PanelColors Current { get; set; }

    static PanelThemes()
    {
        Current = Dark; // 静态构造在字段初始化完成后执行，Dark 已就绪
    }

    // ── Desert Dark A palette (方案 A: U形沙丘统一) ──
    // 夜空层 #1F1F1F: 阅读(对话) + 框架(Header/Status/分隔线)
    // charcoal层 #2D2424: 输入区 — 夜空浅一档同族色，与用户气泡同族（输入=用户层）
    // 沙丘层 #5B3A29: 功能(面板/tab strip)
    // 沙丘影层 #3A2E26: 呼吸带 + 对话框芯面（暖炭，文字可读层）
    // 篝火岩层 #6E4A36: 控件底 + 对话框壳（边框/标题条，比背景亮 1.5:1 提供轮廓）
    // 篝火层 #A65E2E: 焦点(选中tab/hover/运行中)
    // 月光层 #C7B299: 文字(面板文本/tab文字/输入文字)
    //
    // 过渡色:
    // 铜色 #D68A48: 主强调(按钮accent/输入前缀/链接)
    // 暗月光 #8F8270: dim文本/placeholder

    /// <summary>
    /// 暗色主题调色板 — Desert Dark。
    /// 色层体系见类顶部注释。
    /// </summary>
    public static readonly PanelColors Dark = new(
        // ── 气泡 ──
        UserBubbleBg: Color.Rgb(0x2D, 0x24, 0x24), // charcoal — 与输入框同族
        UserBubbleFg: Color.Rgb(0xFB, 0xF1, 0xC7), // 亮月光
        AssistantBubbleBg: Color.Rgb(0x1F, 0x1F, 0x1F), // 夜空 — 与对话区统一
        AssistantBubbleFg: Color.Rgb(0xC7, 0xB2, 0x99), // 月光
        ToolBubbleBg: Color.Rgb(0x2D, 0x24, 0x24), // charcoal — 与输入框同族
        ToolBubbleFg: Color.Rgb(0xFA, 0xBD, 0x2F), // 金琥珀
        ThinkingBubbleBg: Color.Rgb(0x1F, 0x1F, 0x1F), // 夜空
        ThinkingBubbleFg: Color.Rgb(0x8F, 0x82, 0x70), // 暗月光
        InterruptBubbleBg: Color.Rgb(0x2C, 0x1C, 0x1C), // 暗赤陶色
        InterruptBubbleFg: Color.Rgb(0xFB, 0x49, 0x34), // 赤陶红
        ErrorBubbleBg: Color.Rgb(0x3C, 0x1C, 0x1C), // 暗红色
        ErrorBubbleFg: Color.Rgb(0xFF, 0x6B, 0x6B), // 亮红色
        // ── 面板背景 ──
        ConversationBg: Color.Rgb(0x1F, 0x1F, 0x1F), // 夜空 — 沙漠夜景最暗
        SidePanelBg: Color.Rgb(0x5B, 0x3A, 0x29), // 沙丘 — 深棕面板
        // 搜索框/输入框填充：比面板底色更深一档，让输入区从面板里「凹」出来
        InputFill: Color.Rgb(0x45, 0x2A, 0x1D),
        SidePanelFg: Color.Rgb(0xC7, 0xB2, 0x99), // 月光
        TabStripBg: Color.Rgb(0x5B, 0x3A, 0x29), // 沙丘 — 与面板同色
        TabFg: Color.Rgb(0xC7, 0xB2, 0x99), // 月光 — tab文字可读
        TabSelectedBg: Color.Rgb(0xA6, 0x5E, 0x2E), // 篝火 — 选中tab点亮
        TabSelectedFg: Color.Rgb(0xFB, 0xF1, 0xC7), // 亮月光 — 选中tab文字
        TabHoveredBg: Color.Rgb(0x5B, 0x3A, 0x29), // 沙丘 — hover保持面板色
        TabHoveredFg: Color.Rgb(0xD6, 0x8A, 0x48), // 铜色 — hover点亮
        HeaderBg: Color.Rgb(0x1F, 0x1F, 0x1F), // 夜空 — 融入天空
        HeaderFg: Color.Rgb(0xD6, 0x8A, 0x48), // 铜色 — 标题用强调色点亮
        PromptBg: Color.Rgb(0x2D, 0x24, 0x24), // charcoal 暖炭 — 对话区夜空 #1F1F1F 的浅一档同族色，
        //                                  与用户气泡同族（输入=用户层），不引入独立色相；
        //                                  与右列沙丘面板 #5B3A29 仍明显区分
        PromptFg: Color.Rgb(0xFB, 0xF1, 0xC7), // 亮月光 — 输入文字提亮（charcoal 上对比 7.2:1）
        StatusBg: Color.Rgb(0x1F, 0x1F, 0x1F), // 夜空 — 与顶部呼应
        StatusFg: Color.Rgb(0xC7, 0xB2, 0x99), // 月光 — 状态栏文字
        // ── 对话框 ──
        DialogContentBg: Color.Rgb(0x3A, 0x3A, 0x3A), // 提亮夜空 — 对话框整体一色（夜空 #1F1F1F 的中性灰提亮版，暖色环境中的中性浮层）
        DialogContentFg: Color.Rgb(0xFB, 0xF1, 0xC7), // 亮月光 — 对话框正文（on 面 8:1）
        // ── 强调色 ──
        AccentBlue: Color.Rgb(0xD6, 0x8A, 0x48), // 铜色（主强调色）
        AccentGreen: Color.Rgb(0x8E, 0xC0, 0x7C), // 鼠尾草绿
        AccentRed: Color.Rgb(0xFB, 0x49, 0x34), // 赤陶红
        AccentYellow: Color.Rgb(0xFA, 0xBD, 0x2F), // 金琥珀
        AccentOrange: Color.Rgb(0xFB, 0x94, 0x37), // 琥珀橙
        Dim: Color.Rgb(0x8F, 0x82, 0x70), // 暗月光 — dim文本
        Border: Color.Rgb(0x6E, 0x4A, 0x36), // 篝火岩 — 边框
        DividerBg: Color.Rgb(0x3A, 0x2E, 0x26), // 沙丘影 — 面板间呼吸带（夜空↔沙丘的暖棕灰过渡，柔和不发黑）
        // ── Markdown ──
        MarkdownCodeBg: Color.Rgb(0x2D, 0x24, 0x24), // charcoal — 代码块
        MarkdownCodeFg: Color.Rgb(0xFB, 0x49, 0x34), // 赤陶红
        MarkdownInlineBg: Color.Rgb(0x2D, 0x24, 0x24), // charcoal — 行内代码
        MarkdownInlineFg: Color.Rgb(0x8E, 0xC0, 0x7C), // 鼠尾草绿
        MarkdownHeadingFg: Color.Rgb(0xA6, 0x5E, 0x2E), // 篝火 — 标题用赭石
        MarkdownLinkFg: Color.Rgb(0xD6, 0x8A, 0x48), // 铜色
        // ── Toast ──
        ToastSuccessBg: Color.Rgb(0x22, 0x2C, 0x22), // 暗鼠尾草色
        ToastSuccessFg: Color.Rgb(0x8E, 0xC0, 0x7C), // 鼠尾草绿
        ToastWarningBg: Color.Rgb(0x2C, 0x28, 0x1A), // 暗琥珀色
        ToastWarningFg: Color.Rgb(0xFA, 0xBD, 0x2F), // 金琥珀
        ToastErrorBg: Color.Rgb(0x2C, 0x1C, 0x1C), // 暗赤陶色
        ToastErrorFg: Color.Rgb(0xFB, 0x49, 0x34), // 赤陶红
        ToastInfoBg: Color.Rgb(0x1F, 0x1F, 0x1F), // 夜空
        ToastInfoFg: Color.Rgb(0xD6, 0x8A, 0x48), // 铜色
        // ── 工具 ──
        ScrollbarTrack: Color.Rgb(0x1F, 0x1F, 0x1F), // 夜空 — 隐藏式
        ScrollbarThumb: Color.Rgb(0x6E, 0x4A, 0x36), // 篝火岩
        SelectionBg: Color.Rgb(0x6E, 0x4A, 0x36), // 篝火岩
        SelectionFg: Color.Rgb(0xFB, 0xF1, 0xC7) // 亮月光
    );

    /// <summary>
    /// 浅色主题调色板 — Desert Light。
    /// 暖色沙色调，与 Dark 主题色层对应但明度反转。
    /// </summary>
    public static readonly PanelColors Light = new(
        // ── 气泡 ──
        UserBubbleBg: Color.Rgb(0xF5, 0xF0, 0xE1), // 暖奶油色
        UserBubbleFg: Color.Rgb(0x3C, 0x38, 0x36), // 深沙色
        AssistantBubbleBg: Color.Rgb(0xFB, 0xF1, 0xC7), // 浅沙色
        AssistantBubbleFg: Color.Rgb(0x3C, 0x38, 0x36), // 深沙色
        ToolBubbleBg: Color.Rgb(0xFF, 0xF0, 0xD4), // 浅琥珀奶油
        ToolBubbleFg: Color.Rgb(0xAF, 0x87, 0x14), // 深琥珀
        ThinkingBubbleBg: Color.Rgb(0xFB, 0xF1, 0xC7), // 浅沙色
        ThinkingBubbleFg: Color.Rgb(0x9A, 0x8C, 0x7A), // 暖灰
        InterruptBubbleBg: Color.Rgb(0xF5, 0xE0, 0xE0), // 浅赤陶色
        InterruptBubbleFg: Color.Rgb(0xCC, 0x24, 0x1D), // 深赤陶色
        ErrorBubbleBg: Color.Rgb(0xFF, 0xE0, 0xE0), // 浅红色
        ErrorBubbleFg: Color.Rgb(0xB9, 0x1C, 0x1C), // 深红色
        // ── 面板背景 ──
        ConversationBg: Color.Rgb(0xFB, 0xF1, 0xC7), // 浅沙色
        SidePanelBg: Color.Rgb(0xF5, 0xF0, 0xE1), // 暖奶油色
        // 搜索框/输入框填充：比面板底色更深一档，让输入区从面板里「凹」出来
        InputFill: Color.Rgb(0xE4, 0xD7, 0xBC),
        SidePanelFg: Color.Rgb(0x3C, 0x38, 0x36), // 深沙色
        TabStripBg: Color.Rgb(0xF5, 0xF0, 0xE1), // 暖奶油色
        TabFg: Color.Rgb(0x9A, 0x8C, 0x7A), // 暖灰
        TabSelectedBg: Color.Rgb(0xEB, 0xDB, 0xB2), // 中沙色
        TabSelectedFg: Color.Rgb(0x1C, 0x19, 0x17), // 最深沙色
        TabHoveredBg: Color.Rgb(0xEB, 0xDB, 0xB2), // 中沙色
        TabHoveredFg: Color.Rgb(0x3C, 0x38, 0x36), // 深沙色
        HeaderBg: Color.Rgb(0xF5, 0xF0, 0xE1), // 暖奶油色
        HeaderFg: Color.Rgb(0x3C, 0x38, 0x36), // 深沙色
        PromptBg: Color.Rgb(0xF5, 0xF0, 0xE1), // 暖奶油色
        PromptFg: Color.Rgb(0xAF, 0x60, 0x14), // 深铜色
        StatusBg: Color.Rgb(0xF5, 0xF0, 0xE1), // 暖奶油色
        StatusFg: Color.Rgb(0x4A, 0x3F, 0x35), // 暖深棕
        // ── 对话框 ──
        DialogContentBg: Color.Rgb(0xEB, 0xDB, 0xB2), // 中沙色
        DialogContentFg: Color.Rgb(0x3C, 0x38, 0x36), // 深沙色
        // ── 强调色 ──
        AccentBlue: Color.Rgb(0xAF, 0x60, 0x14), // 深铜色
        AccentGreen: Color.Rgb(0x4E, 0x7E, 0x3E), // 深鼠尾草
        AccentRed: Color.Rgb(0xCC, 0x24, 0x1D), // 深赤陶色
        AccentYellow: Color.Rgb(0xAF, 0x87, 0x14), // 深琥珀
        AccentOrange: Color.Rgb(0xD6, 0x5D, 0x00), // 深橙
        Dim: Color.Rgb(0x9A, 0x8C, 0x7A), // 暖灰
        Border: Color.Rgb(0xD5, 0xC4, 0xA1), // 沙色边框
        DividerBg: Color.Rgb(0xD5, 0xC4, 0xA1), // 沙色边框（浅色主题呼吸带回退与边框同色）
        // ── Markdown ──
        MarkdownCodeBg: Color.Rgb(0xF5, 0xF0, 0xE1), // 暖奶油色
        MarkdownCodeFg: Color.Rgb(0xCC, 0x24, 0x1D), // 深赤陶色
        MarkdownInlineBg: Color.Rgb(0xEB, 0xDB, 0xB2), // 中沙色
        MarkdownInlineFg: Color.Rgb(0x4E, 0x7E, 0x3E), // 深鼠尾草
        MarkdownHeadingFg: Color.Rgb(0xAF, 0x87, 0x14), // 深琥珀
        MarkdownLinkFg: Color.Rgb(0xAF, 0x60, 0x14), // 深铜色
        // ── Toast ──
        ToastSuccessBg: Color.Rgb(0xE8, 0xF2, 0xE0), // 浅鼠尾草色
        ToastSuccessFg: Color.Rgb(0x4E, 0x7E, 0x3E), // 深鼠尾草
        ToastWarningBg: Color.Rgb(0xF5, 0xEE, 0xD0), // 浅琥珀色
        ToastWarningFg: Color.Rgb(0xAF, 0x87, 0x14), // 深琥珀
        ToastErrorBg: Color.Rgb(0xF5, 0xE0, 0xE0), // 浅赤陶色
        ToastErrorFg: Color.Rgb(0xCC, 0x24, 0x1D), // 深赤陶色
        ToastInfoBg: Color.Rgb(0xF5, 0xF0, 0xE1), // 暖奶油色
        ToastInfoFg: Color.Rgb(0xAF, 0x60, 0x14), // 深铜色
        // ── 工具 ──
        ScrollbarTrack: Color.Rgb(0xF5, 0xF0, 0xE1), // 暖奶油色
        ScrollbarThumb: Color.Rgb(0xD5, 0xC4, 0xA1), // 沙色边框
        SelectionBg: Color.Rgb(0xD5, 0xC4, 0xA1), // 沙色边框
        SelectionFg: Color.Rgb(0x1C, 0x19, 0x17) // 最深沙色
    );

    // ── Desert Dialog style: no border lines, warm popup surface ──
    internal static readonly LineGlyphs NoLineGlyphs = new(
        horizontal: new Rune(' '),
        vertical: new Rune(' '),
        topLeft: new Rune(' '),
        topRight: new Rune(' '),
        bottomLeft: new Rune(' '),
        bottomRight: new Rune(' '),
        teeTop: new Rune(' '),
        teeBottom: new Rune(' '),
        teeLeft: new Rune(' '),
        teeRight: new Rune(' '),
        cross: new Rune(' ')
    );

    /// <summary>
    /// Desert dialog style — 无边框字形（NoLine）：边框 = 纯色块。
    /// 整体一色：提亮夜空（#3A3A3A）贯穿边框 + 标题条 + 底部铭牌 + 内容面——
    /// 夜空 #1F1F1F（对话区/框架色）的中性灰提亮版：desert 里唯一的中性色系，
    /// 在暖色环境（沙丘/charcoal/篝火岩）中形成色相 + 亮度双重区分；
    /// vs 夜空 2.2:1、vs charcoal 1.45:1、vs 沙丘 1.35:1。
    /// 文字（on 面）：正文/标题亮月光（#FBF1C7，8:1）；次要信息月光
    /// （#C7B299，5.5:1）——markup 无需改动。
    /// 注意：边框字形为空格，fg 色永不显示——边框观感完全由 bg 决定；
    /// 实心块模式下焦点反馈与颜色协调冲突，焦点/失焦边框统一。
    /// </summary>
    public static readonly DialogStyle DesertDialog = DialogStyle.Default with
    {
        Glyphs = NoLineGlyphs,
        SurfaceStyle = Style
            .None.WithForeground(Color.Rgb(0xFB, 0xF1, 0xC7)) // 亮月光 — 对话框正文
            .WithBackground(Color.Rgb(0x3A, 0x3A, 0x3A)), // 提亮夜空 — 内容面（整体一色）
        BorderCellStyle = Style
            .None.WithForeground(Color.Rgb(0x3A, 0x3A, 0x3A)) // 提亮夜空（字形为空格，fg 不显示）
            .WithBackground(Color.Rgb(0x3A, 0x3A, 0x3A)), // 提亮夜空 — 边框与面同色，整圈一体
        FocusedBorderCellStyle = Style
            .None.WithForeground(Color.Rgb(0x3A, 0x3A, 0x3A)) // 提亮夜空
            .WithBackground(Color.Rgb(0x3A, 0x3A, 0x3A)), // 提亮夜空 — 焦点/失焦统一（协调优先）
        LabelBackgroundStyle = Style
            .None.WithForeground(Color.Rgb(0xFB, 0xF1, 0xC7)) // 亮月光 — 标题/提示文字（8:1）
            .WithBackground(Color.Rgb(0x3A, 0x3A, 0x3A)), // 提亮夜空 — 标题条/提示铭牌与壳同色
        ResizeHandleHoverStyle = Style
            .None.WithForeground(Color.Rgb(0xFB, 0xF1, 0xC7)) // 亮月光
            .WithBackground(Color.Rgb(0x3A, 0x3A, 0x3A)), // 提亮夜空 — 拖拽高亮与壳统一
    };

    /// <summary>
    /// Desert TabControl 样式 — 无边框布局，扁平 tab 条，篝火色选中 tab。
    /// 所有含 tab 的对话框共用（SettingsDialog、AboutDialog 等）。
    /// </summary>
    public static readonly TabControlStyle DesertTabControl = TabControlStyle.NoBorder with
    {
        TabSelectedStyle =
            Style
                .None.WithForeground(Color.Rgb(0xFB, 0xF1, 0xC7)) // 亮月光
                .WithBackground(Color.Rgb(0xA6, 0x5E, 0x2E)) // 篝火
            | TextStyle.Bold,
        // strip / 未选中 tab 底色 = 对话框内容面（提亮夜空）——框架重画 strip 时宽度会算错，
        // 底色与面同色后那类错画在视觉上消失（用户实机：切 tab 后色块出现在对话框右缘内外）
        TabStyle = Style
            .None.WithForeground(Color.Rgb(0xC7, 0xB2, 0x99)) // 月光
            .WithBackground(Color.Rgb(0x3A, 0x3A, 0x3A)), // 提亮夜空（对话框内容面）
        TabHoveredStyle = Style
            .None.WithForeground(Color.Rgb(0xD6, 0x8A, 0x48)) // 铜色
            .WithBackground(Color.Rgb(0x6E, 0x4A, 0x36)), // 篝火岩
        StripStyle = Style
            .None.WithForeground(Color.Rgb(0xC7, 0xB2, 0x99)) // 月光
            .WithBackground(Color.Rgb(0x3A, 0x3A, 0x3A)), // 提亮夜空（与对话框面同色）
        // 溢出箭头（tab 放不下时）：实心三角 ◀▶，按钮底与 strip 同色（沙丘）——
        // 不再有独立深色块；常态月光显眼、hover 铜色点亮、disabled 沙丘同色隐没
        OverflowPreviousRune = new Rune('◀'),
        OverflowNextRune = new Rune('▶'),
        OverflowButtonStyle = Style
            .None.WithForeground(Color.Rgb(0xC7, 0xB2, 0x99)) // 月光 — 显眼
            .WithBackground(Color.Rgb(0x5B, 0x3A, 0x29)), // 沙丘 — 与 strip 同色
        OverflowButtonHoveredStyle = Style
            .None.WithForeground(Color.Rgb(0xD6, 0x8A, 0x48)) // 铜色 — 点亮
            .WithBackground(Color.Rgb(0x5B, 0x3A, 0x29)), // 沙丘
        OverflowButtonPressedStyle = Style
            .None.WithForeground(Color.Rgb(0xFB, 0xF1, 0xC7)) // 亮月光
            .WithBackground(Color.Rgb(0x5B, 0x3A, 0x29)), // 沙丘
        OverflowButtonDisabledStyle = Style
            .None.WithForeground(Color.Rgb(0x5B, 0x3A, 0x29)) // 沙丘 — 隐没
            .WithBackground(Color.Rgb(0x5B, 0x3A, 0x29)), // 沙丘
    };

    /// <summary>
    /// Desert 开关样式 — 圆形滑块。
    /// ON：铜色轨道（激活）/ 沙丘轨道（非激活）；OFF：篝火岩轨道（比面板背景亮以突出）。
    /// 滑块：ON 时夜空底+铜色面，OFF 时月光底+沙丘面。
    /// </summary>
    public static readonly SwitchStyle DesertSwitch = SwitchStyle.Round with
    {
        // ON 状态：铜色轨道（激活），沙丘轨道（非激活）
        TrackOnActive = Style
            .None.WithForeground(Color.Rgb(0xFB, 0xF1, 0xC7)) // 亮月光
            .WithBackground(Color.Rgb(0xD6, 0x8A, 0x48)), // 铜色
        TrackOnInactive = Style
            .None.WithForeground(Color.Rgb(0x6E, 0x4A, 0x36)) // 篝火岩
            .WithBackground(Color.Rgb(0x5B, 0x3A, 0x29)), // 沙丘
        // OFF 状态：篝火岩轨道（比面板背景亮以突出）
        TrackOffActive = Style
            .None.WithForeground(Color.Rgb(0xC7, 0xB2, 0x99)) // 月光
            .WithBackground(Color.Rgb(0x6E, 0x4A, 0x36)), // 篝火岩
        TrackOffInactive = Style
            .None.WithForeground(Color.Rgb(0x6E, 0x4A, 0x36)) // 篝火岩
            .WithBackground(Color.Rgb(0x6E, 0x4A, 0x36)), // 篝火岩
        // 滑块：ON 亮色，OFF 暗色
        ThumbOn = Style
            .None.WithForeground(Color.Rgb(0x1F, 0x1F, 0x1F)) // 夜空
            .WithBackground(Color.Rgb(0xD6, 0x8A, 0x48)), // 铜色
        ThumbOff = Style
            .None.WithForeground(Color.Rgb(0xC7, 0xB2, 0x99)) // 月光
            .WithBackground(Color.Rgb(0x5B, 0x3A, 0x29)), // 沙丘
        // 悬停/焦点
        TrackHovered = Style
            .None.WithForeground(Color.Rgb(0xC7, 0xB2, 0x99)) // 月光
            .WithBackground(Color.Rgb(0x6E, 0x4A, 0x36)), // 篝火岩
        TrackFocused = Style
            .None.WithForeground(Color.Rgb(0xD6, 0x8A, 0x48)) // 铜色
            .WithBackground(Color.Rgb(0x5B, 0x3A, 0x29)), // 沙丘
    };

    /// <summary>
    /// Desert 折叠项样式。Header 背景 = 沙丘 #5B3A29（与面板同色）——
    /// 分界靠 glyph ▸/▾ + hover 铜色反馈。hover/focus/pressed 前景 = 铜色/亮月光。
    /// </summary>
    public static readonly CollapsibleStyle DesertCollapsible = CollapsibleStyle.Default with
    {
        ExpandedGlyph = new Rune('▾'),
        CollapsedGlyph = new Rune('▸'),
        SpaceBetweenGlyphAndHeader = 1,
        ContentSpacing = 0,
        Header = Style
            .None.WithForeground(Color.Rgb(0xC7, 0xB2, 0x99)) // 月光
            .WithBackground(Color.Rgb(0x5B, 0x3A, 0x29)), // 沙丘
        HeaderHovered = Style
            .None.WithForeground(Color.Rgb(0xD6, 0x8A, 0x48)) // 铜色
            .WithBackground(Color.Rgb(0x5B, 0x3A, 0x29)), // 沙丘
        HeaderPressed = Style
            .None.WithForeground(Color.Rgb(0xFB, 0xF1, 0xC7)) // 亮月光
            .WithBackground(Color.Rgb(0x5B, 0x3A, 0x29)), // 沙丘
        HeaderFocused = Style
            .None.WithForeground(Color.Rgb(0xD6, 0x8A, 0x48)) // 铜色
            .WithBackground(Color.Rgb(0x5B, 0x3A, 0x29)), // 沙丘
    };

    /// <summary>
    /// Desert 默认按钮样式 — 篝火岩底+月光文字，hover 铜色+沙丘底，焦点下划线。
    /// </summary>
    public static readonly ButtonStyle DesertButton = ButtonStyle.Default with
    {
        Padding = new Thickness(1, 0, 1, 0),
        Normal = Style
            .None.WithForeground(Color.Rgb(0xC7, 0xB2, 0x99)) // 月光
            .WithBackground(Color.Rgb(0x6E, 0x4A, 0x36)), // 篝火岩
        Hovered =
            Style
                .None.WithForeground(Color.Rgb(0xD6, 0x8A, 0x48)) // 铜色
                .WithBackground(Color.Rgb(0x5B, 0x3A, 0x29)) // 沙丘
            | TextStyle.Bold,
        Pressed =
            Style
                .None.WithForeground(Color.Rgb(0xFB, 0xF1, 0xC7)) // 亮月光
                .WithBackground(Color.Rgb(0x5B, 0x3A, 0x29)) // 沙丘
            | TextStyle.Bold,
        Focused =
            Style
                .None.WithForeground(Color.Rgb(0xD6, 0x8A, 0x48)) // 铜色
                .WithBackground(Color.Rgb(0x6E, 0x4A, 0x36)) // 篝火岩
            | TextStyle.Underline,
    };

    /// <summary>
    /// 强调按钮样式 — 用于主要/建设性操作（添加、创建）。
    /// 铜色为主色，与默认 DesertButton 明显区分。
    /// </summary>
    public static readonly ButtonStyle DesertButtonAccent = ButtonStyle.Default with
    {
        Padding = new Thickness(1, 0, 1, 0),
        Normal =
            Style
                .None.WithForeground(Color.Rgb(0x1F, 0x1F, 0x1F)) // 夜空 — dark on copper
                .WithBackground(Color.Rgb(0xD6, 0x8A, 0x48)) // 铜色
            | TextStyle.Bold,
        Hovered =
            Style
                .None.WithForeground(Color.Rgb(0x1F, 0x1F, 0x1F)) // 夜空
                .WithBackground(Color.Rgb(0xFA, 0xBD, 0x2F)) // golden amber
            | TextStyle.Bold,
        Pressed =
            Style
                .None.WithForeground(Color.Rgb(0x1F, 0x1F, 0x1F)) // 夜空
                .WithBackground(Color.Rgb(0xAF, 0x60, 0x14)) // deep copper
            | TextStyle.Bold,
        Focused =
            Style
                .None.WithForeground(Color.Rgb(0x1F, 0x1F, 0x1F)) // 夜空
                .WithBackground(Color.Rgb(0xD6, 0x8A, 0x48)) // 铜色
            | TextStyle.Bold
            | TextStyle.Underline,
    };

    /// <summary>
    /// 危险按钮样式 — 用于破坏性操作（删除、移除）。
    /// 亮红色底+白色文字，hover 赤陶色，pressed 深绯红。
    /// </summary>
    public static readonly ButtonStyle DesertButtonDanger = ButtonStyle.Default with
    {
        Padding = new Thickness(1, 0, 1, 0),
        Normal = Style
            .None.WithForeground(Color.Rgb(0xFF, 0xFF, 0xFF)) // white — max contrast on danger bg
            .WithBackground(Color.Rgb(0xCC, 0x24, 0x1D)), // bright danger red
        Hovered =
            Style
                .None.WithForeground(Color.Rgb(0xFF, 0xFF, 0xFF)) // white
                .WithBackground(Color.Rgb(0xFB, 0x49, 0x34)) // bright terracotta
            | TextStyle.Bold,
        Pressed =
            Style
                .None.WithForeground(Color.Rgb(0xFB, 0xF1, 0xC7)) // 亮月光
                .WithBackground(Color.Rgb(0x9D, 0x00, 0x10)) // deep crimson
            | TextStyle.Bold,
        Focused =
            Style
                .None.WithForeground(Color.Rgb(0xFF, 0xFF, 0xFF)) // white
                .WithBackground(Color.Rgb(0xCC, 0x24, 0x1D)) // bright danger red
            | TextStyle.Underline,
    };

    /// <summary>
    /// 对话框关闭按钮样式 — 融入标题条。
    /// 背景与 DesertDialog 标题区同色（提亮夜空），赤陶色文字，hover 变亮红。
    /// </summary>
    public static readonly ButtonStyle DesertCloseButton = ButtonStyle.Default with
    {
        Padding = new Thickness(1, 0, 0, 0),
        ShowBorder = false,
        Normal = Style
            .None.WithForeground(Color.Rgb(0xCC, 0x24, 0x1D)) // terracotta — always visible
            .WithBackground(Color.Rgb(0x3A, 0x3A, 0x3A)), // 提亮夜空 — matches label bar
        Hovered =
            Style
                .None.WithForeground(Color.Rgb(0xFB, 0x49, 0x34)) // bright red on hover
                .WithBackground(Color.Rgb(0x3A, 0x3A, 0x3A)) // 提亮夜空
            | TextStyle.Bold,
        Pressed =
            Style
                .None.WithForeground(Color.Rgb(0xFB, 0xF1, 0xC7)) // 亮月光
                .WithBackground(Color.Rgb(0x3A, 0x3A, 0x3A)) // 提亮夜空
            | TextStyle.Bold,
        Focused =
            Style
                .None.WithForeground(Color.Rgb(0xFB, 0x49, 0x34)) // terracotta
                .WithBackground(Color.Rgb(0x3A, 0x3A, 0x3A)) // 提亮夜空
            | TextStyle.Bold,
    };

    /// <summary>
    /// 创建对话框右上角关闭按钮。
    /// 使用 ✕（U+2715，宽度 1 的细十字，非 emoji）——❎ 等 emoji 字形在部分终端
    /// 宽度账本与渲染不一致，会在右上角留下黑块；左 padding (1,0,0,0) 提供 2 格点击区域。
    /// </summary>
    /// <param name="onClose">关闭回调。</param>
    /// <returns>关闭按钮。</returns>
    public static Button CreateDialogCloseButton(Action onClose)
    {
        var btn = new Button(new TextBlock("✕")).Style(ThemeStyles.BuildCloseButton(Current));
        btn.Click(onClose);
        return btn;
    }

    /// <summary>
    /// 创建滚动条样式 — 轨道和滑块融入指定内容背景色。
    /// 避免滚动条轨道显示对话框面色导致右侧出现可见间隙。
    /// </summary>
    /// <param name="contentBg">内容背景色。</param>
    /// <returns>滚动条样式。</returns>
    public static ScrollViewerStyle ContentScrollStyle(Color contentBg)
    {
        var trackStyle = Style.None.WithBackground(contentBg).WithForeground(contentBg);
        return new ScrollViewerStyle
        {
            ScrollBarThickness = 1,
            TrackStyle = trackStyle,
            ThumbStyle = trackStyle,
        };
    }

    // ════════════════════════════════════════
    // 共享对话框构建辅助方法
    // ════════════════════════════════════════

    /// <summary>
    /// 对话框标准内容填充样式 — 提亮夜空浮层面（与边框/标题同色，整体一色）。
    /// 暖色控件（沙丘/篝火岩）与文字在其上呈现。
    /// </summary>
    public static Style DesertContentFill =>
        Style.None.WithBackground(Current.DialogContentBg).WithForeground(Current.DialogContentFg);

    /// <summary>
    /// 对话框标准内容背景色。
    /// </summary>
    public static Color DesertContentBg => Current.DialogContentBg;

    /// <summary>
    /// 创建标准对话框内容面板 — BackgroundPanel 包裹内容，统一内边距和填充样式。
    /// 所有对话框共用此方法确保一致外观。
    /// </summary>
    /// <param name="content">内容视觉元素。</param>
    /// <param name="padding">内边距（默认 1,1,1,1）。</param>
    /// <returns>背景面板。</returns>
    public static BackgroundPanel CreateDialogContent(Visual content, Thickness? padding = null)
    {
        return new BackgroundPanel
        {
            FillStyle = DesertContentFill,
            Content = new Padder
            {
                Content = content,
                Padding = padding ?? new Thickness(1, 1, 1, 1),
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            },
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
        };
    }

    /// <summary>
    /// 创建可滚动的对话框内容面板 — 用于可能溢出的表单和列表。
    /// </summary>
    /// <param name="content">内容视觉元素。</param>
    /// <param name="padding">内边距（默认 1,1,1,1）。</param>
    /// <returns>背景面板。</returns>
    public static BackgroundPanel CreateScrollableDialogContent(
        Visual content,
        Thickness? padding = null
    )
    {
        var contentBg = DesertContentBg;
        return new BackgroundPanel
        {
            FillStyle = DesertContentFill,
            Content = new Padder
            {
                Content = new ScrollViewer { Content = content, HorizontalScrollEnabled = false }
                    .Style(ContentScrollStyle(contentBg))
                    .VerticalScrollEnabled(true),
                Padding = padding ?? new Thickness(1, 1, 1, 1),
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            },
        };
    }

    /// <summary>
    /// 创建标准 TabControl — 使用共享 DesertTabControl 样式。
    /// 所有含 tab 的对话框必须使用此方法确保一致外观。
    /// </summary>
    /// <param name="pages">Tab 页面数组。</param>
    /// <returns>TabControl。</returns>
    /// <summary>
    /// 弹层表面样式（Popup）：显式底色 + 文字色。
    /// 不设的话弹层表面没有背景，直接透出 app 基础底（夜空 #1F1F1F）——
    /// 在暖色主题里读起来就是「一块黑」（用户报障：下拉 / ⋯ 菜单展开一块黑）。
    /// </summary>
    public static PopupStyle BuildPopup(PanelColors c) =>
        PopupStyle.Default with
        {
            Padding = new Thickness(0),
            SurfaceStyle = Style
                .None.WithForeground(c.DialogContentFg)
                .WithBackground(c.DialogContentBg),
            BorderStyle = Style
                .None.WithForeground(c.DialogContentBg)
                .WithBackground(c.DialogContentBg),
        };

    /// <summary>框架 ListBox（框架自带 Select 的列表）样式：三态统一上主题浮层色。</summary>
    public static ListBoxStyle BuildListBox(PanelColors c) =>
        ListBoxStyle.Default with
        {
            Item = Style.None.WithForeground(c.DialogContentFg).WithBackground(c.DialogContentBg),
            SelectedFocused = Style
                .None.WithForeground(c.TabSelectedFg)
                .WithBackground(c.TabSelectedBg),
            SelectedUnfocused = Style
                .None.WithForeground(c.TabSelectedFg)
                .WithBackground(c.TabSelectedBg),
        };

    /// <summary>菜单列表（MenuList）样式：同上，避免菜单弹层透基础底。</summary>
    public static MenuListStyle BuildMenuList(PanelColors c) =>
        MenuListStyle.NoBorder with
        {
            ItemStyle = Style
                .None.WithForeground(c.DialogContentFg)
                .WithBackground(c.DialogContentBg),
            HoveredStyle = Style.None.WithForeground(c.TabHoveredFg).WithBackground(c.TabHoveredBg),
            SelectedStyle = Style
                .None.WithForeground(c.TabSelectedFg)
                .WithBackground(c.TabSelectedBg),
            DisabledStyle = Style
                .None.WithForeground(c.SidePanelFg)
                .WithBackground(c.DialogContentBg),
        };

    /// <summary>Tooltip 表面样式：同样别透基础底。</summary>
    public static TooltipStyle BuildTooltip(PanelColors c) =>
        TooltipStyle.Default with
        {
            SurfaceStyle = Style
                .None.WithForeground(c.DialogContentFg)
                .WithBackground(c.DialogContentBg),
            BorderStyle = Style
                .None.WithForeground(c.DialogContentBg)
                .WithBackground(c.DialogContentBg),
        };

    /// <summary>
    /// 把「弹层家族」样式一次性注入根视觉：Popup / OptionList / ListBox / MenuList / Tooltip。
    /// 框架自带的弹层（框架 Select 的列表、tooltip、菜单）与我们的 AnchoredPopup 都靠它继承主题色——
    /// 不注入时它们各自用框架默认主题（中性冷灰 / 基础底），在暖色主题里就是一块黑。
    /// </summary>
    public static void InjectOverlayStyles(Visual root, PanelColors c)
    {
        root.SetStyle(BuildPopup(c));
        root.SetStyle(BuildOptionList(c));
        root.SetStyle(BuildListBox(c));
        root.SetStyle(BuildMenuList(c));
        root.SetStyle(BuildTooltip(c));
        // 滚动条：轨道/滑块显式上色（框架默认两处都是 null → 终端默认 = 黑条）
        root.SetStyle(ThemeStyles.BuildScrollBar(c));
        root.SetStyle(ThemeStyles.BuildScrollViewer(c));
    }

    /// <summary>弹层选项列表样式：普通 / 悬停 / 选中三态都带显式底色（同上，别透基础底）。</summary>
    public static OptionListStyle BuildOptionList(PanelColors c) =>
        OptionListStyle.Default with
        {
            Item = Style.None.WithForeground(c.DialogContentFg).WithBackground(c.DialogContentBg),
            Hovered = Style.None.WithForeground(c.TabHoveredFg).WithBackground(c.TabHoveredBg),
            SelectedFocused = Style
                .None.WithForeground(c.TabSelectedFg)
                .WithBackground(c.TabSelectedBg),
            SelectedUnfocused = Style
                .None.WithForeground(c.TabSelectedFg)
                .WithBackground(c.TabSelectedBg),
        };

    public static TabControl CreateDialogTabs(params TabPage[] pages)
    {
        // 每页内容包一层「对话框内容面 + 主题化滚动条」容器（与 CreateScrollableDialogContent 同形态）：
        // 裸 TabControl 的页面宿主不画背景，页内滚动条用默认样式（只有前景色）——
        // 内容溢出 / 悬停时那些格会透出终端默认底（黑块）。
        var wrapped = pages
            .Select(p => new TabPage(
                p.Header,
                CreateScrollableDialogContent(p.Content, new Thickness(1, 0, 1, 1))
            ))
            .ToArray();
        var tabs = new TabControl(wrapped)
        {
            HorizontalAlignment = Align.Stretch,
            VerticalAlignment = Align.Stretch,
            AutoFocus = true,
        }; // TabControl 样式继承根主题
        return tabs;
    }

    /// <summary>
    /// 创建 Tab 内容面板 — 统一内边距和背景。
    /// 每个 tab 页面内容都应使用此方法确保一致外观。
    /// </summary>
    /// <param name="content">内容视觉元素。</param>
    /// <param name="padding">内边距（默认 1,1,1,1）。</param>
    /// <returns>背景面板。</returns>
    public static BackgroundPanel CreateTabContent(Visual content, Thickness? padding = null)
    {
        return new BackgroundPanel
        {
            FillStyle = DesertContentFill,
            Content = new Padder
            {
                Content = content,
                Padding = padding ?? new Thickness(1, 1, 1, 1),
            },
        };
    }

    /// <summary>
    /// 创建可滚动的 Tab 内容面板 — 统一样式，整条链显式 Stretch 占满宽度。
    /// </summary>
    /// <param name="content">内容视觉元素。</param>
    /// <param name="padding">内边距（默认 1,1,1,1）。</param>
    /// <returns>背景面板。</returns>
    public static BackgroundPanel CreateScrollableTabContent(
        Visual content,
        Thickness? padding = null
    )
    {
        var contentBg = DesertContentBg;
        // 整条链显式 Stretch（Visual.Arrange 会把非 Stretch 的 child 收缩回内容宽）：
        // Padder → ScrollViewer → 调用方 content（含 Accordion/Collapsible/header）
        // 逐层占满，保证折叠项/表单占满整行、右侧控件统一右对齐；
        // 水平滚动关闭：tab 内容宽度 = 视口宽，Stretch 才有界可伸。
        return new BackgroundPanel
        {
            FillStyle = DesertContentFill,
            Content = new Padder
            {
                Content = new ScrollViewer
                {
                    Content = content,
                    HorizontalAlignment = Align.Stretch,
                    VerticalAlignment = Align.Stretch,
                }
                    .Style(ContentScrollStyle(contentBg))
                    .VerticalScrollEnabled(true)
                    .HorizontalScrollEnabled(false),
                Padding = padding ?? new Thickness(1, 1, 1, 1),
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Stretch,
            },
        };
    }
}
