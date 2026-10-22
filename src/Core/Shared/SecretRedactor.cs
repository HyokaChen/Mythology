using System.Text.RegularExpressions;

namespace Core.Shared;

/// <summary>
/// 日志与错误消息中的凭据脱敏。
///
/// 背景：LlmTornado 在请求失败时会把完整请求头拼进异常消息
/// （<c>Request: {"Authorization":["Bearer sk-..."],"api-key":["sk-..."]}</c>），
/// 这类异常若原样写入 Serilog 就会把用户 API Key 落盘到 ~/.mythology/logs 与控制台。
/// 所有面向日志/控制台的异常文本都应先经过本类脱敏。
/// </summary>
public static partial class SecretRedactor
{
    /// <summary>脱敏占位符。</summary>
    public const string Mask = "***REDACTED***";

    /// <summary>
    /// 脱敏文本中的凭据：HTTP 头形态的 Authorization/api-key 值、Bearer 令牌、sk-/pk- 字面量。
    /// </summary>
    /// <param name="text">原始文本（可为 null）。</param>
    /// <returns>脱敏后的文本；null 输入返回空字符串。</returns>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        // 无凭据特征时直接返回原串，避免热路径上三次正则替换的分配
        if (
            !text.Contains("sk-", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("earer ", StringComparison.Ordinal)
            && !text.Contains("uthorization", StringComparison.Ordinal)
            && !text.Contains("api-key", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("api_key", StringComparison.OrdinalIgnoreCase)
        )
            return text;

        var result = HeaderValueRegex().Replace(text, $"$1\"{Mask}\"");
        result = BearerRegex().Replace(result, $"Bearer {Mask}");
        result = KeyLiteralRegex().Replace(result, Mask);
        return result;
    }

    /// <summary>JSON/头部形态的凭据键值：保留键名，替换整个值（含数组形态）。</summary>
    [GeneratedRegex(
        """("(?:authorization|api[-_]?key|x-apiKey)"\s*:\s*)(?:\[[^\]]*\]|"[^"]*")""",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex HeaderValueRegex();

    /// <summary>Bearer 令牌。</summary>
    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._\-+/]{8,}={0,2}", RegexOptions.IgnoreCase)]
    private static partial Regex BearerRegex();

    /// <summary>裸露的 sk-/pk- 密钥字面量（OpenAI 兼容端点通用前缀）。</summary>
    [GeneratedRegex(@"\b(?:sk|pk)-[A-Za-z0-9_\-]{12,}")]
    private static partial Regex KeyLiteralRegex();
}
