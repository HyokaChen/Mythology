using Core.Tools;
using Tui.Services;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

namespace Tui.Controls;

/// <summary>
/// 工具/插件设置标签页。
/// </summary>
internal sealed class PluginTab
{
    private readonly List<PluginCardState> _entries = [];

    /// <summary>
    /// 构建插件设置标签页的 UI。
    /// </summary>
    /// <param name="cfg">设置服务。</param>
    /// <param name="toolRegistry">工具注册表。</param>
    /// <param name="onPluginRemove">删除插件回调（参数：pluginId）。</param>
    /// <returns>视觉元素。</returns>
    public Visual Build(
        SettingsService cfg,
        ToolRegistry? toolRegistry,
        Action<string>? onPluginRemove = null
    )
    {
        var accordion = new Accordion
        {
            SingleExpanded = false,
            Spacing = 1,
            VerticalAlignment = Align.Start,
        };

        foreach (var p in cfg.DiscoveredPlugins)
        {
            var enabledState = new State<bool>(cfg.IsPluginEnabled(p.Id));
            var pluginId = p.Id; // 捕获到闭包
            _entries.Add(new PluginCardState(p.Id, p.Name, enabledState));

            var tools = toolRegistry?.GetToolsByPlugin(p.Name) ?? [];

            var title = new Markup($"🔧 [bold]{p.Name}[/] [dim]({tools.Count} tools)[/]");

            var controls = new List<Visual>
            {
                new Switch().Style(ThemeStyles.BuildSwitch(PanelThemes.Current)).IsOn(enabledState),
            };

            // 显示删除按钮（内置插件由 RemovePlugin 回调中拦截）
            if (onPluginRemove is not null)
            {
                controls.Add(
                    new Button(new Markup("[dim]✕[/]"))
                        .Style(ThemeStyles.BuildCloseButton(PanelThemes.Current))
                        .Click(() => ShowRemoveConfirm(p.Name, pluginId, onPluginRemove))
                );
            }

            // 标题左对齐、开关/删除按钮右对齐：
            // 弹性 spacer 吃掉中间宽度（Grid 方案会把高 2 的控件推到下一行）
            var headerItems = new List<Visual>
            {
                title,
                // 弹性占位（Padder 无边框无填充，纯粹吃掉中间宽度；
                // 不能用 Border——它默认带边框线会显示一个丑框）
                new Padder { HorizontalAlignment = Align.Stretch },
            };
            headerItems.AddRange(controls);
            var header = new HStack(headerItems.ToArray()).Spacing(2);
            header.HorizontalAlignment = Align.Stretch; // 占满折叠项整行（右缘对齐依赖）

            var list = new VStack().Spacing(0);
            foreach (var tool in tools.OrderBy(t => t.Name))
                list.Children.Add(new Markup($"  • [bold]{tool.Name}[/] — [dim]{tool.Label}[/]"));

            accordion.Children.Add(
                new Collapsible(header, list)
                {
                    IsExpanded = false,
                    HorizontalAlignment = Align.Stretch, // 折叠项占满整行
                }
            );
        }

        if (cfg.DiscoveredPlugins.Count == 0)
        {
            accordion.Children.Add(
                new Collapsible(
                    new Markup("[dim]未发现插件，请运行 mythology build --install[/]"),
                    new Markup("")
                )
                {
                    IsExpanded = false,
                    HorizontalAlignment = Align.Stretch,
                }
            );
        }

        // 占满整行：折叠项右缘统一（配合 CreateScrollableTabContent 的 Stretch 链）
        var content = new VStack(
            accordion,
            new Markup("[dim]▸/▾ 展开插件查看工具列表 · 开关控制插件是否可用 · ✕ 删除插件[/]")
        ).Spacing(2);
        content.HorizontalAlignment = Align.Stretch;

        return PanelThemes.CreateScrollableTabContent(content);
    }

    /// <summary>
    /// 显示删除插件确认对话框。
    /// </summary>
    private static void ShowRemoveConfirm(
        string pluginName,
        string pluginId,
        Action<string> onRemove
    )
    {
        var dlg = new Dialog()
            .IsResizable(false)
            .IsDraggable(false)
            .IsModal(true) // 焦点锁定在 dialog 内：否则文本光标/鼠标 I-beam 会留在主输入框
            .Title("🚫 删除插件")
            .BottomLeftText(new Markup("[#C7B299]Esc 取消[/]"))
            .Width(44)
            .MinHeight(8)
            .Padding(new Thickness(2, 1, 2, 1))
            .Content(
                new Markup(
                    $"确定要删除插件「[bold]{ToolCallRenderer.Escape(pluginName)}[/]」吗？\n插件将从当前会话卸载并从磁盘移除。\n此操作不可撤销。"
                )
            );

        var closeBtn = PanelThemes.CreateDialogCloseButton(dlg.Close);

        var cancelBtn = new Button(new Markup("[dim]取消[/]")).Click(dlg.Close);
        var deleteBtn = new Button(new Markup("[error]确认删除[/]"))
            .Style(ThemeStyles.BuildButtonDanger(PanelThemes.Current))
            .Click(() =>
            {
                dlg.Close();
                onRemove(pluginId);
            });

        dlg.TopRightText(closeBtn);
        dlg.BottomRightText(new HStack(cancelBtn, deleteBtn).Spacing(2));
        dlg.KeyDown(
            (_, e) =>
            {
                if (e.Key == TerminalKey.Escape)
                {
                    dlg.Close();
                    e.Handled = true;
                }
            }
        );
        dlg.Show();
    }

    /// <summary>
    /// 构建插件条目列表。
    /// </summary>
    /// <returns>插件条目列表。</returns>
    public List<(string Id, bool Enabled)> BuildPluginEntries() =>
        _entries.Select(e => (e.Id, e.Enabled.Value)).ToList();
}

/// <summary>
/// 插件卡片状态。
/// </summary>
internal sealed class PluginCardState(string id, string name, State<bool> enabled)
{
    public string Id = id; // 插件 ID
    public string Name = name; // 插件名称
    public readonly State<bool> Enabled = enabled; // 是否启用
}
