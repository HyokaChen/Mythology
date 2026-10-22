using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Desktop.Controls;

/// <summary>快速切换列表项（Name = 模型名）。</summary>
public sealed record QuickModelItem(string Name, bool IsCurrent)
{
    /// <summary>是否当前使用中（XAML 绑定）。</summary>
    public bool HasMarker => IsCurrent;
}

/// <summary>
/// 模型快速切换窗（状态栏模型区 / 命令面板）：搜索过滤 + 键盘导航。
/// 单击列表项即切换（Tapped，含已选中项）；键盘 ↑↓ 浏览 + Enter 切换；Esc 关闭。
/// 先 Close 再 onApply（防回调再开窗叠加模态层）。
/// </summary>
public partial class ModelQuickSwitchWindow : Window
{
    private readonly IReadOnlyList<QuickModelItem> _items;
    private readonly List<QuickModelItem> _filtered = [];
    private readonly Action<string> _onApply;
    private bool _isInteractive;

    public ModelQuickSwitchWindow(
        string currentModel,
        IReadOnlyList<string> models,
        Action<string> onApply
    )
    {
        _items = models
            .Select(m => new QuickModelItem(
                m,
                string.Equals(m, currentModel, StringComparison.OrdinalIgnoreCase)
            ))
            .ToList();
        _onApply = onApply;

        InitializeComponent();
        DialogTheme.ApplyWindowStyle(this, "快速切换模型", 520, 460);

        SearchInput.AttachedToVisualTree += (_, _) => SearchInput.Focus();
        RefreshFilter("");
        _isInteractive = true; // 构造完成（程序化选中不触发应用），此后单击即切换
        KeyDown += OnKeyDown;
    }

    /// <summary>搜索过滤：模型名子串（不区分大小写）；默认选中当前模型。</summary>
    private void RefreshFilter(string query)
    {
        _filtered.Clear();
        var q = query.Trim();
        foreach (var item in _items)
        {
            if (q.Length == 0 || item.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                _filtered.Add(item);
        }
        ModelList.ItemsSource = _filtered;
        // 默认选中当前模型，其次首项
        var idx = _filtered.FindIndex(i => i.IsCurrent);
        if (idx < 0 && _filtered.Count > 0)
            idx = 0;
        ModelList.SelectedIndex = idx;
        if (ModelList.SelectedItem is not null)
            ModelList.ScrollIntoView(ModelList.SelectedItem);
    }

    private void SearchInput_OnTextChanged(object? sender, TextChangedEventArgs e) =>
        RefreshFilter(SearchInput.Text ?? "");

    private void SearchInput_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Up or Key.Enter or Key.Escape)
            HandleNavigation(e); // 输入框内也响应导航/执行/关闭
    }

    private void ModelList_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Up or Key.Enter)
            HandleNavigation(e);
    }

    /// <summary>单击列表项即切换（点在空白区域忽略；当前模型不重复应用）。</summary>
    private void ModelList_OnTapped(object? sender, TappedEventArgs e)
    {
        if (!_isInteractive)
            return;
        if (
            (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext
            is not QuickModelItem item
        )
            return;
        Apply(item);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Up or Key.Enter)
            HandleNavigation(e);
    }

    /// <summary>↑↓ 移动选中（钳位）/ Enter 执行 / Esc 关闭。</summary>
    private void HandleNavigation(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (ModelList.SelectedIndex < _filtered.Count - 1)
                    ModelList.SelectedIndex++;
                if (ModelList.SelectedItem is { } downItem)
                    ModelList.ScrollIntoView(downItem);
                e.Handled = true;
                break;
            case Key.Up:
                if (ModelList.SelectedIndex > 0)
                    ModelList.SelectedIndex--;
                if (ModelList.SelectedItem is { } upItem)
                    ModelList.ScrollIntoView(upItem);
                e.Handled = true;
                break;
            case Key.Enter:
                if (ModelList.SelectedItem is QuickModelItem selected)
                    Apply(selected);
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    /// <summary>应用模型：先取引用 → 关闭（解除模态）→ 执行（防再开窗叠加）。</summary>
    private void Apply(QuickModelItem item)
    {
        if (item.IsCurrent)
            return; // 点当前模型不重复应用
        _isInteractive = false; // 防关闭期间二次点击重复触发
        var model = item.Name;
        Close();
        _onApply(model);
    }
}
