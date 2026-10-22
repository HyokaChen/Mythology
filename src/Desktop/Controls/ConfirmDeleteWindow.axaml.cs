using Avalonia.Controls;
using Avalonia.Input;

namespace Desktop.Controls;

/// <summary>
/// 删除确认对话框（移植自 TUI ConfirmDeleteDialog）：
/// 危险操作红色确认 + 取消 + Esc 关闭。
/// 对齐 DESIGN.md：Crimson Specimen 危险按钮。
/// </summary>
public partial class ConfirmDeleteWindow : Window
{
    private readonly Func<Task> _onConfirm;

    public ConfirmDeleteWindow(string label, string entityName, Func<Task> onConfirm)
    {
        _onConfirm = onConfirm;
        InitializeComponent();

        Title = $"删除{label}";
        ConfirmText.Text = $"确定要删除{label}「{entityName}」吗？\n此操作不可撤销。";

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
    }

    private void CancelButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }

    private async void DeleteButton_OnClick(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e
    )
    {
        Close();
        await _onConfirm();
    }
}
