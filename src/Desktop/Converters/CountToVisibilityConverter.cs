using System.Globalization;
using Avalonia.Data.Converters;

namespace Desktop.Converters;

/// <summary>集合数量 → 可见性（0 隐藏，非 0 可见）。</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => value is > 0;

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => throw new NotSupportedException();
}
