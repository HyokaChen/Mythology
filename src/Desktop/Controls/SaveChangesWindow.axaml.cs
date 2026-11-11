using Avalonia.Controls;
using Avalonia.Input;

namespace Desktop.Controls;

/// <summary>未保存修改的处理方式。</summary>
public enum SaveChangesChoice
{
    Save,
    Discard,
    Cancel,
}

/// <summary>
/// 「有未保存修改」三选一对话框：保存并继续 / 不保存 / 取消。
/// 关窗或 Esc = 取消（留在当前章节）。
/// </summary>
public partial class SaveChangesWindow : Window
{
    /// <summary>最近一次选择（测试 / 调用方在 ShowAsync 返回 null 时可回退读取）。</summary>
    public SaveChangesChoice Choice { get; private set; } = SaveChangesChoice.Cancel;

    public SaveChangesWindow(string heading, string message, string saveText = "保存并继续")
    {
        InitializeComponent();
        DialogTheme.ApplyWindowStyle(this, "未保存的修改", 520, 240);

        HeadingText.Text = heading;
        MessageText.Text = message;
        SaveButton.Content = saveText;

        SaveButton.Click += (_, _) => Choose(SaveChangesChoice.Save);
        DiscardButton.Click += (_, _) => Choose(SaveChangesChoice.Discard);
        CancelButton.Click += (_, _) => Choose(SaveChangesChoice.Cancel);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Choose(SaveChangesChoice.Cancel);
                e.Handled = true;
            }
        };
        Opened += (_, _) => SaveButton.Focus();
    }

    /// <summary>模态显示并返回选择；关窗 / Esc = null（等价取消）。</summary>
    public Task<SaveChangesChoice?> ShowAsync(Window owner) =>
        ShowDialog<SaveChangesChoice?>(owner);

    private void Choose(SaveChangesChoice choice)
    {
        Choice = choice;
        Close(choice);
    }
}
