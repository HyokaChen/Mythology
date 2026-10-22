using Core.Data;
using Core.Tools;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

namespace Tui.Controls;

/// <summary>
/// 变体审核对话框：显示 explore 工具返回的多个变体，供用户选择采用、修改或忽略。
/// </summary>
public sealed record VariantReviewDecision
{
    /// <summary>用户选择的操作（采用/修改/忽略）。</summary>
    public VariantReviewAction Action { get; init; }

    /// <summary>选中的变体 ID。</summary>
    public string? VariantId { get; init; }

    /// <summary>选中的变体标签。</summary>
    public string? VariantLabel { get; init; }

    /// <summary>选中的变体内容。</summary>
    public string? VariantContent { get; init; }
}

/// <summary>
/// 变体审核操作类型。
/// </summary>
public enum VariantReviewAction
{
    /// <summary>采用此变体并写入。</summary>
    Apply,

    /// <summary>基于此变体继续修改。</summary>
    Refine,

    /// <summary>忽略此变体。</summary>
    Dismiss,
}

/// <summary>
/// 变体审核对话框：以 Tab 页形式展示多个变体，
/// 用户可预览内容后选择采用（Apply）、继续修改（Refine）或忽略（Dismiss）。
/// </summary>
public static class VariantReviewDialog
{
    /// <summary>
    /// 异步显示变体审核对话框，返回用户的选择。
    /// </summary>
    /// <param name="details">explore 工具返回的变体详情。</param>
    /// <param name="store">小说存储（用于查新章节信息）。</param>
    /// <returns>用户的审核决策。</returns>
    public static Task<VariantReviewDecision> ShowAsync(ExploreDetails details, INovelStore store)
    {
        // TaskCompletionSource 用于异步等待用户决策
        var tcs = new TaskCompletionSource<VariantReviewDecision>();
        var variants = details.Variants;

        // 当前选中的变体索引
        var selectedIdx = 0;
        var tabLabels = variants
            .Select((v, i) => string.IsNullOrEmpty(v.Label) ? $"Variant {i + 1}" : v.Label)
            .ToList();

        // ── 内容区（在捕获它的 lambda 之前声明） ──
        var contentMarkup = new Markup(ToolCallRenderer.Escape(variants[0].Content)).Wrap(true);

        // ── 标签栏：按钮 HStack ──
        var tabButtons = new List<Button>();
        for (var i = 0; i < tabLabels.Count; i++)
        {
            var idx = i;
            var label = tabLabels[i];
            var failed = variants[i].IsFailed ? " [FAILED]" : "";
            var btn = new Button(new Markup($"{label}{failed}"));
            btn.Style(
                i == 0
                    ? ThemeStyles.BuildButtonAccent(PanelThemes.Current)
                    : ThemeStyles.BuildButton(PanelThemes.Current)
            );
            btn.Click(() =>
            {
                if (selectedIdx != idx)
                {
                    selectedIdx = idx;
                    RefreshTabs();
                    RefreshContent();
                }
            });
            tabButtons.Add(btn);
        }

        var tabBar = new HStack([.. tabButtons]).Spacing(0);

        // ── 操作按钮 ──
        var target = details.ApplyTarget;
        string applyLabel;
        if (target == null)
            applyLabel = "Add to Conversation";
        else if (string.IsNullOrEmpty(target.ChapterId))
            applyLabel = "Apply & Create Chapter";
        else
            applyLabel = "Apply & Replace Chapter";

        var hasChapterTarget = target != null;

        var refineBtn = new Button(new Markup("[bold]Continue Modifying[/]")).Style(
            ThemeStyles.BuildButton(PanelThemes.Current)
        );
        var applyBtn = new Button(new Markup($"[bold]{applyLabel}[/]")).Style(
            ThemeStyles.BuildButtonAccent(PanelThemes.Current)
        );

        // ── 目标信息 ──
        var targetInfo =
            target != null
                ? $"Target: Ch.{target.Order} \"{target.Title}\""
                : "Target: Conversation only";

        var contentStack = new VStack(
            new Markup($"[#C7B299]{targetInfo}[/]"),
            tabBar,
            new Markup("[#C7B299]── Content ──[/]"),
            contentMarkup
        ).Spacing(1);

        // ── 对话框 ──
        var dialog = new Dialog()
            .IsResizable(false)
            .IsDraggable(false)
            .IsModal(true)
            .Title($"Variant Review — {variants.Count} variants")
            .Width(72)
            .MinHeight(22)
            .Padding(new Thickness(2, 1, 2, 1))
            .BottomLeftText(new Markup("[#C7B299]← → switch tab · Enter confirm · Esc close[/]"))
            .BottomRightText(
                hasChapterTarget
                    ? new HStack(refineBtn, applyBtn).Spacing(2)
                    : new HStack(refineBtn).Spacing(2)
            )
            .Content(contentStack);

        refineBtn.Click(() =>
        {
            tcs.TrySetResult(Decision(VariantReviewAction.Refine));
            dialog.Close();
        });
        applyBtn.Click(() =>
        {
            if (!CanApply())
                return;
            tcs.TrySetResult(Decision(VariantReviewAction.Apply));
            dialog.Close();
        });

        var closeBtn = PanelThemes.CreateDialogCloseButton(() =>
        {
            tcs.TrySetResult(new VariantReviewDecision { Action = VariantReviewAction.Dismiss });
            dialog.Close();
        });
        dialog.TopRightText(closeBtn);

        dialog.KeyDown(
            (_, e) =>
            {
                switch (e.Key)
                {
                    case TerminalKey.Left:
                        if (variants.Count > 1)
                        {
                            selectedIdx = (selectedIdx - 1 + variants.Count) % variants.Count;
                            RefreshTabs();
                            RefreshContent();
                        }
                        e.Handled = true;
                        break;
                    case TerminalKey.Right:
                        if (variants.Count > 1)
                        {
                            selectedIdx = (selectedIdx + 1) % variants.Count;
                            RefreshTabs();
                            RefreshContent();
                        }
                        e.Handled = true;
                        break;
                    case TerminalKey.Escape:
                        tcs.TrySetResult(
                            new VariantReviewDecision { Action = VariantReviewAction.Dismiss }
                        );
                        dialog.Close();
                        e.Handled = true;
                        break;
                }
            }
        );

        dialog.Show();
        dialog.App?.Focus(dialog);

        return tcs.Task;

        // 刷新标签栏样式（选中项高亮）
        void RefreshTabs()
        {
            for (var i = 0; i < tabButtons.Count; i++)
                tabButtons[i]
                    .Style(
                        i == selectedIdx
                            ? ThemeStyles.BuildButtonAccent(PanelThemes.Current)
                            : ThemeStyles.BuildButton(PanelThemes.Current)
                    );
        }

        // 刷新内容区（切换变体时更新文本）
        void RefreshContent()
        {
            contentMarkup.Text = ToolCallRenderer.Escape(variants[selectedIdx].Content);
        }

        // 构造审核决策的辅助方法
        VariantReviewDecision Decision(VariantReviewAction action)
        {
            if (selectedIdx < 0 || selectedIdx >= variants.Count)
                return new VariantReviewDecision { Action = VariantReviewAction.Dismiss };
            var v = variants[selectedIdx];
            return new VariantReviewDecision
            {
                Action = action,
                VariantId = v.Id,
                VariantLabel = v.Label,
                VariantContent = v.Content,
            };
        }

        // 判断当前选中变体是否可应用（非失败状态）
        bool CanApply()
        {
            return selectedIdx >= 0
                && selectedIdx < variants.Count
                && !variants[selectedIdx].IsFailed;
        }
    }
}
