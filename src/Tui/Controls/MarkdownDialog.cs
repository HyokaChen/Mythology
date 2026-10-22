using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

namespace Tui.Controls;

/// <summary>
/// 可复用的 Markdown 内容对话框 — 使用 Desert 主题。
/// 右上角 ✕ 关闭按钮 + Esc 关闭。
/// </summary>
public static class MarkdownDialog
{
    /// <summary>
    /// 显示 Markdown 内容对话框。
    /// </summary>
    /// <param name="title">对话框标题。</param>
    /// <param name="markdown">Markdown 内容。</param>
    /// <param name="width">对话框宽度。</param>
    /// <param name="minHeight">对话框最小高度。</param>
    /// <param name="resizable">是否允许用户调整大小（长文阅读器用 true）。</param>
    public static void Show(
        string title,
        string markdown,
        int width = 56,
        int minHeight = 22,
        bool resizable = false
    )
    {
        var dialog = new Dialog()
            .IsResizable(resizable)
            .IsDraggable(false)
            .IsModal(true) // 焦点锁定在 dialog 内：否则文本光标/鼠标 I-beam 会留在主输入框
            .Title(title)
            .Width(width)
            .MinHeight(minHeight)
            .Padding(new Thickness(2, 1, 2, 1))
            .Content(PanelThemes.CreateScrollableDialogContent(new MarkdownControl(markdown)));

        var closeBtn = PanelThemes.CreateDialogCloseButton(dialog.Close);
        dialog.TopRightText(closeBtn);
        dialog.KeyDown(
            (_, e) =>
            {
                if (e.Key is TerminalKey.Escape or TerminalKey.Enter)
                {
                    dialog.Close();
                    e.Handled = true;
                }
            }
        );
        dialog.Show();
    }
}
