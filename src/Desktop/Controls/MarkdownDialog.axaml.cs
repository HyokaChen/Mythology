using Avalonia;
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

        // 退场：淡出 + 下沉（120ms）。Esc/Enter 与右上角关闭都走 Closing；
        // 先用 _isClosing 拦住重入（Close() 会再次触发 Closing），动画播完再真正关闭。
        // 动画失败也不能卡住窗口：PlayExit 内部已吞异常，finally 里无条件 Close。
        Closing += async (_, e) =>
        {
            if (_isClosing)
                return;
            e.Cancel = true;
            _isClosing = true;
            try
            {
                if (Content is Visual content)
                    await Desktop.Styles.Motion.PlayExit(content);
            }
            finally
            {
                Close();
            }
        };
    }

    private bool _isClosing;
}
