using System.Collections.Concurrent;
using Core.Agent;
using LlmTornado.Code;
using XenoAtom.Terminal.UI.Threading;

namespace Tui.Services;

/// <summary>
/// Agent 事件适配器。
/// 将 Core 层的 AgentEvent 转换为 UI 层的事件，使用队列确保线程安全。
/// </summary>
public sealed class AgentEventAdapter
{
    /// <summary>UI 操作队列，用于在 UI 线程上执行回调。</summary>
    private readonly ConcurrentQueue<Action> _uiActions = new();

    /// <summary>
    /// 处理 Agent 事件，转换为 UI 回调并加入队列。
    /// </summary>
    /// <param name="evt">Agent 事件。</param>
    public void OnAgentEvent(AgentEvent evt)
    {
        switch (evt)
        {
            // Steer 消息转正：占位提示被提升为正式消息
            case SteerPromoted sp:
                Enqueue(() => OnSteerPromoted?.Invoke(sp.SteerId, sp.Message.Content ?? ""));
                break;

            // 消息开始：区分用户消息和助手消息
            case MessageStart ms:
                switch (ms.Message.Role)
                {
                    case ChatMessageRoles.User:
                    {
                        var text = ms.Message.Content ?? "";
                        Enqueue(() => OnUserMessage?.Invoke(text));
                        break;
                    }
                    case ChatMessageRoles.Assistant:
                        Enqueue(() => OnAssistantStart?.Invoke());
                        break;
                }
                break;

            // 流式分块：思考内容 / 思考结束 / 文本增量
            case StreamChunk chunk:
                switch (chunk.ContentType)
                {
                    case StreamContentType.Thinking:
                        Enqueue(() => OnThinkingUpdate?.Invoke(chunk.AccumulatedText));
                        break;
                    case StreamContentType.ThinkingEnd:
                        Enqueue(() => OnThinkingEnd?.Invoke());
                        break;
                    case StreamContentType.Text:
                        Enqueue(() => OnAssistantDelta?.Invoke(chunk.Delta));
                        break;
                }
                break;

            // 消息结束：助手消息完成，有工具调用时清空文本
            case MessageEnd me:
                if (me.Message.Role == ChatMessageRoles.Assistant)
                {
                    // 有 ToolCalls 时，Content 可能是原始 API 对象（如 <param> 标签），
                    // 不能作为 assistant 文本传给 ConversationPanel
                    var text = me.Message.ToolCalls?.Count > 0 ? "" : me.Message.Content ?? "";
                    Enqueue(() => OnAssistantEnd?.Invoke(text));
                }
                break;

            // 工具执行开始
            case ToolExecutionStart tes:
                Enqueue(() => OnToolStart?.Invoke(tes.ToolCallId, tes.ToolName, tes.Arguments));
                break;

            // 工具执行结束
            case ToolExecutionEnd tee:
                Enqueue(() =>
                    OnToolEnd?.Invoke(
                        tee.ToolCallId,
                        "",
                        tee.IsError,
                        tee.Result ?? "",
                        tee.Details
                    )
                );
                break;

            // Agent 开始运行
            case AgentStart:
                Enqueue(() => OnAgentStart?.Invoke());
                break;

            // Agent 运行结束
            case AgentEnd:
                Enqueue(() => OnAgentEnd?.Invoke());
                break;
            // 用户中断 Agent
            case AgentInterrupted:
                Enqueue(() => OnInterrupted?.Invoke());
                break;
            // LLM 调用错误
            case AgentError ae:
                Enqueue(() =>
                    OnError?.Invoke(
                        ae.Message,
                        ae.HttpStatusCode,
                        ae.IsContextOverflow,
                        ae.Recoverable
                    )
                );
                break;
            // 断路器触发：工具调用频率过高被熔断
            case CircuitBreakerTriggered cbt:
                Enqueue(() => OnCircuitBreaker?.Invoke(cbt.ToolName, cbt.Reason));
                break;
            // PromptTokens = 本次 LLM 调用实际发送的完整 prompt（系统 + 工具 + 会话消息），
            // 即当前会话此刻的真实上下文大小。不在这里做跨会话累积——
            // 会话切换 / compact 后由 TuiApp 负责按 session 重算显示。
            case TokenUsageEvent tu:
                Enqueue(() => OnContextTokens?.Invoke(tu.PromptTokens));
                break;
        }
    }

    /// <summary>
    /// 分发所有队列中的 UI 操作。
    /// 应在 UI 线程的主循环中调用。
    /// </summary>
    public void DispatchAll()
    {
        while (_uiActions.TryDequeue(out var action))
            action();
    }

    /// <summary>
    /// 取消所有待处理的 UI 操作，清空队列。
    /// 在 Agent 被中断时调用，防止取消后 UI 继续消费积压事件。
    /// </summary>
    public void Cancel()
    {
        while (_uiActions.TryDequeue(out _)) { }
    }

    /// <summary>
    /// 将 UI 操作加入队列并唤醒 UI 线程。
    /// </summary>
    /// <param name="action">要执行的 UI 操作。</param>
    private void Enqueue(Action action)
    {
        _uiActions.Enqueue(action);
        // 唤醒 UI 线程，让 DispatchAll 尽快执行
        // 如果 OnAgentEvent 在后台线程调用，UI 主循环可能在 sleep，
        // 不唤醒的话要等到下一个 deadline/输入事件才会 DispatchAll
        try
        {
            Dispatcher.Current.Post(static () => { });
        }
        catch (InvalidOperationException)
        {
            // 未附着运行中的 TerminalApp（单元测试 / headless 场景）：
            // 唤醒是纯优化，队列仍可由 DispatchAll 显式消费，忽略即可
        }
    }

    /// <summary>Steer 消息转正事件。参数：steerId, 消息文本。</summary>
    public event Action<string, string>? OnSteerPromoted;

    /// <summary>用户消息事件。</summary>
    public event Action<string>? OnUserMessage;

    /// <summary>Assistant 开始事件。</summary>
    public event Action? OnAssistantStart;

    /// <summary>思考内容更新事件。</summary>
    public event Action<string>? OnThinkingUpdate;

    /// <summary>思考结束事件。</summary>
    public event Action? OnThinkingEnd;

    /// <summary>Assistant 增量文本事件（单个 token）。</summary>
    public event Action<string>? OnAssistantDelta;

    /// <summary>Assistant 结束事件。</summary>
    public event Action<string>? OnAssistantEnd;

    /// <summary>工具开始执行事件。</summary>
    public event Action<string, string, string>? OnToolStart;

    /// <summary>工具结束执行事件。</summary>
    public event Action<string, string, bool, string, object?>? OnToolEnd;

    /// <summary>Agent 开始事件。</summary>
    public event Action? OnAgentStart;

    /// <summary>Agent 结束事件。</summary>
    public event Action? OnAgentEnd;

    /// <summary>用户中断事件。</summary>
    public event Action? OnInterrupted;

    /// <summary>LLM 错误事件。参数：原始消息, HTTP 状态码, 是否上下文溢出, 是否可恢复。</summary>
    public event Action<string, int?, bool, bool>? OnError;

    /// <summary>断路器触发事件。</summary>
    public event Action<string, string>? OnCircuitBreaker;

    /// <summary>
    /// 上下文 token 测量事件。参数：最近一次 LLM 调用的 PromptTokens（当前上下文大小）。
    /// 非累积计数——每次都是当次 prompt 的实测值，由 TuiApp 按 session 决定如何使用。
    /// </summary>
    public event Action<int>? OnContextTokens;

    /// <summary>上下文压缩开始事件。</summary>
    public event Action? OnCompactStart;

    /// <summary>上下文压缩完成事件。参数：结果消息。</summary>
    public event Action<string>? OnCompactEnd;

    /// <summary>触发压缩开始（非 Agent 事件，由 TuiApp 直接调用）。</summary>
    public void FireCompactStart() => Enqueue(() => OnCompactStart?.Invoke());

    /// <summary>触发压缩完成（非 Agent 事件，由 TuiApp 直接调用）。</summary>
    public void FireCompactEnd(string message) => Enqueue(() => OnCompactEnd?.Invoke(message));
}
