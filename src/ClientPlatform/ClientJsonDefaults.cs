using System.Text.Json;

namespace ClientPlatform;

/// <summary>
/// 客户端 Cloud wire 序列化选项 — 与云端 backend/Mythology.Json/JsonDefaults 对齐：
/// kebab-case 属性名 + 属性大小写不敏感（兼容读取）。
/// 仅用于 Cloud API 的 HTTP wire；本地持久化（CloudSessionStore 等）与
/// 标准协议（MCP JSON-RPC、OAuth RFC 8414）字段名由规范固定，不走此选项。
/// </summary>
public static class ClientJsonDefaults
{
    /// <summary>Cloud wire 选项：kebab-case 出站 + 大小写不敏感入站。</summary>
    public static readonly JsonSerializerOptions Cloud = new(
        System.Text.Json.JsonSerializerDefaults.Web
    )
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.KebabCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// 请求体出站选项：服务端以 <c>JsonDocument</c> 手工读取 camelCase 字面量键的端点专用
    /// （<c>onlyMainContent</c> / <c>maxDepth</c> / <c>captureWindowMs</c> / <c>texts</c> 等
    /// 多词键必须 camelCase——这些端点不走 MVC 模型绑定，kebab 化会被静默读成默认值且无报错）。
    /// 与 <see cref="Cloud"/>（kebab，供模型绑定端点）分属两条 wire 约定，不可互换。
    /// </summary>
    public static readonly JsonSerializerOptions CamelCaseRequest = new(
        System.Text.Json.JsonSerializerDefaults.Web
    )
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
    };
}
