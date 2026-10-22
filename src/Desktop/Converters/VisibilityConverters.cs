using System.Globalization;
using Avalonia.Data.Converters;

namespace Desktop.Converters;

/// <summary>bool → 可见性。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true;

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => value is true;
}

/// <summary>bool → 可见性（取反）。</summary>
public sealed class InvertedBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => value is not true;
}

/// <summary>多值 AND：全部为 true 才可见（用于多个可见性条件叠加）。</summary>
public sealed class BoolAndVisibilityConverter : IMultiValueConverter
{
    public object Convert(
        IList<object?> values,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => values.All(v => v is true);

    public IList<object?> ConvertBack(
        IList<object?> value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => throw new NotSupportedException();
}
