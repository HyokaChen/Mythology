using Core.Data;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;

namespace Tui.Controls;

/// <summary>
/// 新建章节快速对话框：只填「标题 + 所属卷」（预选调用行所在卷），回车即建；
/// 细纲 / 计划推进 / 正文等重字段仍走完整编辑器（章节行 ⋯ → 编辑细纲）。
/// 结构与 <see cref="ChapterFilterDialog"/> 一致（Dialog + State + 确认/取消按钮行）。
/// </summary>
internal static class ChapterCreateDialog
{
    /// <summary>打开对话框；<paramref name="onCreate"/> 收到（标题, 目标卷 id｜null = 未分卷）。</summary>
    public static void Show(
        IReadOnlyList<Volume> volumes,
        string? presetVolumeId,
        Action<string, string?> onCreate
    )
    {
        var titleState = new State<string?>("");
        var volumeState = new State<int>(
            VolumePickerDialog.PreselectIndex(volumes, presetVolumeId)
        );
        var submitted = false;

        var titleBox = new TextBox()
            .Text(titleState)
            .Placeholder("章节标题（留空 = 新章节）")
            .HorizontalAlignment(Align.Stretch);

        var volumeLabels = VolumePickerDialog.BuildLabels(volumes);
        var volumeSelect = new MythSelect(volumeLabels, volumeState.Value)
            .SelectedIndex(volumeState)
            .Style(SelectStyle.NoBorder);
        volumeSelect.HorizontalAlignment(Align.Stretch);

        var grid = new Grid()
            .Columns(
                new ColumnDefinition { Width = GridLength.Fixed(10) },
                new ColumnDefinition { Width = GridLength.Star() }
            )
            .Rows(
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto }
            )
            .ColumnGap(2)
            .RowGap(1)
            .Cell(new Markup("[dim]标题[/]"), 0, 0)
            .Cell(titleBox, 0, 1)
            .Cell(new Markup("[dim]所属卷[/]"), 1, 0)
            .Cell(volumeSelect, 1, 1)
            .HorizontalAlignment(Align.Stretch);

        var dialog = new Dialog()
            .IsResizable(false)
            .IsDraggable(false)
            .IsModal(true)
            .Title("📄 新建章节")
            .BottomLeftText(new Markup("[#C7B299]Enter 创建 · Esc 取消[/]"))
            .Width(56)
            .MinHeight(12)
            .Padding(new Thickness(2, 1, 2, 1))
            .Content(new VStack(grid).Spacing(1));

        void Submit()
        {
            if (submitted)
                return;
            submitted = true;
            dialog.Close();
            var index = Math.Clamp(volumeState.Value, 0, volumeLabels.Length - 1);
            onCreate(titleState.Value?.Trim() ?? "", index == 0 ? null : volumes[index - 1].Id);
        }

        var closeBtn = PanelThemes.CreateDialogCloseButton(dialog.Close);
        var cancelBtn = new Button(new Markup("[dim]取消[/]")).Click(dialog.Close);
        var createBtn = new Button(new Markup("[bold]创建[/]"))
            .Style(ThemeStyles.BuildButtonAccent(PanelThemes.Current))
            .Click(Submit);

        dialog.TopRightText(closeBtn);
        dialog.BottomRightText(new HStack(cancelBtn, createBtn).Spacing(2));
        titleBox.KeyDown(
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
