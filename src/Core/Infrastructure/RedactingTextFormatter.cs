using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace Core.Infrastructure;

/// <summary>
/// Serilog 输出脱敏包装：先按 outputTemplate 渲染日志事件，再对渲染结果做凭据脱敏后写出。
///
/// 必要性：SDK 异常（如 LlmTornado 的 HttpRequestException）把完整请求头写进 Message，
/// <c>{Exception}</c> 渲染后即包含 <c>Authorization: Bearer sk-...</c>。
/// 在 formatter 层统一拦截，避免逐个 LogWarning(ex, ...) 调用点漏掉。
/// </summary>
/// <param name="outputTemplate">Serilog 输出模板（与非脱敏配置保持一致的格式）。</param>
public sealed class RedactingTextFormatter(string outputTemplate) : ITextFormatter
{
    private readonly MessageTemplateTextFormatter _inner = new(outputTemplate);

    /// <summary>渲染并脱敏后写入目标。</summary>
    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(output);

        var buffer = new StringWriter();
        _inner.Format(logEvent, buffer);
        output.Write(Shared.SecretRedactor.Redact(buffer.ToString()));
    }
}
