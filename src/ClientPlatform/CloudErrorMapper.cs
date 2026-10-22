using System.Net;
using System.Text.Json;
using Core.Cloud;

namespace ClientPlatform;

/// <summary>
/// 云端 API 错误 — 携带稳定错误码（与 Cloud API ProblemDetails 的 code 字段对齐）。
/// </summary>
public sealed class CloudApiException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// 云端错误映射 — 把 HTTP 状态码/错误码映射为客户端可用的连接状态与异常。
/// 任何云端失败都必须可识别、可展示，且不影响本地创作。
/// </summary>
public static class CloudErrorMapper
{
    public static CloudApiException Map(HttpStatusCode status, string body) =>
        new(ExtractCode(status, body), ExtractMessage(status, body));

    /// <summary>
    /// 从错误码推断客户端连接状态（用于 UI 展示，不作为授权依据）。
    /// 必须与服务端 CloudErrors.cs 的实际 code 对齐（usage_limit_exceeded / rate_limit_exceeded），
    /// 否则额度/限流会被误判为「云端不可用」。
    /// </summary>
    public static CloudConnectionState ToConnectionState(string code) =>
        code switch
        {
            "quota_exceeded" or "insufficient_credits" or "usage_limit_exceeded" =>
                CloudConnectionState.QuotaExceeded,
            "rate_limited" or "too_many_requests" or "rate_limit_exceeded" =>
                CloudConnectionState.RateLimited,
            "invalid_api_key"
            or "invalid_credentials"
            or "unauthorized"
            or "forbidden"
            or "invalid_grant"
            or "expired_code"
            or "unauthorized_client" => CloudConnectionState.Authenticating,
            _ => CloudConnectionState.Unavailable,
        };

    /// <summary>
    /// 服务端业务拒绝码（4xx 且 detail 是具体修复指引）— 与连接/凭据/额度类故障区分。
    /// 这类错误应直接呈现服务端 detail，避免误报「云端不可用」（例如
    /// 422 invalid_analysis_input：深度超出当前套餐）。
    /// </summary>
    public static bool IsBusinessRejection(string code) =>
        code
            is "invalid_analysis_input"
                or "capability_not_entitled"
                or "payload_too_large"
                or "contract_version_conflict"
                or "invalid_request"
                or "bad_request"
                or "not_found"
                or "conflict";

    /// <summary>
    /// 权限/凭证类拒绝 —— **会话级粘滞降级**（深度诊断长期停用）的判据。
    /// 只含「重试无意义且需用户动作」的原因：鉴权/权限被拒。
    /// 其余业务拒绝（invalid_analysis_input / payload_too_large / contract_version_conflict /
    /// not_found / conflict / invalid_request / bad_request / capability_not_entitled）属**一次性**失败
    /// （多半是请求或契约/配置问题）——不应因此把整个会话的深度诊断关掉。
    ///
    /// 服务端能力/额度口径调整后，<c>capability_not_entitled</c> 仅作为历史兼容分支；
    /// 若旧服务端返回该码，按一次性业务拒绝处理（展示 detail），不粘滞整个会话。
    /// 额度类失败走 402 <c>usage_limit_exceeded</c>（连接状态 QuotaExceeded，一次性降级提示）。
    /// </summary>
    public static bool IsEntitlementDenial(string code) =>
        code is "unauthorized" or "forbidden" or "invalid_api_key";

    private static string ExtractCode(HttpStatusCode status, string body)
    {
        // Mythology 错误体统一带 code；OAuth 端点（RFC 6749）以 error 字段承载同一稳定码。
        var embedded = FindProblemField(body, "code") ?? FindProblemField(body, "error");
        if (!string.IsNullOrWhiteSpace(embedded))
            return embedded;

        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "unauthorized",
            HttpStatusCode.TooManyRequests => "rate_limited",
            HttpStatusCode.ServiceUnavailable => "service_unavailable",
            HttpStatusCode.PaymentRequired => "quota_exceeded",
            HttpStatusCode.BadRequest => "bad_request",
            HttpStatusCode.NotFound => "not_found",
            _ => "cloud_error",
        };
    }

    private static string ExtractMessage(HttpStatusCode status, string body)
    {
        // 服务端 CloudProblems 把 title 设为错误码、detail 设为可读描述；
        // OAuth 错误体（RFC 6749）的可读描述在 error_description。按此优先级取第一个非空。
        var embedded =
            FindProblemField(body, "detail")
            ?? FindProblemField(body, "error_description")
            ?? FindProblemField(body, "title");
        return !string.IsNullOrWhiteSpace(embedded)
            ? embedded
            : $"Cloud API returned {(int)status}.";
    }

    private static string? FindProblemField(string body, string field)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (
                root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(field, out var property)
                && property.ValueKind == JsonValueKind.String
            )
            {
                return property.GetString();
            }
        }
        catch (JsonException)
        {
            // Non-JSON error bodies use the status-code fallback above.
        }

        return null;
    }
}
