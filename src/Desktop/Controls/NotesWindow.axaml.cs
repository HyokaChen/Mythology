using Avalonia.Controls;
using Avalonia.Input;
using Core.Data;
using Core.Vector;
using Microsoft.Extensions.Logging;

namespace Desktop.Controls;

/// <summary>
/// 笔记窗口（对齐 TUI NotesDialog）：列出本书记忆 / 收件箱候选，可过滤、切换状态、编辑/新建。
/// 状态切换即确认（收件箱 → 已确认），无需额外按钮；写后显式重建检索索引
/// （Desktop 没有实体变更 → 索引的自动接线）。
/// </summary>
public partial class NotesWindow : Window
{
    private static readonly (string Label, NoteStatus? Status)[] Filters =
    [
        ("全部", null),
        ("收件箱", NoteStatus.Inbox),
        ("已确认", NoteStatus.Active),
        ("已落地", NoteStatus.Done),
        ("已废弃", NoteStatus.Dropped),
    ];

    private readonly INovelStore _store;
    private readonly RetrievalKernel? _kernel;
    private readonly string _novelId;
    private List<AuthorNote> _notes = [];
    private bool _suppressStatusChange;

    public NotesWindow(
        INovelStore store,
        RetrievalKernel? kernel,
        string novelId,
        string novelTitle
    )
    {
        _store = store;
        _kernel = kernel;
        _novelId = novelId;

        InitializeComponent();
        DialogTheme.ApplyWindowStyle(this, "笔记", 820, 620);
        if (!string.IsNullOrWhiteSpace(novelTitle))
            Subtitle.Text = $"{novelTitle} · 作者随手记：候选确认后才参与远期推演";

        FilterBox.ItemsSource = Filters.Select(f => f.Label).ToList();
        FilterBox.SelectedIndex = 0;
        FilterBox.SelectionChanged += (_, _) => Render();

        DetailStatusBox.ItemsSource = Filters.Skip(1).Select(f => f.Label).ToList();
        DetailStatusBox.SelectionChanged += async (_, _) => await ChangeStatusAsync();

        NoteList.SelectionChanged += (_, _) => ShowDetail();
        NoteList.DoubleTapped += (_, _) => _ = OpenEditorAsync(selectedId: SelectedNoteId);
        NewButton.Click += (_, _) => _ = OpenEditorAsync(selectedId: null);
        EditButton.Click += (_, _) => _ = OpenEditorAsync(selectedId: SelectedNoteId);
        CloseButton.Click += (_, _) => Close();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
            else if (e.Key == Key.Enter && SelectedNoteId is { } id)
            {
                _ = OpenEditorAsync(selectedId: id);
                e.Handled = true;
            }
        };

        _store.OnEntityChanged += OnEntityChangedAsync;
        Closed += OnClosed;

        _ = ReloadAsync();
    }

    private string? SelectedNoteId => NoteList.SelectedItem is NoteRow row ? row.Id : null;

    private AuthorNote? SelectedNote =>
        SelectedNoteId is { } id
            ? _notes.FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.Ordinal))
            : null;

    private async Task ReloadAsync(string? selectId = null)
    {
        try
        {
            _notes = await _store.GetNotes(_novelId);
            Render(selectId);
        }
        catch (Exception ex)
        {
            var logcz = (ILogger<NotesWindow>?)
                Program.Services?.GetService(typeof(ILogger<NotesWindow>));
            logcz?.LogWarning(ex, "加载笔记失败");
            _notes = [];
            Render(selectId);
        }
    }

    /// <summary>按当前过滤/排序重建列表与详情（Inbox 优先，其后 UpdatedAt 倒序）。</summary>
    private void Render(string? selectId = null)
    {
        var filterIndex = Math.Max(0, FilterBox.SelectedIndex);
        var filter = Filters[filterIndex].Status;

        var visible = _notes
            .Where(n => filter is null || n.Status == filter)
            .OrderBy(n => n.Status == NoteStatus.Inbox ? 0 : 1)
            .ThenByDescending(n => n.UpdatedAt)
            .ToList();

        var rows = visible.Select(n => new NoteRow(n.Id, RowTitle(n), RowMeta(n), n.Body)).ToList();
        NoteList.ItemsSource = rows;

        var inboxCount = _notes.Count(n => n.Status == NoteStatus.Inbox);
        CountNote.Text = $"共 {visible.Count} 条 · 候选 {inboxCount}";
        EmptyNote.IsVisible = rows.Count == 0;
        ListCard.IsVisible = rows.Count > 0;

        var targetId = selectId ?? SelectedNoteId;
        var index = targetId is null ? -1 : rows.FindIndex(r => r.Id == targetId);
        if (index < 0 && rows.Count > 0)
            index = 0;
        NoteList.SelectedIndex = index;
        ShowDetail();
    }

    private void ShowDetail()
    {
        var note = SelectedNote;
        var hasSelection = note is not null;
        EditButton.IsEnabled = hasSelection;
        DetailStatusBox.IsEnabled = hasSelection;

        if (note is null)
        {
            DetailTitle.Text = "未选择笔记";
            DetailMeta.Text = "";
            DetailBody.Text = "从左侧选择一条笔记查看全文。";
            _suppressStatusChange = true;
            DetailStatusBox.SelectedIndex = -1;
            _suppressStatusChange = false;
            return;
        }

        DetailTitle.Text = note.Title.Length > 0 ? note.Title : "（无标题）";
        DetailMeta.Text = RowMeta(note);
        DetailBody.Text = note.Body;
        _suppressStatusChange = true;
        DetailStatusBox.SelectedIndex = Array.FindIndex(Filters, f => f.Status == note.Status) - 1;
        _suppressStatusChange = false;
    }

    /// <summary>状态切换 = 确认 / 落地 / 废弃（软删除）；写后重建索引并刷新列表。</summary>
    private async Task ChangeStatusAsync()
    {
        if (_suppressStatusChange)
            return;
        if (SelectedNote is not { } note)
            return;
        if (DetailStatusBox.SelectedIndex < 0)
            return;

        var target = Filters[DetailStatusBox.SelectedIndex + 1].Status;
        if (target is null || note.Status == target)
            return;

        note.Status = target.Value;
        await _store.SaveNote(note);
        await ReindexAsync(note.Id);
        await ReloadAsync(note.Id);
    }

    private async Task OpenEditorAsync(string? selectedId)
    {
        var editor = new EntityEditWindow(_store, EntityType.Note, _novelId, selectedId);
        await editor.ShowDialog(this);

        // 新建时不知道 id：用最近更新的一条重建索引即可（编辑已知 id 则精确重建）
        var reindexId = selectedId;
        await ReloadAsync(selectedId);
        reindexId ??= _notes.OrderByDescending(n => n.UpdatedAt).FirstOrDefault()?.Id;
        if (reindexId is not null)
            await ReindexAsync(reindexId);
    }

    private async Task ReindexAsync(string noteId)
    {
        if (_kernel is null)
            return;
        var note = _notes.FirstOrDefault(n =>
            string.Equals(n.Id, noteId, StringComparison.Ordinal)
        );
        if (note is null)
            return;
        var (doc, chunks) = _kernel.Builder.BuildAuthorNote(note);
        await _kernel.IndexDocumentAsync(doc, chunks);
    }

    private Task OnEntityChangedAsync(EntityChangeEvent evt)
    {
        if (evt.EntityType != EntityType.Note)
            return Task.CompletedTask;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = ReloadAsync());
        return Task.CompletedTask;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _store.OnEntityChanged -= OnEntityChangedAsync;
        Closed -= OnClosed;
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
}

// 顶级 internal record：XAML x:DataType 无法引用嵌套私有类型
internal sealed record NoteRow(string Id, string Title, string Meta, string Body);
