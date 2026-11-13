using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data.Converters;
using Desktop.Models;

namespace Desktop.Converters;

/// <summary>ConversationRowKind / StepKind → 行模板。避免在同一个模板里创建所有隐藏分支。</summary>
public sealed class ConversationRowTemplateConverter : IValueConverter, IDataTemplate
{
    public IDataTemplate? UserTemplate { get; set; }
    public IDataTemplate? SystemTemplate { get; set; }
    public IDataTemplate? WaitingTemplate { get; set; }
    public IDataTemplate? StepTemplate { get; set; }
    public IDataTemplate? StepContentTemplate { get; set; }
    public IDataTemplate? StepNoticeTemplate { get; set; }
    public IDataTemplate? StepSystemEventTemplate { get; set; }
    public IDataTemplate? ContentTemplate { get; set; }
    public IDataTemplate? InterruptedTemplate { get; set; }
    public IDataTemplate? SourcesTemplate { get; set; }
    public IDataTemplate? ProcessTemplate { get; set; }
    public IDataTemplate? ActionsTemplate { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            ConversationRow row => TemplateFor(row),
            ConversationRowKind kind => ForKind(kind),
            _ => null,
        };
    }

    public Control? Build(object? param) =>
        param is ConversationRow row ? TemplateFor(row)?.Build(param) : null;

    public bool Match(object? data) => data is ConversationRow;

    private IDataTemplate? TemplateFor(ConversationRow row) =>
        row.Kind == ConversationRowKind.Step && row.Step is { } step
            ? step.Kind switch
            {
                StepKind.Content => StepContentTemplate ?? StepTemplate,
                StepKind.Notice => StepNoticeTemplate ?? StepTemplate,
                StepKind.SystemEvent => StepSystemEventTemplate ?? StepTemplate,
                _ => StepTemplate,
            }
            : ForKind(row.Kind);

    private IDataTemplate? ForKind(ConversationRowKind kind) =>
        kind switch
        {
            ConversationRowKind.User => UserTemplate,
            ConversationRowKind.System => SystemTemplate,
            ConversationRowKind.Waiting => WaitingTemplate,
            ConversationRowKind.Step => StepTemplate,
            ConversationRowKind.Content => ContentTemplate,
            ConversationRowKind.Interrupted => InterruptedTemplate,
            ConversationRowKind.Sources => SourcesTemplate,
            ConversationRowKind.Process => ProcessTemplate,
            ConversationRowKind.Actions => ActionsTemplate,
            _ => null,
        };

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => throw new NotSupportedException();
}
