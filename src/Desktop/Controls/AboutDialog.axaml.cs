using Avalonia.Controls;
using Avalonia.Input;

namespace Desktop.Controls;

/// <summary>
/// 关于/帮助窗口（移植 TUI AboutDialog）：
/// Tab 页：快捷键 / 斜杠命令 / 关于。
/// 布局对齐 DESIGN.md（Adaline）：Linen 底 · Fragment Mono 标签表 · 字段标签。
/// </summary>
public partial class AboutDialog : Window
{
    /// <summary>一条键位/命令条目（等宽键 + 说明）。</summary>
    public sealed record Entry(string Key, string Description);

    /// <summary>快捷键表（顺序即显示顺序）。</summary>
    public static IReadOnlyList<Entry> Shortcuts { get; } =
    [
        new("Ctrl+P", "系统设置"),
        new("Ctrl+N", "新建对话"),
        new("Ctrl+O", "切换小说项目"),
        new("Ctrl+E", "会话切换"),
        new("Ctrl+H", "帮助与关于"),
        new("Ctrl+L", "清除当前对话"),
        new("Ctrl+T", "切换深色/浅色主题"),
        new("Ctrl+K", "命令面板"),
        new("Ctrl+S / Esc", "中断 Agent"),
        new("Enter", "发送消息"),
        new("Shift+Enter", "换行"),
        new("↑/↓", "输入历史 / 补全导航"),
        new("Tab", "选择补全项"),
    ];

    /// <summary>斜杠命令表（顺序即显示顺序）。</summary>
    public static IReadOnlyList<Entry> Commands { get; } =
    [
        new("/help", "显示帮助"),
        new("/clear", "清除当前对话历史"),
        new("/state", "Agent 状态 + 工具统计"),
        new("/history", "查看对话历史"),
        new("/tools", "列出可用工具"),
        new("/new", "新建对话"),
        new("/session", "会话切换"),
        new("/novel", "切换小说项目"),
        new("/settings", "打开系统设置"),
        new("/theme", "切换主题（toggle | light | dark）"),
        new("/tree", "对话分支树导航"),
        new("/mode", "切换增强模式（手动 / 半自动 / 全自动）"),
        new("/context", "上下文管理模式（压缩 / 接续，立即生效，无需重启）"),
        new("/read", "读章节（序号 / ID / 标题片段；阅读器 ← → 翻章）"),
        new("/chapter", "定位章节（设定面板 · 大纲：按卷收窄 + 标题过滤）"),
        new("/search", "全书检索（正文 / 细纲 / 标题，双击打开该章）"),
        new("/variants", "重新打开变体审阅"),
        new("/resume", "恢复指定小说的最新会话（/resume <novel-id>）"),
        new("/commands", "命令速查"),
        new(":cloud sync", "云端同步 / 拉取（pull） / 状态（status）"),
    ];

    public AboutDialog(int initialTab = 0)
    {
        InitializeComponent();
        if (initialTab > 0)
            AboutTabs.SelectedIndex = initialTab;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
    }
}
