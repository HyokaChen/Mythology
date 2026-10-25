using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Core.Data;
using Core.Novels;

namespace Desktop.Controls;

/// <summary>
/// 全书检索结果窗口（Desktop）：跨章节列出命中（序号 + 标题 + 字段 + 处数 + 片段），
/// 双击/Enter 打开该章阅读器。定位场景是「我忘了这个设定在哪章提过」——离线、结果确定。
/// </summary>
public sealed class ChapterSearchWindow : Window
{
    private readonly INovelStore _store;
    private readonly string _novelId;
    private readonly ListBox _list = new();
    private readonly List<ChapterSearchHit> _hits;

    /// <summary>构造检索结果窗口（已在 UI 线程）。</summary>
    public ChapterSearchWindow(
        INovelStore store,
        string novelId,
        string query,
        IReadOnlyList<ChapterSearchHit> hits
    )
    {
        _store = store;
        _novelId = novelId;
        _hits = [.. hits];

        Title = $"「{query}」· {hits.Count} 章命中";
        Width = 720;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _list.ItemsSource = _hits.Select(h => new SearchRow(h)).ToList();
        _list.DoubleTapped += (_, _) => OpenSelected();
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                OpenSelected();
                e.Handled = true;
            }
        };
        // 行容器与逐项入场：与 XAML 侧的列表同一套（Adaline 扁平行 + Sage Mist 状态雾 + 错峰淡入）
        _list.ItemContainerTheme = (ControlTheme)
            Application.Current!.Resources["AdalineRowContainer"]!;
        Desktop.Styles.Motion.SetStagger(_list, true);

        // 窗口入场由 App 级 WindowOpened 处理器统一负责（淡入 + 上浮），此处无需重复挂。

        var hint = new TextBlock
        {
            Margin = new Avalonia.Thickness(14, 12, 14, 6),
            FontSize = 11,
            Opacity = 0.7,
            Text = "双击（或 Enter）打开该章阅读器 · 共 " + hits.Count + " 章命中，按命中处数排序",
        };
        DockPanel.SetDock(hint, Dock.Top);

        var panel = new DockPanel { LastChildFill = true };
        panel.Children.Add(hint);
        panel.Children.Add(_list);
        Content = panel;
    }

    /// <summary>打开当前选中章节的阅读器。</summary>
    private void OpenSelected()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _hits.Count)
            return;
        var reader = new ChapterReaderWindow(
            _store,
            _novelId,
            _hits[_list.SelectedIndex].Chapter.Id
        );
        reader.Show(this);
    }

    /// <summary>列表行（模板用；避免在 Selector 上写 XAML）。</summary>
    private sealed record SearchRow(ChapterSearchHit Hit)
    {
        public override string ToString()
        {
            var h = Hit;
            return $"第{h.Chapter.Order}章 {h.Chapter.Title}  ·  {NovelTextSearch.FieldLabel(h.Field)} {h.MatchCount} 处\n{h.Snippet}";
        }
    }
}
