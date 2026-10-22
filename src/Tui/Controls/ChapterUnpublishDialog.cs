using Core.Data;
using Core.Evolution;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

namespace Tui.Controls;

/// <summary>
/// 章节撤回确认对话框：撤回是变更集回滚（可恢复），但仍会改动实体状态，
/// 所以先展示阻断条件（后续章已发布 / 实体在发布后被修改）与确认按钮。
///
/// 有阻断项时只读展示并返回 false（不打服务端——服务端同样会拒绝）。
/// </summary>
internal static class ChapterUnpublishDialog
{
    /// <summary>打开撤回确认；true = 用户确认撤回，false = 取消或被阻断。</summary>
    public static Task<bool> ShowAsync(string title, UnpublishPreview preview)
    {
        var tcs = new TaskCompletionSource<bool>();
        var blockers = DescribeBlockers(preview);

        var body = new VStack().Spacing(0);
        body.Children.Add(new Markup($"[bold]{ToolCallRenderer.Escape(title)}[/]"));

        if (blockers.Count > 0)
        {
            body.Children.Add(new Markup("[error]── 无法撤回 ──[/]"));
            foreach (var blocker in blockers)
                body.Children.Add(
                    new Markup($"[error]•[/] {ToolCallRenderer.Escape(blocker)}").Wrap(true)
                );
        }
        else
        {
            body.Children.Add(
                new Markup(
                    "[dim]撤回会按变更集精确回滚本章发布时应用的实体变化，章节回到草稿。[/]"
                ).Wrap(true)
            );
            body.Children.Add(
                new Markup(
                    preview.HasAppliedChangeSets
                        ? "[dim]本章有已应用的变更集，可完整回滚。[/]"
                        : "[dim]本章无变更集记录（变更集机制上线前发布）：只把状态改回草稿。[/]"
                ).Wrap(true)
            );
        }

        var dialog = new Dialog()
            .IsResizable(false)
            .IsDraggable(false)
            .IsModal(true)
            .Title("↩ 撤回章节")
            .BottomLeftText(new Markup("[#C7B299]Esc 取消[/]"))
            .Width(64)
            .MinHeight(12)
            .Padding(new Thickness(2, 1, 2, 1))
            .Content(PanelThemes.CreateScrollableDialogContent(body));

        void Close(bool result)
        {
            tcs.TrySetResult(result);
            dialog.Close();
        }

        var closeBtn = PanelThemes.CreateDialogCloseButton(() => Close(false));
        var cancelBtn = new Button(new Markup("[dim]取消[/]")).Click(() => Close(false));

        dialog.TopRightText(closeBtn);
        if (blockers.Count > 0)
        {
            dialog.BottomRightText(new HStack(cancelBtn).Spacing(2));
        }
        else
        {
            var confirmBtn = new Button(new Markup("[error]确认撤回[/]"))
                .Style(ThemeStyles.BuildButtonDanger(PanelThemes.Current))
                .Click(() => Close(true));
            dialog.BottomRightText(new HStack(cancelBtn, confirmBtn).Spacing(2));
        }

        dialog.KeyDown(
            (_, e) =>
            {
                if (e.Key == TerminalKey.Escape)
                {
                    Close(false);
                    e.Handled = true;
                }
            }
        );
        dialog.Show();
        return tcs.Task;
    }

    /// <summary>撤回阻断条件（纯函数，可单测）：后续章已发布 → 只能从后往前撤；实体冲突 → 需人工处理。</summary>
    internal static IReadOnlyList<string> DescribeBlockers(UnpublishPreview preview)
    {
        var blockers = new List<string>();
        if (preview.LaterPublishedChapters.Count > 0)
            blockers.Add(
                "后续章节已发布："
                    + string.Join("、", preview.LaterPublishedChapters.Take(5))
                    + "；只能从最后一章往前撤回"
            );
        if (preview.Conflicts.Count > 0)
        {
            foreach (var conflict in preview.Conflicts.Take(8))
                blockers.Add(
                    $"{EntityDisplayHelper.DisplayName(conflict.EntityType)} {conflict.EntityId}：{conflict.Reason}"
                );
            if (preview.Conflicts.Count > 8)
                blockers.Add($"… 另有 {preview.Conflicts.Count - 8} 条冲突");
        }
        return blockers;
    }
}
