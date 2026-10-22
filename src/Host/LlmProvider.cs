using LlmTornado;
using LlmTornado.Code;

namespace Host;

/// <summary>
/// LlmTornado API 工厂。创建配置好的 TornadoApi 实例供 Agent 使用。
/// </summary>
public static class LlmProvider
{
    /// <summary>全局 debug 模式标志。当设置为 true 时，输出详细的 LLM 请求/响应日志。</summary>
    public static bool DebugMode { get; set; }

    /// <summary>
    /// 创建 TornadoApi（单 provider）。
    /// </summary>
    public static TornadoApi CreateApi(LLmProviders provider, string apiKey)
    {
        // 创建单提供商认证的 TornadoApi 实例
        return new TornadoApi([new ProviderAuthentication(provider, apiKey)]);
    }

    /// <summary>
    /// 创建 TornadoApi（多 provider，支持自动按模型名路由）。
    /// </summary>
    public static TornadoApi CreateApi(
        List<ProviderAuthentication> authentications,
        string? baseUrl = null
    )
    {
        // 创建多提供商认证的 TornadoApi 实例
        var api = new TornadoApi(authentications);

        // 如果提供了自定义 Base URL，配置 API URL 格式
        if (string.IsNullOrEmpty(baseUrl))
            return api;

        var trimmed = baseUrl.TrimEnd('/');
        if (
            trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith("/v2", StringComparison.OrdinalIgnoreCase)
        )
        {
            // URL 已包含版本号，直接使用
            api.ApiUrlFormat = trimmed + "/{1}";
        }
        else
        {
            // URL 不包含版本号，添加版本占位符
            api.ApiUrlFormat = trimmed + "/{0}/{1}";
        }
        return api;
    }
}
