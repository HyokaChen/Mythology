using Avalonia.Controls;
using Avalonia.Interactivity;
using Core.Data;
using Desktop.Models;
using Desktop.ViewModels;

namespace Desktop.Controls;

/// <summary>
/// 设定检查窗（原「检查」Flyout 平移）：摘要 / findings（含反馈与深链）/ 结构量 / 覆盖度。
/// 深链：解析 involved 实体 → 关窗 → onNavigate（由设定面板切 Tab 并打开编辑器）。
/// </summary>
public partial class SetupCheckWindow : Window
{
    private readonly Action<EntityType, string> _onNavigate;

    public SetupCheckWindow(SetUpPanelViewModel vm, Action<EntityType, string> onNavigate)
    {
        _onNavigate = onNavigate;

        InitializeComponent();
        DialogTheme.ApplyWindowStyle(this, "设定检查", 560, 640);
        DataContext = vm;

        CloseButton.Click += (_, _) => Close();

        RunCheckButton.Click += async (_, _) =>
        {
            if (vm.IsChecking)
                return;
            RunCheckButton.IsEnabled = false;
            RunCheckButton.Content = "检查中…";
            try
            {
                await vm.ExecuteCheckSetupAsync();
            }
            finally
            {
                RunCheckButton.IsEnabled = true;
                RunCheckButton.Content = "运行检查";
            }
        };
    }

    /// <summary>👍/👎 反馈按钮 → 上报 finding 采纳/忽略（CHANGE_10 §4.3）。</summary>
    private async void OnSetupFeedback_Click(object? sender, RoutedEventArgs e)
    {
        if (
            sender is Button { DataContext: SetupFindingItem finding, Tag: string decision }
            && DataContext is SetUpPanelViewModel vm
        )
        {
            await vm.ReportSetupFeedback(finding.Id, decision);
        }
    }

    /// <summary>🔗 深链按钮 → 关窗后跳转到 involved 实体（由面板切 Tab 并打开编辑器）。</summary>
    private void OnFindingNavigate_Click(object? sender, RoutedEventArgs e)
    {
        if (
            sender is Button { Tag: SetupFindingItem finding }
            && DataContext is SetUpPanelViewModel vm
            && vm.TryResolveEntityLink(finding, out var type, out var entityItem)
            && entityItem is not null
        )
        {
            Close();
            _onNavigate(type, entityItem.Id);
        }
    }
}
