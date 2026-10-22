using Core.Data;
using Core.Vector;
using Tui.Services;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Templating;

namespace Tui.Controls;

/// <summary>
/// 笔记对话框（对齐 Desktop NotesWindow）：列出本书随手记，Tab 循环状态过滤，
/// Enter 编辑、n 新建、c 确认、x 已落地、d 废弃（软删除）。
/// 状态切换后显式重建检索索引（笔记的索引由工具/窗口显式写）。
/// </summary>
public sealed class NotesDialog(INovelStore store, RetrievalKernel? kernel, string novelId)
{
    private static readonly (string Label, NoteStatus? Status)[] Filters =
    [
        ("全部", null),
        ("收件箱", NoteStatus.Inbox),
        ("已确认", NoteStatus.Active),
        ("已落地", NoteStatus.Done),
        ("已废弃", NoteStatus.Dropped),
    ];

    private readonly List<AuthorNote> _notes = [];
    private List<NoteItem> _items = [];
    private OptionList<NoteItem>? _list;
    private Markup? _detail;
    private Dialog? _dialog;
    private int _filterIndex;

    /// <summary>加载并弹出笔记对话框。</summary>
    public async Task ShowAsync()
    {
        try
        {
            _notes.Clear();
            _notes.AddRange(await store.GetNotes(novelId));
        }
        catch (Exception ex)
        {
            ToastService.Error($"加载笔记失败: {ex.Message}");
            return;
        }

        var list = new OptionList<NoteItem>()
            .Items(BuildItems())
            .ItemSearchText(item => item.Title)
            .ItemTemplate(
                new DataTemplate<NoteItem>(
                    Display: (value, in _) =>
                    {
                        var item = value.GetValue();
                        // 标题/摘要是作者文本：必须转义后再进 Markup（含 [ 会被当标签）
                        return new VStack(
                            new Markup($"[bold]{ToolCallRenderer.Escape(item.Title)}[/]"),
                            new Markup($"[dim]{ToolCallRenderer.Escape(item.Meta)}[/]")
                        ).Spacing(0);
                    },
                    Editor: null
                )
            )
            .ActivateOnClick(true);
        list.HorizontalAlignment = Align.Stretch;
        list.AutoFocus = true;
        _list = list;

        _detail = new Markup("从左侧选择一条笔记查看全文。").Wrap(true);

        var body = new FixedRatioPanel(list, PanelThemes.CreateScrollableDialogContent(_detail))
        {
            Ratio = 0.38,
        };

        var dialog = new Dialog()
            .IsResizable(true)
            .IsDraggable(false)
            .IsModal(true)
            .Title("笔记")
            .BottomLeftText(new Markup(FooterHint))
            .Width(96)
            .MinHeight(24)
            .Padding(new Thickness(2, 1, 2, 1));
        _dialog = dialog;
        dialog.Content(PanelThemes.CreateDialogContent(body));
        dialog.TopRightText(PanelThemes.CreateDialogCloseButton(Close));

        // 动作只走按钮：OptionList 会把所有可打印字符当 type-to-jump 吞掉（Handled=true），
        // 字母快捷键在列表聚焦时收不到（TreeSelectorPanel 的 s/l 同款缺陷）。
        var filterMarkup = new Markup($"[dim]过滤：{Filters[_filterIndex].Label}[/]");
        var filterBtn = new Button(filterMarkup).Click(() =>
        {
            _filterIndex = (_filterIndex + 1) % Filters.Length;
            filterMarkup.Text = $"[dim]过滤：{Filters[_filterIndex].Label}[/]";
            RefreshList(SelectedId());
        });
        var newBtn = new Button(new Markup("[bold]新建[/]")).Click(() => OpenEditor(null));
        var confirmBtn = new Button(new Markup("[success]确认[/]"))
            .Style(ThemeStyles.BuildButtonAccent(PanelThemes.Current))
            .Click(() => _ = SetStatusAsync(NoteStatus.Active));
        var doneBtn = new Button(new Markup("[dim]落地[/]")).Click(() =>
            _ = SetStatusAsync(NoteStatus.Done)
        );
        var dropBtn = new Button(new Markup("[error]废弃[/]")).Click(() =>
            _ = SetStatusAsync(NoteStatus.Dropped)
        );
        dialog.BottomRightText(
            new HStack(filterBtn, newBtn, confirmBtn, doneBtn, dropBtn).Spacing(2)
        );

        list.SelectionChanged((_, _) => UpdateDetail());
        list.ItemActivated(
            (_, e) =>
            {
                if (e.Index >= 0 && e.Index < _items.Count)
                    OpenEditor(_items[e.Index].Id);
            }
        );
        dialog.KeyDown(OnKeyDown);
        store.OnEntityChanged += OnEntityChangedAsync;

        dialog.Show();
        dialog.App?.Focus(list);
        UpdateDetail();
    }

    private const string FooterHint =
        "↑↓ 导航 · Enter 编辑 · Tab 切到按钮（过滤/新建/确认/落地/废弃） · Esc 关闭";

    private List<NoteItem> BuildItems()
    {
        var filter = Filters[_filterIndex].Status;
        _items = _notes
            .Where(n => filter is null || n.Status == filter)
            .OrderBy(n => n.Status == NoteStatus.Inbox ? 0 : 1)
            .ThenByDescending(n => n.UpdatedAt)
            .Select(n => new NoteItem(n.Id, RowTitle(n), RowMeta(n)))
            .ToList();
        return _items;
    }

    private void UpdateDetail()
    {
        if (_detail is null)
            return;
        var note = SelectedNote();
        if (note is null)
        {
            _detail.Text = _items.Count == 0 ? "暂无笔记。" : "从左侧选择一条笔记查看全文。";
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[bold]{ToolCallRenderer.Escape(note.Title)}[/]");
        sb.AppendLine($"[dim]{ToolCallRenderer.Escape(RowMeta(note))}[/]");
        sb.AppendLine();
        sb.Append(ToolCallRenderer.Escape(note.Body));
        _detail.Text = sb.ToString();
    }

    private AuthorNote? SelectedNote()
    {
        var id = SelectedId();
        return id is null ? null : _notes.FirstOrDefault(n => n.Id == id);
    }

    private string? SelectedId()
    {
        var index = _list?.SelectedIndex ?? -1;
        return index >= 0 && index < _items.Count ? _items[index].Id : null;
    }

    private void RefreshList(string? selectId = null)
    {
        BuildItems();
        _list?.Items(_items);
        var index = selectId is null ? -1 : _items.FindIndex(i => i.Id == selectId);
        if (_list is not null)
            _list.SelectedIndex =
                index >= 0 ? index
                : _items.Count > 0 ? 0
                : -1;
        UpdateDetail();
    }

    private async Task ReloadAsync(string? selectId = null)
    {
        try
        {
            _notes.Clear();
            _notes.AddRange(await store.GetNotes(novelId));
        }
        catch (Exception ex)
        {
            ToastService.Error($"加载笔记失败: {ex.Message}");
            return;
        }
        RefreshList(selectId);
    }

    private async Task SetStatusAsync(NoteStatus status)
    {
        if (SelectedNote() is not { } note)
            return;
        note.Status = status;
        await SaveAndReindexAsync(note);
        await ReloadAsync(note.Id);
    }

    private async Task SaveAndReindexAsync(AuthorNote note)
    {
        await store.SaveNote(note);
        if (kernel is null)
            return;
        var (doc, chunks) = kernel.Builder.BuildAuthorNote(note);
        await kernel.IndexDocumentAsync(doc, chunks);
    }

    private void OpenEditor(string? entityId)
    {
        new EntityEditDialog(store, EntityType.Note, entityId, novelId).Show();
    }

    private Task OnEntityChangedAsync(EntityChangeEvent evt)
    {
        if (evt.EntityType != EntityType.Note)
            return Task.CompletedTask;
        return ReloadAsync(SelectedId());
    }

    private void Close()
    {
        store.OnEntityChanged -= OnEntityChangedAsync;
        _dialog?.Close();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case TerminalKey.Escape:
                Close();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private static string RowTitle(AuthorNote note) =>
        $"{StatusPrefix(note.Status)}{(note.Title.Length > 0 ? note.Title : "（无标题）")}";

    private static string StatusPrefix(NoteStatus status) =>
        status switch
        {
            NoteStatus.Inbox => "【收件箱】",
            NoteStatus.Active => "【已确认】",
            NoteStatus.Done => "【已落地】",
            _ => "【已废弃】",
        };

    private static string RowMeta(AuthorNote note)
    {
        var captured = note.CapturedChapterOrder is { } order ? $"记于第{order}章 · " : "";
        return $"{captured}{note.UpdatedAt.ToLocalTime():MM-dd HH:mm}";
    }

    /// <summary>列表项（OptionList 的搜索文本/模板都基于它）。</summary>
    private sealed record NoteItem(string Id, string Title, string Meta);
}
