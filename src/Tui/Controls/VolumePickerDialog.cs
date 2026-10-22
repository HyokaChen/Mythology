using Core.Data;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;

namespace Tui.Controls;

/// <summary>
/// 章节「移动到卷」选择框：未分卷 + 各分卷（按卷序）；确认后回调目标卷 ID（null = 未分卷）。
/// 结构与 <see cref="ChapterFilterDialog"/> 一致（Dialog + State + 确认/取消按钮行）。
/// </summary>
internal static class VolumePickerDialog
{
    /// <summary>未分卷选项文案（与面板「（未分卷）」分组一致）。</summary>
    private const string UnassignedLabel = "（未分卷）";

    /// <summary>
    /// 打开选择框。<paramref name="volumes"/> 应为已按卷序排序的分卷列表；
    /// <paramref name="currentVolumeId"/> 命中时预选对应项（未命中 → 未分卷）。
    /// </summary>
    public static void Show(
        IReadOnlyList<Volume> volumes,
        string? currentVolumeId,
        Action<string?> onPick
    )
    {
        var labels = BuildLabels(volumes);
        var selected = new State<int>(PreselectIndex(volumes, currentVolumeId));

        var picker = new MythSelect(labels, selected.Value)
            .SelectedIndex(selected)
            .Style(SelectStyle.NoBorder);
        picker.HorizontalAlignment(Align.Stretch);

        var dialog = new Dialog()
            .IsResizable(false)
            .IsDraggable(false)
            .IsModal(true)
            .Title("📦 移动到卷")
            .BottomLeftText(new Markup("[#C7B299]Enter 确认 · Esc 取消[/]"))
            .Width(56)
            .MinHeight(12)
            .Padding(new Thickness(2, 1, 2, 1))
            .Content(new VStack(new Markup("[dim]目标分卷[/]"), picker).Spacing(1));

        var closeBtn = PanelThemes.CreateDialogCloseButton(dialog.Close);
        var cancelBtn = new Button(new Markup("[dim]取消[/]")).Click(dialog.Close);
        var okBtn = new Button(new Markup("[bold]确定[/]"))
            .Style(ThemeStyles.BuildButtonAccent(PanelThemes.Current))
            .Click(() =>
            {
                var index = Math.Clamp(selected.Value, 0, labels.Length - 1);
                dialog.Close();
                onPick(index == 0 ? null : volumes[index - 1].Id);
            });

        dialog.TopRightText(closeBtn);
        dialog.BottomRightText(new HStack(cancelBtn, okBtn).Spacing(2));
        dialog.KeyDown(
            (_, e) =>
            {
                if (e.Key == TerminalKey.Escape)
                {
                    dialog.Close();
                    e.Handled = true;
                }
            }
        );
        dialog.Show();
    }

    /// <summary>选项文案：未分卷 + 各分卷（按传入顺序）；下标 0 = 未分卷。</summary>
    internal static string[] BuildLabels(IReadOnlyList<Volume> volumes)
    {
        var labels = new string[volumes.Count + 1];
        labels[0] = UnassignedLabel;
        for (var i = 0; i < volumes.Count; i++)
            labels[i + 1] = $"第{volumes[i].Order}卷 {volumes[i].Name}";
        return labels;
    }

    /// <summary>当前所属卷在选项中的下标（0 = 未分卷；未命中 → 0）。</summary>
    internal static int PreselectIndex(IReadOnlyList<Volume> volumes, string? currentVolumeId)
    {
        if (string.IsNullOrEmpty(currentVolumeId))
            return 0;

        for (var i = 0; i < volumes.Count; i++)
        {
            if (volumes[i].Id == currentVolumeId)
                return i + 1;
        }
        return 0;
    }
}
