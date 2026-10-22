using Avalonia.Controls;
using Avalonia.Input;

namespace Desktop.Controls;

/// <summary>
/// 新建分卷快速窗口：只填卷名（可留空），回车即建；
/// 卷纲 / 目标 / 情节线等重字段仍走完整实体编辑窗（卷行 ⋯ → 查看 / 编辑）。
/// </summary>
public partial class VolumeQuickCreateWindow : Window
{
    private readonly Action<string> _onCreate;
    private bool _submitted;

    public VolumeQuickCreateWindow(Action<string> onCreate)
    {
        _onCreate = onCreate;
        InitializeComponent();
        Title = "新建分卷";

        NameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Submit();
                e.Handled = true;
            }
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                Submit();
                e.Handled = true;
            }
        };
        Opened += (_, _) => NameBox.Focus();
    }

    /// <summary>提交（幂等）：关闭窗口并回调 trim 后的卷名。</summary>
    private void Submit()
    {
        if (_submitted)
            return;
        _submitted = true;
        var name = NameBox.Text?.Trim() ?? "";
        Close();
        _onCreate(name);
    }

    private void CancelButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        Close();

    private void CreateButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        Submit();
}
