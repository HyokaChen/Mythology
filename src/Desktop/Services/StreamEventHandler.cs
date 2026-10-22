using Core.Agent;
using Core.Tools;
using Desktop.Models;

namespace Desktop.Services;

/// <summary>
/// 流式事件分发：将 Core.Agent 事件映射到目标 ChatMessage 的 UI 状态
/// （内容追加 / 思考步骤 / 工具步骤 / 来源暂存）。
/// 调用方保证在 UI 线程上调用 Handle。
/// </summary>
public sealed class StreamEventHandler(
    ChatMessage target,
    Action<string>? onError = null,
    Action<ExploreDetails>? onExploreVariant = null
)
{
    private readonly List<SourceInfo> _pendingSources = [];
    private readonly Dictionary<string, AgentStep> _toolSteps = [];
    private AgentStep? _thinkingStep;

    /// <summary>事件处理的目标消息。</summary>
    public ChatMessage Target => target;

    /// <summary>处理单个 Agent 事件。</summary>
    public void Handle(AgentEvent evt)
    {
        switch (evt)
        {
            // ── 流式文本 ──
            case StreamChunk { ContentType: StreamContentType.Text } chunk:
                target.Content.Append(chunk.Delta);
                break;

            // ── 思考内容 ──
            case StreamChunk { ContentType: StreamContentType.Thinking } chunk:
                _thinkingStep ??= new AgentStep
                {
                    Kind = StepKind.Thinking,
                    Title = "思考中…",
                    Status = AgentStepStatus.Processing,
                };
                _thinkingStep.ThinkingBuilder.Append(chunk.Delta);
                _thinkingStep.NotifyThinkingChanged();
                if (!target.AgentSteps.Contains(_thinkingStep))
                    target.AgentSteps.Add(_thinkingStep);
                break;

            case StreamChunk { ContentType: StreamContentType.ThinkingEnd }:
                _thinkingStep?.Status = AgentStepStatus.Completed;
                break;

            // ── 工具调用 ──
            case ToolExecutionStart tes:
            {
                // 上下文接续机制对用户隐藏（new_context / history_* 不建步骤卡）
                if (Core.Tools.HiddenTools.IsHidden(tes.ToolName))
                    break;

                var step = new AgentStep
                {
                    Kind = StepKind.ToolCall,
                    Title = tes.ToolName,
                    Status = AgentStepStatus.Processing,
                    ArgumentsSummary = Summarize(tes.Arguments, 60),
                };
                _toolSteps[tes.ToolCallId] = step;
                target.AgentSteps.Add(step);
                break;
            }

            case ToolExecutionUpdate tue:
                if (
                    _toolSteps.TryGetValue(tue.ToolCallId, out var updating)
                    && !string.IsNullOrWhiteSpace(tue.PartialResult)
                )
                    updating.ResultSummary = tue.PartialResult;
                break;

            case ToolExecutionEnd tee:
                if (_toolSteps.TryGetValue(tee.ToolCallId, out var done))
                {
                    done.Status = tee.IsError ? AgentStepStatus.Failed : AgentStepStatus.Completed;
                    done.IsError = tee.IsError;
                    if (!string.IsNullOrWhiteSpace(tee.Result))
                        done.ResultSummary = Summarize(
                            // tool_search 摘要：隐藏工具名脱敏（步骤 Title 即工具名）
                            string.Equals(done.Title, "tool_search", StringComparison.Ordinal)
                                ? Core.Tools.HiddenTools.Redact(tee.Result)
                                : tee.Result,
                            120
                        );
                }
                // 变体审阅：详情为 ExploreDetails 时转发
                if (tee.Details is ExploreDetails ed)
                    onExploreVariant?.Invoke(ed);
                break;

            // ── LLM 错误 ──
            case AgentError ae:
                target.Content.Append($"\n\n> ⚠️ **调用出错**：{ae.Message}");
                onError?.Invoke(ae.Message);
                break;

            // ── 断路器 ──
            case CircuitBreakerTriggered cbt:
                target.AgentSteps.Add(
                    new AgentStep
                    {
                        Kind = StepKind.ToolCall,
                        Title = cbt.ToolName,
                        Status = AgentStepStatus.Failed,
                        ResultSummary = $"已熔断：{cbt.Reason}",
                    }
                );
                break;
        }
    }

    /// <summary>提交暂存的来源信息（Agent End 时调用）。</summary>
    public void CommitSources()
    {
        foreach (var source in _pendingSources)
            target.Sources.Add(source);
        _pendingSources.Clear();
    }

    /// <summary>摘录长文本。</summary>
    private static string Summarize(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var single = text.Replace('\n', ' ').Trim();
        return single.Length <= max ? single : single[..max] + "…";
    }
}
