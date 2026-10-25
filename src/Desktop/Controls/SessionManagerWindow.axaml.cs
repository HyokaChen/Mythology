using Avalonia.Controls;
using Avalonia.Input;
using Core.Data;
using Desktop.Controls;
using Microsoft.Extensions.Logging;

namespace Desktop.Controls;

/// <summary>
/// 会话管理器窗口（对齐 TUI SessionPickerDialog）：
/// 列出当前小说的 JSONL 会话，支持打开 / 删除。
/// 布局对齐 DESIGN.md（Adaline）：Linen 底 · Card 列表 · 字段标签 · Primary/Ghost 按钮。
/// </summary>
public partial class SessionManagerWindow : Window
{
    private readonly Func<Task<List<AgentSession>>> _loader;
    private readonly Action<AgentSession> _onOpen;
    private readonly Func<AgentSession, Task> _onDelete;
    private readonly string? _currentSessionId;
    private List<AgentSession> _items = [];

    /// <summary>
    /// 创建会话管理器。
    /// </summary>
    /// <param name="currentSessionId">当前会话：列表标「当前」并预选（回车即切回）。</param>
    public SessionManagerWindow(
        Func<Task<List<AgentSession>>> loader,
        Action<AgentSession> onOpen,
        Func<AgentSession, Task> onDelete,
        string? currentSessionId = null
    )
    {
        _loader = loader;
        _onOpen = onOpen;
        _onDelete = onDelete;
        _currentSessionId = currentSessionId;

        InitializeComponent();

        SessionList.SelectionChanged += (_, _) => UpdateButtons();
        SessionList.DoubleTapped += (_, _) => OpenSelected();
        OpenButton.Click += (_, _) => OpenSelected();
        DeleteButton.Click += async (_, _) => await DeleteSelectedAsync();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
            else if (e.Key == Key.Enter)
                OpenSelected();
        };

        _ = LoadAsync();
    }

    private void UpdateButtons()
    {
        var hasSelection = SelectedSession is not null;
        OpenButton.IsEnabled = hasSelection;
        DeleteButton.IsEnabled = hasSelection;
    }

    /// <summary>行（SessionRow）→ 数据（AgentSession）映射：行模板只暴露显示字段。</summary>
    private AgentSession? SelectedSession =>
        SessionList.SelectedItem is SessionRow row
            ? _items.FirstOrDefault(s => s.Id == row.Id)
            : null;

    private async Task LoadAsync()
    {
        try
        {
            _items = await _loader();
            // 行模板用 SessionRow：标题兜底 + 本地时间（Id 仅作选择映射，不直显）
            var rows = _items
                .Select(s => new SessionRow(
                    s.Id,
                    string.IsNullOrWhiteSpace(s.Title) ? "未命名会话" : s.Title,
                    $"更新 {s.UpdatedAt.ToLocalTime():MM-dd HH:mm}",
                    s.Id == _currentSessionId
                ))
                .ToList();
            SessionList.ItemsSource = rows;
            CountNote.Text = $"共 {rows.Count} 条";
            EmptyNote.IsVisible = rows.Count == 0;
            ListCard.IsVisible = rows.Count > 0;
            // 预选当前会话：打开按钮即时可用，回车即切回
            var currentIdx = rows.FindIndex(r => r.IsCurrent);
            if (currentIdx >= 0)
            {
                SessionList.SelectedIndex = currentIdx;
                SessionList.ScrollIntoView(currentIdx);
            }
            UpdateButtons();
        }
        catch (Exception ex)
        {
            var log = (ILogger<SessionManagerWindow>?)
                Program.Services?.GetService(typeof(ILogger<SessionManagerWindow>));
            log?.LogWarning(ex, "加载会话列表失败");
            SessionList.ItemsSource = Array.Empty<SessionRow>();
            CountNote.Text = "共 0 条";
            EmptyNote.IsVisible = true;
            ListCard.IsVisible = false;
        }
    }

    private void OpenSelected()
    {
        if (SelectedSession is { } item)
        {
            _onOpen(item);
            Close();
        }
    }

    private async Task DeleteSelectedAsync()
    {
        if (SelectedSession is not { } item)
            return;
        var confirm = new ConfirmDeleteWindow(
            "会话",
            item.Title ?? "未命名",
            async () => await _onDelete(item)
        );
        await confirm.ShowDialog(this);
        await LoadAsync();
    }
}

// 顶级 internal record：XAML x:DataType 无法引用嵌套私有类型
internal sealed record SessionRow(string Id, string Title, string Meta, bool IsCurrent);
