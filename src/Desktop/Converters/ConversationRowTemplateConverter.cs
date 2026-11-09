using System.Globalization;
using Avalonia.Controls.Templates;
using Avalonia.Data.Converters;
using Desktop.Models;

namespace Desktop.Converters;

/// <summary>ConversationRowKind → 行模板。避免在同一个模板里创建所有隐藏分支。</summary>
public sealed class ConversationRowTemplateConverter : IValueConverter
{
    public IDataTemplate? UserTemplate { get; set; }
    public IDataTemplate? SystemTemplate { get; set; }
    public IDataTemplate? WaitingTemplate { get; set; }
    public IDataTemplate? StepTemplate { get; set; }
    public IDataTemplate? ContentTemplate { get; set; }
    public IDataTemplate? InterruptedTemplate { get; set; }
    public IDataTemplate? SourcesTemplate { get; set; }

    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) =>
        value is ConversationRowKind kind
            ? kind switch
            {
                ConversationRowKind.User => UserTemplate,
                ConversationRowKind.System => SystemTemplate,
                ConversationRowKind.Waiting => WaitingTemplate,
                ConversationRowKind.Step => StepTemplate,
                ConversationRowKind.Content => ContentTemplate,
                ConversationRowKind.Interrupted => InterruptedTemplate,
                ConversationRowKind.Sources => SourcesTemplate,
                _ => null,
            }
            : null;

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => throw new NotSupportedException();
}
