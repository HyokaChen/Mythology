using Microsoft.Extensions.Logging;

namespace Tui.Services;

/// <summary>结构化记录 TUI 的后台操作失败与诊断状态。</summary>
internal static partial class TuiLogger
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "TUI operation failed: {Operation}")]
    internal static partial void OperationFailed(
        ILogger logger,
        Exception exception,
        string operation
    );

    [LoggerMessage(Level = LogLevel.Information, Message = "TUI diagnostic: {Operation}; {Detail}")]
    internal static partial void Diagnostic(ILogger logger, string operation, string detail);
}
