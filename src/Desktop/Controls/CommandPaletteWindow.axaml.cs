using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Threading;

namespace Desktop.Controls;

/// <summary>命令面板项（标题 + 快捷键 + 描述 + 执行动作）。</summary>
public sealed record PaletteCommand
{
    public required string Title { get; init; }

    /// <summary>快捷键提示（如 "Ctrl+N"），null 不显示。</summary>
    public string? Shortcut { get; init; }

    /// <summary>一句话描述，null 不显示。</summary>
    public string? Description { get; init; }

    /// <summary>执行动作（面板关闭后调用）。</summary>
    public required Action Execute { get; init; }
}

/// <summary>
/// 命令面板窗口（Ctrl+K）：搜索过滤 + 键盘游标 + Enter / 单击执行。
/// 命令以表格呈现（命令 / 说明 / 快捷键），不是可选列表：没有二次点击语义。
/// 执行前先关闭——防止 /tree 等命令再开窗时叠加模态层。
/// </summary>
public partial class CommandPaletteWindow : Window
{
    private readonly IReadOnlyList<PaletteCommand> _commands;
    private IReadOnlyList<PaletteCommand> _filtered = [];
    private int _selectedIndex;

    /// <summary>构造：传入全部命令。</summary>
    public CommandPaletteWindow(IReadOnlyList<PaletteCommand> commands)
    {
        _commands = commands;

        InitializeComponent();
        DialogTheme.ApplyWindowStyle(this, "命令面板", 640, 480);
        // ApplyWindowStyle 把最小尺寸压到 460×340；三列表格要保住 520×400。
        MinWidth = 520;
        MinHeight = 400;

        // 打开即聚焦搜索框
        SearchInput.AttachedToVisualTree += (_, _) => SearchInput.Focus();
        CommandTable.Loaded += (_, _) => UpdateCurrentRow();
        RefreshFilter("");

        KeyDown += OnKeyDown;
    }

    /// <summary>当前键盘游标指向的命令；列表为空时为 null。</summary>
    private PaletteCommand? SelectedCommand =>
        (uint)_selectedIndex < (uint)_filtered.Count ? _filtered[_selectedIndex] : null;

    /// <summary>搜索过滤：Title/Description 子串（不区分大小写）。</summary>
    private void RefreshFilter(string query)
    {
        var q = query.Trim();
        _filtered =
            q.Length == 0
                ? _commands
                : _commands
                    .Where(cmd =>
                        cmd.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || (
                            cmd.Description?.Contains(q, StringComparison.OrdinalIgnoreCase)
                            ?? false
                        )
                    )
                    .ToArray();

        _selectedIndex = 0;
        CommandTable.ItemsSource = _filtered;
        EmptyNote.IsVisible = _filtered.Count == 0;

        // ItemsSource 刚替换时容器尚未生成；下一轮布局后再落一次游标。
        UpdateCurrentRow();
        Dispatcher.UIThread.Post(UpdateCurrentRow, DispatcherPriority.Loaded);
    }

    private void SearchInput_OnTextChanged(object? sender, TextChangedEventArgs e) =>
        RefreshFilter(SearchInput.Text ?? "");

    private void SearchInput_OnKeyDown(object? sender, KeyEventArgs e)
    {
        HandleNavigation(e);
        if (e.Handled)
            return;
        if (e.Key == Key.Enter)
        {
            ExecuteSelected();
            e.Handled = true;
        }
    }

    private void CommandRow_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PaletteCommand command })
            Execute(command);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Up)
            HandleNavigation(e);
        else if (e.Key == Key.Enter)
        {
            ExecuteSelected();
            e.Handled = true;
        }
    }

    /// <summary>↑↓ 移动键盘游标（钳位）并滚动到可见。</summary>
    private void HandleNavigation(KeyEventArgs e)
    {
        if (_filtered.Count == 0)
            return;
        switch (e.Key)
        {
            case Key.Down:
                _selectedIndex = Math.Min(_selectedIndex + 1, _filtered.Count - 1);
                UpdateCurrentRow();
                e.Handled = true;
                break;
            case Key.Up:
                _selectedIndex = Math.Max(_selectedIndex - 1, 0);
                UpdateCurrentRow();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// 把 <c>current</c> 类写到当前行容器上（行首游标 + 20% 雾），
    /// 并滚动到可见。ItemsControl 不是 ListBox，没有 SelectedIndex/ScrollIntoView。
    /// </summary>
    private void UpdateCurrentRow()
    {
        if (_filtered.Count == 0)
            return;

        for (var i = 0; i < _filtered.Count; i++)
        {
            if (CommandTable.ContainerFromIndex(i) is ContentPresenter { Child: Button row })
                row.Classes.Set("current", i == _selectedIndex);
        }

        CommandTable.ContainerFromIndex(_selectedIndex)?.BringIntoView();
    }

    /// <summary>执行选中命令：先取引用 → 关闭（解除模态）→ 执行（防再开窗叠加）。</summary>
    private void ExecuteSelected()
    {
        if (SelectedCommand is { } command)
            Execute(command);
    }

    private void Execute(PaletteCommand command)
    {
        Close();
        command.Execute();
    }
}
