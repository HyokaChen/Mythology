using System.Text.Json;
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
    Action<ExploreDetails>? onExploreVariant = null,
    ToolDisplayFormatter? toolDisplay = null
)
{
    private readonly ToolDisplayFormatter _toolDisplay = toolDisplay ?? new ToolDisplayFormatter();
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
                CloseThinking();
                target.AppendContent(chunk.Delta);
                break;

            // ── 思考内容 ──
            case StreamChunk { ContentType: StreamContentType.Thinking } chunk:
                // 正文后面出现新的思考阶段：先把正文冻结到它发生的位置。
                SealDraftContent();

                // 每轮思考独立成卡：工具调用/正文之后的推理必须新开步骤，
                // 否则第一轮思考卡会一直增长并停在最上方，时间线错乱。
                if (_thinkingStep is null || !_thinkingStep.IsProcessing)
                {
                    _thinkingStep = new AgentStep
                    {
                        Kind = StepKind.Thinking,
                        Status = AgentStepStatus.Processing,
                        IsDetailsExpanded = true,
                    };
                    target.AgentSteps.Add(_thinkingStep);
                }

                _thinkingStep.ThinkingBuilder.Append(chunk.Delta);
                _thinkingStep.NotifyThinkingChanged();
                break;

            case StreamChunk { ContentType: StreamContentType.ThinkingEnd }:
                CloseThinking();
                break;

            // ── 工具调用 ──
            case ToolExecutionStart tes:
            {
                // 上下文接续机制对用户隐藏（new_context / history_* 不建步骤卡），
                // 也不应该切断可见的思考/正文时间线。
                if (Core.Tools.HiddenTools.IsHidden(tes.ToolName))
                    break;

                // 工具边界就是思考/正文阶段边界；部分模型整回合只发一次 ThinkingEnd，
                // 不在这里收口会让工具后的推理继续写进上一轮思考卡、正文堆到回合末尾。
                CloseThinking();
                SealDraftContent();

                var step = new AgentStep
                {
                    Kind = StepKind.ToolCall,
                    ToolName = tes.ToolName,
                    Title = _toolDisplay.Label(tes.ToolName),
                    Status = AgentStepStatus.Processing,
                    ArgumentsSummary = ToolDisplayFormatter.Arguments(tes.Arguments),
                    FullArguments = ToolDisplayFormatter.Detail(tes.Arguments),
                };
                _toolSteps[tes.ToolCallId] = step;
                target.AgentSteps.Add(step);
                break;
            }

            case ToolExecutionUpdate:
                // 原始 partial 可能是完整文件内容/大量 JSON；进度由 spinner 表达，
                // 不把原始输出刷进对话，结束事件再写语义摘要 + 详情。
                break;

            case ToolExecutionEnd tee:
                if (_toolSteps.TryGetValue(tee.ToolCallId, out var done))
                {
                    done.Status = tee.IsError ? AgentStepStatus.Failed : AgentStepStatus.Completed;
                    done.IsError = tee.IsError;
                    var rawResult = tee.Result;
                    if (
                        string.Equals(done.ToolName, "tool_search", StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(rawResult)
                    )
                        rawResult = Core.Tools.HiddenTools.Redact(rawResult);

                    var diff = ToolDiffBuilder.Build(tee.Details as IDiffDetails);
                    if (diff is not null)
                    {
                        done.ApplyDiff(diff.Lines, diff.Summary);
                        done.ResultSummary = diff.Summary;
                    }

                    if (!string.IsNullOrWhiteSpace(rawResult))
                    {
                        done.FullResult = ToolDisplayFormatter.Detail(rawResult);
                        if (diff is null)
                            done.ResultSummary = ToolDisplayFormatter.Result(rawResult);
                    }
                    else if (tee.IsError && diff is null)
                    {
                        done.ResultSummary = "失败";
                    }
                }
                // 变体审阅：详情为 ExploreDetails 时转发
                if (tee.Details is ExploreDetails ed)
                    onExploreVariant?.Invoke(ed);
                break;

            // ── LLM 错误 ──
            case AgentError ae:
            {
                CloseThinking();
                var message = FormatError(ae);
                target.AppendContent($"\n\n> ⚠️ **调用出错**：{message}");
                onError?.Invoke(message);
                break;
            }

            // ── 断路器 ──
            case CircuitBreakerTriggered cbt:
                SealDraftContent();
                target.AgentSteps.Add(
                    new AgentStep
                    {
                        Kind = StepKind.ToolCall,
                        ToolName = cbt.ToolName,
                        Title = _toolDisplay.Label(cbt.ToolName),
                        Status = AgentStepStatus.Failed,
                        ResultSummary = $"已熔断：{cbt.Reason}",
                    }
                );
                break;
        }
    }

    /// <summary>
    /// 回合结束时兜底关闭仍在处理中的思考步骤：
    /// 部分模型只返回工具调用、不返回正文，Core 不会发 ThinkingEnd；
    /// 不兜底会把「思考中…」和 spinner 永久留在已结束的回合里。
    /// </summary>
    public void CompleteThinking() => CloseThinking();

    /// <summary>
    /// 结束当前思考阶段并释放引用。下一次 Thinking 片段会新建独立步骤，
    /// 因此「思考 → 工具 → 思考」在时间线上保持各自的卡片。
    /// </summary>
    private void CloseThinking()
    {
        if (_thinkingStep is { IsProcessing: true })
            _thinkingStep.Status = AgentStepStatus.Completed;
        _thinkingStep = null;
    }

    /// <summary>
    /// 把当前流式正文冻结为独立 Content 步骤，并清空最终气泡。
    /// 工具/下一轮思考开始即阶段结束；冻结后新正文会重新从最终气泡开始。
    /// </summary>
    private void SealDraftContent()
    {
        if (!target.HasVisibleContent)
        {
            if (target.Content.Length > 0)
                target.ResetContent();
            return;
        }

        // 冻结时去掉首尾换行/空格：TextBlock 会把尾部空行按 24px 行高照常撑开，
        // 在正文与下一步骤之间留下一个大空洞（模型常在句末输出 "\n\n"）。
        target.AgentSteps.Add(AgentStep.CreateContent(target.Content.ToString().Trim()));
        target.ResetContent();
    }

    /// <summary>提交暂存的来源信息（Agent End 时调用）。</summary>
    public void CommitSources()
    {
        foreach (var source in _pendingSources)
            target.Sources.Add(source);
        _pendingSources.Clear();
    }

    /// <summary>
    /// 把 provider 原始错误（常见为整段 JSON）压成一行用户可读文本。
    /// 原始错误仍由 Core 日志保留；这里只负责展示。
    /// </summary>
    internal static string FormatError(AgentError error)
    {
        var raw = error.Message?.Trim();
        if (string.IsNullOrEmpty(raw))
            return "未知错误";

        var message = raw;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (
                    root.TryGetProperty("error", out var nested)
                    && nested.ValueKind == JsonValueKind.Object
                    && nested.TryGetProperty("message", out var nestedMessage)
                    && nestedMessage.ValueKind == JsonValueKind.String
                )
                    message = nestedMessage.GetString() ?? raw;
                else if (
                    root.TryGetProperty("message", out var direct)
                    && direct.ValueKind == JsonValueKind.String
                )
                    message = direct.GetString() ?? raw;
            }
        }
        catch (JsonException)
        {
            // 非 JSON：直接使用原文。
        }

        message = message.Replace("\r", " ").Replace("\n", " ").Trim();
        const string prefix = "OpenAIException - ";
        if (message.StartsWith(prefix, StringComparison.Ordinal))
            message = message[prefix.Length..];
        if (message.Length > 300)
            message = message[..299].TrimEnd() + "…";

        return error.HttpStatusCode is { } status ? $"HTTP {status} · {message}" : message;
    }
}
