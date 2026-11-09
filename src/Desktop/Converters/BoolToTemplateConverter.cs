using System.Globalization;
using Avalonia.Controls.Templates;
using Avalonia.Data.Converters;

namespace Desktop.Converters;

/// <summary>bool → DataTemplate：用于在「轻量纯文本」与「LiveMarkdown」之间切换正文模板。</summary>
public sealed class BoolToTemplateConverter : IValueConverter
{
    public IDataTemplate? TrueTemplate { get; set; }

    public IDataTemplate? FalseTemplate { get; set; }

    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => value is true ? TrueTemplate : FalseTemplate;

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => throw new NotSupportedException();
}
