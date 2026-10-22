using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Desktop.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        KeyDown += MainWindow_OnKeyDown;
        Loaded += OnLoadedRestorePanelWidth;
    }

    /// <summary>启动时恢复上次的设定面板列宽（非法/缺失 → config 默认 320）。</summary>
    private void OnLoadedRestorePanelWidth(object? sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedRestorePanelWidth;
        var width = ConfigStoreAccessor()?.Load().Ui.OutlinePanelWidth;
        if (width is not { } value || MainColumns.ColumnDefinitions.Count < 3)
            return;
        MainColumns.ColumnDefinitions[2].Width = new GridLength(
            Core.Data.UiConfig.ClampWidth(value),
            GridUnitType.Pixel
        );
    }

    /// <summary>拖拽结束：列宽归一后落盘（先 Load 再改字段再 Save，避免覆盖 providers 等其它节）。</summary>
    private async void SidePanelSplitter_OnDragCompleted(object? sender, VectorEventArgs e)
    {
        if (MainColumns.ColumnDefinitions.Count < 3)
            return;
        var store = ConfigStoreAccessor();
        if (store is null)
            return;

        var width = Core.Data.UiConfig.ClampWidth(MainColumns.ColumnDefinitions[2].ActualWidth);
        MainColumns.ColumnDefinitions[2].Width = new GridLength(width, GridUnitType.Pixel);
        var config = store.Load();
        config.Ui.OutlinePanelWidth = width;
        await store.SaveAsync(config);
    }

    /// <summary>DI 里的 ConfigStore（测试/设计时可能为 null）。</summary>
    private static Core.Data.ConfigStore? ConfigStoreAccessor() =>
        Program.Services?.GetService<Core.Data.ConfigStore>();

    /// <summary>
    /// 全局快捷键（对齐 TUI：Ctrl+P 设置 / Ctrl+O 项目 / Ctrl+N 新建 / Ctrl+H 帮助 / Ctrl+L 清屏 / Ctrl+T 主题）。
    ///
    /// 注意：快捷键注册在 Window.KeyDown；输入框内组合键（Ctrl+L 等）由 TextBox 自行处理，
    /// 此处仅捕获未被输入控件消费的全局组合键。
    /// </summary>
    private void MainWindow_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ViewModels.MainWindowViewModel vm)
            return;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (!ctrl)
            return;

        switch (e.Key)
        {
            case Key.P:
                OpenSettings();
                e.Handled = true;
                break;
            case Key.O:
                vm.ShowNovelPicker();
                e.Handled = true;
                break;
            case Key.E:
                vm.ShowSessionPicker();
                e.Handled = true;
                break;
            case Key.N:
                vm.NewConversationCommand.Execute();
                e.Handled = true;
                break;
            case Key.H:
                ShowAbout();
                e.Handled = true;
                break;
            case Key.L:
                vm.ActiveConversationVm?.TryExecuteCommand("/clear");
                e.Handled = true;
                break;
            case Key.T:
                vm.ToggleTheme();
                e.Handled = true;
                break;
            case Key.K:
                vm.ShowCommandPalette();
                e.Handled = true;
                break;
        }
    }

    /// <summary>打开设置窗口。</summary>
    private void OpenSettings()
    {
        if (Program.Services is null)
            return;
        var vm = Program.Services.GetRequiredService<ViewModels.SettingsViewModel>().Refresh();
        var window = new SettingsWindow { DataContext = vm };
        window.ShowDialog(this);
    }

    /// <summary>显示帮助对话框。</summary>
    private void ShowAbout()
    {
        var about = new AboutDialog();
        about.ShowDialog(this);
    }

    /// <summary>设置按钮 → 模态设置窗口。</summary>
    private void SettingsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        OpenSettings();
    }

    /// <summary>Header 小说按钮 → 打开项目选择器（同 Ctrl+O）。</summary>
    private void NovelButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel vm)
            vm.ShowNovelPicker();
    }

    /// <summary>Header 会话按钮 → 打开会话管理器（同 Ctrl+E）。</summary>
    private void SessionButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel vm)
            vm.ShowSessionPicker();
    }

    /// <summary>☁️ 云端按钮 → 已登录打开账号管理；未登录/失效打开登录窗。</summary>
    private void CloudButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel vm)
            vm.HandleCloudButtonClick();
    }

    /// <summary>吊销横幅「立即重新登录」→ 打开登录窗（非模态，不打断创作）。</summary>
    private void CloudReloginButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel vm)
            vm.ReloginFromInvalidBanner();
    }

    /// <summary>吊销横幅「×」→ 关闭横幅（可随时点击 ☁️ 重登）。</summary>
    private void CloudBannerDismiss_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel vm)
            vm.DismissCloudInvalidBanner();
    }

    /// <summary>Header 主题按钮：切换浅/深色（同 Ctrl+T）。</summary>
    private void ThemeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel vm)
            vm.ToggleTheme();
    }

    /// <summary>状态栏模型区 → 快速切换模型（当前提供商模型列表）。</summary>
    private async void ModelButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel vm)
            await vm.ShowModelQuickSwitchAsync();
    }

    /// <summary>状态栏上下文区 → 会话状态（/state：消息数 / 上下文 / 模式）。</summary>
    private void StatusContext_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel vm)
            vm.ActiveConversationVm?.TryExecuteCommand("/state");
    }

    /// <summary>状态栏工具区 → 设置·工具 Tab（索引 4）。</summary>
    private void StatusTools_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel vm)
            vm.OpenSettings(initialTab: 4);
    }
}
