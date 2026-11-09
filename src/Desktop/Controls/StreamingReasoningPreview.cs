using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Desktop.Controls;

/// <summary>
/// 固定两行的流式思考预览（借鉴 TKCode.Arc ReasoningBlock 的 preview box）：
/// 文本按最新行底部对齐、超出裁剪；新行从下方顶上来，而非在顶部反复重排。
/// </summary>
public sealed class StreamingReasoningPreview : Border
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<
        StreamingReasoningPreview,
        string?
    >(nameof(Text));

    public static readonly StyledProperty<IBrush?> TextForegroundProperty =
        AvaloniaProperty.Register<StreamingReasoningPreview, IBrush?>(
            nameof(TextForeground),
            Brushes.Gray
        );

    private readonly TextBlock _text;

    public StreamingReasoningPreview()
    {
        Height = 40;
        ClipToBounds = true;
        _text = new TextBlock
        {
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            LineHeight = 20,
        };
        _text.Bind(TextBlock.ForegroundProperty, this.GetObservable(TextForegroundProperty));
        Child = _text;
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public IBrush? TextForeground
    {
        get => GetValue(TextForegroundProperty);
        set => SetValue(TextForegroundProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
            _text.Text = change.GetNewValue<string?>();
    }
}
