using System.Globalization;
using Avalonia.Data.Converters;
using LiveMarkdown.Avalonia;

namespace Desktop.Converters;

/// <summary>ObservableStringBuilder → string（非流式场景显示用）。</summary>
public sealed class ObservableStringBuilderToStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ObservableStringBuilder builder ? builder.ToString() : value?.ToString() ?? "";

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => throw new NotSupportedException();
}
