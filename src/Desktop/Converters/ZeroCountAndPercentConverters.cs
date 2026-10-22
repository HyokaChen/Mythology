using System.Globalization;
using Avalonia.Data.Converters;

namespace Desktop.Converters;

/// <summary>集合数量为 0 → 可见（空状态提示）。</summary>
public sealed class ZeroCountToVisibilityConverter : IValueConverter
{
    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => value is int and 0;

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => throw new NotSupportedException();
}

/// <summary>百分比数字 → "N%"。</summary>
public sealed class PercentToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d ? $"{d:F0}%" : "0%";

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => throw new NotSupportedException();
}
