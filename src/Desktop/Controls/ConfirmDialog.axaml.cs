using Avalonia.Controls;
using Avalonia.Input;

namespace Desktop.Controls;

/// <summary>
/// 确认对话框（否/是，红色危险操作样式，对齐 TUI ConfirmDeleteDialog + DESIGN.md）。
/// </summary>
public partial class ConfirmDialog : Window
{
    /// <summary>是否确认（点击「确认删除」为 true，取消/Esc 为 false）。</summary>
    public bool Confirmed { get; private set; }

    public ConfirmDialog(string message, string title = "确认操作", string confirmText = "确认删除")
    {
        InitializeComponent();

        Title = title;
        ConfirmText.Text = message;
        OkButton.Content = confirmText;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
    }

    private void OkButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void CancelButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }
}
