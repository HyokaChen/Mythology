using Core.Tools;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Templating;

namespace Tui.Controls;

/// <summary>
/// 工具浏览器对话框 — 两种模式：
/// 1. 通过 /tools [Enter] 调用时显示所有插件（展开工具）
/// 2. 通过 /tools plugin-id [Enter] 调用时显示单个插件的工具
///
/// 选择工具会触发 <see cref="OnToolSelected"/>，
/// 返回 "plugin-id:tool-name" 字符串，调用者将其转换为 LLM 的用户消息提示。
/// </summary>
public sealed class ToolsDialog
{
    /// <summary>工具注册表。</summary>
    private readonly ToolRegistry _registry;

    /// <summary>工具选择回调，参数为 "plugin-id:tool-name"。</summary>
    private readonly Action<string> _onToolSelected;

    /// <summary>
    /// 创建工具对话框。
    /// </summary>
    /// <param name="registry">要浏览的工具注册表。</param>
    /// <param name="onToolSelected">用户选择工具时的回调，参数为 "plugin-id:tool-name"。</param>
    public ToolsDialog(ToolRegistry registry, Action<string> onToolSelected)
    {
        _registry = registry;
        _onToolSelected = onToolSelected;
    }

    /// <summary>
    /// 显示所有插件及其展开的工具。
    /// 触发方式: /tools [Enter]
    /// </summary>
    public void ShowAll()
    {
        var items = BuildAllItems();
        ShowDialog("🔧 工具列表", items);
    }

    /// <summary>
    /// 显示单个插件的工具。
    /// 触发方式: /tools plugin-id [Enter]
    /// </summary>
    public void ShowPlugin(string pluginId)
    {
        var pluginName =
            _registry.GetPluginNameById(pluginId)
            ?? _registry
                .GetPluginNames()
                .FirstOrDefault(p => p.Equals(pluginId, StringComparison.OrdinalIgnoreCase));

        if (pluginName is null)
            return;

        var tools = _registry.GetToolsByPlugin(pluginName);
        var id = ToolRegistry.NormalizePluginId(pluginName);
        // 工具名/标签来自插件清单（外部数据）→ 转义保号，标签按字面量拼进来才安全
        var items = tools
            .Select(tool =>
                (
                    $"  {ToolCallRenderer.Escape(tool.Name)} — {ToolCallRenderer.Escape(tool.Label)}",
                    $"{id}:{tool.Name}"
                )
            )
            .ToList();

        ShowDialog($"🔧 {id}", items);
    }

    // ── 构建所有插件视图的扁平项列表 ──

    private List<(string Display, string Value)> BuildAllItems()
    {
        var items = new List<(string Display, string Value)>();
        var allByPlugin = _registry.GetAllByPlugin();

        foreach (var (pluginName, tools) in allByPlugin)
        {
            var pluginId = ToolRegistry.NormalizePluginId(pluginName);

            // 上下文接续机制对用户隐藏：隐藏工具不进列表，标题计数也要与列表一致
            var visible = tools.Where(t => !HiddenTools.IsHidden(t.Name)).ToList();
            if (visible.Count == 0)
                continue;

            // 插件标题行（不可选中的分隔符）
            items.Add(
                (
                    $"[bold]{ToolCallRenderer.Escape(pluginId)}[/] ({visible.Count})",
                    $"__header__:{pluginId}"
                )
            );

            // 插件下的工具列表
            items.AddRange(
                visible.Select(tool =>
                    (
                        $"  {ToolCallRenderer.Escape(tool.Name)} — {ToolCallRenderer.Escape(tool.Label)}",
                        $"{pluginId}:{tool.Name}"
                    )
                )
            );
        }

        return items;
    }

    // ── 共享对话框渲染 ──

    /// <summary>
    /// 显示工具对话框。
    /// </summary>
    /// <param name="title">对话框标题。</param>
    /// <param name="items">显示项列表。</param>
    private void ShowDialog(string title, List<(string Display, string Value)> items)
    {
        var displayTexts = items.Select(i => i.Display).ToList();
        var values = items.Select(i => i.Value).ToList();

        var list = new OptionList<string>()
            .Items(displayTexts)
            .ItemTemplate(
                new DataTemplate<string>(
                    Display: (value, in _) =>
                    {
                        var text = value.GetValue();
                        // 插件标题行（加粗显示，不可选中）
                        return text.StartsWith("[bold]")
                            ? new Markup($"  {text}")
                            : new Markup($"    {text}");
                    },
                    Editor: null
                )
            )
            .ActivateOnClick(true);
        list.HorizontalAlignment = Align.Stretch;
        list.AutoFocus = true;

        const string bottomHint = "↑↓ 选择 · Enter 调用 · Esc 关闭";

        var dialog = new Dialog()
            .IsResizable(false)
            .IsDraggable(false)
            .IsModal(true) // 焦点锁定在 dialog 内：否则文本光标/鼠标 I-beam 会留在主输入框
            .Title(title)
            .BottomLeftText(new Markup($"[#C7B299]{bottomHint}[/]"))
            .Width(58)
            .MinHeight(16)
            .Padding(new Thickness(2, 1, 2, 1));

        list.ItemActivated(
            (_, e) =>
            {
                if (e.Index < 0 || e.Index >= values.Count)
                    return;

                var val = values[e.Index];
                // 跳过插件标题行
                if (val.StartsWith("__header__"))
                    return;

                dialog.Close();
                _onToolSelected(val);
            }
        );

        var contentVStack = new VStack(list).Spacing(0);
        dialog.Content(PanelThemes.CreateDialogContent(contentVStack));

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
                    {
                        // 如果选中项是插件标题行，跳到下一个可选项
                        var idx = list.SelectedIndex;
                        if (idx >= 0 && idx < values.Count)
                        {
                            var val = values[idx];
                            if (val.StartsWith("__header__"))
                            {
                                // 移动到下一个可选项
                                for (var i = idx + 1; i < values.Count; i++)
                                {
                                    if (!values[i].StartsWith("__header__"))
                                    {
                                        list.SelectedIndex = i;
                                        break;
                                    }
                                }
                                e.Handled = true;
                                return;
                            }
                        }

                        dialog.Close();
                        if (idx >= 0 && idx < values.Count)
                            _onToolSelected(values[idx]);
                        e.Handled = true;
                        break;
                    }
                }
            }
        );

        dialog.Show();
        dialog.App?.Focus(list);
    }
}
