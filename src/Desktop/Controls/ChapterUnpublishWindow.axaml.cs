using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Core.Evolution;

namespace Desktop.Controls;

/// <summary>
/// 章节撤回预览窗口：只读列出后续已发布章节与「发布后被修改」冲突；
/// 可撤回时确认返回 true，否则确认按钮禁用（只能取消）。
/// </summary>
public partial class ChapterUnpublishWindow : Window
{
    /// <summary>
    /// 是否允许撤回：只看「后续已发布章」与「发布后被修改的实体」
    /// （变更集机制上线前发布的章也能撤回，服务会带警告回退状态）。
    /// </summary>
    public bool CanUnpublish { get; }

    public ChapterUnpublishWindow(string chapterTitle, UnpublishPreview preview)
    {
        InitializeComponent();
        DialogTheme.ApplyWindowStyle(this, "撤回章节", 640, 480);

        CanUnpublish = preview.LaterPublishedChapters.Count == 0 && preview.Conflicts.Count == 0;

        TitleText.Text = $"撤回「{chapterTitle}」";
        SubtitleText.Text = "发布 → 草稿（回滚本章应用的实体变化与联动）";

        SummaryText.Text = preview.HasAppliedChangeSets
            ? "撤回会按变更集精确回滚本章的实体变化；取消则保持发布。"
            : "本章在变更集机制上线前发布，撤回只切换状态，不会回滚实体。";

        if (preview.LaterPublishedChapters.Count > 0)
        {
            foreach (var title in preview.LaterPublishedChapters)
                LaterPanel.Children.Add(BodyText(title));
        }
        else
        {
            LaterSection.IsVisible = false;
        }

        if (preview.Conflicts.Count > 0)
        {
            foreach (var conflict in preview.Conflicts)
            {
                var text = BodyText(
                    $"{conflict.EntityType} {conflict.EntityId} · {conflict.Reason}"
                );
                text.Classes.Add("Danger");
                ConflictsPanel.Children.Add(text);
            }
        }
        else
        {
            ConflictsSection.IsVisible = false;
        }

        ConfirmButton.IsEnabled = CanUnpublish;
        ConfirmButton.Click += (_, _) =>
        {
            if (!CanUnpublish)
                return;
            Close(true);
        };
        CancelButton.Click += (_, _) => Close(false);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(false);
                e.Handled = true;
            }
        };
    }

    /// <summary>模态显示并返回是否确认撤回（关窗 / Esc = false）。</summary>
    public Task<bool> ShowAsync(Window owner) => ShowDialog<bool>(owner);

    /// <summary>撤回被阻止的原因（后续已发布章 / 发布后被修改的实体；两者都无则空串）。</summary>
    public static string DescribeBlockers(UnpublishPreview preview) =>
        (
            preview.LaterPublishedChapters.Count > 0
                ? $"后续章节已发布（{string.Join("、", preview.LaterPublishedChapters.Take(5))}），请从后往前撤回。"
                : ""
        )
        + (
            preview.Conflicts.Count > 0
                ? "实体在发布后被修改："
                    + string.Join("；", preview.Conflicts.Take(5).Select(c => c.Reason))
                : ""
        );

    private static TextBlock BodyText(string text) =>
        new()
        {
            Text = text,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Classes = { "Tag" },
        };
}
