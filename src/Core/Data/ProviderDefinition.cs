using LlmTornado.Code;

namespace Core.Data;

/// <summary>
/// 结构化的 LLM Provider 定义，替代 KnownProviders 字符串数组。
/// 每个 Provider 有唯一的 ConfigKey（持久化键名）、对应的 LlmTornado 枚举值、
/// 是否内置 BaseUrl、以及默认模型提示。
/// </summary>
public sealed class ProviderDefinition
{
    public string DisplayName { get; init; } = ""; // UI 显示名
    public string ConfigKey { get; init; } = ""; // 持久化键名
    public LLmProviders? LlmProvider { get; init; } // 对应的 LlmTornado 枚举值 (null = 自定义)
    public bool HasBuiltInUrl { get; init; } // LlmTornado 是否已内置 BaseUrl
    public string? DefaultModelHint { get; init; } // 默认模型名提示

    /// <summary>自定义 Provider 定义。</summary>
    public static readonly ProviderDefinition Custom = new()
    {
        DisplayName = "自定义",
        ConfigKey = "custom",
        LlmProvider = null,
        HasBuiltInUrl = false,
        DefaultModelHint = null,
    };

    /// <summary>所有预设 Provider 定义（不含自定义）。</summary>
    public static readonly ProviderDefinition[] All =
    [
        new()
        {
            DisplayName = "OpenAI",
            ConfigKey = "openai",
            LlmProvider = LLmProviders.OpenAi,
            HasBuiltInUrl = true,
            DefaultModelHint = "gpt-4o-mini",
        },
        new()
        {
            DisplayName = "Claude",
            ConfigKey = "anthropic",
            LlmProvider = LLmProviders.Anthropic,
            HasBuiltInUrl = true,
            DefaultModelHint = "claude-sonnet-4-20250514",
        },
        new()
        {
            DisplayName = "DeepSeek",
            ConfigKey = "deepseek",
            LlmProvider = LLmProviders.DeepSeek,
            HasBuiltInUrl = true,
            DefaultModelHint = "deepseek-chat",
        },
        new()
        {
            DisplayName = "xAI",
            ConfigKey = "xai",
            LlmProvider = LLmProviders.XAi,
            HasBuiltInUrl = true,
            DefaultModelHint = "grok-3-mini",
        },
        new()
        {
            DisplayName = "Google",
            ConfigKey = "google",
            LlmProvider = LLmProviders.Google,
            HasBuiltInUrl = true,
            DefaultModelHint = "gemini-2.5-flash",
        },
        new()
        {
            DisplayName = "Groq",
            ConfigKey = "groq",
            LlmProvider = LLmProviders.Groq,
            HasBuiltInUrl = true,
            DefaultModelHint = "llama-3.3-70b-versatile",
        },
        new()
        {
            DisplayName = "Mistral",
            ConfigKey = "mistral",
            LlmProvider = LLmProviders.Mistral,
            HasBuiltInUrl = true,
            DefaultModelHint = "mistral-large-latest",
        },
        new()
        {
            DisplayName = "OpenRouter",
            ConfigKey = "openrouter",
            LlmProvider = LLmProviders.OpenRouter,
            HasBuiltInUrl = true,
            DefaultModelHint = "openai/gpt-4o-mini",
        },
        new()
        {
            DisplayName = "Kimi",
            ConfigKey = "moonshot",
            LlmProvider = LLmProviders.MoonshotAi,
            HasBuiltInUrl = true,
            DefaultModelHint = "moonshot-v1-auto",
        },
        new()
        {
            DisplayName = "阿里百炼",
            ConfigKey = "alibaba",
            LlmProvider = LLmProviders.Alibaba,
            HasBuiltInUrl = true,
            DefaultModelHint = "qwen-turbo",
        },
    ];

    /// <summary>按 ConfigKey 查找预设定义，找不到则返回 Custom。</summary>
    public static ProviderDefinition FindByConfigKey(string configKey)
    {
        if (string.IsNullOrEmpty(configKey) || IsCustom(configKey))
            return Custom;

        return All.FirstOrDefault(d => d.ConfigKey == configKey) ?? Custom;
    }

    /// <summary>按 DisplayName 查找预设定义，找不到则返回 Custom。</summary>
    public static ProviderDefinition FindByDisplayName(string displayName)
    {
        if (string.IsNullOrEmpty(displayName) || displayName == "自定义")
            return Custom;

        return All.FirstOrDefault(d => d.DisplayName == displayName) ?? Custom;
    }

    /// <summary>判断是否为自定义 provider（支持 "custom" 和 "custom:名称" 格式）。</summary>
    public static bool IsCustom(string configKey) =>
        configKey == "custom" || (configKey?.StartsWith("custom:") ?? false);

    /// <summary>从复合键中提取自定义名称，如 "custom:商汤" → "商汤"。</summary>
    public static string? GetCustomName(string configKey)
    {
        if (string.IsNullOrEmpty(configKey))
            return null;
        return configKey.StartsWith("custom:") ? configKey["custom:".Length..] : null;
    }
}
