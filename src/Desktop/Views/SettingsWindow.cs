using Avalonia.Controls;
using Desktop.Controls;

namespace Desktop.Views;

/// <summary>设置模态窗口（Header ⚙ / ☁️ 打开）。对齐 DESIGN.md：Linen 底 · Adaline Tab。</summary>
public sealed class SettingsWindow : Window
{
    public SettingsWindow(int initialTab = 0)
    {
        DialogTheme.ApplyWindowStyle(this, "Mythology — 设置", 600, 720);
        MinWidth = 480;
        MinHeight = 560;
        Content = new SettingsView { InitialTab = initialTab };
    }
}
