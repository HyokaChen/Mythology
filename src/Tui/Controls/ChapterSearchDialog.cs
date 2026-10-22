using Core.Data;
using Core.Novels;
using Tui.Services;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Templating;

namespace Tui.Controls;

/// <summary>
/// 全书检索结果（弹窗）：跨章节按关键字列出命中（序号 + 标题 + 命中处数 + 片段），
/// Enter 打开该章阅读器。定位场景是「我忘了这个设定在哪章提过」——不依赖 embedding，离线可用。
/// </summary>
internal static class ChapterSearchDialog
{
    /// <summary>
    /// 展示结果。<paramref name="hits"/> 为空时给出可行动提示（换关键字/确认范围）。
    /// </summary>
    public static void Show(
        string query,
        IReadOnlyList<ChapterSearchHit> hits,
        IReadOnlyList<Chapter> chapters,
        Action<Chapter> onOpenChapter
    )
    {
        if (hits.Count == 0)
        {
            ToastService.Info($"没有找到包含「{query}」的章节（全书 {chapters.Count} 章）");
            return;
        }

        var items = hits.Select(h => new SearchItem(h)).ToList();
        var list = new OptionList<SearchItem>()
            .Items(items)
            .ItemSearchText(item => $"{item.Hit.Chapter.Title} {item.Hit.Snippet}")
            .ItemTemplate(
                new DataTemplate<SearchItem>(
                    Display: (value, in _) =>
                    {
                        var hit = value.GetValue().Hit;
                        // 标题与片段是小说正文/用户查询词：必须转义后再进 Markup（否则 [ ] 被当标签解析）
                        var title = ToolCallRenderer.Escape(hit.Chapter.Title);
                        var snippet = ToolCallRenderer.Escape(hit.Snippet);
                        return new VStack(
                            new Markup(
                                $"[bold]第{hit.Chapter.Order}章 {title}[/] "
                                    + $"[dim]· {NovelTextSearch.FieldLabel(hit.Field)} {hit.MatchCount} 处[/]"
                            ),
                            new Markup($"[dim]{snippet}[/]")
                        ).Spacing(0);
                    },
                    Editor: null
                )
            )
            .ActivateOnClick(true);
        list.HorizontalAlignment = Align.Stretch;
        list.AutoFocus = true;

        var dialog = new Dialog()
            .IsResizable(true)
            .IsDraggable(false)
            .IsModal(true)
            .Title($"🔍 「{ChromeText.ForTitle(query, 40)}」· {hits.Count} 章命中")
            .BottomLeftText(new Markup("[#C7B299]↑↓ 导航 · Enter 打开 · Esc 关闭[/]"))
            .Width(90)
            .MinHeight(20)
            .Padding(new Thickness(2, 1, 2, 1));
        dialog.Content(PanelThemes.CreateDialogContent(list));

        list.ItemActivated(
            (_, e) =>
            {
                if (e.Index < 0 || e.Index >= items.Count)
                    return;
                dialog.Close();
                onOpenChapter(items[e.Index].Hit.Chapter);
            }
        );

        var closeBtn = PanelThemes.CreateDialogCloseButton(dialog.Close);
        dialog.TopRightText(closeBtn);
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
        dialog.App?.Focus(list);
    }

    /// <summary>列表项（OptionList 的搜索文本/模板都基于它）。</summary>
    private sealed record SearchItem(ChapterSearchHit Hit);
}
