using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClientPlatform;

namespace Desktop.Controls;

/// <summary>
/// 两级题材选择字段（单字段 + Flyout TreeView）。
///
/// 行为：
///   - 默认选第一个一级分类下的第一个二级，避免空表单；
///   - 点一级分类只展开，不提交；点二级分类才提交并关闭浮层；
///   - 搜索命中一级时展示该一级全部二级，命中二级时只展示命中的二级；
///   - SetCatalog 用云端目录热替换时保留当前选择（旧数据无法匹配则保留原值）。
/// </summary>
public partial class GenrePicker : UserControl
{
    private GenreCatalog _catalog = GenreCatalog.Local;
    private GenreSelection? _selected;
    private IReadOnlyList<GenreNode> _roots = [];
    private bool _suppressSelection;
    private bool _builtOnce;

    public GenrePicker()
    {
        InitializeComponent();
        BuildTree();
        ApplySelection(_catalog.DefaultSelection, notify: false);
        _builtOnce = true;
    }

    /// <summary>用户确认后的两级选择（表单保存读这个值）。</summary>
    public GenreSelection? SelectedGenre => _selected;

    /// <summary>选择变化（只有用户点击二级分类时触发；程序化回填不触发）。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>
    /// 热替换目录：优先保留当前 parent/child；旧值不在新目录时保留原值，不静默丢失。
    /// </summary>
    public void SetCatalog(GenreCatalog catalog)
    {
        var previous = _selected;
        _catalog = catalog.Categories.Count > 0 ? catalog : GenreCatalog.Local;

        var next =
            previous is null || previous.IsEmpty
                ? _catalog.DefaultSelection
                : _catalog.Resolve(previous.Parent, previous.Child);

        BuildTree();
        ApplySelection(next, notify: false);

        if (_builtOnce && previous is not null && !Equals(previous, next))
            SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>编辑窗回填：按 parent/child 解析；旧数据只有二级时按二级反查一级。</summary>
    public void Select(string? parent, string? child) =>
        ApplySelection(_catalog.Resolve(parent, child), notify: false);

    /// <summary>程序化设置选中项（测试 / 外部调用）。</summary>
    public void Select(GenreSelection? selection) => ApplySelection(selection, notify: false);

    private void BuildTree()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        var roots = new List<GenreNode>();

        foreach (var category in _catalog.Categories)
        {
            var categoryMatches =
                query.Length == 0
                || category.Name.Contains(query, StringComparison.OrdinalIgnoreCase);

            var childNames =
                query.Length == 0 || categoryMatches
                    ? category.Subcategories
                    : category
                        .Subcategories.Where(subcategory =>
                            subcategory.Contains(query, StringComparison.OrdinalIgnoreCase)
                        )
                        .ToList();

            if (query.Length > 0 && !categoryMatches && childNames.Count == 0)
                continue;

            var children = childNames
                .Select(subcategory => new GenreNode
                {
                    Label = subcategory,
                    ParentName = category.Name,
                    ChildName = subcategory,
                })
                .ToList();

            if (children.Count == 0)
                continue;

            roots.Add(
                new GenreNode
                {
                    Label = category.Name,
                    IsExpanded =
                        query.Length > 0
                        || string.Equals(
                            category.Name,
                            _selected?.Parent,
                            StringComparison.Ordinal
                        ),
                    Children = children,
                }
            );
        }

        _roots = roots;
        GenreTree.ItemsSource = roots;
        RestoreTreeSelection();
    }

    private void RestoreTreeSelection()
    {
        _suppressSelection = true;
        try
        {
            if (_selected?.Parent is not { Length: > 0 } parent)
            {
                GenreTree.SelectedItem = null;
                return;
            }

            var parentNode = _roots.FirstOrDefault(node =>
                string.Equals(node.Label, parent, StringComparison.Ordinal)
            );
            var childNode = parentNode?.Children.FirstOrDefault(node =>
                string.Equals(node.ChildName, _selected.Child, StringComparison.Ordinal)
            );
            GenreTree.SelectedItem = childNode;
        }
        finally
        {
            _suppressSelection = false;
        }
    }

    private void ApplySelection(GenreSelection? selection, bool notify)
    {
        _selected = selection;
        ValueText.Text = selection?.Display is { Length: > 0 } display ? display : "选择题材";
        ValueText.Foreground =
            selection is null || selection.IsEmpty
                ? (Avalonia.Media.IBrush?)
                    Avalonia.Application.Current?.FindResource("BrushSageMist")
                : (Avalonia.Media.IBrush?)
                    Avalonia.Application.Current?.FindResource("BrushForestInk");

        RestoreTreeSelection();

        if (notify)
            SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SearchBox_OnTextChanged(object? sender, TextChangedEventArgs e) => BuildTree();

    private void GenreTree_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || GenreTree.SelectedItem is not GenreNode node)
            return;

        if (node.IsCategory)
        {
            // 一级分类只展开/收起，不当作最终选择。
            node.IsExpanded = true;
            _suppressSelection = true;
            try
            {
                GenreTree.SelectedItem = null;
            }
            finally
            {
                _suppressSelection = false;
            }
            return;
        }

        if (
            node.ParentName is not { Length: > 0 } parent
            || node.ChildName is not { Length: > 0 } child
        )
            return;

        ApplySelection(new GenreSelection(parent, child), notify: true);
        SearchBox.Text = "";
        FieldButton.Flyout?.Hide();
    }

    private void FieldButton_OnClick(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Input);
}

/// <summary>
/// TreeView 节点：一级节点 Children=二级列表；二级节点 Children=空。
/// IsExpanded 实现通知，点击一级分类时 TreeViewItem 才能立即展开。
/// </summary>
public sealed class GenreNode : INotifyPropertyChanged
{
    private bool _isExpanded;

    public string Label { get; init; } = "";
    public string? ParentName { get; init; }
    public string? ChildName { get; init; }
    public IReadOnlyList<GenreNode> Children { get; init; } = [];

    public bool IsCategory => ChildName is null;

    public string CountLabel =>
        IsCategory ? Children.Count.ToString(CultureInfo.InvariantCulture) : "";

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
