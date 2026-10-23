using Avalonia.Controls;
using Avalonia.Interactivity;
using Desktop.Controls;
using Desktop.ViewModels;

namespace Desktop.Views;

public partial class SettingsView : UserControl
{
    /// <summary>MCP 传输选项（供下拉绑定）。</summary>
    public static readonly string[] TransportOptions = ["stdio", "streamable-http", "sse"];

    /// <summary>初始选中 Tab 索引（顺序：连接器0 / 嵌入1 / 上下文2 / MCP3 / 工具4 / 外观5 / 账号6 / 关于7）。</summary>
    public int InitialTab { get; set; }

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (InitialTab > 0)
                SettingsTabControl.SelectedIndex = InitialTab;
        };
        SettingsTabControl.SelectionChanged += (_, _) =>
        {
            if (SettingsTabControl.SelectedContent is Avalonia.Visual content)
                _ = Desktop.Styles.Motion.PlayEnter(content, rise: 6);
        };
    }

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    private void AddApiKey_OnClick(object? sender, RoutedEventArgs e)
    {
        // 新卡片直接进入编辑态（展示态无内容可看）
        Vm?.ApiKeys.Add(new ApiKeyRow("openai", "", "", "") { IsEditing = true });
    }

    /// <summary>卡片「修改」：切到编辑态（同一时刻只展开一张）。</summary>
    private void EditApiKey_OnClick(object? sender, RoutedEventArgs e) =>
        BeginEdit(sender, Vm?.ApiKeys);

    /// <summary>嵌入卡片「修改」。</summary>
    private void EditEmbeddingApiKey_OnClick(object? sender, RoutedEventArgs e) =>
        BeginEdit(sender, Vm?.EmbeddingApiKeys);

    /// <summary>MCP 卡片「修改」。</summary>
    private void EditMcp_OnClick(object? sender, RoutedEventArgs e) =>
        BeginEdit(sender, Vm?.McpServers);

    /// <summary>卡片「完成」：收起编辑态（三种卡片共用）。</summary>
    private void EndEdit_OnClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is IEditToggleRow row)
            row.IsEditing = false;
    }

    private static void BeginEdit(object? sender, IReadOnlyList<IEditToggleRow>? rows)
    {
        if ((sender as Control)?.DataContext is not IEditToggleRow target)
            return;
        if (rows is not null)
            foreach (var row in rows)
                row.IsEditing = ReferenceEquals(row, target);
    }

    /// <summary>「发现模型」→ 从当前提供商 API 拉取模型列表（异步；VM 内完成状态更新）。</summary>
    private async void DiscoverModels_OnClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null)
            await Vm.DiscoverModelsAsync();
    }

    /// <summary>嵌入 Tab「发现模型」→ 同一发现逻辑（独立 embedding 连接器）。</summary>
    private async void DiscoverEmbeddingModels_OnClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null)
            await Vm.DiscoverEmbeddingModelsAsync();
    }

    private void RemoveApiKey_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ApiKeyRow row })
            Vm?.ApiKeys.Remove(row);
    }

    private void AddEmbeddingApiKey_OnClick(object? sender, RoutedEventArgs e)
    {
        Vm?.EmbeddingApiKeys.Add(new ApiKeyRow("openai", "", "", "") { IsEditing = true });
    }

    private void RemoveEmbeddingApiKey_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ApiKeyRow row })
            Vm?.EmbeddingApiKeys.Remove(row);
    }

    private void AddMcp_OnClick(object? sender, RoutedEventArgs e)
    {
        // 新卡片直接进入编辑态（与「添加密钥」一致）
        Vm?.McpServers.Add(new McpServerRow("", "stdio", "", "", "", true) { IsEditing = true });
    }

    private void RemoveMcp_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: McpServerRow row })
            Vm?.McpServers.Remove(row);
    }

    /// <summary>「去登录」→ 打开独立 CloudLoginWindow（OAuth / 设备码 / 手动 Key）。</summary>
    private async void CloudLogin_OnClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.CloudAuth is not { } cloudAuth)
            return;
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;
        var login = new CloudLoginWindow(cloudAuth);
        await login.ShowDialog(owner);
        Vm.RefreshCloudSession(); // 登录窗口关闭后刷新账号状态（不重载设置表单）
    }

    private void CloudLogout_OnClick(object? sender, RoutedEventArgs e)
    {
        Vm?.Logout();
    }

    /// <summary>「扫码绑定 / 重新扫码」→ 触发微信握手（QR 由通道服务出，卡片展示）。</summary>
    private async void WeChatBind_OnClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.WeChat is not { } weChat)
            return;

        await weChat.BindAsync();
    }

    /// <summary>「解除绑定」→ 清掉落盘凭证并释放轮询权。</summary>
    private void WeChatUnbind_OnClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.WeChat?.Unbind() != true)
            return;
    }
}
