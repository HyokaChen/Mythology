using Core.Tools;
using LlmTornado.Chat;

namespace Core.Agent;

/// <summary>
/// Agent 状态记录，用于快照和持久化。
/// 包含系统提示词、模型配置、工具列表、消息历史和运行状态。
/// </summary>
public sealed record AgentState
{
    /// <summary>系统提示词（UserPrompt + NovelContext 的合并结果）。</summary>
    public string SystemPrompt { get; init; } = "";

    /// <summary>用户自定义提示词（来自配置文件，不变）。</summary>
    public string UserPrompt { get; init; } = "";

    /// <summary>小说上下文（每次发消息前注入，可变）。</summary>
    public string NovelContext { get; init; } = "";

    /// <summary>模型名称。</summary>
    public string ModelName { get; init; } = "";

    /// <summary>思考深度级别（low/medium/high）。</summary>
    public string ThinkingLevel { get; init; } = "medium";

    /// <summary>当前激活的工具列表。</summary>
    public IReadOnlyList<AgentTool> Tools { get; init; } = [];

    /// <summary>消息历史。</summary>
    public IReadOnlyList<ChatMessage> Messages { get; init; } = [];

    /// <summary>是否正在流式输出。</summary>
    public bool IsStreaming { get; init; }

    /// <summary>当前流式消息（如果有）。</summary>
    public ChatMessage? StreamMessage { get; init; }

    /// <summary>待处理的工具调用 ID 集合。</summary>
    public IReadOnlySet<string> PendingToolCalls { get; init; } = new HashSet<string>();

    /// <summary>错误信息（如果有）。</summary>
    public string? Error { get; init; }
}
