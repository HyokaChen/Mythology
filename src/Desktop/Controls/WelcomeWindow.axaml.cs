using Avalonia.Controls;
using Avalonia.Input;

namespace Desktop.Controls;

/// <summary>
/// 欢迎/引导窗口 — 首次启动或未配置 LLM API Key 时弹出。
/// 对齐 DESIGN.md（Adaline）：serif 品牌 moment · Forest Ink 正文 · Primary/Ghost 按钮。
/// </summary>
public partial class WelcomeWindow : Window
{
    private readonly Action _onOpenSettings;

    public WelcomeWindow(Action onOpenSettings)
    {
        _onOpenSettings = onOpenSettings;
        InitializeComponent();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
    }

    private void SettingsButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
        _onOpenSettings();
    }

    private void BrowseButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }
}
