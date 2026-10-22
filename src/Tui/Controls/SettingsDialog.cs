using Core.Data;
using Core.Plugins;
using Core.Tools;
using Tui.Services;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

namespace Tui.Controls;

/// <summary>
/// 系统设置对话框 — 包含连接器 / MCP / 工具标签页。
/// 按 Ctrl+P 打开此对话框。
/// 每个 Tab 由独立的内部类管理 State 和 UI，Show() 只做组装。
/// </summary>
public sealed class SettingsDialog(
    SettingsService cfg, // 设置服务
    ToolRegistry? toolRegistry = null, // 工具注册表（工具标签页用）
    Action? onProviderSaved = null, // 连接器保存后回调
    Action? onEmbeddingSaved = null, // 嵌入模型保存后回调（即时重配）
    Action? onSystemOneSaved = null, // 本地判定（System One）保存后回调（即时热生效）
    Action? onToolsSaved = null, // 工具保存后回调
    Action<string>? onPluginRemove = null, // 删除插件回调（参数：pluginId）
    ContextModeSwitcher? contextSwitcher = null // 上下文管理模式热切换（上下文标签页用）
)
{
    /// <summary>
    /// 显示系统设置对话框。
    /// </summary>
    public void Show()
    {
        var providerTab = new ProviderTab(cfg);
        var mcpTab = new McpTab();
        var pluginTab = new PluginTab();
        var systemOneSection = new SystemOneSection(cfg);
        var embeddingTab = new EmbeddingTab(cfg, systemOneSection.Build());
        var contextTab = new ContextTab(cfg, contextSwitcher);

        var tabs = PanelThemes.CreateDialogTabs(
            new TabPage("连接器", providerTab.Build()),
            new TabPage("嵌入", embeddingTab.Build()),
            new TabPage("上下文", contextTab.Build()),
            new TabPage("MCP", mcpTab.Build(cfg.Mcp)),
            new TabPage("工具", pluginTab.Build(cfg, toolRegistry, onPluginRemove))
        );

        var dialog = new Dialog()
            .IsResizable(false)
            .IsDraggable(false)
            .IsModal(true) // 焦点锁定在 dialog 内：否则文本光标/鼠标 I-beam 会留在主输入框
            .Title("🔧 系统设置")
            .BottomLeftText(new Markup("[#C7B299]← → 切换 Tab · Esc 关闭[/]"))
            .Width(60)
            .MinHeight(20)
            .Padding(new Thickness(2, 1, 2, 1))
            .Content(tabs);

        var closeBtn = PanelThemes.CreateDialogCloseButton(dialog.Close);

        var cancelBtn = new Button(new Markup("[dim]取消[/]")).Click(dialog.Close);
        var saveBtn = new Button(new Markup("[bold]保存[/]"))
            .Style(ThemeStyles.BuildButtonAccent(PanelThemes.Current))
            .Click(Save);

        dialog.TopRightText(closeBtn);
        dialog.BottomRightText(new HStack(cancelBtn, saveBtn).Spacing(2));

        dialog.KeyDown(
            (_, e) =>
            {
                if (e.Key == TerminalKey.Escape)
                {
                    dialog.Close();
                    e.Handled = true;
                }
            }
        );

        dialog.Show();
        dialog.App?.Focus(tabs);
        return;

        // UI 事件处理器：异常处理收敛到 TuiSafeFire（Toast），核心逻辑内新增 await 无需扩 try。
        // dialog.Close 在保存成功后调用。
        void Save()
        {
            TuiSafeFire.SafeFire(
                async () =>
                {
                    cfg.Provider = new ProviderConfig
                    {
                        Default = providerTab.SelectedConfigKey,
                        DefaultModel = providerTab.SelectedModel,
                        ThinkingLevel = providerTab.SelectedThinkingLevel,
                        ContextWindowSize = providerTab.SelectedContextWindowSize,
                        ApiKeys = providerTab.BuildApiKeyEntries(),
                    };
                    onProviderSaved?.Invoke();

                    cfg.Embedding = embeddingTab.BuildEmbeddingConfig();
                    await cfg.SaveProviderAsync();
                    onEmbeddingSaved?.Invoke();

                    cfg.SystemOne = systemOneSection.BuildSystemOneConfig();
                    await cfg.SaveProviderAsync();
                    onSystemOneSaved?.Invoke();
                    cfg.Mcp = new McpConfig { Servers = mcpTab.BuildServerEntries() };
                    await cfg.SaveMcpAsync();

                    cfg.Tool = new ToolConfig
                    {
                        Tools =
                        [
                            .. pluginTab
                                .BuildPluginEntries()
                                .Select(e => new ToolEntry { Name = e.Id, Enabled = e.Enabled }),
                        ],
                    };
                    await cfg.SaveToolAsync();
                    onToolsSaved?.Invoke();

                    // 上下文管理模式：热切换（重注册工具 + 重建检索索引 + 落盘），无需重启
                    cfg.ContextManagement = contextTab.BuildConfig();
                    if (contextSwitcher is not null)
                    {
                        var (ok, message) = await contextSwitcher.SwitchAsync(
                            cfg.ContextManagement
                        );
                        if (ok)
                            ToastService.Success(message);
                        else
                            ToastService.Warning(message);
                    }
                    else
                    {
                        await cfg.SaveContextManagementAsync();
                        ToastService.Success("设置已保存");
                    }
                    dialog.Close();
                },
                "save settings",
                errorMessage: "保存设置失败"
            );
        }
    }
}
