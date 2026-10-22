namespace Core.Infrastructure;

/// <summary>
/// LLM 调用错误提取工具。
/// 从 LLM API 抛出的异常中提取结构化错误信息（HTTP 状态码、原始消息、context overflow 检测）。
///
/// 消除 ConversationLoop 和 TuiApp 中的重复实现。
/// </summary>
public static class LlmErrorHelper
{
    /// <summary>
    /// Context overflow 检测关键词集合。
    /// 上下文相关词：必须出现在错误消息中，表明问题与上下文/prompt 相关。
    /// </summary>
    private static readonly string[] ContextKeywords = ["context", "prompt"];

    /// <summary>
    /// 溢出相关词：必须同时出现上下文关键词，表明是长度/容量超限。
    /// 包含各种时态和拼写变体（exceed/exceeds/exceeded）。
    /// </summary>
    private static readonly string[] OverflowKeywords =
    [
        "length",
        "window",
        "exceed",
        "too long",
        "too many",
    ];

    /// <summary>
    /// 独立匹配模式：无需上下文关键词配合，单独出现即可判定为 context overflow。
    /// 收录各 LLM 提供商返回的典型错误码/短语。
    /// </summary>
    private static readonly string[] OverflowPatterns =
    [
        "context_length_exceeded",
        "maximum context",
        "prompt is too long",
        "too many tokens",
        "exceed_context_size",
        "context_size_exceeded",
        "context_window_exceeded",
    ];

    /// <summary>
    /// 从 LLM 异常中提取结构化错误信息。
    /// 不做分类翻译，只提取 HTTP 状态码和原始消息。
    /// 仅检测 context_overflow（唯一需要自动恢复的错误类型）。
    /// </summary>
    /// <param name="ex">LLM API 调用抛出的异常。</param>
    /// <returns>(HTTP状态码, 原始错误消息, 是否为 context overflow)</returns>
    public static (int? httpStatusCode, string rawMessage, bool isContextOverflow) Extract(
        Exception ex
    )
    {
        // 提取 HTTP 状态码（.NET 6+ HttpRequestException.StatusCode）
        int? statusCode = null;
        if (ex is HttpRequestException hre)
            statusCode = (int?)hre.StatusCode;

        // 原始错误消息（不翻译，直接透传 LLM API 返回的内容）
        var rawMessage = ex.InnerException?.Message ?? ex.Message;

        // 仅检测 context_overflow（唯一需要自动恢复的错误类型）
        var isContextOverflow = statusCode == 400 && DetectContextOverflow(rawMessage);

        return (statusCode, rawMessage, isContextOverflow);
    }

    /// <summary>
    /// 检测错误消息是否表明上下文溢出。
    /// 匹配策略：双关键词组合（上下文词 + 溢出词）或独立模式匹配。
    /// </summary>
    /// <param name="message">原始错误消息。</param>
    /// <returns>是否为上下文溢出错误。</returns>
    private static bool DetectContextOverflow(string message)
    {
        var lower = message.ToLowerInvariant();

        // 独立模式匹配：各提供商的典型错误码/短语
        if (OverflowPatterns.Any(lower.Contains))
            return true;

        // 组合匹配：上下文关键词 + 溢出关键词同时出现
        var hasContextWord = ContextKeywords.Any(lower.Contains);
        var hasOverflowWord = OverflowKeywords.Any(lower.Contains);

        return hasContextWord && hasOverflowWord;
    }
}
