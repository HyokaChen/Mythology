using LlmTornado.Chat;

namespace Core.Agent;

/// <summary>流式内容类型枚举。</summary>
public enum StreamContentType
{
    /// <summary>思考/推理内容（ReasoningTokenHandler 回调产生）。</summary>
    Thinking,

    /// <summary>思考结束，文本内容即将开始。</summary>
    ThinkingEnd,

    /// <summary>文本内容（MessageTokenHandler 回调产生）。</summary>
    Text,
}

/// <summary>Agent 生命周期事件基类，所有具体事件都继承自此。</summary>
public abstract record AgentEvent
{
    /// <summary>事件时间戳。</summary>
    public long Timestamp { get; init; }
}

/// <summary>Agent 开始运行事件。</summary>
public sealed record AgentStart : AgentEvent;

/// <summary>Agent 运行结束事件，携带本次运行产生的所有消息。</summary>
public sealed record AgentEnd(ChatMessage[] Messages) : AgentEvent;

/// <summary>
/// 用户中断事件。由双击 ESC 触发，ConversationLoop 在取消路径上发射。
/// </summary>
public sealed record AgentInterrupted : AgentEvent;

/// <summary>单次 LLM 调用开始事件。</summary>
public sealed record TurnStart : AgentEvent;

/// <summary>单次 LLM 调用结束事件，携带助手消息和工具结果。</summary>
public sealed record TurnEnd(ChatMessage Message, IReadOnlyList<ChatMessage>? ToolResults = null)
    : AgentEvent;

/// <summary>
/// Steer 消息转正事件。在 turn 边界消费 SteeringQueue 时发射，
/// 先于 MessageStart，让 TUI 删除对应的 ⏳ waiting 占位气泡。
/// SteerId 为空字符串时表示非用户 steer（Hook/CRUD 等），TUI 应跳过 waiting 删除。
/// </summary>
public sealed record SteerPromoted(string SteerId, ChatMessage Message) : AgentEvent;

/// <summary>消息开始事件，标记一条新消息的开始。</summary>
public sealed record MessageStart(ChatMessage Message) : AgentEvent;

/// <summary>
/// 流式内容更新事件。直接从 handler 的原始字符串构建，无需创建 ChatMessage。
/// </summary>
public sealed record StreamChunk(
    string Delta,
    string AccumulatedText,
    StreamContentType ContentType
) : AgentEvent;

/// <summary>消息结束事件。</summary>
public sealed record MessageEnd(ChatMessage Message) : AgentEvent;

/// <summary>工具执行开始事件。</summary>
public sealed record ToolExecutionStart(string ToolCallId, string ToolName, string Arguments)
    : AgentEvent;

/// <summary>工具执行进度更新事件。</summary>
public sealed record ToolExecutionUpdate(string ToolCallId, string? PartialResult) : AgentEvent;

/// <summary>工具执行结束事件，携带执行结果和错误状态。</summary>
public sealed record ToolExecutionEnd(
    string ToolCallId,
    bool IsError,
    string? Result,
    object? Details = null
) : AgentEvent;

/// <summary>断路器触发事件，防止重复调用同一工具。</summary>
public sealed record CircuitBreakerTriggered(
    string ToolName,
    string Arguments,
    int RepeatCount,
    string Reason
) : AgentEvent;

/// <summary>
/// LLM 调用错误事件。携带原始错误消息和 HTTP 状态码，不翻译不分类。
/// 仅 IsContextOverflow 需要识别（触发 auto-compact 自动恢复）。
/// </summary>
public sealed record AgentError(
    string Message,
    int? HttpStatusCode,
    bool IsContextOverflow,
    bool Recoverable
) : AgentEvent;

/// <summary>
/// Token 用量事件，由 ConversationLoop 从 LLM 响应的实际 Usage 数据发出。
/// </summary>
public sealed record TokenUsageEvent(int PromptTokens, int CompletionTokens) : AgentEvent
{
    /// <summary>总 Token 数（提示 + 完成）。</summary>
    public int TotalTokens => PromptTokens + CompletionTokens;
}
