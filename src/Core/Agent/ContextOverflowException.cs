namespace Core.Agent;

/// <summary>
/// 上下文溢出异常。当 LLM API 返回 context length / token limit 错误时，
/// ConversationLoop 将其包装为此异常抛出，供 TuiApp 触发 auto-compact + retry。
/// </summary>
public class ContextOverflowException : Exception
{
    /// <summary>HTTP 状态码（通常为 400）。</summary>
    public int? HttpStatusCode { get; }

    public ContextOverflowException(string message, int? httpStatusCode = null)
        : base(message)
    {
        HttpStatusCode = httpStatusCode;
    }

    public ContextOverflowException(
        string message,
        Exception innerException,
        int? httpStatusCode = null
    )
        : base(message, innerException)
    {
        HttpStatusCode = httpStatusCode;
    }
}
