using Avalonia.Controls;
using Avalonia.Input;

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

    /// <summary>描述是否可见（XAML 绑定）。</summary>
    public bool HasDescription => !string.IsNullOrEmpty(Description);

    /// <summary>快捷键是否可见（XAML 绑定）。</summary>
    public bool HasShortcut => !string.IsNullOrEmpty(Shortcut);
}

/// <summary>
/// 命令面板窗口（Ctrl+K）：搜索过滤 + 键盘导航 + Enter 执行。
/// Enter 先关闭再执行——防止 /tree 等命令再开窗时叠加模态层。
/// </summary>
public partial class CommandPaletteWindow : Window
{
    private readonly IReadOnlyList<PaletteCommand> _commands;
    private readonly List<PaletteCommand> _filtered = [];

    /// <summary>构造：传入全部命令。</summary>
    public CommandPaletteWindow(IReadOnlyList<PaletteCommand> commands)
    {
        _commands = commands;

        InitializeComponent();
        DialogTheme.ApplyWindowStyle(this, "命令面板", 560, 440);

        // 打开即聚焦搜索框
        SearchInput.AttachedToVisualTree += (_, _) => SearchInput.Focus();
        RefreshFilter("");

        KeyDown += OnKeyDown;
    }

    /// <summary>搜索过滤：Title/Description 子串（不区分大小写）。</summary>
    private void RefreshFilter(string query)
    {
        _filtered.Clear();
        foreach (var cmd in _commands)
        {
            var q = query.Trim();
            if (
                q.Length == 0
                || cmd.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (cmd.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            )
                _filtered.Add(cmd);
        }
        CommandList.ItemsSource = _filtered;
        if (_filtered.Count > 0)
            CommandList.SelectedIndex = 0;
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

    private void CommandList_OnKeyDown(object? sender, KeyEventArgs e)
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

    private void CommandList_OnDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e) =>
        ExecuteSelected();

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

    /// <summary>↑↓ 移动选中（钳位）。</summary>
    private void HandleNavigation(KeyEventArgs e)
    {
        if (_filtered.Count == 0)
            return;
        switch (e.Key)
        {
            case Key.Down:
                CommandList.SelectedIndex = Math.Min(
                    CommandList.SelectedIndex + 1,
                    _filtered.Count - 1
                );
                if (CommandList.SelectedItem is { } downItem)
                    CommandList.ScrollIntoView(downItem);
                e.Handled = true;
                break;
            case Key.Up:
                CommandList.SelectedIndex = Math.Max(CommandList.SelectedIndex - 1, 0);
                if (CommandList.SelectedItem is { } upItem)
                    CommandList.ScrollIntoView(upItem);
                e.Handled = true;
                break;
        }
    }

    /// <summary>执行选中命令：先取引用 → 关闭（解除模态）→ 执行（防再开窗叠加）。</summary>
    private void ExecuteSelected()
    {
        if (CommandList.SelectedItem is not PaletteCommand cmd)
            return;
        Close();
        cmd.Execute();
    }
}
