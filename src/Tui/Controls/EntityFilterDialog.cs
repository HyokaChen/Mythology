using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;

namespace Tui.Controls;

/// <summary>
/// 实体筛选（角色/组织/地点/情节/场景/道具/关系）：每个实体类型 1~2 个维度
/// （定位 / 类型 / 紧张度 / 强度 / 所属卷…，选项由 <see cref="SetUpPanel"/> 按类型与当前数据构建）。
/// 关键字仍然只有搜索框一个输入口；过滤规则本体是 <see cref="EntityListFilter.Matches"/>（纯函数，可单测）。
/// </summary>
internal static class EntityFilterDialog
{
    /// <summary>
    /// 打开筛选对话框。<paramref name="current"/> 为当前条件，确认后回调新条件
    /// （"清除"回调 <see cref="EntityListFilter.None"/>）。
    /// </summary>
    public static void Show(
        string sectionName,
        EntityFilterField primary,
        EntityFilterField? secondary,
        EntityListFilter current,
        Action<EntityListFilter> onApply
    )
    {
        var primaryState = new State<int>(IndexOf(current.Primary, primary.Options));

        var rows = new List<RowDefinition> { new() { Height = GridLength.Auto } };
        var grid = new Grid()
            .Columns(
                new ColumnDefinition { Width = GridLength.Fixed(10) },
                new ColumnDefinition { Width = GridLength.Star() }
            )
            .ColumnGap(2)
            .RowGap(1);

        var primarySelect = new Select<string>()
            .Items(primary.Options.Select(o => o.Label).ToArray())
            .SelectedIndex(primaryState)
            .Style(SelectStyle.NoBorder);
        primarySelect.HorizontalAlignment(Align.Stretch);
        grid.Cell(new Markup($"[dim]{primary.Label}[/]"), 0, 0).Cell(primarySelect, 0, 1);

        Select<string>? secondarySelect = null;
        var secondaryState = new State<int>(0);
        if (secondary is not null)
        {
            secondaryState = new State<int>(IndexOf(current.Secondary, secondary.Options));
            secondarySelect = new Select<string>()
                .Items(secondary.Options.Select(o => o.Label).ToArray())
                .SelectedIndex(secondaryState)
                .Style(SelectStyle.NoBorder);
            secondarySelect.HorizontalAlignment(Align.Stretch);
            rows.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Cell(new Markup($"[dim]{secondary.Label}[/]"), 1, 0).Cell(secondarySelect, 1, 1);
        }
        grid.Rows([.. rows]);

        var dialog = new Dialog()
            .IsResizable(false)
            .IsDraggable(false)
            .IsModal(true)
            .Title($"🔍 筛选{sectionName}")
            .BottomLeftText(new Markup("[#C7B299]Enter 应用 · Esc 取消[/]"))
            .Width(56)
            .MinHeight(secondary is null ? 12 : 14)
            .Padding(new Thickness(2, 1, 2, 1))
            .Content(new VStack(grid).Spacing(1));

        var closeBtn = PanelThemes.CreateDialogCloseButton(dialog.Close);
        var clearBtn = new Button(new Markup("[dim]清除[/]")).Click(() =>
        {
            dialog.Close();
            onApply(EntityListFilter.None);
        });
        var applyBtn = new Button(new Markup("[bold]应用[/]"))
            .Style(ThemeStyles.BuildButtonAccent(PanelThemes.Current))
            .Click(() =>
            {
                dialog.Close();
                onApply(
                    new EntityListFilter(
                        ValueAt(primary.Options, primaryState.Value),
                        secondarySelect is null
                            ? null
                            : ValueAt(secondary!.Options, secondaryState.Value)
                    )
                );
            });

        dialog.TopRightText(closeBtn);
        dialog.BottomRightText(new HStack(clearBtn, applyBtn).Spacing(2));
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

    private static int IndexOf(string? value, IReadOnlyList<(string Label, string? Value)> options)
    {
        if (value is null)
            return 0;
        for (var i = 0; i < options.Count; i++)
        {
            if (string.Equals(options[i].Value, value, StringComparison.Ordinal))
                return i;
        }
        return 0;
    }

    private static string? ValueAt(
        IReadOnlyList<(string Label, string? Value)> options,
        int index
    ) => options[Math.Clamp(index, 0, options.Count - 1)].Value;
}

/// <summary>实体筛选的一个维度：字段标签 + 选项（首项必须是 ("全部", null)）。</summary>
internal sealed record EntityFilterField(
    string Label,
    IReadOnlyList<(string Label, string? Value)> Options
);

/// <summary>
/// 实体列表筛选条件（纯数据 + 匹配规则，可单测）：
/// <see cref="Primary"/> / <see cref="Secondary"/> 是该实体两个维度上的取值
/// （null = 不限；各类型如何解释这两个槽位由构建选项的一方决定，Matches 只做等值判断）。
/// </summary>
internal readonly record struct EntityListFilter(string? Primary, string? Secondary = null)
{
    public static EntityListFilter None => new(null, null);

    public bool IsActive => Primary is not null || Secondary is not null;

    public bool Matches(string primary, string? secondary) =>
        (Primary is null || string.Equals(Primary, primary, StringComparison.Ordinal))
        && (Secondary is null || string.Equals(Secondary, secondary, StringComparison.Ordinal));
}
