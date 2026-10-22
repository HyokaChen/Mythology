using Avalonia.Controls;
using Avalonia.Input;
using LiveMarkdown.Avalonia;

namespace Desktop.Controls;

/// <summary>
/// 通用 Markdown 内容对话框（移植 TUI MarkdownDialog）：
/// 显示 /help、/state、/history、/commands 等命令的富文本输出。
/// 复用 LiveMarkdown.Avalonia 渲染器（已注册 Math/Mermaid 扩展节点）。
/// 布局对齐 DESIGN.md（Adaline）：Linen 底 · 可滚动内容区。
/// </summary>
public partial class MarkdownDialog : Window
{
    public MarkdownDialog(string title, string markdown, int width = 640, int height = 520)
    {
        InitializeComponent();

        Title = title;
        Width = width;
        Height = height;

        var builder = new ObservableStringBuilder();
        builder.Append(markdown);
        MarkdownRenderer.MarkdownBuilder = builder;

        KeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape or Key.Enter)
            {
                Close();
                e.Handled = true;
            }
        };
    }
}
