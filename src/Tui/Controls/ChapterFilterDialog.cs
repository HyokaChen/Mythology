using Core.Data;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;

namespace Tui.Controls;

/// <summary>
/// 大纲筛选（长篇导航）：章节范围（全部 / 某一卷 / 未分卷）+ 章节状态。
/// 标题关键字不在这里编辑——唯一入口是面板搜索框（一个条件一个输入口）。
/// 千章规模下"看全部"没有意义，筛出"还没写的 / 草稿堆"才是常态需求。
/// 过滤规则本体是 <see cref="ChapterListFilter.Matches"/>（纯函数，可单测）。
/// </summary>
internal static class ChapterFilterDialog
{
    /// <summary>状态选项（"全部" + 草稿/发布两态；审核/终稿已移除，用户报障）。</summary>
    private static readonly (string Label, ChapterStatus? Status)[] StatusOptions =
    [
        ("全部状态", null),
        ("草稿", ChapterStatus.Draft),
        ("发布", ChapterStatus.Published),
    ];

    /// <summary>
    /// 打开筛选对话框。<paramref name="current"/> 为当前筛选条件；
    /// <paramref name="volumes"/> 提供范围选项（全部 / 各卷 / 未分卷）；
    /// 确认后回调新条件（"清除"回调 <see cref="ChapterListFilter.None"/>）。
    /// </summary>
    public static void Show(
        ChapterListFilter current,
        IReadOnlyList<Volume> volumes,
        Action<ChapterListFilter> onApply
    )
    {
        var statusState = new State<int>(IndexOf(current.Status));

        // 范围选项：全部 / 各卷（第N卷 · 卷名）/ 未分卷；索引 0 = 全部，末位 = 未分卷
        var scopeOptions = new List<(string Label, string? VolumeId)> { ("全部章节", null) };
        foreach (var volume in volumes.OrderBy(v => v.Order))
            scopeOptions.Add(
                (
                    string.IsNullOrWhiteSpace(volume.Name)
                        ? $"第{volume.Order}卷"
                        : $"第{volume.Order}卷 · {volume.Name}",
                    volume.Id
                )
            );
        scopeOptions.Add(("（未分卷）", ""));
        var scopeState = new State<int>(IndexOfScope(current.ScopeVolumeId, scopeOptions));

        var scopeSelect = new Select<string>()
            .Items(scopeOptions.Select(o => o.Label).ToArray())
            .SelectedIndex(scopeState)
            .Style(SelectStyle.NoBorder);
        scopeSelect.HorizontalAlignment(Align.Stretch);

        var statusSelect = new Select<string>()
            .Items(StatusOptions.Select(o => o.Label).ToArray())
            .SelectedIndex(statusState)
            .Style(SelectStyle.NoBorder);
        statusSelect.HorizontalAlignment(Align.Stretch);

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
            .Cell(new Markup("[dim]范围[/]"), 0, 0)
            .Cell(scopeSelect, 0, 1)
            .Cell(new Markup("[dim]状态[/]"), 1, 0)
            .Cell(statusSelect, 1, 1)
            .HorizontalAlignment(Align.Stretch);

        var dialog = new Dialog()
            .IsResizable(false)
            .IsDraggable(false)
            .IsModal(true)
            .Title("🔍 筛选章节")
            .BottomLeftText(new Markup("[#C7B299]Enter 应用 · Esc 取消[/]"))
            .Width(56)
            .MinHeight(14)
            .Padding(new Thickness(2, 1, 2, 1))
            .Content(new VStack(grid).Spacing(1));

        var closeBtn = PanelThemes.CreateDialogCloseButton(dialog.Close);
        var clearBtn = new Button(new Markup("[dim]清除[/]")).Click(() =>
        {
            dialog.Close();
            onApply(ChapterListFilter.None);
        });
        var applyBtn = new Button(new Markup("[bold]应用[/]"))
            .Style(ThemeStyles.BuildButtonAccent(PanelThemes.Current))
            .Click(() =>
            {
                dialog.Close();
                onApply(
                    new ChapterListFilter(
                        // 关键字不在本对话框编辑（唯一入口 = 面板搜索框），原样保留
                        current.Keyword,
                        StatusOptions[
                            Math.Clamp(statusState.Value, 0, StatusOptions.Length - 1)
                        ].Status,
                        scopeOptions[
                            Math.Clamp(scopeState.Value, 0, scopeOptions.Count - 1)
                        ].VolumeId
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

    private static int IndexOf(ChapterStatus? status)
    {
        for (var i = 0; i < StatusOptions.Length; i++)
        {
            if (StatusOptions[i].Status == status)
                return i;
        }
        return 0;
    }

    /// <summary>范围值 → 选项索引（null = 全部；"" = 未分卷；否则该卷）。</summary>
    private static int IndexOfScope(
        string? scopeVolumeId,
        IReadOnlyList<(string Label, string? VolumeId)> options
    )
    {
        if (scopeVolumeId is null)
            return 0;
        for (var i = 0; i < options.Count; i++)
        {
            if (string.Equals(options[i].VolumeId, scopeVolumeId, StringComparison.Ordinal))
                return i;
        }
        return 0;
    }
}

/// <summary>
/// 章节列表筛选条件（纯数据 + 匹配规则，可单测）：
/// 关键字为空 = 不限标题；<see cref="Status"/> 为 null = 不限状态；
/// <see cref="ScopeVolumeId"/> 为 null = 全部章节、"" = 仅未分卷、否则该卷 id。
/// </summary>
internal readonly record struct ChapterListFilter(
    string? Keyword,
    ChapterStatus? Status,
    string? ScopeVolumeId = null
)
{
    /// <summary>无条件（显示全部）。</summary>
    public static ChapterListFilter None => new(null, null);

    /// <summary>是否有任一条件生效。</summary>
    public bool IsActive =>
        !string.IsNullOrWhiteSpace(Keyword) || Status is not null || ScopeVolumeId is not null;

    /// <summary>关键字（已 Trim，空串归一为 null）。</summary>
    public string? KeywordText => string.IsNullOrWhiteSpace(Keyword) ? null : Keyword.Trim();

    /// <summary>人工可读的条件描述（用于面板表头）。</summary>
    public string Describe()
    {
        var parts = new List<string>(3);
        if (ScopeVolumeId is { } scope)
            parts.Add(scope.Length == 0 ? "未分卷" : "指定卷");
        if (KeywordText is { } keyword)
            parts.Add($"标题含「{keyword}」");
        if (Status is { } status)
            parts.Add(StatusLabel(status));
        return parts.Count == 0 ? "未筛选" : string.Join(" · ", parts);
    }

    /// <summary>
    /// 是否命中（关键字为模糊子序列匹配，见 <see cref="Core.Shared.FuzzySearch"/>）。
    /// 范围只按 Chapter.VolumeId 判等（悬空归属由 <see cref="Novels.OutlineRows"/> 结合卷集合归一）。
    /// </summary>
    public bool Matches(Chapter chapter)
    {
        if (Status is { } status && chapter.Status != status)
            return false;
        if (ScopeVolumeId is { } scope)
        {
            if (
                scope.Length == 0
                    ? !string.IsNullOrEmpty(chapter.VolumeId)
                    : chapter.VolumeId != scope
            )
                return false;
        }
        return Core.Shared.FuzzySearch.Matches(chapter.Title, KeywordText);
    }

    private static string StatusLabel(ChapterStatus status) =>
        status switch
        {
            ChapterStatus.Draft => "草稿",
            ChapterStatus.Published => "发布",
            _ => status.ToString(),
        };
}
