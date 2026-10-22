using Core.Agent;
using Microsoft.Extensions.Logging;

namespace Desktop.Services;

/// <summary>
/// Agent 桥接（单例）：将 Core.Agent 的流式执行封装为 UI 友好的事件推送。
/// Desktop 直接调用 Core.Agent（本地执行），无网络层 / SignalR。
/// </summary>
public sealed class DesktopAgentBridge
{
    private readonly ILogger<DesktopAgentBridge>? _logger;

    /// <summary>Agent 事件推送（后台线程触发，UI 侧需调度到 UI 线程）。</summary>
    public event EventHandler<AgentEvent>? OnStreamEvent;

    /// <summary>处理状态变化。</summary>
    public event Action<bool>? OnProcessingChanged;

    /// <summary>Token 用量事件。</summary>
    public event Action<int, int>? OnTokenUsage;

    /// <summary>当前是否正在处理。</summary>
    public bool IsProcessing { get; private set; }

    /// <summary>底层 Agent 实例。</summary>
    public Agent Agent { get; }

    public DesktopAgentBridge(Agent agent, ILogger<DesktopAgentBridge>? logger = null)
    {
        Agent = agent;
        _logger = logger;

        // 所有 Agent 生命周期事件 → 转发给订阅者
        Agent.OnEvent += async (evt, ct) =>
        {
            try
            {
                OnStreamEvent?.Invoke(this, evt);
                if (evt is TokenUsageEvent tu)
                    OnTokenUsage?.Invoke(tu.PromptTokens, tu.CompletionTokens);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Agent 事件转发失败: {EventType}", evt.GetType().Name);
            }
        };
    }

    /// <summary>
    /// 发送用户消息并等待 Agent 完成（流式事件通过 OnStreamEvent 推送）。
    /// </summary>
    public async Task SendAsync(string userMessage, CancellationToken ct)
    {
        if (IsProcessing)
            throw new InvalidOperationException("Agent 正在处理中，请等待当前操作完成。");

        IsProcessing = true;
        OnProcessingChanged?.Invoke(true);
        try
        {
            await Agent.Prompt(userMessage, ct);
        }
        finally
        {
            IsProcessing = false;
            OnProcessingChanged?.Invoke(false);
        }
    }

    /// <summary>
    /// 继续当前对话（不追加用户消息）：被迫换页后在新页接续上一任务，不必等用户发话。
    /// </summary>
    public async Task ContinueAsync(CancellationToken ct)
    {
        if (IsProcessing)
            throw new InvalidOperationException("Agent 正在处理中，请等待当前操作完成。");

        IsProcessing = true;
        OnProcessingChanged?.Invoke(true);
        try
        {
            await Agent.Continue(ct);
        }
        finally
        {
            IsProcessing = false;
            OnProcessingChanged?.Invoke(false);
        }
    }

    /// <summary>中断当前正在处理的请求。</summary>
    public void Stop()
    {
        if (!IsProcessing)
            return;
        try
        {
            Agent.Abort();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "中断 Agent 失败");
        }
    }
}
