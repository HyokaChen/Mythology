using System.Text.Json;

namespace ClientPlatform;

/// <summary>
/// §13 Tier 2 云端契约（docs/DESKTOP-LOGIN-FLOW.md §13.5）：
/// 登录响应（POST /v1/oauth/token、POST /v1/device/token、SSE /v1/device/token/stream）与
/// GET /v1/capabilities 可选携带服务端过期时间 `expires-at`（ISO-8601 UTC 字符串）。
///
/// 字段为**可选、向后兼容**：旧服务端不返回时返回 null，客户端回退滑动续期（Tier 1 行为不变）；
/// 返回时客户端优先采用服务端真值，服务端成为过期/撤销的唯一权威。
/// </summary>
public static class CloudContract
{
    /// <summary>
    /// 客户端实现的契约版本（能力语义基线，与 Cloud.Api CapabilityCatalog.ContractVersion 对齐）。
    /// 探活时与服务端 /v1/capabilities 的 contract-version 比对，不一致只告警不改行为。
    /// </summary>
    public const string SupportedContractVersion = "1.0";

    /// <summary>
    /// 服务端 contract-version 是否与客户端支持基线一致。
    /// 未知（null，旧服务端未携带）视为容忍——比对仅用于诊断告警，不作为拒绝依据。
    /// </summary>
    public static bool IsContractVersionSupported(string? serverVersion) =>
        serverVersion is null || serverVersion == SupportedContractVersion;

    /// <summary>
    /// 从 /v1/capabilities 响应体解析 contract-version。缺失 / 非 JSON / 非字符串 → null（不破坏调用方）。
    /// </summary>
    public static string? TryParseContractVersion(string jsonBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (!root.TryGetProperty("contract-version", out var el))
                return null;
            return
                el.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(el.GetString())
                ? el.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从响应体解析可选的服务端 expiresAt。无该字段 / 非 JSON / 解析失败 → null（回退滑动续期）。
    /// 畸形字段不抛异常、不破坏调用方（旧服务端或非 2xx 兜底响应体均可容忍）。
    /// </summary>
    public static DateTimeOffset? TryParseExpiresAt(string jsonBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (!root.TryGetProperty("expires-at", out var el))
                return null;
            if (
                el.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(el.GetString(), out var parsed)
            )
                return parsed;
            return null;
        }
        catch
        {
            return null;
        }
    }
}
