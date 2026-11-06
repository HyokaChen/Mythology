using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Threading;
using Desktop.Services;

namespace Desktop.Controls;

/// <summary>
/// 密钥输入：默认只显示脱敏摘要（保留末 4 位），获得焦点后才显示完整值；
/// 失焦立即恢复遮蔽。设置页所有密钥输入共用，避免逐个字段重复遮蔽逻辑。
/// </summary>
public partial class SecretTextBox : UserControl
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<
        SecretTextBox,
        string?
    >(nameof(Text), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string?> PlaceholderTextProperty =
        AvaloniaProperty.Register<SecretTextBox, string?>(nameof(PlaceholderText));

    private bool _isRevealed;

    public SecretTextBox()
    {
        InitializeComponent();

        Editor.Bind(
            TextBox.TextProperty,
            new Binding(nameof(Text)) { Source = this, Mode = BindingMode.TwoWay }
        );
        Editor.Bind(
            TextBox.PlaceholderTextProperty,
            new Binding(nameof(PlaceholderText)) { Source = this }
        );

        MaskedDisplay.GotFocus += (_, _) => Reveal();
        Editor.LostFocus += (_, _) => Conceal();

        UpdateMaskedText();
    }

    /// <summary>真实密钥值；默认双向绑定。</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>空值时的占位文案（同时用于展示态和编辑态）。</summary>
    public string? PlaceholderText
    {
        get => GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    /// <summary>当前遮蔽后的展示文本；保留末 4 位，供测试与无障碍读取。</summary>
    public string MaskedText => MaskedDisplay.Text ?? "";

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty || change.Property == PlaceholderTextProperty)
            UpdateMaskedText();
    }

    private void UpdateMaskedText()
    {
        var emptyText = string.IsNullOrWhiteSpace(PlaceholderText)
            ? "未填写密钥"
            : PlaceholderText!;
        MaskedDisplay.Text = SecretMask.Format(Text, emptyText);
    }

    private void Reveal()
    {
        if (_isRevealed)
            return;

        _isRevealed = true;
        Editor.Opacity = 0;
        Editor.IsVisible = true;

        // 切换可见性后布局可能还没完成：先同步聚焦，失败再排一次后台任务。
        if (TryFocusEditor())
            return;

        Dispatcher.UIThread.Post(
            () =>
            {
                if (!_isRevealed || TryFocusEditor())
                    return;

                // 聚焦失败时不能把明文留在未聚焦的控件上。
                Conceal();
            },
            DispatcherPriority.Background
        );
    }

    private bool TryFocusEditor()
    {
        if (!Editor.Focus())
            return false;

        MaskedDisplay.IsVisible = false;
        Editor.Opacity = 1;
        Editor.CaretIndex = Editor.Text?.Length ?? 0;
        return true;
    }

    private void Conceal()
    {
        if (!_isRevealed)
            return;

        _isRevealed = false;
        Editor.IsVisible = false;
        Editor.Opacity = 1;
        MaskedDisplay.IsVisible = true;
        UpdateMaskedText();
    }
}
