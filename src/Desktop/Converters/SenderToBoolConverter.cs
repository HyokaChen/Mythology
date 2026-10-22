using System.Globalization;
using Avalonia.Data.Converters;
using Desktop.Models;

namespace Desktop.Converters;

/// <summary>消息发送者 → bool（ConverterParameter: "User"/"Agent"/"System"）。</summary>
public sealed class SenderToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not MessageSender sender || parameter is not string expected)
            return false;
        return sender.ToString().Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => throw new NotSupportedException();
}
