using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Core.Data;

namespace Desktop.Controls;

/// <summary>分支树导航结果（镜像 TUI TreeSelectorPanel.TreeNavigationResult）。</summary>
public sealed record NavigationResult(
    /// <summary>分支点 / 标签目标的 entryId。</summary>
    string EntryId,
    /// <summary>是否生成 AI 摘要。</summary>
    bool GenerateSummary,
    /// <summary>标签文本（null 表示非标签模式）。</summary>
    string? LabelText
);

/// <summary>
/// 会话分支树窗口（对齐 TUI TreeSelectorPanel）：
/// 扁平化展示分支结构（缩进/角色色/活跃路径标记），支持切换分支、分支+摘要、添加标签。
/// </summary>
public partial class TreeWindow : Window
{
    private readonly Action<NavigationResult> _onNavigate;
    private readonly string? _activeLeafId;

    /// <summary>
    /// 树行数据（UI 展示模型：缩进与颜色经计算属性，XAML 直接绑定）。
    /// </summary>
    public sealed record TreeRow
    {
        public required string EntryId { get; init; }
        public required string DisplayText { get; init; }
        public required int Depth { get; init; }
        public required bool IsActive { get; init; }
        public required bool IsFork { get; init; }
        public required IReadOnlyList<string> Labels { get; init; }
        public required JsonlEntryType Type { get; init; }

        /// <summary>缩进（Depth * 16px 左边距）。</summary>
        public Avalonia.Thickness IndentMargin => new(Depth * 16, 2, 0, 2);

        /// <summary>是否有标签（行尾 Tag 图标仅在有标签时出现）。</summary>
        public bool HasLabels => Labels.Count > 0;

        /// <summary>角色/类型着色（非活跃行整体弱化）。镜像 TUI TreeSelectorPanel 配色语义。</summary>
        public IBrush TextBrush
        {
            get
            {
                if (!IsActive)
                    return DialogTheme.SageMistBrush;
                return Type switch
                {
                    JsonlEntryType.Event => DialogTheme.SageGray,
                    JsonlEntryType.BranchSummary => DialogTheme.DeepTealBrush,
                    _ => RoleText switch
                    {
                        "user" => DialogTheme.SageLeaf,
                        "assistant" => DialogTheme.ForestInk,
                        "tool" => DialogTheme.AmberPin,
                        _ => DialogTheme.SageGray,
                    },
                };
            }
        }

        public string? RoleText { get; init; }
    }

    /// <summary>构造：传入树结构与导航回调。</summary>
    public TreeWindow(SessionTreeStructure tree, Action<NavigationResult> onNavigate)
    {
        _onNavigate = onNavigate;
        _activeLeafId = tree.ActiveLeafId;

        InitializeComponent();
        DialogTheme.ApplyWindowStyle(this, "对话分支树", 640, 560);

        var rows = FlattenTree(tree);
        TreeList.ItemsSource = rows;
        if (rows.Count > 0)
            TreeList.SelectedIndex = 0;

        KeyDown += OnKeyDown;
        TreeList.DoubleTapped += (_, _) => TryNavigate(generateSummary: false);
    }

    /// <summary>
    /// 扁平化树结构（镜像 TUI FlattenTree）：roots → 递归缩进 + processed 去重，孤立节点追加 Depth=0。
    /// </summary>
    private static List<TreeRow> FlattenTree(SessionTreeStructure tree)
    {
        var rows = new List<TreeRow>();
        var nodeMap = tree.Nodes.DistinctBy(n => n.EntryId).ToDictionary(n => n.EntryId);
        var processed = new HashSet<string>();

        var roots = tree.Nodes.Where(n => n.ParentEntryId is null).OrderBy(n => n.Order).ToList();
        if (roots.Count == 0)
        {
            // 无明确根节点 → 取 order 最小者
            var minOrder = tree.Nodes.Min(n => n.Order);
            roots = tree.Nodes.Where(n => n.Order == minOrder).ToList();
        }

        foreach (var root in roots)
            FlattenNode(root, nodeMap, rows, 0, processed, tree);

        // 孤立节点（未在遍历中访问到）
        foreach (var node in tree.Nodes.Where(n => !processed.Contains(n.EntryId)))
        {
            rows.Add(ToRow(node, 0, tree));
            processed.Add(node.EntryId);
        }
        return rows;

        static void FlattenNode(
            SessionTreeNode node,
            Dictionary<string, SessionTreeNode> nodeMap,
            List<TreeRow> rows,
            int depth,
            HashSet<string> processed,
            SessionTreeStructure tree
        )
        {
            if (!processed.Add(node.EntryId))
                return;
            rows.Add(ToRow(node, depth, tree));
            foreach (var childId in node.ChildIds)
            {
                if (nodeMap.TryGetValue(childId, out var child))
                    FlattenNode(child, nodeMap, rows, depth + 1, processed, tree);
            }
        }

        static TreeRow ToRow(SessionTreeNode node, int depth, SessionTreeStructure tree) =>
            new()
            {
                EntryId = node.EntryId,
                DisplayText = node.DisplayText,
                Depth = depth,
                IsActive = tree.ActiveEntryIds.Contains(node.EntryId),
                IsFork = node.IsFork,
                Labels = node.Labels,
                Type = node.Type,
                RoleText = node.Role,
            };
    }

    private TreeRow? SelectedRow => TreeList.SelectedItem as TreeRow;

    /// <summary>选中变化：刷新守卫提示与按钮可用态（镜像 TUI 分支守卫）。</summary>
    private void TreeList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var row = SelectedRow;
        LabelPanel.IsVisible = false;
        if (row is null)
        {
            GuardText.IsVisible = false;
            SwitchButton.IsEnabled = false;
            SummaryButton.IsEnabled = false;
            return;
        }

        // 守卫 1：非 Message 节点不能分支
        if (row.Type != JsonlEntryType.Message)
        {
            ShowGuard("只能在消息节点处创建分支");
            return;
        }
        // 守卫 2：当前活跃路径末端分支无意义
        if (row.EntryId == _activeLeafId)
        {
            ShowGuard("已在当前分支末端，无需分支");
            return;
        }
        GuardText.IsVisible = false;
        SwitchButton.IsEnabled = true;
        SummaryButton.IsEnabled = true;
        return;

        void ShowGuard(string message)
        {
            GuardText.Text = message;
            GuardText.IsVisible = true;
            SwitchButton.IsEnabled = false;
            SummaryButton.IsEnabled = false;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Enter:
                TryNavigate(generateSummary: false);
                e.Handled = true;
                break;
            default:
                break;
        }
    }

    private void SwitchButton_OnClick(object? sender, RoutedEventArgs e) =>
        TryNavigate(generateSummary: false);

    private void SummaryButton_OnClick(object? sender, RoutedEventArgs e) =>
        TryNavigate(generateSummary: true);

    private void LabelToggle_OnClick(object? sender, RoutedEventArgs e)
    {
        LabelPanel.IsVisible = !LabelPanel.IsVisible;
        if (LabelPanel.IsVisible)
            LabelInput.Focus();
    }

    private void LabelConfirm_OnClick(object? sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        var label = LabelInput.Text?.Trim();
        if (row is null || string.IsNullOrEmpty(label))
            return;
        // 标签模式：BranchPoint = Target = 选中节点（镜像 TUI TreeNavigationResult 标签分支）
        Close();
        _onNavigate(new NavigationResult(row.EntryId, GenerateSummary: false, LabelText: label));
    }

    /// <summary>分支导航（守卫后关闭并回调）。</summary>
    private void TryNavigate(bool generateSummary)
    {
        var row = SelectedRow;
        if (row is null || row.Type != JsonlEntryType.Message || row.EntryId == _activeLeafId)
            return; // 守卫已由 TreeList_OnSelectionChanged 的禁用按钮 + 提示承载，此处双保险
        Close();
        _onNavigate(new NavigationResult(row.EntryId, generateSummary, LabelText: null));
    }
}
