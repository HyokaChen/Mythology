using Core.Data;
using LlmTornado;
using LlmTornado.Code;

namespace Tui.Services;

/// <summary>
/// 模型列表异步加载服务。
/// 根据 ApiKeyEntry 和 ProviderDefinition 获取可用模型列表，
/// 供 SettingsDialog 的模型下拉使用。
/// </summary>
public sealed class ModelDiscoveryService
{
    /// <summary>
    /// 根据 ApiKeyEntry 和 ProviderDefinition 异步获取可用模型列表。
    /// 任何异常返回空列表，UI 回退到手动输入。
    /// </summary>
    /// <param name="entry">API 密钥条目。</param>
    /// <param name="def">提供商定义。</param>
    /// <returns>可用模型列表。</returns>
    public static async Task<List<string>> GetModelsAsync(
        ApiKeyEntry entry,
        ProviderDefinition? def
    )
    {
        try
        {
            // 无 API Key，返回空列表
            if (string.IsNullOrEmpty(entry.ApiKey))
                return [];

            TornadoApi api;

            // 预设提供商：使用内置 provider 枚举直接查询
            if (def?.LlmProvider is { } provider) // 预设
            {
                api = new TornadoApi([new ProviderAuthentication(provider, entry.ApiKey)]);
                // 获取模型列表并按 ID 排序
                var models = await api.Models.GetModels(provider);
                return models?.Select(m => m.Id).OrderBy(x => x).ToList() ?? [];
            }
            // 自定义提供商：需要拼接 API URL 格式
            else // 自定义
            {
                var baseUrl = entry.BaseUrl ?? "";
                // 无 BaseUrl 也无法查询
                if (string.IsNullOrEmpty(baseUrl))
                    return [];

                var trimmed = baseUrl.TrimEnd('/');
                // URL 格式：已有 /v1 或 /v2 后缀时只追加模型路径，否则追加 provider+模型路径
                var format =
                    trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                    || trimmed.EndsWith("/v2", StringComparison.OrdinalIgnoreCase)
                        ? trimmed + "/{1}"
                        : trimmed + "/{0}/{1}";

                api = new TornadoApi([
                    new ProviderAuthentication(LLmProviders.Custom, entry.ApiKey),
                ])
                {
                    ApiUrlFormat = format,
                };
                var models = await api.Models.GetModels(LLmProviders.Custom);
                return models?.Select(m => m.Id).OrderBy(x => x).ToList() ?? [];
            }
        }
        catch
        {
            return []; // 任何异常返回空列表，UI 回退到手动输入
        }
    }
}
