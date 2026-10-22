using Avalonia.Controls;
using Avalonia.Input;

namespace Desktop.Controls;

/// <summary>
/// 简易模态消息框（Avalonia 无内置 MessageBox）。
/// 对齐 DESIGN.md（Adaline）：Linen 底 · Primary 按钮 · 细节排版。
/// </summary>
public partial class MessageBox : Window
{
    /// <summary>异步显示（await 关闭）。</summary>
    public Task ShowDialogAsync(Window owner)
    {
        var tcs = new TaskCompletionSource();
        Closed += (_, _) => tcs.TrySetResult();
        ShowDialog(owner);
        return tcs.Task;
    }

    public MessageBox(string title, string message)
    {
        InitializeComponent();

        Title = title;
        MessageText.Text = message;

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
        Close();
    }
}
