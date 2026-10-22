using Core.Data;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

namespace Tui.Controls;

/// <summary>
/// 实体删除确认对话框。
/// 从 TuiApp 中提取以便复用。
/// </summary>
public static class ConfirmDeleteDialog
{
    /// <summary>
    /// 显示删除确认对话框。
    /// </summary>
    /// <param name="entityType">实体类型。</param>
    /// <param name="entityName">实体名称。</param>
    /// <param name="onConfirm">确认删除后的回调。</param>
    public static void Show(EntityType entityType, string entityName, Action onConfirm)
    {
        var label = EntityDisplayHelper.DisplayName(entityType);
        var dlg = new Dialog()
            .IsResizable(false)
            .IsDraggable(false)
            .IsModal(true) // 焦点锁定在 dialog 内：否则文本光标/鼠标 I-beam 会留在主输入框
            .Title($"🚫 删除{ChromeText.ForTitle(label, 20)}")
            .BottomLeftText(new Markup("[#C7B299]Esc 取消[/]"))
            .Width(44)
            .MinHeight(8)
            .Padding(new Thickness(2, 1, 2, 1))
            .Content(
                new Markup(
                    $"确定要删除{label}「[bold]{ToolCallRenderer.Escape(entityName)}[/]」吗？\n此操作不可撤销。"
                )
            );

        var closeBtn = PanelThemes.CreateDialogCloseButton(dlg.Close);

        var cancelBtn = new Button(new Markup("[dim]取消[/]")).Click(dlg.Close);
        var deleteBtn = new Button(new Markup("[error]确认删除[/]"))
            .Style(ThemeStyles.BuildButtonDanger(PanelThemes.Current))
            .Click(() =>
            {
                dlg.Close();
                onConfirm();
            });

        dlg.TopRightText(closeBtn);
        dlg.BottomRightText(new HStack(cancelBtn, deleteBtn).Spacing(2));
        dlg.KeyDown(
            (_, e) =>
            {
                if (e.Key == TerminalKey.Escape)
                {
                    dlg.Close();
                    e.Handled = true;
                }
            }
        );
        dlg.Show();
    }
}
