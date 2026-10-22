using Core.Data;
using LlmTornado;
using LlmTornado.Code;
using Microsoft.Extensions.Configuration;

namespace Core.Infrastructure;

/// <summary>
/// LlmTornado API 工厂。统一为 TUI / Desktop 两个入口创建配置好的 TornadoApi 实例。
/// 逻辑自 Host.HostBootstrapper / Host.LlmProvider 提取，两个入口共享同一份实现。
/// </summary>
public static class LlmApiFactory
{
    /// <summary>全局 debug 模式标志。为 true 时输出详细的 LLM 请求/响应日志。</summary>
    public static bool DebugMode { get; set; }

    /// <summary>
    /// 当前活跃的 CancellationHttpHandler。在创建新 handler 时，
    /// 先取消旧 handler 的活跃请求，避免资源泄漏和竞态。
    /// </summary>
    private static CancellationHttpHandler? _currentHandler;

    /// <summary>
    /// 根据 config.yaml 中的提供商配置创建 TornadoApi。
    /// 只为当前选中的连接器创建 API，而不是把所有 key 混在一起。
    /// 同时注入 CancellationHttpHandler 以支持强制中断流式 HTTP 请求。
    /// </summary>
    /// <param name="cfg">提供商配置（ConfigStore.Providers）。</param>
    /// <returns>TornadoApi 实例、模型名称和 CancellationHttpHandler。</returns>
    public static (
        TornadoApi Api,
        string Model,
        CancellationHttpHandler Handler
    ) CreateFromProviderConfig(ProviderConfig cfg)
    {
        _currentHandler?.CancelActiveRequests();

        var handler = new CancellationHttpHandler();
        _currentHandler = handler;
        var client = new HttpClient(handler);

        EndpointBase.OnHttpClientRequested = _ => client;

        // cfg.Default 格式：预设如 "openai"，自定义如 "custom:商汤"
        var currentEntry = ProviderDefinition.IsCustom(cfg.Default)
            ? cfg.ApiKeys.FirstOrDefault(k =>
                k.Provider == "custom" && k.Name == ProviderDefinition.GetCustomName(cfg.Default)
            )
            : cfg.ApiKeys.FirstOrDefault(k => k.Provider == cfg.Default);

        var model = cfg.DefaultModel;

        if (currentEntry is null || string.IsNullOrEmpty(currentEntry.ApiKey))
            return (new TornadoApi(), model, handler);

        var def = ProviderDefinition.FindByConfigKey(currentEntry.Provider);

        TornadoApi api;
        if (def.LlmProvider is { } provider)
        {
            api = new TornadoApi([new ProviderAuthentication(provider, currentEntry.ApiKey)]);
        }
        else
        {
            var baseUrl = currentEntry.BaseUrl ?? "";
            if (string.IsNullOrEmpty(baseUrl))
            {
                var wire = IsAnthropicProtocol(currentEntry.Protocol)
                    ? LLmProviders.Anthropic
                    : LLmProviders.Custom;
                api = new TornadoApi([new ProviderAuthentication(wire, currentEntry.ApiKey)]);
            }
            else
            {
                var trimmed = baseUrl.TrimEnd('/');
                var format =
                    trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                    || trimmed.EndsWith("/v2", StringComparison.OrdinalIgnoreCase)
                        ? trimmed + "/{1}"
                        : trimmed + "/{0}/{1}";
                // 协议决定线路格式：anthropic-message 走 Anthropic 协议，其余为 OpenAI 兼容
                var wire = IsAnthropicProtocol(currentEntry.Protocol)
                    ? LLmProviders.Anthropic
                    : LLmProviders.Custom;
                api = new TornadoApi([new ProviderAuthentication(wire, currentEntry.ApiKey)])
                {
                    ApiUrlFormat = format,
                };
            }
        }

        return (api, model, handler);
    }

    /// <summary>协议是否走 Anthropic 线路格式。</summary>
    private static bool IsAnthropicProtocol(string? protocol) =>
        protocol?.Equals("anthropic-message", StringComparison.OrdinalIgnoreCase) ?? false;

    /// <summary>
    /// 根据 appsettings.json / 环境变量的扁平 LLM 配置创建 TornadoApi（启动期初始 API）。
    /// 与 TUI 的启动逻辑一致：遍历所有支持 LLM 的提供商定义读取其 API Key，
    /// 构建多提供商自动路由的 TornadoApi；无任何 Key 时返回 null。
    /// </summary>
    /// <param name="config">IConfiguration（appsettings.json + 环境变量）。</param>
    /// <param name="useDebug">是否输出调试日志。</param>
    /// <returns>TornadoApi 实例（可能为 null）和默认模型名称。</returns>
    public static (TornadoApi? Api, string Model) CreateFromAppSettings(
        IConfiguration config,
        bool useDebug
    )
    {
        var llmProvider = config["LLM:Provider"]?.ToLowerInvariant();
        var llmModel = config["LLM:Model"] ?? "gpt-4o-mini";
        var apiKeyEnv = ResolveKey(config["LLM:ApiKey"]) ?? "";

        // 遍历所有支持 LLM 的提供商定义，读取其 API Key
        var providerKeys = new Dictionary<string, string?>();
        foreach (var def in ProviderDefinition.All.Where(d => d.LlmProvider is not null))
        {
            var pascalKey = char.ToUpperInvariant(def.ConfigKey[0]) + def.ConfigKey[1..];
            var configKey = $"LLM:Providers:{pascalKey}:ApiKey";
            providerKeys[def.ConfigKey] = ResolveKey(config[configKey]);
        }

        // 旧格式兼容：LLM:ApiKey 作为全局 key，当该提供商无独立 key 时回退使用
        if (!string.IsNullOrEmpty(apiKeyEnv) && llmProvider is not null and not "mock")
        {
            if (string.IsNullOrEmpty(providerKeys.GetValueOrDefault(llmProvider)))
                providerKeys[llmProvider] = apiKeyEnv;
        }

        var authentications = new List<ProviderAuthentication>();
        foreach (var (name, key) in providerKeys)
        {
            if (string.IsNullOrEmpty(key))
                continue;
            var def = ProviderDefinition.FindByConfigKey(name);
            if (def.LlmProvider is not null)
                authentications.Add(new ProviderAuthentication(def.LlmProvider.Value, key));
        }

        TornadoApi? tornadoApi = null;

        if (authentications.Count > 0)
        {
            tornadoApi = CreateApi(authentications);
            DebugMode = useDebug;
        }
        else if (!string.IsNullOrEmpty(apiKeyEnv) && llmProvider is not null and not "mock")
        {
            var def = ProviderDefinition.FindByConfigKey(llmProvider);
            var providerEnum = def.LlmProvider ?? LLmProviders.OpenAi;
            tornadoApi = CreateApi(providerEnum, apiKeyEnv);
            DebugMode = useDebug;
        }

        return (tornadoApi, llmModel);
    }

    /// <summary>
    /// 根据 EmbeddingConfig 创建独立的 TornadoApi（embedding 专用，与聊天连接器隔离）。
    /// 无有效密钥时返回空 API，由调用方回退到聊天 API。
    /// </summary>
    public static (
        TornadoApi Api,
        string Model,
        CancellationHttpHandler Handler
    ) CreateFromEmbeddingConfig(EmbeddingConfig cfg)
    {
        _currentHandler?.CancelActiveRequests();

        var handler = new CancellationHttpHandler();
        _currentHandler = handler;
        var client = new HttpClient(handler);
        EndpointBase.OnHttpClientRequested = _ => client;

        var currentEntry = ProviderDefinition.IsCustom(cfg.Default)
            ? cfg.ApiKeys.FirstOrDefault(k =>
                k.Provider == "custom" && k.Name == ProviderDefinition.GetCustomName(cfg.Default)
            )
            : cfg.ApiKeys.FirstOrDefault(k => k.Provider == cfg.Default);

        var model = cfg.DefaultModel;

        if (currentEntry is null || string.IsNullOrEmpty(currentEntry.ApiKey))
            return (new TornadoApi(), model, handler);

        var def = ProviderDefinition.FindByConfigKey(currentEntry.Provider);

        TornadoApi api;
        if (def.LlmProvider is { } provider)
        {
            api = new TornadoApi([new ProviderAuthentication(provider, currentEntry.ApiKey)]);
        }
        else
        {
            var baseUrl = currentEntry.BaseUrl ?? "";
            if (string.IsNullOrEmpty(baseUrl))
            {
                var wire = IsAnthropicProtocol(currentEntry.Protocol)
                    ? LLmProviders.Anthropic
                    : LLmProviders.Custom;
                api = new TornadoApi([new ProviderAuthentication(wire, currentEntry.ApiKey)]);
            }
            else
            {
                var trimmed = baseUrl.TrimEnd('/');
                var format =
                    trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                    || trimmed.EndsWith("/v2", StringComparison.OrdinalIgnoreCase)
                        ? trimmed + "/{1}"
                        : trimmed + "/{0}/{1}";
                // 协议决定线路格式：anthropic-message 走 Anthropic 协议，其余为 OpenAI 兼容
                var wire = IsAnthropicProtocol(currentEntry.Protocol)
                    ? LLmProviders.Anthropic
                    : LLmProviders.Custom;
                api = new TornadoApi([new ProviderAuthentication(wire, currentEntry.ApiKey)])
                {
                    ApiUrlFormat = format,
                };
            }
        }

        return (api, model, handler);
    }

    /// <summary>
    /// 创建 TornadoApi（单 provider）。
    /// </summary>
    public static TornadoApi CreateApi(LLmProviders provider, string apiKey)
    {
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
        var api = new TornadoApi(authentications);
        if (string.IsNullOrEmpty(baseUrl))
            return api;

        var trimmed = baseUrl.TrimEnd('/');
        api.ApiUrlFormat =
            trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith("/v2", StringComparison.OrdinalIgnoreCase)
                ? trimmed + "/{1}"
                : trimmed + "/{0}/{1}";
        return api;
    }

    /// <summary>
    /// 解析配置键，支持环境变量引用（格式：${ENV_NAME}）。
    /// </summary>
    private static string? ResolveKey(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        if (value.StartsWith("${") && value.EndsWith('}'))
        {
            var envName = value[2..^1];
            return Environment.GetEnvironmentVariable(envName);
        }
        return value;
    }
}
