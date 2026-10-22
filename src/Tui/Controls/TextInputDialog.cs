using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;

namespace Tui.Controls;

/// <summary>
/// 单行文本输入对话框（轻量创建 / 改名用）：Enter 确认 · Esc 取消。
/// 结构与 <see cref="ChapterFilterDialog"/> 一致（Dialog + State + 确认/取消按钮行）。
/// </summary>
internal static class TextInputDialog
{
    /// <summary>打开对话框。<paramref name="onOk"/> 收到 trim 后的文本（允许空串）。</summary>
    public static void Show(string title, string initial, string placeholder, Action<string> onOk)
    {
        var textState = new State<string?>(initial);
        var submitted = false;

        var box = new TextBox()
            .Text(textState)
            .Placeholder(placeholder)
            .HorizontalAlignment(Align.Stretch);

        var dialog = new Dialog()
            .IsResizable(false)
            .IsDraggable(false)
            .IsModal(true)
            .Title(title)
            .BottomLeftText(new Markup("[#C7B299]Enter 确认 · Esc 取消[/]"))
            .Width(56)
            .MinHeight(10)
            .Padding(new Thickness(2, 1, 2, 1))
            .Content(new VStack(box).Spacing(1));

        void Submit()
        {
            if (submitted)
                return;
            submitted = true;
            dialog.Close();
            onOk(textState.Value?.Trim() ?? "");
        }

        var closeBtn = PanelThemes.CreateDialogCloseButton(dialog.Close);
        var cancelBtn = new Button(new Markup("[dim]取消[/]")).Click(dialog.Close);
        var okBtn = new Button(new Markup("[bold]确定[/]"))
            .Style(ThemeStyles.BuildButtonAccent(PanelThemes.Current))
            .Click(Submit);

        dialog.TopRightText(closeBtn);
        dialog.BottomRightText(new HStack(cancelBtn, okBtn).Spacing(2));
        // Enter 提交：输入框与对话框两级都要接（输入框可能先吃掉按键）
        box.KeyDown(
            (_, e) =>
            {
                if (e.Key == TerminalKey.Enter)
                {
                    Submit();
                    e.Handled = true;
                }
            }
        );
        dialog.KeyDown(
            (_, e) =>
            {
                if (e.Key == TerminalKey.Enter)
                {
                    Submit();
                    e.Handled = true;
                }
                else if (e.Key == TerminalKey.Escape)
                {
                    dialog.Close();
                    e.Handled = true;
                }
            }
        );
        dialog.Show();
    }
}
