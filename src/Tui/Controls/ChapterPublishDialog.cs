using Core.Data;
using Core.Evolution;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;

namespace Tui.Controls;

/// <summary>
/// 章节发布预览对话框（发布事务前的一步）：待生效变化 + 正文检测到的未登记死亡 +
/// 联动建议 + 预检问题，确认后返回 <see cref="PublishOptions"/>（null = 取消）。
///
/// 语义边界（与 <see cref="ChapterLifecycleService"/> 一致）：
/// 存在 <see cref="ValidationIssue.BlocksPublish"/> 时只读展示问题，不提供发布按钮；
/// 自动联动不可取消（服务端恒应用），可选联动按 <see cref="CascadeSuggestion.DefaultSelected"/> 预选。
/// </summary>
internal static class ChapterPublishDialog
{
    /// <summary>选项文案（索引 0 = 应用，1 = 跳过）。</summary>
    private static readonly string[] ApplyOptions = ["应用", "跳过"];

    /// <summary>
    /// 打开发布预览。<paramref name="targetName"/>/<paramref name="locationName"/>/
    /// <paramref name="plotlineName"/> 用于把 id 解析成可读名（缺省原样回显 id，不隐藏信息）。
    /// </summary>
    public static Task<PublishOptions?> ShowAsync(
        PublishPreview preview,
        Func<EntityType, string, string>? targetName = null,
        Func<string, string>? locationName = null,
        Func<string, string>? plotlineName = null
    )
    {
        var tcs = new TaskCompletionSource<PublishOptions?>();
        var blockers = preview.ValidationIssues.Where(i => i.BlocksPublish).ToList();
        var warnings = preview.ValidationIssues.Where(i => !i.BlocksPublish).ToList();

        // 每行的选择状态（index 0 = 应用 / 1 = 跳过）
        var deathStates = preview.DetectedDeaths.Select(_ => new State<int>(0)).ToList();
        var suggestionStates = preview
            .Suggestions.Select(s => new State<int>(s.DefaultSelected ? 0 : 1))
            .ToList();

        var body = new VStack().Spacing(0);
        body.Children.Add(
            new Markup(
                $"[bold]第{preview.ChapterOrder}章 · {ToolCallRenderer.Escape(preview.ChapterTitle)}[/]"
            )
        );

        if (blockers.Count > 0)
        {
            body.Children.Add(new Markup("[error]── 阻止发布的问题 ──[/]"));
            foreach (var issue in blockers)
                body.Children.Add(
                    new Markup($"[error]•[/] {ToolCallRenderer.Escape(issue.Message)}").Wrap(true)
                );
        }
        else
        {
            body.Children.Add(
                new Markup(
                    $"[dim]待生效变化 {preview.PendingChanges.Count} 条 · "
                        + $"联动 {preview.Suggestions.Count} 条 · "
                        + $"未登记死亡 {preview.DetectedDeaths.Count} 条[/]"
                )
            );

            body.Children.Add(new Markup("[#C7B299]── 待生效变化 ──[/]"));
            if (preview.PendingChanges.Count == 0)
            {
                body.Children.Add(new Markup("[dim]（无待生效变化：发布只推进章节状态）[/]"));
            }
            else
            {
                foreach (
                    var line in DescribePendingChanges(
                        preview,
                        targetName,
                        locationName,
                        plotlineName
                    )
                )
                    body.Children.Add(new Markup($"[dim]•[/] {line}").Wrap(true));
            }

            if (preview.DetectedDeaths.Count > 0)
            {
                body.Children.Add(new Markup("[#C7B299]── 正文检测到但未登记 ──[/]"));
                var grid = new Grid()
                    .Columns(
                        new ColumnDefinition { Width = GridLength.Star() },
                        new ColumnDefinition { Width = GridLength.Fixed(10) }
                    )
                    .Rows([
                        .. preview.DetectedDeaths.Select(_ => new RowDefinition
                        {
                            Height = GridLength.Auto,
                        }),
                    ])
                    .ColumnGap(1)
                    .HorizontalAlignment(Align.Stretch);
                for (var i = 0; i < preview.DetectedDeaths.Count; i++)
                {
                    var death = preview.DetectedDeaths[i];
                    grid.Cell(
                        new Markup(
                            $"{ToolCallRenderer.Escape(death.CharacterName)}: "
                                + $"{ToolCallRenderer.Escape(death.Summary)}"
                        ).Wrap(true),
                        0,
                        i
                    );
                    grid.Cell(
                        new MythSelect(ApplyOptions, 0)
                            .SelectedIndex(deathStates[i])
                            .Style(SelectStyle.NoBorder),
                        1,
                        i
                    );
                }
                body.Children.Add(grid);
            }

            if (preview.Suggestions.Count > 0)
            {
                body.Children.Add(new Markup("[#C7B299]── 联动 ──[/]"));
                var grid = new Grid()
                    .Columns(
                        new ColumnDefinition { Width = GridLength.Star() },
                        new ColumnDefinition { Width = GridLength.Fixed(10) }
                    )
                    .Rows([
                        .. preview.Suggestions.Select(_ => new RowDefinition
                        {
                            Height = GridLength.Auto,
                        }),
                    ])
                    .ColumnGap(1)
                    .HorizontalAlignment(Align.Stretch);
                for (var i = 0; i < preview.Suggestions.Count; i++)
                {
                    var suggestion = preview.Suggestions[i];
                    grid.Cell(
                        new Markup(
                            suggestion.IsAutomatic
                                ? $"[dim]\\[自动][/] {ToolCallRenderer.Escape(suggestion.Summary)}"
                                : ToolCallRenderer.Escape(suggestion.Summary)
                        ).Wrap(true),
                        0,
                        i
                    );
                    if (suggestion.IsAutomatic)
                        grid.Cell(new Markup("[dim]必应用[/]"), 1, i);
                    else
                        grid.Cell(
                            new MythSelect(ApplyOptions, 0)
                                .SelectedIndex(suggestionStates[i])
                                .Style(SelectStyle.NoBorder),
                            1,
                            i
                        );
                }
                body.Children.Add(grid);
            }

            foreach (var warning in warnings)
                body.Children.Add(
                    new Markup($"[amber]❗ {ToolCallRenderer.Escape(warning.Message)}[/]").Wrap(true)
                );
        }

        var dialog = new Dialog()
            .IsResizable(true)
            .IsDraggable(false)
            .IsModal(true)
            .Title("📤 发布预览")
            .BottomLeftText(new Markup("[#C7B299]Esc 取消[/]"))
            .Width(84)
            .MinHeight(20)
            .Padding(new Thickness(2, 1, 2, 1))
            .Content(PanelThemes.CreateScrollableDialogContent(body));

        void Close(PublishOptions? result)
        {
            tcs.TrySetResult(result);
            dialog.Close();
        }

        var closeBtn = PanelThemes.CreateDialogCloseButton(() => Close(null));
        var cancelBtn = new Button(new Markup("[dim]取消[/]")).Click(() => Close(null));
        var publishBtn = new Button(new Markup("[bold]发布[/]"))
            .Style(ThemeStyles.BuildButtonAccent(PanelThemes.Current))
            .Click(() =>
            {
                if (blockers.Count > 0)
                    return;
                var deaths = preview
                    .DetectedDeaths.Where((_, i) => Math.Clamp(deathStates[i].Value, 0, 1) == 0)
                    .Select(d => d.Id)
                    .ToList();
                var suggestions = preview
                    .Suggestions.Where((_, i) => Math.Clamp(suggestionStates[i].Value, 0, 1) == 0)
                    .Select(s => s.Id)
                    .ToList();
                Close(new PublishOptions(deaths, suggestions));
            });

        dialog.TopRightText(closeBtn);
        dialog.BottomRightText(
            blockers.Count > 0
                ? new HStack(cancelBtn).Spacing(2)
                : new HStack(cancelBtn, publishBtn).Spacing(2)
        );
        dialog.KeyDown(
            (_, e) =>
            {
                if (e.Key == TerminalKey.Escape)
                {
                    Close(null);
                    e.Handled = true;
                }
            }
        );
        dialog.Show();
        return tcs.Task;
    }

    /// <summary>
    /// 待生效变化的一行文案：`目标 · 切面 · 概要 · 地点 · 情节线`（纯函数，可单测）。
    /// 概要为空时回退「前值 → 后值」；地点/情节线缺省不占位。
    /// </summary>
    internal static IReadOnlyList<string> DescribePendingChanges(
        PublishPreview preview,
        Func<EntityType, string, string>? targetName = null,
        Func<string, string>? locationName = null,
        Func<string, string>? plotlineName = null
    )
    {
        var lines = new List<string>();
        foreach (var change in preview.PendingChanges)
        {
            var target =
                targetName?.Invoke(change.TargetEntityType, change.TargetId) ?? change.TargetId;
            var summary = string.IsNullOrWhiteSpace(change.Summary)
                ? $"{change.FromSummary ?? "?"} → {change.ToSummary ?? "?"}"
                : change.Summary;

            var parts = new List<string>
            {
                ToolCallRenderer.Escape(target),
                AspectLabel(change.Aspect),
                ToolCallRenderer.Escape(summary),
            };
            if (change.LocationId is { Length: > 0 } locationId)
                parts.Add(
                    $"📍{ToolCallRenderer.Escape(locationName?.Invoke(locationId) ?? locationId)}"
                );
            if (change.PlotlineIds.Count > 0)
                parts.Add(
                    "情节线 "
                        + ToolCallRenderer.Escape(
                            string.Join(
                                "、",
                                change.PlotlineIds.Select(id => plotlineName?.Invoke(id) ?? id)
                            )
                        )
                );
            lines.Add(string.Join(" · ", parts));
        }
        return lines;
    }

    /// <summary>变化面 → 中文标签（与本体「状态」切面词汇一致）。</summary>
    internal static string AspectLabel(EvolutionAspect aspect) =>
        aspect switch
        {
            EvolutionAspect.Status => "状态",
            EvolutionAspect.Personality => "性格",
            EvolutionAspect.Motivation => "动机",
            EvolutionAspect.Relationship => "关系",
            EvolutionAspect.Ability => "能力",
            EvolutionAspect.Possession => "持有",
            EvolutionAspect.Location => "位置",
            EvolutionAspect.Knowledge => "认知",
            EvolutionAspect.Behavior => "行为",
            _ => "其他",
        };
}
