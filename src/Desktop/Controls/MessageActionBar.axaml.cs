using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Desktop.Models;
using Lucide.Avalonia;

namespace Desktop.Controls;

/// <summary>
/// 消息块悬停动作条：复制是通用动作；编辑只出现在用户消息，重试只出现在助手回合。
/// 具体显示/隐藏由 <c>ConversationView</c> 的 <c>Grid.MessageHoverHost</c> 样式负责。
/// </summary>
public partial class MessageActionBar : UserControl
{
    public static readonly StyledProperty<bool> ShowCopyProperty = AvaloniaProperty.Register<
        MessageActionBar,
        bool
    >(nameof(ShowCopy), defaultValue: true);

    public static readonly StyledProperty<bool> ShowEditProperty = AvaloniaProperty.Register<
        MessageActionBar,
        bool
    >(nameof(ShowEdit));

    public static readonly StyledProperty<bool> ShowRetryProperty = AvaloniaProperty.Register<
        MessageActionBar,
        bool
    >(nameof(ShowRetry));

    private CancellationTokenSource? _copyFeedbackCts;

    public MessageActionBar()
    {
        InitializeComponent();
        ApplyVisibility();
    }

    public bool ShowCopy
    {
        get => GetValue(ShowCopyProperty);
        set => SetValue(ShowCopyProperty, value);
    }

    public bool ShowEdit
    {
        get => GetValue(ShowEditProperty);
        set => SetValue(ShowEditProperty, value);
    }

    public bool ShowRetry
    {
        get => GetValue(ShowRetryProperty);
        set => SetValue(ShowRetryProperty, value);
    }

    /// <summary>用户消息请求进入内联编辑（由宿主决定编辑哪条消息）。</summary>
    public event EventHandler? EditRequested;

    /// <summary>助手消息请求重新生成。</summary>
    public event EventHandler? RetryRequested;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (
            change.Property == ShowCopyProperty
            || change.Property == ShowEditProperty
            || change.Property == ShowRetryProperty
        )
            ApplyVisibility();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _copyFeedbackCts?.Cancel();
        _copyFeedbackCts = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void ApplyVisibility()
    {
        // Styled property defaults may be applied before InitializeComponent on some paths.
        if (CopyButton is null || EditButton is null || RetryButton is null)
            return;
        CopyButton.IsVisible = ShowCopy;
        EditButton.IsVisible = ShowEdit;
        RetryButton.IsVisible = ShowRetry;
    }

    private void CopyButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConversationRow row)
            return;

        var text = MessageCopy.Build(row);
        if (text.Length == 0)
            return;

        _ = CopyWithFeedbackAsync(text);
    }

    private async Task CopyWithFeedbackAsync(string text)
    {
        if (!await MessageCopy.CopyAsync(TopLevel.GetTopLevel(this), text))
            return;

        CopyIcon.Kind = LucideIconKind.Check;
        ToolTip.SetTip(CopyButton, "已复制");

        _copyFeedbackCts?.Cancel();
        var cts = new CancellationTokenSource();
        _copyFeedbackCts = cts;
        try
        {
            await Task.Delay(1200, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        CopyIcon.Kind = LucideIconKind.Copy;
        ToolTip.SetTip(CopyButton, "复制");
    }

    private void EditButton_OnClick(object? sender, RoutedEventArgs e) =>
        EditRequested?.Invoke(this, EventArgs.Empty);

    private void RetryButton_OnClick(object? sender, RoutedEventArgs e) =>
        RetryRequested?.Invoke(this, EventArgs.Empty);
}
