using Core.Data;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Templating;

namespace Tui.Controls;

/// <summary>
/// 会话树形选择器 — 展示对话分支结构，支持分支导航。
/// 按 /tree 命令打开。
/// </summary>
public sealed class TreeSelectorPanel(
    FileSessionStore store, // 会话存储服务
    Action<TreeSelectorPanel.TreeNavigationResult>? onNavigate = null // 树导航回调
)
{
    /// <summary>
    /// 树导航结果。
    /// </summary>
    /// <param name="BranchPointEntryId">分支点的 entryId。</param>
    /// <param name="GenerateSummary">是否生成 AI 摘要。</param>
    /// <param name="LabelTargetEntryId">要添加标签的目标 entryId（null 表示不添加标签）。</param>
    public sealed record TreeNavigationResult(
        string BranchPointEntryId,
        bool GenerateSummary,
        string? LabelTargetEntryId
    );

    /// <summary>
    /// 异步显示树形选择器对话框。
    /// </summary>
    /// <param name="sessionId">当前会话 ID。</param>
    public void Show(string sessionId)
    {
        _ = ShowAsync(sessionId);
    }

    /// <summary>
    /// 异步显示树形选择器对话框。
    /// </summary>
    /// <param name="sessionId">当前会话 ID。</param>
    private async Task ShowAsync(string sessionId)
    {
        try
        {
            var tree = await store.GetTreeStructure(sessionId);
            if (tree.Nodes.Count == 0)
            {
                ToastService.Warning("当前会话无消息，无法展示树结构");
                return;
            }

            if (!tree.HasBranches)
            {
                ToastService.Info("当前会话为线性对话，无分支点");
                return;
            }

            ShowTreeList(sessionId, tree);
        }
        catch (Exception ex)
        {
            ToastService.Error($"加载树结构失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 显示树形列表对话框。
    /// </summary>
    /// <param name="sessionId">会话 ID。</param>
    /// <param name="tree">树结构数据。</param>
    private void ShowTreeList(string sessionId, SessionTreeStructure tree)
    {
        // 构建扁平化的树行列表（带缩进）
        var rows = new List<TreeRow>();
        var nodeMap = tree.Nodes.DistinctBy(n => n.EntryId).ToDictionary(n => n.EntryId);
        var processed = new HashSet<string>();

        // 找到根节点
        var roots = tree.Nodes.Where(n => n.ParentEntryId is null).OrderBy(n => n.Order).ToList();

        // 如果没有明确的根节点，取 order 最小的
        if (roots.Count == 0)
        {
            var minOrder = tree.Nodes.Min(n => n.Order);
            roots = tree.Nodes.Where(n => n.Order == minOrder).ToList();
        }

        foreach (var root in roots)
        {
            FlattenTree(root, nodeMap, rows, 0, processed, tree.ActiveEntryIds);
        }

        // 处理未访问的节点（孤立节点）
        rows.AddRange(
            tree.Nodes.Where(n => !processed.Contains(n.EntryId))
                .Select(node => new TreeRow(
                    node.EntryId,
                    node.DisplayText,
                    node.Role,
                    node.Order,
                    0,
                    tree.ActiveEntryIds.Contains(node.EntryId),
                    node.IsFork,
                    node.Labels,
                    node.Type
                ))
        );

        var list = new OptionList<TreeRow>()
            .Items(rows)
            .ItemSearchText(item => item.DisplayText)
            .ItemTemplate(
                new DataTemplate<TreeRow>(
                    Display: (value, in _) =>
                    {
                        var row = value.GetValue();
                        var indent = new string(' ', row.Depth * 2);

                        // 活跃路径标记
                        var activeMarker = row.IsActive ? " ←" : "";

                        // 分支点标记
                        var forkMarker = row.IsFork ? " 🔀" : "";

                        // 标签
                        var labelMarker =
                            row.Labels.Count > 0
                                ? " "
                                    + string.Join(
                                        " ",
                                        row.Labels.Select(l =>
                                            $"[italic yellow]🔖{ToolCallRenderer.Escape(l)}[/]"
                                        )
                                    )
                                : "";

                        // 角色颜色

                        var contentColor = row.Type switch
                        {
                            // 非消息类型用特殊颜色
                            JsonlEntryType.Event => "orange3",
                            JsonlEntryType.BranchSummary => "blue",
                            _ => row.Role switch
                            {
                                "user" => "success",
                                "assistant" => "primary",
                                "tool" => "warning",
                                _ => "dim",
                            },
                        };

                        var displayText = ToolCallRenderer.Escape(row.DisplayText);
                        var text = row.IsActive
                            ? $"[{contentColor}]{displayText}[/]"
                            : $"[dim]{displayText}[/]";

                        return new HStack(
                            new Markup($"[dim]{indent}[/]"),
                            new Markup(text),
                            new Markup($"[dim]{forkMarker}{activeMarker}[/]"),
                            new Markup(labelMarker)
                        ).Spacing(0);
                    },
                    Editor: null
                )
            )
            .ActivateOnClick(true);

        list.HorizontalAlignment = Align.Stretch;
        list.AutoFocus = true;

        var dialog = new Dialog()
            .IsResizable(false)
            .IsDraggable(false)
            .IsModal(true) // 焦点锁定在 dialog 内：否则文本光标/鼠标 I-beam 会留在主输入框
            .Title($"🌳 对话分支树 · {sessionId}")
            .BottomLeftText(new Markup("[#C7B299]Enter 分支 · s 分支+摘要 · l 标签 · Esc 关闭[/]"))
            .Width(70)
            .MinHeight(15)
            .Padding(new Thickness(2, 1, 2, 1));

        list.ItemActivated(OnActivated);

        // 统一走 DesertContentFill（整体一色篝火岩）——此前硬编码 charcoal 内容区导致旧组合
        dialog.Content(PanelThemes.CreateDialogContent(list));

        var closeBtn = PanelThemes.CreateDialogCloseButton(dialog.Close);
        dialog.TopRightText(closeBtn);

        dialog.KeyDown(
            (_, e) =>
            {
                switch (e.Key)
                {
                    case TerminalKey.Escape:
                        dialog.Close();
                        e.Handled = true;
                        break;
                    case TerminalKey.Enter:
                        // Enter: 直接分支（不生成摘要）
                        HandleSelection(list, rows, generateSummary: false, labelMode: false);
                        e.Handled = true;
                        break;
                    default:
                        switch (e.Char)
                        {
                            case 's' or 'S':
                                // s: 分支并生成 AI 摘要
                                HandleSelection(
                                    list,
                                    rows,
                                    generateSummary: true,
                                    labelMode: false
                                );
                                e.Handled = true;
                                break;
                            case 'l' or 'L':
                                // l: 为选中节点添加标签
                                HandleSelection(
                                    list,
                                    rows,
                                    generateSummary: false,
                                    labelMode: true
                                );
                                e.Handled = true;
                                break;
                        }

                        break;
                }
            }
        );

        dialog.Show();
        dialog.App?.Focus(list);
        return;

        void OnActivated(object? _, ItemActivatedEventArgs e)
        {
            // 默认 Enter 行为：直接分支
            HandleSelection(list, rows, generateSummary: false, labelMode: false);
        }

        void HandleSelection(
            OptionList<TreeRow> optionList,
            List<TreeRow> rowList,
            bool generateSummary,
            bool labelMode
        )
        {
            var idx = optionList.SelectedIndex;
            if (idx < 0 || idx >= rowList.Count)
                return;

            var selected = rowList[idx];

            // 标签模式：为选中节点添加标签
            if (labelMode)
            {
                dialog.Close();
                onNavigate?.Invoke(
                    new TreeNavigationResult(
                        BranchPointEntryId: selected.EntryId,
                        GenerateSummary: false,
                        LabelTargetEntryId: selected.EntryId
                    )
                );
                return;
            }

            // 分支模式：只允许在分支点或任意消息节点分支
            // 不允许在 event/branch_summary 节点分支
            if (selected.Type != JsonlEntryType.Message)
            {
                ToastService.Warning("只能在消息节点处创建分支");
                return;
            }

            // 不允许在当前活跃路径的叶子节点分支（无意义）
            if (selected.EntryId == tree.ActiveLeafId)
            {
                ToastService.Info("已在当前分支末端，无需分支");
                return;
            }

            CloseDialogAndNavigate(selected.EntryId, generateSummary);
        }

        void CloseDialogAndNavigate(string branchPointId, bool generateSummary)
        {
            dialog.Close();
            onNavigate?.Invoke(
                new TreeNavigationResult(
                    BranchPointEntryId: branchPointId,
                    GenerateSummary: generateSummary,
                    LabelTargetEntryId: null
                )
            );
        }
    }

    /// <summary>
    /// 递归扁平化树结构为行列表。
    /// </summary>
    private static void FlattenTree(
        SessionTreeNode node,
        Dictionary<string, SessionTreeNode> nodeMap,
        List<TreeRow> rows,
        int depth,
        HashSet<string> processed,
        HashSet<string> activeEntryIds
    )
    {
        if (!processed.Add(node.EntryId))
            return;

        rows.Add(
            new TreeRow(
                node.EntryId,
                node.DisplayText,
                node.Role,
                node.Order,
                depth,
                activeEntryIds.Contains(node.EntryId),
                node.IsFork,
                node.Labels,
                node.Type
            )
        );

        foreach (var childId in node.ChildIds)
        {
            if (nodeMap.TryGetValue(childId, out var child))
            {
                FlattenTree(child, nodeMap, rows, depth + 1, processed, activeEntryIds);
            }
        }
    }

    /// <summary>
    /// 树行数据，用于 OptionList 展示。
    /// </summary>
    private sealed record TreeRow(
        string EntryId,
        string DisplayText,
        string? Role,
        int Order,
        int Depth,
        bool IsActive,
        bool IsFork,
        List<string> Labels,
        JsonlEntryType Type
    );
}
