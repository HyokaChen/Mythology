using LlmTornado.Chat.Models;

namespace Core.Shared;

/// <summary>
/// 模型能力解析器 — 从 LlmTornado 的 ChatModel 元数据中提取上下文窗口大小
/// 和 adaptive thinking 能力，用于构建 ModelContextProfile。
///
/// 解决 ConfigStore.ContextWindowSize（用户手动选择）与
/// ChatModel.ContextTokens（模型声明）之间的割裂：
/// 当用户选择的 ContextWindowSize 与模型声明不一致时，
/// 自动同步为模型声明的值（用户仍可在 TUI 手动覆盖）。
/// </summary>
public static class ModelCapabilityResolver
{
    /// <summary>
    /// 从 LlmTornado 的模型注册表中查找模型的上下文窗口大小。
    /// 如果模型未注册，返回 null（调用方应使用用户配置的值作为兜底）。
    /// </summary>
    /// <param name="modelName">模型名称（如 "claude-opus-4-8"）。</param>
    /// <returns>模型声明的上下文窗口大小（tokens），或 null。</returns>
    public static int? ResolveContextTokens(string? modelName)
    {
        if (string.IsNullOrEmpty(modelName))
            return null;

        // 从 LlmTornado 的全局模型注册表查找
        // IModel 上没有 ContextTokens，需要转换为 ChatModel 才能访问
        if (ChatModel.AllModelsMap.TryGetValue(modelName, out var model))
        {
            if (model is ChatModel { ContextTokens: > 0 } chatModel)
                return chatModel.ContextTokens;
        }

        return null;
    }

    /// <summary>
    /// 检测模型是否为 adaptive-only(仅自适应)-only thinking 模型。
    /// 这些模型（Claude Opus 4.7+, Claude 5）不接受 manual budget_tokens，
    /// thinking 深度由模型自主决定。
    /// </summary>
    /// <param name="modelName">模型名称。</param>
    /// <returns>是否为 adaptive-only thinking 模型。</returns>
    public static bool IsAdaptiveThinkingModel(string? modelName)
    {
        return !string.IsNullOrEmpty(modelName)
            && ChatModelAnthropicHelper.IsAdaptiveOnlyThinkingModel(modelName);
    }

    /// <summary>
    /// 解析模型的有效上下文窗口大小。
    /// 优先使用模型声明（ChatModel.ContextTokens），兜底使用用户配置的值。
    /// </summary>
    /// <param name="modelName">模型名称。</param>
    /// <param name="userConfiguredSize">用户在 TUI 中选择的上下文窗口大小。</param>
    /// <returns>有效的上下文窗口大小。</returns>
    public static int ResolveEffectiveContextSize(string? modelName, int userConfiguredSize)
    {
        var declared = ResolveContextTokens(modelName);
        return declared ?? userConfiguredSize;
    }
}
